using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MISAML.Interfaces;

namespace MISAML.Godot;

using Microsoft.Extensions.Logging;

public class GodotBridge() : IGodotBridge
{
    private static ILogger _logger = NullLogger.Instance;
    public GodotBridge(ILogger<GodotBridge> logger) : this()
    {
        _logger = logger;
    }

    private static Assembly? _isolatedApi;
    private static readonly Dictionary<string, MethodInfo> _cache = new();
    private static readonly object _lock = new();

    // loader calls this once it has the isolated context MISAML.GodotAPI.dll
    // ref, no resolving w/ this
    public static void Bind(Assembly isolatedApi) => _isolatedApi = isolatedApi;

    // first use of a primitive looks it up by name and caches MethodInfo
    private MethodInfo Resolve(string name)
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
            _logger.LogDebug($"resolved {name}");
            return method;
        }
    }

    // unwrap anything wrapped in TargetInvocationExpection, some primitives wrap
    // them another time (InvokeMember ones), unwrap every layer here
    private object? Invoke(string name, params object?[] args)
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
    public object? Get(object instance, string propertyName) => Invoke(nameof(Get), instance, propertyName);
    public void Set(object instance, string propertyName, object? value) => Invoke(nameof(Set), instance, propertyName, value);
    public object? Call(object instance, string methodName, params object?[] args) => Invoke(nameof(Call), instance, methodName, args);
    public void CallDeferred(object instance, string methodName, params object?[] args) => Invoke(nameof(CallDeferred), instance, methodName, args);
    public object Instantiate(string className) => Invoke(nameof(Instantiate), className)!;
    public bool IsInstanceValid(object instance) => (bool)Invoke(nameof(IsInstanceValid), instance)!;

    // signals (handler gets signals args, empty if none)
    public void Connect(object instance, string signalName, Action<object?[]> handler) => Invoke(nameof(Connect), instance, signalName, handler);
    public object CreateCallable(Func<object?[], object?> function) => Invoke(nameof(CreateCallable), function)!;
    public void Disconnect(object instance, string signalName, Action<object?[]> handler) => Invoke(nameof(Disconnect), instance, signalName, handler);

    // bridge for GDScript -> C#
    private readonly Dictionary<string, Func<object?[], object?>> _exposed = new()
    {
        ["log"] = args => { _logger.LogInformation("Script", args: args); return null; },
    };

    private static object? _scriptApi;

    public void Expose(string name, Func<object?[], object?> function)
    {
        lock (_lock) _exposed[name] = function;
    }

    // built the first time something asks for it
    public object ScriptApi
    {
        get { lock (_lock) return _scriptApi ??= CreateCallable(CallExposed); }
    }

    private object? CallExposed(object?[] args)
    {
        if (args.Length == 0 || args[0] is not string name)
            throw new ArgumentException("misaml.call needs a function name as the first argument");

        Func<object?[], object?>? function;
        lock (_lock) _exposed.TryGetValue(name, out function);

        if (function == null)
            throw new ArgumentException($"nothing exposed as '{name}'");

        return function(args[1..]);
    }



    // static
    public object? CallStatic(string typeName, string methodName, params object?[] args) => Invoke(nameof(CallStatic), typeName, methodName, args);
    public object? GetStatic(string typeName, string memberName) => Invoke(nameof(GetStatic), typeName, memberName);
    public void SetStatic(string typeName, string memberName, object? value) => Invoke(nameof(SetStatic), typeName, memberName, value);

    // builders
    public object Construct(string typeName, params object?[] args) => Invoke(nameof(Construct), typeName, args)!;

    // non-Godot stuff
    public object? GetMember(object instance, string name) => Invoke(nameof(GetMember), instance, name);
    public void SetMember(object instance, string name, object? value) => Invoke(nameof(SetMember), instance, name, value);
    public object? CallMember(object instance, string name, params object?[] args) => Invoke(nameof(CallMember), instance, name, args);

    // threading
    public void RunOnMainThread(Action action) => Invoke(nameof(RunOnMainThread), action);

    // ready hook
    // mods load VERY early, before scene tree exists, so any mod that wants to
    // touch Godot does it inside of OnReady, runs on the main thread
    private static readonly List<Action> _readyActions = new();
    private static bool _ready;

    public void OnReady(Action action)
    {
        lock (_lock) { if (!_ready) { _readyActions.Add(action); return; } }
        RunOnMainThread(action); // already ready, run now
    }

    // loader calls this once, the first time the game calls into a bridge method
    public void RaiseReady()
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
                try { a(); } catch (Exception e) { _logger.LogWarning($"ready handler threw: {e.Message}"); }
        });
    }

}