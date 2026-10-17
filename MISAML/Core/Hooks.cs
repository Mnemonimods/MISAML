using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MISAML.Debug;

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

    // priority the given for a mod in misaml.toml, wins over whatever the mod
    // passes to Register
    private static readonly ConcurrentDictionary<string, int> ModPriorities = new(StringComparer.OrdinalIgnoreCase);

    public static void SetModPriority(string modId, int priority) => ModPriorities[modId] = priority;

    // tracks the id of the mod which is currently initializing, incl.
    // across threads/async work
    private static readonly System.Threading.AsyncLocal<string?> CurrentMod = new();

    // sets the current mod while scope is active
    /// <summary>
    /// 
    /// </summary>
    /// <param name="modId"></param>
    /// <returns></returns>
    public static IDisposable EnterMod(string modId) => new ModScope(modId);

    /// <summary>
    /// 
    /// </summary>
    private sealed class ModScope : IDisposable
    {
        private readonly string? _previous;

        public ModScope(string modId)
        {
            _previous = CurrentMod.Value;
            CurrentMod.Value = modId;
        }

        public void Dispose() => CurrentMod.Value = _previous;
    }

    // one handler in a chain. Owner is only used for log messages.
    private sealed record Registration(
        Func<object?, object?[], HookResult> Handler,
        int Priority,
        long Order, /* reg order, tiebreaker for equal priorities */
        string Owner,
        string? Signature /* null = every overload, otherwise only the overload with these parameter types */
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

    // methods Harmony has already been given, so two registrations that land
    // on the same method (Child::Foo & Base::Foo, e.g.) don't add the prefix
    // twice
    private static readonly HashSet<RuntimeMethodHandle> PatchedMethods = [];

    // protects patch state from concurrent access.
    private static readonly object PatchLock = new();

    // handler gets the call's instance (null for static methods) and args
    // (Harmony's __args) and returns either HookResult.Continue() or
    // HookResult.Replace(value) to skip the original (value is ignored for
    // void methods).
    //
    // same signature mods were already compiled against, so existing mods keep
    // working
    /// <summary>
    /// 
    /// </summary>
    /// <param name="typeName"></param>
    /// <param name="methodName"></param>
    /// <param name="handler"></param>
    public static void Register(
        string typeName,
        string methodName,
        Func<object?, object?[], HookResult> handler
    ) => Register(typeName, methodName, handler, DefaultPriority);

    // same as prev register, with a priority, lower numbers run first
    // Returns false and logs why if the reg is not accepted
    /// <summary>
    /// 
    /// </summary>
    /// <param name="typeName"></param>
    /// <param name="methodName"></param>
    /// <param name="handler"></param>
    /// <param name="priority"></param>
    /// <returns></returns>
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

        // "Foo" hooks every overload, "Foo(System.String,System.Int32)" means 
        // only that one
        var (name, signature) = SplitSignature(methodName);
        if (name.Length == 0)
        {
            Log($"failed to register '{typeName}::{methodName}', the method name is empty!");
            return false;
        }

        var key = Key(typeName, name);
        var label = signature == null ? key : $"{key}({signature})";
        var owner = Describe(handler);
        if (ModPriorities.TryGetValue(owner, out var configured)) priority = configured;
        int pos;
        int total;

        lock (ChainLock)
        {
            var current = Chains.TryGetValue(key, out var existing) ? existing : Array.Empty<Registration>();
            var added = new Registration(handler, priority, _nextOrder++, owner, signature);

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

        Log($"{label}: successfully registered handler from {owner} (priority {priority}), position {pos} of {total}!");

        EnsurePatched(typeName, name, signature, label);
        return true;
    }

    // "Foo" -> ("Foo", null), "Foo(System.String,System.Int32)" ->
    // ("Foo", "System.String,System.Int32"), "Foo()" -> ("Foo", "") which 
    // means the overload with no parameters
    /// <summary>
    /// 
    /// </summary>
    /// <param name="methodName"></param>
    /// <returns></returns>
    private static (string Name, string? Signature) SplitSignature(string methodName)
    {
        var open = methodName.IndexOf('(');
        if (open < 0 || !methodName.EndsWith(')')) return (methodName.Trim(), null);

        var signature = methodName[(open + 1)..^1].Replace(" ", "");
        return (methodName[..open].Trim(), signature);
    }

    // parameter types of a method written the way a mod writes them
    /// <summary>
    /// 
    /// </summary>
    /// <param name="method"></param>
    /// <returns></returns>
    private static string SignatureOf(MethodBase method)
        => string.Join(",", method.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name));

    // get the name of the assembly containing the handler, so log msg's can
    // identify which mod it belongs to
    /// <summary>
    /// 
    /// </summary>
    /// <param name="handler"></param>
    /// <returns></returns>
    private static string Describe(Delegate handler)
        => CurrentMod.Value ?? handler.Method.DeclaringType?.Assembly.GetName().Name ?? "unknown";

    // checks PatchedKeys to see if given method has been patched, if not, patch
    /// <summary>
    /// 
    /// </summary>
    /// <param name="typeName"></param>
    /// <param name="methodName"></param>
    /// <param name="signature"></param>
    /// <param name="patchKey"></param>
    private static void EnsurePatched(string typeName, string methodName, string? signature, string patchKey)
    {
        lock (PatchLock)
        {
            if (PatchedKeys.Contains(patchKey)) return;
        }

        // subscribe before searching, so an assembly that loads in between is
        // seen by the listener instead of being ignored/slipping through
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

        var finished = false;
        lock (PatchLock)
        {
            if (PatchedKeys.Contains(patchKey))
            {
                finished = true;
            }
            else
            {
                var methods = FindMethods(typeName, methodName, signature);
                if (methods.Count > 0)
                {
                    ApplyPatches(methods, patchKey);
                    finished = true;
                }
            }
        }

        // found now, or already done by listener, unsub
        if (finished) AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;

        // calls whenever a new Assembly is loaded, checks the loaded assembly
        // for the method we are looking for
        void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
        {
            var found = FindMethodsIn(args.LoadedAssembly, typeName, methodName, signature);
            if (found.Count == 0) return; /* not found, return */

            lock (PatchLock)
            {
                if (!PatchedKeys.Contains(patchKey))
                {
                    ApplyPatches(found, patchKey);
                }
            }

            // remove handler (OnAssemblyLoad) from AssemblyLoad method once the
            // method is found and patched
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        }
    }

    // searches every Assembly currently loaded for the method(s)
    private static List<MethodInfo> FindMethods(string typeName, string methodName, string? signature)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var found = FindMethodsIn(asm, typeName, methodName, signature);
            if (found.Count > 0) return found;
        }
        return []; /* not found */
    }

    // single assembly search, returns every overload with the name (or only the
    // single matching signature), incl ones inherited from a base type
    private static List<MethodInfo> FindMethodsIn(Assembly asm, string typeName, string methodName, string? signature)
    {
        var found = new List<MethodInfo>();

        try
        {
            var type = asm.GetType(typeName);
            if (type == null) return found;

            var sameName = type
                .GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy
                )
                .Where(m => m.Name == methodName)
                .ToList();

            // a bare name on an overloaded method is ambiguous, "Foo(*)" hooks
            // all of them on purpose
            if (signature == null && sameName.Count > 1)
            {
                var overloads = string.Join(" | ", sameName.Select(m => $"({SignatureOf(m)})"));
                Log($"{typeName}::{methodName} has several overloads: {overloads}. Use one of those signatures, or {methodName}(*) to hook all of them");
                return found;
            }

            foreach (var method in sameName)
            {
                if (signature != null && signature != "*" && SignatureOf(method) != signature) continue;

                if (method.IsAbstract || method.IsGenericMethodDefinition)
                {
                    Log($"{typeName}::{methodName}({SignatureOf(method)}) is abstract or generic, can't be hooked");
                    continue;
                }

                // a prefix on Object's methods would run for every object in
                // the game which is obv problematic
                if (method.DeclaringType == typeof(object))
                {
                    Log($"{typeName}::{methodName} is declared on System.Object, refusing to hook it");
                    continue;
                }

                // a method found through a child type is a different MethodInfo from
                // the declared one, and Harmony only accepts the declared one
                found.Add(DeclaredVersion(method));
            }

            if (signature != null && found.Count == 0 && sameName.Count > 0)
            {
                var have = string.Join(" | ", sameName.Select(m => $"({SignatureOf(m)})"));
                Log($"{typeName}::{methodName}({signature}) matches no overload, the ones that exist are: {have}");
            }
        }
        catch (Exception e)
        {
            // an assembly that can't be searched must never break an assembly load
            StartupHook.DebugLog($"could not search {asm.GetName().Name} for {typeName}::{methodName}: {e.GetType().Name}");
        }

        return found;
    }

    // the MethodInfo as seen from the type that declares it, as Harmony refuses
    // a copy obtained from a child type
    private static MethodInfo DeclaredVersion(MethodInfo method)
        => MethodBase.GetMethodFromHandle(method.MethodHandle, method.DeclaringType!.TypeHandle) as MethodInfo
            ?? method;

    private static void ApplyPatches(List<MethodInfo> methods, string patchKey)
    {
        foreach (var method in methods) ApplyPatch(method, patchKey);
    }

    // applies Harmony prefix to the target method
    /// <summary>
    /// 
    /// </summary>
    /// <param name="method"></param>
    /// <param name="patchKey"></param>
    private static void ApplyPatch(MethodInfo method, string patchKey)
    {
        // another registration already patched this exact method
        if (PatchedMethods.Contains(method.MethodHandle))
        {
            PatchedKeys.Add(patchKey);
            return;
        }

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
            PatchedMethods.Add(method.MethodHandle);
            PatchedKeys.Add(patchKey);
            Log($"patched {method.DeclaringType?.FullName}::{method.Name}({SignatureOf(method)})");
        }
        catch (Exception e)
        {
            Log($"failed to patch {patchKey}, its handlers will not run: {e.GetType().Name}: {e.Message}");
        }
    }

    // prefix for void instance methods
    // Harmony fills these params in by name, don't rename
    /// <summary>
    /// 
    /// </summary>
    /// <param name="__originalMethod"></param>
    /// <param name="__instance"></param>
    /// <param name="__args"></param>
    /// <returns></returns>
    private static bool GenericPrefixVoidInstance(MethodBase __originalMethod, object __instance, object[] __args)
        => !RunChain(__originalMethod, __instance, __args).SkipOriginal;

    // prefix for void static methods
    /// <summary>
    /// 
    /// </summary>
    /// <param name="__originalMethod"></param>
    /// <param name="__args"></param>
    /// <returns></returns>
    private static bool GenericPrefixVoidStatic(MethodBase __originalMethod, object[] __args)
        => !RunChain(__originalMethod, null, __args).SkipOriginal;

    // prefix for instance methods that return a value
    /// <summary>
    /// 
    /// </summary>
    /// <param name="__originalMethod"></param>
    /// <param name="__instance"></param>
    /// <param name="__args"></param>
    /// <param name="__result"></param>
    /// <returns></returns>
    private static bool GenericPrefixResultInstance(MethodBase __originalMethod, object __instance, object[] __args, ref object __result)
        => !ApplyReplacement(__originalMethod, RunChain(__originalMethod, __instance, __args), ref __result);

    // prefix for static methods that return a value
    /// <summary>
    /// 
    /// </summary>
    /// <param name="__originalMethod"></param>
    /// <param name="__args"></param>
    /// <param name="__result"></param>
    /// <returns></returns>
    private static bool GenericPrefixResultStatic(MethodBase __originalMethod, object[] __args, ref object __result)
        => !ApplyReplacement(__originalMethod, RunChain(__originalMethod, null, __args), ref __result);

    // runs the handlers registered for the original method in order, until one
    // replaces the call, if None (or none registered), the orig method runs.
    /// <summary>
    /// 
    /// </summary>
    /// <param name="original"></param>
    /// <param name="instance"></param>
    /// <param name="args"></param>
    /// <returns></returns>
    private static HookResult RunChain(MethodBase original, object? instance, object?[] args)
    {
        var chain = CollectChain(original, instance);
        if (chain.Count == 0) return HookResult.Continue();

        // only used for the log message
        var key = Key(original.DeclaringType?.FullName ?? "", original.Name);

        for (var i = 0; i < chain.Count; i++)
        {
            var result = InvokeSafely(chain[i], instance, args, original);
            if (!result.SkipOriginal) continue; /* not this handler's call, next one */

            // not logged every time normally, a handler replacing a call it owns
            // while other handlers wait is the normal case (e.g. each mod owns
            // its own syscall).
            if (StartupHook.DebugEnabled && i < chain.Count - 1)
            {
                Log($"{key}: handler from {chain[i].Owner} replaced the call, {chain.Count - i - 1} later handler(s) not run.");
            }

            return result;
        }

        return HookResult.Continue();
    }

    // every handler that applies to this call, registered on the instance's
    // type or on any type btwn it and the one that declares the method, and
    // for an overload hook, only if the signature matches. a static call has
    // no instance, so only the decl type counts.
    /// <summary>
    /// 
    /// </summary>
    /// <param name="original"></param>
    /// <param name="instance"></param>
    /// <returns></returns>
    private static IReadOnlyList<Registration> CollectChain(MethodBase original, object? instance)
    {
        var declaring = original.DeclaringType;
        string? signature = null;
        List<Registration>? matched = null;

        for (var t = instance?.GetType() ?? declaring; t != null; t = t.BaseType)
        {
            if (Chains.TryGetValue(Key(t.FullName ?? "", original.Name), out var chain))
            {
                foreach (var r in chain)
                {
                    if (r.Signature != null && r.Signature != "*" && r.Signature != (signature ??= SignatureOf(original))) continue;
                    (matched ??= []).Add(r);
                }
            }

            if (t == declaring) break;
        }

        if (matched == null) return Array.Empty<Registration>();
        if (matched.Count == 1) return matched;

        // each chain was already sorted, merge into one run order
        return matched.OrderBy(r => r.Priority).ThenBy(r => r.Order).ToList();
    }

    // uses a handler's replacement value as the method's result, returns true
    // if the orig method should be skipped. a value that doesn't fit the return
    // type is logged and ignored (original runs), throwing here would break the
    // game's own call(s).
    /// <summary>
    /// 
    /// </summary>
    /// <param name="original"></param>
    /// <param name="result"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    private static bool ApplyReplacement(MethodBase original, HookResult result, ref object value)
    {
        if (!result.SkipOriginal) return false;

        var returnType = ((MethodInfo)original).ReturnType;
        var target = Nullable.GetUnderlyingType(returnType) ?? returnType;

        var fit = result.Value == null
            ? !returnType.IsValueType || target != returnType
            : target.IsInstanceOfType(result.Value);

        if (!fit)
        {
            Log($"{original.DeclaringType?.FullName}.{original.Name} returns {returnType.FullName}, but a handler replaced it with {result.Value?.GetType().FullName ?? "null"}, ignoring the replacement");
            return false;
        }

        value = result.Value!;
        return true;
    }

    // calls a registered handler handler and catches any errors
    // if the handler throws, move on to the next handler (or the orig method)
    /// <summary>
    /// 
    /// </summary>
    /// <param name="registration"></param>
    /// <param name="instance"></param>
    /// <param name="args"></param>
    /// <param name="original"></param>
    /// <returns></returns>
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
    /// <summary>
    /// 
    /// </summary>
    /// <param name="typeName"></param>
    /// <param name="methodName"></param>
    /// <returns></returns>
    private static string Key(string typeName, string methodName) => $"{typeName}::{methodName}";
}