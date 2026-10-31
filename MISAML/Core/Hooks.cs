using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using HarmonyLib;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MISAML.Interfaces;

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

/// <summary>
/// Provides a mod-facing API for lazily patching methods and chaining handlers.
/// </summary>
public class Hooks() : IHooks
{
    private static ILogger _logger = NullLogger.Instance;

    public Hooks(ILogger<Hooks> logger) : this()
    {
        _logger = logger;
    }

    // Harmony instance used for all MISAML patches.
    internal static readonly Harmony Harmony = new("MISAML");

    // lower runs first
    public const int DefaultPriority = 100;

    // priority the given for a mod in misaml.toml, wins over whatever the mod
    // passes to Register
    private static readonly ConcurrentDictionary<string, int> ModPriorities = new(StringComparer.OrdinalIgnoreCase);

    public void SetModPriority(string modId, int priority) => ModPriorities[modId] = priority;

    // tracks the id of the mod which is currently initializing, incl.
    // across threads/async work
    private static readonly System.Threading.AsyncLocal<string?> CurrentMod = new();

    /// <summary>
    /// Sets the current mod for the lifetime of the returned scope.
    /// </summary>
    /// <param name="modId">The ID of the mod to set as current.</param>
    /// <returns>A disposable scope that restores the previous mod when disposed.</returns>
    public IDisposable EnterMod(string modId) => new ModScope(modId);

    /// <summary>
    /// Temporarily sets the current mod and restores the previous mod when disposed.
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

    /// <summary>
    /// Represents a registered handler in a hook chain, including its execution priority,
    /// registration order, owner, and optional method signature filter.
    /// </summary>
    /// <param name="Handler">The handler to execute.</param>
    /// <param name="Priority">The priority.</param>
    /// <param name="Order">The registration order.</param>
    /// <param name="Owner">The handler's owner, used for logging.</param>
    /// <param name="Signature">Optional parameter type filter.</param>
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
    private static readonly ConcurrentDictionary<(string Type, string Method), Registration[]> Chains = new();

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
    /// Registers a handler for a method using the default priority.
    /// The handler receives the instance and arguments, and can continue the
    /// call or replace the return value to skip the original method.
    /// </summary>
    /// <param name="typeName">The fully qualified name of the target type
    /// containing the method to hook.</param>
    /// <param name="methodName">The name of the target method.</param>
    /// <param name="handler">The handler to execute.</param>
    public void Register(
        string typeName,
        string methodName,
        Func<object?, object?[], HookResult> handler
    ) => Register(typeName, methodName, handler, DefaultPriority);

    // same as string versions, but takes the type itself, so a mod that
    // references the game's assemblies gets autocomplete and compile errors
    // instead of typos
    /// <summary>
    /// Registers a handler for a method using its type directly and the default
    /// priority.
    /// </summary>
    /// <param name="type">The type containing the target method.</param>
    /// <param name="methodName">The name of the target method.</param>
    /// <param name="handler">The handler to execute.</param>
    public void Register(
        Type type,
        string methodName,
        Func<object?, object?[], HookResult> handler
    ) => Register(type, methodName, handler, DefaultPriority);

    /// <summary>
    /// Registers a handler for a method using its type directly and a custom priority.
    /// </summary>
    /// <param name="type">The type containing the method to hook.</param>
    /// <param name="methodName">The name of the method to hook.</param>
    /// <param name="handler">The handler to execute.</param>
    /// <param name="priority">The priority.</param>
    /// <returns>True if registration succeeds, else, false.</returns>
    public bool Register(
        Type? type,
        string methodName,
        Func<object?, object?[], HookResult> handler,
        int priority
    )
    {
        if (type?.FullName == null)
        {
            _logger.LogError($"failed to register '{type}::{methodName}'. a type with a full name is required!");
            return false;
        }

        return Register(type.FullName, methodName, handler, priority);
    }

