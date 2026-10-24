using System.Collections.Generic;
using MISAML.Bootstrap;

namespace MISAML.Bootstrap.Interfaces;

public interface IDependencyResolver
{
    /// <summary>
    /// Decides which mods load and in what order.
    /// </summary>
    IReadOnlyList<ModLoader.ReadMod> Resolve(IReadOnlyList<ModLoader.ReadMod> mods);
}