using System.Collections.Generic;
using MISAML.Bootstrap;
using MISAML.Bootstrap.Records;

namespace MISAML.Bootstrap.Interfaces;

public interface IDependencyResolver
{
    /// <summary>
    /// Decides which mods to load and in what order. A mod that has dependencies
    /// that are missing, skipped, out of range or in a cycle, and anything that
    /// depends on these dependencies.
    /// </summary>
    /// <param name="mods">Mods that are enabled and ready to load.</param>
    /// <param name="unavailable">Mods that exist but can't load, by id, with the reason.</param>
    /// <returns>The load order (dependencies first) and the mods that were skipped,
    /// each with its problems.</returns>
    ResolveResult Resolve(IReadOnlyList<ReadMod> mods, IReadOnlyDictionary<string, string> unavailable);
}