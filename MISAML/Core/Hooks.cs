using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;

namespace MISAML.Core;

// The single mod-facing patching thing, this is very much WIP and WILL 
// be changed in the near future. A mod names any type+method it wants
// ("Asm.Running.VirtualMachine", "HandleSystemCall", or anything else)
// and patches, lazily, on first registration.
//
// Any number of mods can hook the same method, their handlers chain, run in
// priority (config by user, auto later) order, lower number first, equal
// run in reg order. a handler returns HookResult.Continue() to pass the call
// on, or HookResult.Replace(value) to skip the original method. the first
// Replace wins and the rest of the chain is not run, so a handler with side
// effects is never run for a call another handler already took.
public static class Hooks
{
    // setup Log for Hooks
    private static void Log(string message) => StartupHook.Log(nameof(Hooks), message);
 
    // Harmony instance used for all MISAML patches.
    internal static readonly Harmony Harmony = new("MISAML");
 
    // lower runs first
    public const int DefaultPriority = 100;
 
    // one handler in a chain. Owner is only used for log messages.
    private sealed record Registration(
        Func<object?, object?[], HookResult> Handler,
        int Priority,
        long Order, /* reg order, tiebreaker for equal priorities */
        string Owner
    );

    // handlers per type + method, already sorted into run order. Arrays are
    // replaced whole (copy-on-write)m so the patched prefixes can read a chain
    // while another thread is registering without taking a lock
    private static readonly ConcurrentDictionary<string, Registration[]> Chains = new();

    // protects building a new chain from concurrent registrations
    private static readonly object ChainLock = new();
    private static long _nextOrder;
    
    // deduplication set for patched methods/Harmony targets.
    private static readonly HashSet<string> PatchedKeys = [];

    // protects patch state from concurrent access.
    private static readonly object PatchLock = new();

    // handler gets the call's instance (null for static methods) and args
    // (Harmony's __args) and returns either HookResult.Continue() or
    // HookResult.Replace(value) to skip the original (value is ignored for
    // void methods).
    //
    // same signature mods were already compiled against, so existing mods keep
    // working
    public static void Register(
        string typeName, 
        string methodName, 
        Func<object?, object?[], HookResult> handler
    ) => Register(typeName, methodName, handler, DefaultPriority);

    // same as prev register, with a priority, lower numbers run first
    // Returns false and logs why if the reg is not accepted
    public static bool Register(
        string typeName,
        string methodName,
        Func<object?, object?[], HookResult> handler,
        int priority
    )
    {
        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(methodName) || handler == null)
        {
            Log($"failed to register '{typeName}::{methodName}'. type, method and handler are all required!");
            return false;
        }

        var key = Key(typeName, methodName);
        var owner = Describe(handler);
        int pos;
        int total;

        lock (ChainLock)
        {
            var current = Chains.TryGetValue(key, out var existing) ? existing : Array.Empty<Registration>();
            var added = new Registration(handler, priority, _nextOrder++, owner);

            // rebuild the sorded chain w/ the new handler in it
            var next = current
                .Append(added)
                .OrderBy(r => r.Priority)
                .ThenBy(r => r.Order)
                .ToArray();

            Chains[key] = next;
            pos = Array.IndexOf(next, added) + 1;
            total = next.Length;
        }

        Log($"{key}: successfully registered handler from {owner} (priority {priority}), position {pos} of {total}!");

