using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Godot;
using HarmonyLib;

namespace MISAML.Core;

// Runs in the .NET Default AssemblyLoadContext (see loader as to why).
// Purpose: always hook GodotBridgePatch when Mnemonimov loads (bootstrap),
// and if MISAML_DEBUG=1, run the MISAML.Debug methods.
public static class StartupHook
{   
    // path for log file
    private static readonly string LogPath = Path.Combine(
        Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!, /* null forgiving, location can be null */
        "misaml.log" /* the actual log file */
    );

    // one timer for the entire MISAML.Core/StartupHook thingy
    private static readonly System.Diagnostics.Stopwatch _stopwatch = System.Diagnostics.Stopwatch.StartNew();

    // just check env for debug logging
    internal static readonly bool DebugEnabled 
        = System.Environment.GetEnvironmentVariable("MISAML_DEBUG") != null;

    internal static void Log(string @class, string message)
    {
        var line = $"{_stopwatch.Elapsed.TotalSeconds:F6} [MISAML.Core.{@class}] {message}";
        File.AppendAllText(LogPath, line + "\n");
    }

    private static void Log(string message) => Log(nameof(StartupHook), message);

    internal static void DebugLog(string message) { if (DebugEnabled) Log(message); }

    public static void Initialize()
    {
        Log(nameof(StartupHook), "MISAML.Core starting...");

        var handled = new HashSet<Assembly>();

        void HandleAssembly(Assembly assembly)
        {
            if (!handled.Add(assembly)) return; // don't double handle the same assembly!!

            var loadedName = assembly.GetName().Name;

            // bootstrap patch, ALWAYS runs regardless of debug mode!! this is what
            // lets Loader grab a live GodotObject later, not optional!!
            if (loadedName == "Mnemonimov")
            {
                GodotBridgePatch.TryPatch(Hooks.Harmony, assembly);
            }

            // anything past here is just catalog stuff!!!!
            if (!DebugEnabled) return;

            // one search per assembly we actually care about, Bridge+Mnemonimov
            // both come from the same dll so they fire together!!
            switch (loadedName)
            {
                case "Asm":
                    RunSearch("Asm", Debug.Asm.Run(Hooks.Harmony, assembly));
                    break;
                case "Mnemonimov":
                    RunSearch("Bridge", Debug.Bridge.Run(Hooks.Harmony, assembly));
                    RunSearch("Mnemonimov", Debug.Mnemonimov.Run(Hooks.Harmony, assembly));
                    break;
                case "GodotSharp":
                    RunSearch("GodotSharp", Debug.GodotSharp.Run(Hooks.Harmony, assembly));
                    break;
            }
        }
 
        // subscribe FIRST so nothing loading from this point onwards can slip
        // through the gap between  "check what's loaded" and "start listening"
        AppDomain.CurrentDomain.AssemblyLoad += (sender, args) =>
            HandleAssembly(args.LoadedAssembly);

        // then force check anything that already loaded before getting to this
        // point. before, Mnemonimov (the anchor assembly) was almost always 
        // already loaded by this point, so its AssemblyLoad event already fired 
        // and finished before we could listen for it, and it never loaded
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            HandleAssembly(assembly);
 
        Log(nameof(StartupHook), "MISAML.Core startup complete.");
    }

    //TODO: Add individual timestamping for each method ran in MISAML.Debug
    private static void RunSearch(string label, MISAML.Debug.SearchResult result)
    {
        Log(
            label,
            $"complete: {result.TypesScanned} types, {result.Patchable} patchable, " +
            $"{result.Skipped} skipped, {result.Failed} failed. " +
            "Full list at misaml-debug.log"
        );
    }
}

// The single mod-facing patching thing, this is very much WIP and WILL 
// be changed in the near future. A mod names any type+method it wants
// ("Asm.Running.VirtualMachine", "HandleSystemCall", or anything else)
// and patches, lazily, on first registration.
public static class Hooks
{
    // setup Log for Hooks
    private static void Log(string message) => StartupHook.Log(nameof(Hooks), message);

