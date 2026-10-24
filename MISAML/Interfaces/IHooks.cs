using System;
using MISAML.Core;

namespace MISAML.Interfaces;

public interface IHooks
{
    /// <summary>
    /// Sets the priority of the mod
    /// </summary>
    /// <param name="modId">the ID of the mod</param>
    /// <param name="priority">the priority to set it to</param>
    void SetModPriority(string modId, int priority);

    /// <summary>
    /// Sets the current mod for the lifetime of the returned scope.
    /// </summary>
    /// <param name="modId">The ID of the mod to set as current.</param>
    /// <returns>A disposable scope that restores the previous mod when disposed.</returns>
    IDisposable EnterMod(string modId);

    /// <summary>
    /// Registers a handler for a method using the default priority.
    /// The handler receives the instance and arguments, and can continue the
    /// call or replace the return value to skip the original method.
    /// </summary>
    /// <param name="typeName">The fully qualified name of the target type
    /// containing the method to hook.</param>
    /// <param name="methodName">The name of the target method.</param>
    /// <param name="handler">The handler to execute.</param>
    void Register(string typeName, string methodName, Func<object?, object?[], HookResult> handler);

    /// <summary>
    /// Registers a handler for a method using its type directly and the default
    /// priority.
    /// </summary>
    /// <param name="type">The type containing the target method.</param>
    /// <param name="methodName">The name of the target method.</param>
    /// <param name="handler">The handler to execute.</param>
    void Register(Type type, string methodName, Func<object?, object?[], HookResult> handler);

    /// <summary>
    /// Registers a handler for a method using its type directly and a custom priority.
    /// </summary>
    /// <param name="type">The type containing the method to hook.</param>
    /// <param name="methodName">The name of the method to hook.</param>
    /// <param name="handler">The handler to execute.</param>
    /// <param name="priority">The priority.</param>
    /// <returns>True if registration succeeds, else, false.</returns>
    bool Register(Type? type, string methodName, Func<object?, object?[], HookResult> handler, int priority);

    /// <summary>
    /// Registers a handler for a method using its type parameter and an optional priority.
    /// </summary>
    /// <typeparam name="T">The type containing the method to hook.</typeparam>
    /// <param name="methodName">The name of the method to hook.</param>
    /// <param name="handler">The handler to execute.</param>
    /// <param name="priority">The priority.</param>
    /// <returns>True if registration succeeds, else, false.</returns>
    bool Register<T>(string methodName, Func<object?, object?[], HookResult> handler, int priority = Hooks.DefaultPriority);

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
    bool Register(string typeName, string methodName, Func<object?, object?[], HookResult> handler, int priority);


}