        EnsurePatched(typeName, methodName, key);
        return true;
    }

    // get the name of the assembly containing the handler, so log msg's can
    // identify which mod it belongs to
    private static string Describe(Delegate handler)
        => handler.Method.DeclaringType?.Assembly.GetName().Name ?? "unknown";

    // checks PatchedKeys to see if given method has been patched, if not, patch
    private static void EnsurePatched(string typeName, string methodName, string key)
    {
        lock (PatchLock)
        {
            if (PatchedKeys.Contains(key)) return;

            var method = FindMethod(typeName, methodName);

            if (method != null)
            {
                ApplyPatch(method, key);
                return;
            }
        }

        // calls whenever a new Assembly is loaded, checks the loaded assembly
        // for the method we are looking for
        void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
        {
            var found = FindMethodIn(args.LoadedAssembly, typeName, methodName);
            if (found == null) return; /* not found, return */

            lock (PatchLock)
            {
                if (!PatchedKeys.Contains(key))
                {
                    ApplyPatch(found, key);
                }
            }

            // remove handler (OnAssemblyLoad) from AssemblyLoad method once the
            // method is found and patched
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        }

        // add handler (OnAssemblyLoad) to AssemblyLoad, so whenever AssemblyLoad
        // fires, we also run OnAssemblyLoad (took me so long to understand)
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
    }

    // searches every Assembly currently loaded for a method
    private static MethodInfo? FindMethod(string typeName, string methodName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var found = FindMethodIn(asm, typeName, methodName);
            if (found != null) return found;
        }
        return null; /* not found */
    }

    // single assembly search for a method
    private static MethodInfo? FindMethodIn(Assembly asm, string typeName, string methodName)
    {
        var type = asm.GetType(typeName);
        
        return type?.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
        );
    }

    // applies Harmony prefix to the target method
    private static void ApplyPatch(MethodInfo method, string key)
    {
        try
        {
            // check for void return & static
            bool isVoid = method.ReturnType == typeof(void);
            bool isStatic = method.IsStatic;

            // pick the matching prefix based on return type & whether it's static
            var prefixName = (isVoid, isStatic) switch
            {
                (true, true) => nameof(GenericPrefixVoidStatic),
                (true, false) => nameof(GenericPrefixVoidInstance),
                (false, true) => nameof(GenericPrefixResultStatic),
                (false, false) => nameof(GenericPrefixResultInstance),
            };

            // get prefix method
            var prefix = new HarmonyMethod(
                typeof(Hooks).GetMethod(prefixName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            );

            // patch the target
            Harmony.Patch(method, prefix: prefix);

            // mark the target only after harmony patches successfully
            PatchedKeys.Add(key);
            Log($"patched {key}");
        }
        catch (Exception e)
        {
            // patch failed, so don't add the key, every handler registered for
            // this method will never run, make sure to log
            Log($"failed to patch {key}, its handlers will not run: {e.GetType().Name}: {e.Message}");
        }
    }

    // prefix for void instance methods
    // Harmony fills these parms in by name, don't rename
    private static bool GenericPrefixVoidInstance(MethodBase __originalMethod, object __instance, object[] __args)
        => !RunChain(__originalMethod, __instance, __args).SkipOriginal;

    // prefix for void static methods
    private static bool GenericPrefixVoidStatic(MethodBase __originalMethod, object[] __args)
        => !RunChain(__originalMethod, null, __args).SkipOriginal;

    // prefix for instance methods that return a value
    private static bool GenericPrefixResultInstance(MethodBase __originalMethod, object __instance, object[] __args, ref object __result)
    {
        var result = RunChain(__originalMethod, __instance, __args);

        // if SkipOriginal is true, use Value as the method's return value
        if (result.SkipOriginal) __result = result.Value!;

        // true = run the original method
        // false = skip the original method
        return !result.SkipOriginal;
    }

    // prefix for static methods that return a value
    private static bool GenericPrefixResultStatic(MethodBase __originalMethod, object[] __args, ref object __result)
    {
        var result = RunChain(__originalMethod, null, __args);

        // if SkipOriginal is true, use Value as the method's return value
        if (result.SkipOriginal) __result = result.Value!;

        // true = run the original method
        // false = skip the original method
        return !result.SkipOriginal;
    }

    // runs the handlers registered for the original method in order, until one
    // replaces the call, if None (or none registered), the orig method runs.
    private static HookResult RunChain(MethodBase original, object? instance, object?[] args)
    {
        // make the same key that was used when the handler originally was registered
        var key = Key(original.DeclaringType?.FullName ?? "", original.Name);

        if (!Chains.TryGetValue(key, out var chain)) return HookResult.Continue();

        for (var i = 0; i < chain.Length; i++)
        {
            var result = InvokeSafely(chain[i], instance, args, original);
            if (!result.SkipOriginal) continue; /* not this handler's call, next one */

            // not logged every time normally, a handler replacing a call it owns
            // while other handlers wait is the normal case (e.g. each mod owns
            // its own syscall).
            if (StartupHook.DebugEnabled && i < chain.Length - 1)
            {
                Log($"{key}: handler from {chain[i].Owner} replaced the call, {chain.Length - i - 1} later handler(s) not run.");
            }

            return result;
        }

        return HookResult.Continue();
    }

    // calls a registered handler handler and catches any errors
    // if the handler throws, move on to the next handler (or the orig method)
    private static HookResult InvokeSafely(
        Registration registration,
        object? instance,
        object?[] args,
        MethodBase original
    )
    {
        try
        {
            // call the handler with the original method's args and instance
            return registration.Handler(instance, args);
        }
        catch (Exception e)
        {
            // handler failed, log the error & don't replace the orig, continue
            Log($"handler from {registration.Owner} for {original.DeclaringType?.FullName}.{original.Name} threw: {e}");
            return HookResult.Continue();
        }
    }

    // makes the unique key used to store and find a chain
    private static string Key(string typeName, string methodName) => $"{typeName}::{methodName}";
}