    // Harmony instance used for all MISAML patches.
    internal static readonly Harmony Harmony = new("MISAML");

    // registered hook handlers, keyed by type + method.
    private static readonly ConcurrentDictionary<
        string, /* name used for the override. the key. */
        Func<
            object?, /* instance */
            object?[], /* args passed to the hook, array. */
            HookResult /* value returned by the hook. */
        >
    > Overrides = new();

    // deduplication set for patched methods/Harmony targets.
    private static readonly HashSet<string> PatchedKeys = [];

    // protects patch state from concurrent access.
    private static readonly object PatchLock = new();

    // handler gets the call's args (Harmony's __args) and returns
    // either HookResult.Continue() or HookResult.Replace(value)
    // to skip the original (value is ignored for void methods).
    public static void Register(string typeName, string methodName, Func<object?, object?[], HookResult> handler)
    {
        var key = Key(typeName, methodName);

        if (!Overrides.TryAdd(key, handler))
        {
            Log($"{key} already has a registered override, refusing duplicate.");
            return;
        }

        EnsurePatched(typeName, methodName, key);
    }

    // checks PatchedKeys to see if our method has been patched, if not, patch.
    private static void EnsurePatched(string typeName, string methodName, string key)
    {
        // locks, other threads wait before editing
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

        // called whenever a new assembly is loaded.
        // checks the loaded assembly for the method we are looking for.
        void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
        {
            var found = FindMethodIn(args.LoadedAssembly, typeName, methodName);
            if (found == null) return; /* not found, return */

            lock (PatchLock)
            {
                if (!PatchedKeys.Contains(key))
                    ApplyPatch(found, key);
            }

            // remove handler (OnAssemblyLoad) from AssemblyLoad method once
            // the method is found and patched.
            AppDomain.CurrentDomain.AssemblyLoad -= OnAssemblyLoad;
        }

        // add handler (OnAssemblyLoad) to AssemblyLoad, so whenever
        // AssemblyLoad fires, we also run OnAssemblyLoad.
        AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
    }