    // Hooks.Register<VirtualMachine>(nameof(VirtualMachine.HandleSystemCall), handler)
    /// <summary>
    /// Registers a handler for a method using its type parameter and an optional priority.
    /// </summary>
    /// <typeparam name="T">The type containing the method to hook.</typeparam>
    /// <param name="methodName">The name of the method to hook.</param>
    /// <param name="handler">The handler to execute.</param>
    /// <param name="priority">The priority.</param>
    /// <returns>True if registration succeeds, else, false.</returns>
    public bool Register<T>(
        string methodName,
        Func<object?, object?[], HookResult> handler,
        int priority = DefaultPriority
    ) => Register(typeof(T), methodName, handler, priority);

    // Returns false and logs why if the reg is not accepted
    /// <summary>
    /// Registers a handler for a method using its fully qualified type name and
    /// a custom priority. Handlers with lower priority values run first, with
    /// registration order breaking ties.
    /// </summary>
    /// <param name="typeName">The fully qualified name of the type containing
    /// the method to hook.</param>
    /// <param name="methodName">The method name, optionally including a
    /// parameter type signature to target a specific overload.</param>
    /// <param name="handler">The handler to execute.</param>
    /// <param name="priority">The priority.</param>
    /// <returns>True if registration succeeds, else, false.</returns>
    public bool Register(
        string typeName,
        string methodName,
        Func<object?, object?[], HookResult> handler,
        int priority
    )
    {
        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(methodName) || handler == null)
        {
            _logger.LogError($"failed to register '{typeName}::{methodName}'. type, method and handler are all required!");
            return false;
        }

        // "Foo" hooks every overload, "Foo(System.String,System.Int32)" means 
        // only that one
        var (name, signature) = SplitSignature(methodName);
        if (name.Length == 0)
        {
            _logger.LogError($"failed to register '{typeName}::{methodName}', the method name is empty!");
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
            var current = Chains.TryGetValue((typeName, name), out var existing) ? existing : Array.Empty<Registration>();
            var added = new Registration(handler, priority, _nextOrder++, owner, signature);

            // rebuild the sorded chain w/ the new handler in it
            var next = current
                .Append(added)
                .OrderBy(r => r.Priority)
                .ThenBy(r => r.Order)
                .ToArray();

            Chains[(typeName, name)] = next;
            pos = Array.IndexOf(next, added) + 1;
            total = next.Length;
        }

        _logger.LogInformation($"{label}: successfully registered handler from {owner} (priority {priority}), position {pos} of {total}!");

