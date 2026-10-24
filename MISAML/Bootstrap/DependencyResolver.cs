using System.Collections.Generic;
using MISAML.Bootstrap.Interfaces;
using MISAML.Bootstrap.Records;

namespace MISAML.Bootstrap;

public class DependencyResolver(IVersionReq versionReq) : IDependencyResolver
{
    public ResolveResult Resolve(IReadOnlyList<ReadMod> mods, IReadOnlyDictionary<string, string> unavailable)
        => new(mods, []);

    private string? FindProblem(
        ReadMod mod,
        Dictionary<string, ReadMod> alive,
        IReadOnlyDictionary<string, string> unavailable)
    {
        foreach (var dep in mod.Manifest.Dependencies)
        {
            // dependency isn't alive
            if (!alive.TryGetValue(dep.Id, out var depMod))
            {
                if (unavailable.TryGetValue(dep.Id, out var why))
                    return $"needs {dep.Id}, which is {why}";

                return $"needs {dep.Id}, which isn't installed";
            }

            // dependency is alive but version is out of range
            var depVer = versionReq.ParseVersion(depMod.Manifest.Version);
            if (depVer == null || !dep.Range.Contains(depVer))
                return $"needs {dep.Id} {dep.RawRange}, found {depMod.Manifest.Version}";
        }

        return null;
    }
}