    // searches every assembly currently loaded for a method.
    private static MethodInfo? FindMethod(string typeName, string methodName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var found = FindMethodIn(asm, typeName, methodName);
            if (found != null) return found;
        }
        return null; /* not found */
    }

    // single assembly search for a method.
    private static MethodInfo? FindMethodIn(Assembly asm, string typeName, string methodName)
    {
        var type = asm.GetType(typeName);
        return type?.GetMethod(methodName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
    }

    // applies a Harmony prefix to the target method.
    // one of 4 methods are used for instance patching support.
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
                typeof(Hooks).GetMethod(prefixName, BindingFlags.NonPublic | BindingFlags.Static)
            );

            // patch the target
            Harmony.Patch(method, prefix: prefix);

            // mark the target only after Harmony successfully patches/patched it.
            PatchedKeys.Add(key);
            Log($"patched {key}");
        }
        catch (Exception e)
        {
            // patch failed, so don't add the key to PatchedKeys.
            Log($"failed to patch {key}: {e.GetType().Name}: {e.Message}");
        }
    }

    // prefix for void instance methods.
    private static bool GenericPrefixVoidInstance(MethodBase __originalMethod, object __instance, object[] __args)
    {
        // try to find the handler registered for this method.
        // if there is no handler, run the original method.
        if (!TryGetHandler(__originalMethod, out var handler)) return true;

        // call the registered handler with the instance & arguments.
        var result = InvokeSafely(handler!, __instance, __args, __originalMethod);

        // if SkipOriginal is true, prevent the original method from running.
        return !result.SkipOriginal;
    }

    // prefix for void static methods.
    private static bool GenericPrefixVoidStatic(MethodBase __originalMethod, object[] __args)
    {
        // try to find the handler registered for this method.
        // if there is no handler, run the original method.
        if (!TryGetHandler(__originalMethod, out var handler)) return true;

        // static methods have no instance, so pass null.
        var result = InvokeSafely(handler!, null, __args, __originalMethod);

        // if SkipOriginal is true, prevent the original method from running.
        return !result.SkipOriginal;
    }

    // prefix for instance methods that return a value.
    private static bool GenericPrefixResultInstance(MethodBase __originalMethod, object __instance, object[] __args, ref object __result)
    {
        // try to find the handler registered for this method.
        // if there is no handler, run the original method.
        if (!TryGetHandler(__originalMethod, out var handler)) return true;

        // call the registered handler with the instance & arguments.
        var result = InvokeSafely(handler!, __instance, __args, __originalMethod);

        // if SkipOriginal is true, use Value as the method's return value.
        if (result.SkipOriginal) __result = result.Value!;

        // true = run the original method.
        // false = skip the original method.
        return !result.SkipOriginal;
    }

    // prefix for static methods that return a value.
    private static bool GenericPrefixResultStatic(MethodBase __originalMethod, object[] __args, ref object __result)
    {
        // try to find the handler registered for this method.
        // if there is no handler, run the original method.
        if (!TryGetHandler(__originalMethod, out var handler)) return true;

        // static methods have no instance, so pass null.
        var result = InvokeSafely(handler!, null, __args, __originalMethod);

        // if SkipOriginal is true, use Value as the method's return value.
        if (result.SkipOriginal) __result = result.Value!;

        // true = run the original method.
        // false = skip the original method.
        return !result.SkipOriginal;
    }

    // finds the handler registered for the original method.
    // out gives us the handler as an extra result from the function.
    private static bool TryGetHandler(MethodBase original, out Func<object?, object?[], HookResult>? handler)
    {
        // make the same key that was used when the handler was registered.
        var key = Key(original.DeclaringType?.FullName ?? "", original.Name);

        // try to find the handler using the key, bool return based on if found.
        return Overrides.TryGetValue(key, out handler);
    }

    // calls a registered handler and catches any errors.
    // if the handler throws, continue with the original method instead.
    private static HookResult InvokeSafely(
        Func<object?, object?[], HookResult> handler,
        object? instance, 
        object[] args, 
        MethodBase original
    )
    {
        try
        {   
            // call the handler with the original method's args and instance
            return handler(instance, args);
        }
        catch (Exception e)
        {
            // handler failed, log the error and don't replace the original, continue.
            Log($"handler for {original.DeclaringType?.FullName}.{original.Name} threw: {e}");
            return HookResult.Continue();
        }
    }

    // makes the unique key used to store and find a handler.
    private static string Key(string typeName, string methodName) => $"{typeName}::{methodName}";
}

public readonly struct HookResult
{
    // tells the prefix whether the original method should be skipped.
    public bool SkipOriginal { get; private init; }

    // value to use if the original method is being replaced.
    public object? Value { get; private init; }

    // creates a result that lets the original method run normally.
    public static HookResult Continue() => new() { SkipOriginal = false };

    // creates a result that skips the original method and uses this value.
    public static HookResult Replace(object? value) => new() { SkipOriginal = true, Value = value };
}

public static class GodotBridge
{
    private static void Log(string message) => StartupHook.Log(nameof(GodotBridge), message);

    private static Assembly? _isolatedApi;
    private static readonly Dictionary<string, MethodInfo> _cache = new();
    private static readonly object _lock = new();

    // loader calls this once it has the isolated context MISAML.GodotAPI.dll
    // ref, no resolving w/ this
    public static void Bind(Assembly isolatedApi) => _isolatedApi = isolatedApi;

