using System;
using MISAML.Interfaces;

namespace MISAML.Core;

// mods will do "new Ids()" like they currently do with Hooks, and this will
// forward to the real registry
public class Ids : IIds
{
    private static IIdRegistry? _registry;

    // for mods "new Ids()"
    public Ids() { }

    // for dependency injection
    public Ids(IIdRegistry registry) : this()
    {
        _registry = registry;
    }

    public bool RegisterSyscall(string word, Action<object> handler)
        => _registry?.RegisterSyscall(word, handler) ?? false;

    public bool RegisterConstant(string word, long value)
        => _registry?.RegisterConstant(word, value) ?? false;

    public bool RegisterConstant(string word, float value)
        => _registry?.RegisterConstant(word, value) ?? false;
}