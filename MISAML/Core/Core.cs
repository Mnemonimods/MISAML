using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using HarmonyLib;
using MISAML.Debug;

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
    private static readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    // just check env for debug logging
    internal static readonly bool DebugEnabled
        = Environment.GetEnvironmentVariable("MISAML_DEBUG") != null;

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
                    RunSearch("Asm", Asm.Run(Hooks.Harmony, assembly));
                    break;
                case "Mnemonimov":
                    RunSearch("Bridge", Bridge.Run(Hooks.Harmony, assembly));
                    RunSearch("Mnemonimov", Mnemonimov.Run(Hooks.Harmony, assembly));
                    break;
                case "GodotSharp":
                    RunSearch("GodotSharp", GodotSharp.Run(Hooks.Harmony, assembly));
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
    private static void RunSearch(string label, SearchResult result)
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
            StartupHook.DebugLog($"resolved {name}");
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
    public static object CreateCallable(Func<object?[], object?> function) => Invoke(nameof(CreateCallable), function)!;

    // bridge for GDScript -> C#
    private static readonly Dictionary<string, Func<object?[], object?>> _exposed = new()
    {
        ["log"] = args => { StartupHook.Log("Script", string.Join(" ", args)); return null; },
    };

    private static object? _scriptApi;

    public static void Expose(string name, Func<object?[], object?> function)
    {
        lock (_lock) _exposed[name] = function;
    }

    // built the first time something asks for it
    public static object ScriptApi
    {
        get { lock (_lock) return _scriptApi ??= CreateCallable(CallExposed); }
    }

    private static object? CallExposed(object?[] args)
    {
        if (args.Length == 0 || args[0] is not string name)
            throw new ArgumentException("misaml.call needs a function name as the first argument");

        Func<object?[], object?>? function;
        lock (_lock) _exposed.TryGetValue(name, out function);

        if (function == null)
            throw new ArgumentException($"nothing exposed as '{name}'");

        return function(args[1..]);
    }

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