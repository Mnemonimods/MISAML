using System.Collections.Generic;
using MISAML.Bootstrap;
using MISAML.Bootstrap.Records;

namespace MISAML.Bootstrap.Interfaces;

public interface IDependencyResolver
{
    /// <summary>
    /// Decides which mods to load and the order of loading.
    /// </summary>
    /// <param name="mods">Mods that are enabled and ready to load.</param>
    /// <param name="unavailable">Mods that exist but can't load.</param>
    /// <returns></returns>
    ResolveResult Resolve(IReadOnlyList<ReadMod> mods, IReadOnlyDictionary<string, string> unavailable);
}