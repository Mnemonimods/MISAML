using System;
using MISAML.Interfaces;

namespace MISAML.Core;

/// <summary>
/// Mods will use `new Ids()` like they currently do with Hooks, and this will
/// forward to the real registry.
/// </summary>
public class Ids : IIds
{
    private static IIdRegistry? _registry;

    /// <summary>
    /// For mods, `new Ids()`.
    /// </summary>
    public Ids() { }

    /// <summary>
    /// For dependency injection.
    /// </summary>
    /// <param name="registry">The registry this instance will forward every call to.</param>
    public Ids(IIdRegistry registry) : this()
    {
        _registry = registry;
    }

    /// <summary>
    /// Registers a word as a syscall, forwarding to the registry.
    /// </summary>
    /// <param name="word">The word used to call this syscall.</param>
    /// <param name="handler">The handler to run when the syscall is invoked.</param>
    /// <returns>bool, false if the reuqest is refused, or if no registry has been set yet.</returns>
    public bool RegisterSyscall(string word, Action<object> handler)
        => _registry?.RegisterSyscall(word, handler) ?? false;

    /// <summary>
    /// Registers a word as a long constant, forwarding to the registry.
    /// </summary>
    /// <param name="word">The word used to refer to this constant.</param>
    /// <param name="value">The constant's value.</param>
    /// <returns>bool, if the request is refused, or if no registry has been set yet.</returns>
    public bool RegisterConstant(string word, long value)
        => _registry?.RegisterConstant(word, value) ?? false;

    /// <summary>
    /// Registers a word as a float constant, forwarding to the registry.
    /// </summary>
    /// <param name="word">The word used to refer to this constant.</param>
    /// <param name="value">The constant's value.</param>
    /// <returns>bool, false if the request is refused, or if no registry has been set yet.</returns>
    public bool RegisterConstant(string word, float value)
        => _registry?.RegisterConstant(word, value) ?? false;
}