    // first use of a primitive looks it up by name and caches MethodInfo
    private static MethodInfo Resolve(string name)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(name, out var cached)) return cached;
            if (_isolatedApi == null) throw new InvalidOperationException("GodotBridge.Bind() has not run yet!");

            var type = _isolatedApi.GetType("MISAML.GodotAPI.Primitives")
                ?? throw new Exception("MISAML.GodotAPI.Primitives not found!");
            
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static)
                ?? throw new Exception($"Primitives.{name} not found!");

            _cache[name] = method;
            Log($"resolved {name} (first use)");
            return method;
        }
    }

    // unwrap anything wrapped in TargetInvocationExpection, some primitives wrap
    // them another time (InvokeMember ones), unwrap every layer here
    private static object? Invoke(string name, params object?[] args)
    {
        try { return Resolve(name).Invoke(null, args); }
        catch (TargetInvocationException e)
        {
            Exception ue = e;
            while (ue is TargetInvocationException t && t.InnerException != null)
                ue = t.InnerException;

            ExceptionDispatchInfo.Capture(ue).Throw();
            throw; // unreachable
        }
    }

    // Godot objects
    public static object? Get(object instance, string propertyName) => Invoke(nameof(Get), instance, propertyName);
    public static void Set(object instance, string propertyName, object? value) => Invoke(nameof(Set), instance, propertyName, value);
    public static object? Call(object instance, string methodName, params object?[] args) => Invoke(nameof(Call), instance, methodName, args);
    public static void CallDeferred(object instance, string methodName, params object?[] args) => Invoke(nameof(CallDeferred), instance, methodName, args);
    public static object Instantiate(string className) => Invoke(nameof(Instantiate), className)!;
    public static bool IsInstanceValid(object instance) => (bool)Invoke(nameof(IsInstanceValid), instance)!;

    // signals (handler gets signals args, empty if none)
    public static void Connect(object instance, string signalName, Action<object?[]> handler) => Invoke(nameof(Connect), instance, signalName, handler);
    public static void Disconnect(object instance, string signalName, Action<object?[]> handler) => Invoke(nameof(Disconnect), instance, signalName, handler);

    // static
    public static object? CallStatic(string typeName, string methodName, params object?[] args) => Invoke(nameof(CallStatic), typeName, methodName, args);
    public static object? GetStatic(string typeName, string memberName) => Invoke(nameof(GetStatic), typeName, memberName);
    public static void SetStatic(string typeName, string memberName, object? value) => Invoke(nameof(SetStatic), typeName, memberName, value);

    // builders
    public static object Construct(string typeName, params object?[] args) => Invoke(nameof(Construct), typeName, args)!;

    // non-Godot stuff
    public static object? GetMember(object instance, string name) => Invoke(nameof(GetMember), instance, name);
    public static void SetMember(object instance, string name, object? value) => Invoke(nameof(SetMember), instance, name, value);
    public static object? CallMember(object instance, string name, params object?[] args) => Invoke(nameof(CallMember), instance, name, args);

    // threading
    public static void RunOnMainThread(Action action) => Invoke(nameof(RunOnMainThread), action);

    // ready hook
    // mods load VERY early, before scene tree exists, so any mod that wants to
    // touch Godot does it inside of OnReady, runs on the main thread
    private static readonly List<Action> _readyActions = new();
    private static bool _ready;

    public static void OnReady(Action action)
    {
        lock (_lock) { if (!_ready) { _readyActions.Add(action); return; } }
        RunOnMainThread(action); // already ready, run now
    }

    // loader calls this once, the first time the game calls into a bridge method
    public static void RaiseReady()
    {
        List<Action> run;
        lock (_lock)
        {
            if (_ready) return;
            _ready = true;
            run = [.. _readyActions];
            _readyActions.Clear();
        }
 
        RunOnMainThread(() =>
        {
            foreach (var a in run)
                try { a(); } catch (Exception e) { Log($"ready handler threw: {e}"); }
        });
    }

}

