using System;

namespace MISAML.Interfaces;

public interface IGodotBridge
{
    public object? Get(object instance, string propertyName);
    public void Set(object instance, string propertyName, object? value);
    public object? Call(object instance, string methodName, params object?[] args);
    public void CallDeferred(object instance, string methodName, params object?[] args);
    public object Instantiate(string className);
    public bool IsInstanceValid(object instance);
    public void Connect(object instance, string signalName, Action<object?[]> handler);
    public object CreateCallable(Func<object?[], object?> function);
    public void Expose(string name, Func<object?[], object?> function);
    public void Disconnect(object instance, string signalName, Action<object?[]> handler);
    public object? CallStatic(string typeName, string methodName, params object?[] args);
    public object? GetStatic(string typeName, string memberName);
    public void SetStatic(string typeName, string memberName, object? value);
    public object Construct(string typeName, params object?[] args);
    public object? GetMember(object instance, string name);
    public void SetMember(object instance, string name, object? value);
    public object? CallMember(object instance, string name, params object?[] args);
    public void RunOnMainThread(Action action);
    public void OnReady(Action action);
    public void RaiseReady();
    object ScriptApi { get; }
}