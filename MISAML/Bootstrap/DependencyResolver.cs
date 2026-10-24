using System.Collections.Generic;
using MISAML.Bootstrap.Interfaces;

namespace MISAML.Bootstrap;

public class DependencyResolver : IDependencyResolver
{
    public IReadOnlyList<ModLoader.ReadMod> Resolve(IReadOnlyList<ModLoader.ReadMod> mods) => mods;
}