// MISAML's own bootstrap stuff, not mod-facing, patches all of Mnemonimov's
// known [GlobalClass] bridge types so Loader can fetch a live GodotObject
// and reach the isolated AssemlyLoadContext. How to find this list:
//   ilspycmd Mnemonimov.dll -o decompiled_mnemonimov -p
//   grep -rl "GlobalClass" decompiled_mnemonimov/
public static class GodotBridgePatch
{
    private static void Log(string message) => StartupHook.Log(nameof(GodotBridgePatch), message);

    private static readonly HashSet<string> SkipNames =
    [
        "InvokeGodotClassMethod",
        "HasGodotClassMethod",
        "SetGodotClassPropertyValue",
        "GetGodotClassPropertyValue",
        "SaveGodotObjectData",
        "RestoreGodotObjectData",
        "GetGodotMethodList",
        "GetGodotPropertyList",
        "InvokeGodotClassStaticMethod",
    ];

    private static readonly string[] BridgeTypeNames =
    [
        "Mnemonimov.src.asm.VirtualMachineRunner",
        "Mnemonimov.src.asm.ShellLexer",
        "Mnemonimov.src.asm.AssemblerRunner",
        "Mnemonimov.src.asm.TextSearcher",
        "Mnemonimov.src.asm.CsUtils",
    ];

    // raised with the live instance on every patched bridge call,
    // how Loader gets a real GodotObject to bootstrap the isolated
    // context.
    public static event Action<object>? OnBridgeInstanceSeen;

    public static void TryPatch(Harmony harmony, Assembly mnemonimovAssembly)
    {
        var prefix = new HarmonyMethod(
            typeof(GodotBridgePatch).GetMethod(nameof(BridgePrefix),
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception("BridgePrefix not found!"));

        var postfix = new HarmonyMethod(
            typeof(GodotBridgePatch).GetMethod(nameof(BridgePostfix),
                BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception("BridgePostfix not found!"));

        int totalPatched = 0;
        int totalFailed = 0;

        foreach (var typeName in BridgeTypeNames)
        {
            Type bridgeType;
            try
            {
                bridgeType = mnemonimovAssembly.GetType(typeName)
                    ?? throw new Exception($"{typeName} not found");
            }
            catch (Exception e)
            {
                Log($"Failed to locate {typeName}: {e.Message}");
                continue;
            }

            var methods = bridgeType.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

            foreach (var method in methods)
            {
                if (SkipNames.Contains(method.Name)) continue;
                if (harmony.GetPatchedMethods().Contains(method)) continue;

                var usePostfix = method.ReturnType != typeof(void) ? postfix : null;

                try
                {
                    harmony.Patch(method, prefix: prefix, postfix: usePostfix);
                    totalPatched++;
                }
                catch (Exception e)
                {
                    Log($"Failed to patch {typeName}.{method.Name}: {e.GetType().Name}: {e.Message}");
                    totalFailed++;
                }
            }
        }

        Log($"complete: {totalPatched} patched across {BridgeTypeNames.Length} types, {totalFailed} failed.");
    }
    private static void BridgePrefix(MethodBase __originalMethod, object[] __args, object __instance)
    {
        if (StartupHook.DebugEnabled)
        {
            try
            {
                string args = __args is { Length: > 0 }
                    ? string.Join(", ", __args.Select(a => a?.ToString() ?? "null"))
                    : "";
                Log($"-> {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}({args})");
            }
            catch (Exception e)
            {
                Log("prefix logging failed: " + e.GetType().Name);
            }
        }

        try
        {
            if (__instance != null)
                OnBridgeInstanceSeen?.Invoke(__instance);
        }
        catch (Exception e)
        {
            Log("OnBridgeInstanceSeen subscriber threw: " + e);
        }
    }

    private static void BridgePostfix(MethodBase __originalMethod, object __result)
    {
        if (!StartupHook.DebugEnabled) return;

        try
        {
            string result = __result?.ToString() ?? "void";
            Log($"<- {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name} returned {result}");
        }
        catch (Exception e)
        {
            Log("postfix logging failed: " + e.GetType().Name);
        }

    }
}