        EnsurePatched(typeName, name, signature, label);
        return true;
    }

    // C# keywords a mod can write in a signature instead of the full type name, to avoid mistakes
    private static readonly Dictionary<string, string> TypeAliases = new()
    {
        /* C# Type Names */
        ["bool"] = "System.Boolean",
        ["byte"] = "System.Byte",
        ["sbyte"] = "System.SByte",
        ["short"] = "System.Int16",
        ["ushort"] = "System.UInt16",
        ["int"] = "System.Int32",
        ["uint"] = "System.UInt32",
        ["long"] = "System.Int64",
        ["ulong"] = "System.UInt64",
        ["float"] = "System.Single",
        ["double"] = "System.Double",
        ["decimal"] = "System.Decimal",
        ["char"] = "System.Char",
        ["string"] = "System.String",
        ["object"] = "System.Object",
        ["nint"] = "System.IntPtr",
        ["nuint"] = "System.UIntPtr",
        /* .NET Type Names */
        ["Boolean"] = "System.Boolean",
        ["Byte"] = "System.Byte",
        ["SByte"] = "System.SByte",
        ["Int16"] = "System.Int16",
        ["UInt16"] = "System.UInt16",
        ["Int32"] = "System.Int32",
        ["UInt32"] = "System.UInt32",
        ["Int64"] = "System.Int64",
        ["UInt64"] = "System.UInt64",
        ["Single"] = "System.Single",
        ["Double"] = "System.Double",
        ["Decimal"] = "System.Decimal",
        ["Char"] = "System.Char",
        ["String"] = "System.String",
        ["Object"] = "System.Object",
        ["IntPtr"] = "System.IntPtr",
        ["UIntPtr"] = "System.UIntPtr",
    };

    /// <summary>
    /// Matches whole type aliases without matching parts of identifiers or qualified names.
    /// 
    /// <list type="bullet">
    /// <item><description><c>(?&lt;![\w.])</c> stops a match right after a word character or dot.</description></item>
    /// <item><description><c>(...)</c> matches any alias, joined by <c>|</c> (OR).</description></item>
    /// <item><description><c>(?![\w.])</c> stops a match right before a word character or dot.</description></item>
    /// </list>
    /// </summary>
    private static readonly Regex AliasPattern = new(
        @"(?<![\w.])(" + string.Join("|", TypeAliases.Keys) + @")(?![\w.])",
        RegexOptions.Compiled
    );

    private static string ExpandAliases(string signature)
        => AliasPattern.Replace(signature, m => TypeAliases[m.Value]);

    // "Foo" -> ("Foo", null), "Foo(System.String,System.Int32)" ->
    // ("Foo", "System.String,System.Int32"), "Foo()" -> ("Foo", "") which 
    // means the overload with no parameters
    /// <summary>
    /// Splits a method name into its name and optional parameter type signature.
    /// </summary>
    /// <param name="methodName">The method name, optionally folloewd by
    /// parameter types in parentheses.</param>
    /// <returns>The method name and signature, or null if no signature is specified.</returns>
    private static (string Name, string? Signature) SplitSignature(string methodName)
    {
        var open = methodName.IndexOf('(');
        if (open < 0 || !methodName.EndsWith(')')) return (methodName.Trim(), null);

        var signature = ExpandAliases(methodName[(open + 1)..^1].Replace(" ", ""));
        return (methodName[..open].Trim(), signature);
    }

    // parameter types of a method written the way a mod writes them
    /// <summary>
    /// Get a method's parameter types as a comma-separated signature.
    /// </summary>
    /// <param name="method">The method to get the signature of.</param>
    /// <returns>The fully qualified parameter type names.</returns>
    private static string SignatureOf(MethodBase method)
        => string.Join(",", method.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name));

    // get the name of the assembly containing the handler, so log msg's can
    // identify which mod it belongs to
    /// <summary>
    /// Gets the mod or assembly name associated with a handler for logging.
    /// </summary>
    /// <param name="handler">The handler whose owner to identify</param>
    /// <returns>The mod name, assembly name, or "unknown" if unavailable.</returns>
    private static string Describe(Delegate handler)
        => CurrentMod.Value ?? handler.Method.DeclaringType?.Assembly.GetName().Name ?? "unknown";

    // checks PatchedKeys to see if given method has been patched, if not, patch
    /// <summary>
    /// Ensures the target method is patched, searching loaded assemblies
    /// immediately and listening for future assembly loads if the method isn't
    /// found.
    /// </summary>
    /// <param name="typeName">The fully qualified name of the type containing the method.</param>
    /// <param name="methodName">The name of the method to patch.</param>
    /// <param name="signature">The optional parameter type signature used to select an overload.</param>
    /// <param name="patchKey">The unique key identifying the patch.</param>
    private void EnsurePatched(string typeName, string methodName, string? signature, string patchKey)
    {
        // subscribe before searching, so an assembly that loads in between is
        // seen by the listener instead of being ignored/slipping through
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;

        var finished = false;
        lock (PatchLock)
        {
            if (PatchedKeys.Contains(patchKey))
            {
                AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
                return;
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
    /// <summary>
    /// Searches loaded assemblies for the target method(s).
    /// </summary>
    /// <param name="typeName">The fully qualified name of the type containing the method.</param>
    /// <param name="methodName">The name of the method to find.</param>
    /// <param name="signature">The optional parameter type signature used to
    /// select an overload.</param>
    /// <returns>The matching methods, or an empty list if none are found.</returns>
    private List<MethodInfo> FindMethods(string typeName, string methodName, string? signature)
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
    /// <summary>
    /// Searches an assembly for methods matching the specified type, name and 
    /// optional signature, including inherited methods. Rejects ambiguous or
    /// unsupported matches.
    /// </summary>
    /// <param name="asm">The assembly to search.</param>
    /// <param name="typeName">The fully qualified name of the type containing the method.</param>
    /// <param name="methodName">The name of the method to find.</param>
    /// <param name="signature">The optional parameter type signature used to select 
    /// overloads, "*" matches all overloads.</param>
    /// <returns>The matching methods, or an empty list if none are found.</returns>
    private List<MethodInfo> FindMethodsIn(Assembly asm, string typeName, string methodName, string? signature)
    {
        var found = new List<MethodInfo>();

        Type? type = null;

        try
        {
            type = asm.GetType(typeName);
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
                _logger.LogError($"{typeName}::{methodName} has several overloads: {overloads}. Use one of those signatures, or {methodName}(*) to hook all of them");
                return found;
            }

            foreach (var method in sameName)
            {
                if (signature != null && signature != "*" && SignatureOf(method) != signature) continue;

                // a static call has no instance, so there's no way to tell which
                // child type it came through
                if (method.IsStatic && method.DeclaringType != type)
                {
                    _logger.LogError($"{typeName}::{methodName} is a static method declared on {method.DeclaringType?.FullName}, register the handler on that type instead");
                    continue;
                }

                if (method.IsAbstract || method.IsGenericMethodDefinition)
                {
                    _logger.LogError($"{typeName}::{methodName}({SignatureOf(method)}) is abstract or generic, can't be hooked");
                    continue;
                }

                // a prefix on Object's methods would run for every object in
                // the game which is obv problematic
                if (method.DeclaringType == typeof(object))
                {
                    _logger.LogError($"{typeName}::{methodName} is declared on System.Object, refusing to hook it");
                    continue;
                }

                // a method found through a child type is a different MethodInfo from
                // the declared one, and Harmony only accepts the declared one
                found.Add(DeclaredVersion(method));
            }

            if (signature != null && found.Count == 0 && sameName.Count > 0)
            {
                var have = string.Join(" | ", sameName.Select(m => $"({SignatureOf(m)})"));
                _logger.LogError($"{typeName}::{methodName}({signature}) matches no overload, the ones that exist are: {have}");
            }
            }
            catch (Exception e)
            {
                if (type != null)
                {
                    _logger.LogWarning($"could not read the methods of {typeName} to find '{methodName}': {e.GetType().Name}: {e.Message}");
                }
                else
                {
                    _logger.LogDebug($"could not search {asm.GetName().Name} for {typeName}::{methodName}: {e.GetType().Name}");
                }
            }

        return found;
    }

    // the MethodInfo as seen from the type that declares it, as Harmony refuses
    // a copy obtained from a child type
    /// <summary>
    /// Resolves a method to its version declared by its own type for Harmony patching.
    /// </summary>
    /// <param name="method">The method to resolve.</param>
    /// <returns>The declared method, or the original method if resolution fails.</returns>
    private static MethodInfo DeclaredVersion(MethodInfo method)
        => MethodBase.GetMethodFromHandle(method.MethodHandle, method.DeclaringType!.TypeHandle) as MethodInfo
            ?? method;

    /// <summary>
    /// Applies patches to all matching methods.
    /// </summary>
    /// <param name="methods">The methods to patch.</param>
    /// <param name="patchKey">The key identifying the patch.</param>
    private void ApplyPatches(List<MethodInfo> methods, string patchKey)
    {
        foreach (var method in methods) ApplyPatch(method, patchKey);
    }

    // applies Harmony prefix to the target method
    /// <summary>
    /// Applies a Harmony prefix to the target method, selecting the appropriate
    /// prefix based on whether the method is static and/or returns void.
    /// </summary>
    /// <param name="method">The method to patch.</param>
    /// <param name="patchKey">The key identifying the patch.</param>
    private void ApplyPatch(MethodInfo method, string patchKey)
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
            _logger.LogInformation($"patched {method.DeclaringType?.FullName}::{method.Name}({SignatureOf(method)})");
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, $"failed to patch {patchKey}, its handlers will not run");
        }
    }

    // prefix for void instance methods
    // Harmony fills these params in by name, don't rename
    /// <summary>
    /// Harmony prefix for void instance methods.
    /// </summary>
    /// <param name="__originalMethod">The original method being called.</param>
    /// <param name="__instance">The instance the method is called on.</param>
    /// <param name="__args">The method's arguments.</param>
    /// <returns>False if the original method should be skipped, else, true.</returns>
    private static bool GenericPrefixVoidInstance(MethodBase __originalMethod, object __instance, object[] __args)
        => !RunChain(__originalMethod, __instance, __args).SkipOriginal;

    // prefix for void static methods
    /// <summary>
    /// Harmony prefix for void static methods.
    /// </summary>
    /// <param name="__originalMethod">The original method being called.</param>
    /// <param name="__args">The method's arguments.</param>
    /// <returns>False if the original method should be skipped, else, true.</returns>
    private static bool GenericPrefixVoidStatic(MethodBase __originalMethod, object[] __args)
        => !RunChain(__originalMethod, null, __args).SkipOriginal;

    // prefix for instance methods that return a value
    /// <summary>
    /// Harmony prefix for instance methods that return a value.
    /// </summary>
    /// <param name="__originalMethod">The original method being called.</param>
    /// <param name="__instance">The instance the method is called on.</param>
    /// <param name="__args">The method's arguments.</param>
    /// <param name="__result">The return value, which may be replaced by a handler.</param>
    /// <returns>False if the original method should be skipped, else, true.</returns>
    private static bool GenericPrefixResultInstance(MethodBase __originalMethod, object __instance, object[] __args, ref object __result)
        => !ApplyReplacement(__originalMethod, RunChain(__originalMethod, __instance, __args), ref __result);

    /// <summary>
    /// Harmony prefix for static methods that return a value.
    /// </summary>
    /// <param name="__originalMethod">The original method being called.</param>
    /// <param name="__args">The method's arguments.</param>
    /// <param name="__result">The return value, which may be replaced by a handler.</param>
    /// <returns>False if the original method should be skipped, else, true.</returns>
    private static bool GenericPrefixResultStatic(MethodBase __originalMethod, object[] __args, ref object __result)
        => !ApplyReplacement(__originalMethod, RunChain(__originalMethod, null, __args), ref __result);

    // runs the handlers registered for the original method in order, until one
    // replaces the call, if None (or none registered), the orig method runs.
    /// <summary>
    /// Executes registered handlers in order until one replaces the call.
    /// </summary>
    /// <param name="original">The original method being called.</param>
    /// <param name="instance">The instance the method is called on, or null for static methods.</param>
    /// <param name="args">The method's arguments.</param>
    /// <returns>The first replacement result, or a result indicating that the original method should run.</returns>
    private static HookResult RunChain(MethodBase original, object? instance, object?[] args)
    {
        var chain = CollectChain(original, instance);
        if (chain.Length == 0) return HookResult.Continue();

        for (var i = 0; i < chain.Length; i++)
        {
            var result = InvokeSafely(chain[i], instance, args, original);
            if (!result.SkipOriginal) continue; /* not this handler's call, next one */

            // not logged normally, a handler replacing a call it owns while
            // other handlers wait is the "usual" case (each mod owns its own syscall)
            if (Core.DebugEnabled && i < chain.Length - 1)
            {
                var key = Key(original.DeclaringType?.FullName ?? "", original.Name);
                _logger.LogDebug($"{key}: handler from {chain[i].Owner} replaced the call, {chain.Length - i - 1} later handler(s) not run.");
            }

            return result;
        }

        return HookResult.Continue();
    }

    private static readonly Registration[] NoHandlers = [];

    /// <summary>
    /// Filters one chain down to the handlers that apply to this overload. A
    /// chain with no signature filter(s) is returned as is, so nothing is
    /// allocated.
    /// </summary>
    /// <param name="chain">A chain of handlers, already sorted.</param>
    /// <param name="original">The original method being called.</param>
    /// <returns>The handler that matches the signature filter, in the same order.</returns>
    private static Registration[] ForOverload(Registration[] chain, MethodBase original)
    {
        string? signature = null;
        List<Registration>? kept = null;

        for (var i = 0; i < chain.Length; i++)
        {
            var r = chain[i];
            var applies =
                r.Signature == null ||
                r.Signature == "*" ||
                r.Signature == (signature ??= SignatureOf(original));

            if (applies)
            {
                kept?.Add(r);
            }
            else if (kept == null)
            {
                // first one that does not apply, start a copy of those that did
                kept = new List<Registration>(chain.Length);
                for (var j = 0; j < i; j++) kept.Add(chain[j]);
            }
        }

        return kept == null ? chain : [.. kept];
    }

    // every handler that applies to this call, registered on the instance's
    // type or on any type btwn it and the one that declares the method. one
    // chain applying (usual case) is returned as is, it is already sorted.
    // only a second chain forces a merge and re-sort.
    /// <summary>
    /// Collects all handlers applicable to the original method from the
    /// instance's type up to and including the method's declaring type.
    /// </summary>
    /// <param name="original">The original method being called.</param>
    /// <param name="instance">The instance the method is being called on, or
    /// null for static methods.</param>
    /// <returns>The matching handlers in run order (priority, then registration
    /// order). Empty if none apply.</returns>
    private static Registration[] CollectChain(MethodBase original, object? instance)
    {
        var declaring = original.DeclaringType;
        var name = original.Name;

        Registration[]? only = null;
        List<Registration>? merged = null;

        for (var t = instance?.GetType() ?? declaring; t != null; t = t.BaseType)
        {
            if (Chains.TryGetValue((t.FullName ?? "", name), out var chain))
            {
                var applicable = ForOverload(chain, original);
                if (applicable.Length > 0)
                {
                    if (only == null)
                    {
                        only = applicable;
                    }
                    else
                    {
                        merged ??= [.. only];
                        merged.AddRange(applicable);
                    }
                }
            }

            if (t == declaring) break;
        }

        if (merged != null)
            return merged.OrderBy(r => r.Priority).ThenBy(r => r.Order).ToArray();

        return only ?? NoHandlers;
    }

    // uses a handler's replacement value as the method's result, returns true
    // if the orig method should be skipped. a value that doesn't fit the return
    // type is logged and ignored (original runs), throwing here would break the
    // game's own call(s).
    /// <summary>
    /// Applies a handler's replacement value if it matches the method's return type.
    /// </summary>
    /// <param name="original">The original method being called.</param>
    /// <param name="result">The handler's result.</param>
    /// <param name="value">The return value to replace if valid.</param>
    /// <returns>True if the replacement is accepted and the original method should be skipped.</returns>
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
            _logger.LogWarning($"{original.DeclaringType?.FullName}.{original.Name} returns {returnType.FullName}, but a handler replaced it with {result.Value?.GetType().FullName ?? "null"}, ignoring the replacement");
            return false;
        }

        value = result.Value!;
        return true;
    }

    // calls a registered handler handler and catches any errors
    // if the handler throws, move on to the next handler (or the orig method)
    /// <summary>
    /// Invokes a registered handler and catches exceptions, allowing the chain to continue
    /// if the handler fails.
    /// </summary>
    /// <param name="registration">The handler registration to invoke.</param>
    /// <param name="instance">The instance the method is called on, or null for static methods.</param>
    /// <param name="args">The original method's arguments.</param>
    /// <param name="original">The original method being called.</param>
    /// <returns>The handler's result, or Continue if it throws.</returns>
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
            _logger.LogWarning(e, $"handler from {registration.Owner} for {original.DeclaringType?.FullName}.{original.Name} threw: {e.Message}");
            return HookResult.Continue();
        }
    }

    // makes the unique key used to store and find a chain
    /// <summary>
    /// Creates the key used to identify a method's handler chain.
    /// </summary>
    /// <param name="typeName">The fully qualified name of the declaring type.</param>
    /// <param name="methodName">The name of the method.</param>
    /// <returns>The combined type and method key.</returns>
    private static string Key(string typeName, string methodName) => $"{typeName}::{methodName}";
}