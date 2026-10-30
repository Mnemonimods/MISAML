using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MISAML.Interfaces;

namespace MISAML.Godot;

public class GodotBridge() : IGodotBridge
{
    private static ILogger _logger = NullLogger.Instance;
    public GodotBridge(ILogger<GodotBridge> logger) : this()
    {
        _logger = logger;
    }

    private static readonly object _lock = new();

    // loader calls this once it has the isolated context MISAML.GodotAPI.dll.
    // fills every field in the API with the Primitives method of the same name,
    // Reflection happens here and nowhere else, after this first call every call
    // is a normal delegate call. a mismatch (missing method, wrong args) fails here
    // at startup, not on first use, good for devs
    public static void Bind(Assembly isolatedApi)
    {
        var primitives = isolatedApi.GetType("MISAML.GodotAPI.Primitives")
            ?? throw new Exception("MISAML.GodotAPI.Primitives not found!");

        foreach (var field in typeof(API).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var method = primitives.GetMethod(field.Name, BindingFlags.Public | BindingFlags.Static)
                ?? throw new Exception($"Primitives.{field.Name} not found!");

            try
            {
                field.SetValue(null, Delegate.CreateDelegate(field.FieldType, method));
            }
            catch (ArgumentException e)
            {
                throw new Exception($"Primitives.{field.Name} doesn't match what GodotBridge expected: {e.Message}", e);
            }
        }
    }

    // primitives that use reflection internally (InvokeMember, property getters, constructors)
    // wrap the real error in TargetInvocationException, rethrow the real one
    private static void Rethrow(TargetInvocationException e)
    {
        Exception real = e;
        while (real is TargetInvocationException t && t.InnerException is { } inner)
            real = inner;

        ExceptionDispatchInfo.Capture(real).Throw();
    }

    // Godot objects
    public object? Get(object instance, string propertyName) => API.Get(instance, propertyName);
    public void Set(object instance, string propertyName, object? value) => API.Set(instance, propertyName, value);
    public object? Call(object instance, string methodName, params object?[] args) => API.Call(instance, methodName, args);
    public void CallDeferred(object instance, string methodName, params object?[] args) => API.CallDeferred(instance, methodName, args);
    public object Instantiate(string className) => API.Instantiate(className);
    public bool IsInstanceValid(object instance) => API.IsInstanceValid(instance);
 
    // signals (handler gets signals args, empty if none)
    public void Connect(object instance, string signalName, Action<object?[]> handler) => API.Connect(instance, signalName, handler, 0);
    public object CreateCallable(Func<object?[], object?> function) => API.CreateCallable(function);
    public void Disconnect(object instance, string signalName, Action<object?[]> handler) => API.Disconnect(instance, signalName, handler);

    // bridge for GDScript -> C#
    private readonly Dictionary<string, Func<object?[], object?>> _exposed = new()
    {
        ["log"] = args => { _logger.LogInformation($"Script: {string.Join(" ", args)}"); return null; },
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
    public object? CallStatic(string typeName, string methodName, params object?[] args)
    {
        try { return API.CallStatic(typeName, methodName, args); }
        catch (TargetInvocationException e) { Rethrow(e); throw; }
    }
 
    public object? GetStatic(string typeName, string memberName)
    {
        try { return API.GetStatic(typeName, memberName); }
        catch (TargetInvocationException e) { Rethrow(e); throw; }
    }
 
    public void SetStatic(string typeName, string memberName, object? value)
    {
        try { API.SetStatic(typeName, memberName, value); }
        catch (TargetInvocationException e) { Rethrow(e); throw; }
    }
 
    // builders
    public object Construct(string typeName, params object?[] args)
    {
        try { return API.Construct(typeName, args); }
        catch (TargetInvocationException e) { Rethrow(e); throw; }
    }
 
    // non-Godot stuff
    public object? GetMember(object instance, string name)
    {
        try { return API.GetMember(instance, name); }
        catch (TargetInvocationException e) { Rethrow(e); throw; }
    }
 
    public void SetMember(object instance, string name, object? value)
    {
        try { API.SetMember(instance, name, value); }
        catch (TargetInvocationException e) { Rethrow(e); throw; }
    }
 
    public object? CallMember(object instance, string name, params object?[] args)
    {
        try { return API.CallMember(instance, name, args); }
        catch (TargetInvocationException e) { Rethrow(e); throw; }
    }
 
    // threading
    public void RunOnMainThread(Action action) => API.RunOnMainThread(action);


    // ready hook
    // mods load VERY early, before scene tree exists, so any mod that wants to
    // touch Godot does it inside of OnReady, runs on the main thread
    private static readonly List<Action> _readyActions = new();
    private static bool _ready;

    public void OnReady(Action action)
    {
        lock (_lock) { if (!_ready) { _readyActions.Add(action); return; } }
        try { RunOnMainThread(action); }
        catch (Exception e) { _logger.LogWarning(e, $"ready handler threw: {e.Message}"); }
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
                try { a(); } catch (Exception e) { _logger.LogWarning(e, $"ready handler threw: {e.Message}"); }
        });
    }

}