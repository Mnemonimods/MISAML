using System;

namespace MISAML.Interfaces;

public interface IGodotBridge
{
    /// <summary>
    /// Gets the value of a property on the given instance.
    /// </summary>
    /// <param name="instance">The instance to get the property value from.</param>
    /// <param name="propertyName">The name of the property to get.</param>
    /// <returns>The value of the property.</returns>
    public object? Get(object instance, string propertyName);

    /// <summary>
    /// Sets the value of a property on the given instance.
    /// </summary>
    /// <param name="instance">The instance to set the property value on.</param>
    /// <param name="propertyName">The name of the property to set.</param>
    /// <param name="value">The value to set the property to.</param>   
    public void Set(object instance, string propertyName, object? value);

    /// <summary>
    /// Calls the provided method from an instance
    /// </summary>
    /// <param name="instance">The instance to call the method on.</param>
    /// <param name="methodName">Name of the method to call</param>
    /// <param name="args">Arguments to pass to the method.</param>
    /// <returns>The result of the method call.</returns>
    public object? Call(object instance, string methodName, params object?[] args);

    /// <summary>
    /// Calls the method on the objet during idle time
    /// </summary>
    /// <param name="instance">Instance to call the method on.</param>
    /// <param name="methodName">Name of the method to call</param>
    /// <param name="args">Arguments to pass to the method.</param>
    public void CallDeferred(object instance, string methodName, params object?[] args);

    /// <summary>
    /// Instantiates a class
    /// </summary>
    /// <param name="className">The name of the class to instantiate.</param>
    /// <returns>The instantiated object.</returns>
    public object Instantiate(string className);

    /// <summary>
    /// Checks if the instance is valid
    /// </summary>
    /// <param name="instance">The instance to check</param>
    /// <returns><b>True</b> if the instance is valid <br/>
    /// <b>False</b> if the instance is invalid.</returns>
    public bool IsInstanceValid(object instance);

    /// <summary>
    /// Connects a handler to a signal on the given instance.
    /// </summary>
    /// <param name="instance">The object that emits the signal.</param>
    /// <param name="signalName">The name of the signal on <paramref name="instance"/> to connect to.</param>
    /// <param name="handler">The callback invoked when the signal is emitted.</param>
    public void Connect(object instance, string signalName, Action<object?[]> handler);

    /// <summary>
    /// Creates a callable function.
    /// </summary>
    /// <param name="function">The function to make callable</param>
    /// <returns>The callable function</returns>
    public object CreateCallable(Func<object?[], object?> function);

    /// <summary>
    /// Disconnects a signal from a handler.
    /// </summary>
    /// <param name="instance">The object that emits the signal.</param>
    /// <param name="signalName">The name of the signal on <paramref name="instance"/> to disconnect from.</param>
    /// <param name="handler">The handler to disconnect.</param>
    public void Disconnect(object instance, string signalName, Action<object?[]> handler);

    /// <summary>
    /// Exposes a function to the Godot script API.
    /// </summary>
    /// <param name="name">The name to expose the function with</param>
    /// <param name="function">The function to expose</param>
    public void Expose(string name, Func<object?[], object?> function);

    /// <summary>
    /// Host callable that dispatches to exposed functions by name
    /// </summary>
    object ScriptApi { get; }

    /// <summary>
    /// Calls a static function
    /// </summary>
    /// <param name="typeName">The type that contains the static function</param>
    /// <param name="methodName">The name of the static function</param>
    /// <param name="args">Function args</param>
    /// <returns>The result out of the static function call</returns>
    public object? CallStatic(string typeName, string methodName, params object?[] args);

    /// <summary>
    /// Gets a static function
    /// </summary>
    /// <param name="typeName">The name of the type that contains the static function</param>
    /// <param name="memberName">The name of the function to get</param>
    /// <returns>The static function</returns>
    public object? GetStatic(string typeName, string memberName);

    /// <summary>
    /// Sets a static member
    /// </summary>
    /// <param name="typeName">The type that contains the member</param>
    /// <param name="memberName">The name of the member to set a value in</param>
    /// <param name="value">The value to set the static member to</param>
    public void SetStatic(string typeName, string memberName, object? value);

    /// <summary>
    /// Constructs a type
    /// </summary>
    /// <param name="typeName">The type to construct</param>
    /// <param name="args">Args to construct the type with</param>
    /// <returns>The constructed type</returns>
    public object Construct(string typeName, params object?[] args);

    /// <summary>
    /// Gets a member from an instance
    /// </summary>
    /// <param name="instance">The instance to get a member from</param>
    /// <param name="name">The name of the member to get</param>
    /// <returns>The resulting <c>Member</c></returns>
    public object? GetMember(object instance, string name);

    /// <summary>
    /// Sets a member from an instance
    /// </summary>
    /// <param name="instance">The instance to set a <paramref name="value"/> on</param>
    /// <param name="name">The name of the member to set a <paramref name="value"/> in</param>
    /// <param name="value">The value to set the member to</param>
    public void SetMember(object instance, string name, object? value);

    /// <summary>
    /// Calls a member of an instance
    /// </summary>
    /// <param name="instance">The instance to get a member from</param>
    /// <param name="name">The name of the instance to call</param>
    /// <param name="args">Args to call the member with</param>
    /// <returns>The resulting value from calling the member</returns>
    public object? CallMember(object instance, string name, params object?[] args);

    /// <summary>
    /// Runs the specified action on the main thread.
    /// </summary>
    /// <param name="action">The action to run on the main thread.</param>
    public void RunOnMainThread(Action action);


    //IDK WHAT TO DO HERE FOR THESE TWO HELP ME JJ
    /// <summary>
    /// 
    /// </summary>
    /// <param name="action"></param>
    public void OnReady(Action action);

    /// <summary>
    /// 
    /// </summary>
    public void RaiseReady();

}