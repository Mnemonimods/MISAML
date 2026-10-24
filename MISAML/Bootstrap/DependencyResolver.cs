using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using MISAML.Bootstrap.Interfaces;
using MISAML.Bootstrap.Records;

namespace MISAML.Bootstrap;

public class DependencyResolver(IVersionReq versionReq) : IDependencyResolver
{
    public ResolveResult Resolve(
        IReadOnlyList<ReadMod> mods,
        IReadOnlyDictionary<string, string> unavailable)
    {
        var alive = mods.ToDictionary(m => m.Manifest.Id, StringComparer.OrdinalIgnoreCase);

        var gone = new Dictionary<string, string>(unavailable, StringComparer.OrdinalIgnoreCase);
        var skipped = new List<SkippedMod>();

        bool changed;
        do
        {
            changed = false;

            foreach (var mod in alive.Values.ToList())
            {
                var problem = FindProblem(mod, alive, gone);
                if (problem == null) continue;

                alive.Remove(mod.Manifest.Id);
                gone[mod.Manifest.Id] = "not loading";
                skipped.Add(new SkippedMod(mod, problem));
                changed = true;
            }
        } while (changed);

        var loadOrder = Order(alive);

        // anything Order cannot pass is cyclical (or needs a mod that is cyclical)
        foreach (var mod in alive.Values.Where(m => !loadOrder.Contains(m)))
            skipped.Add(new SkippedMod(mod, "part of a dependency cycle, or needs a mod that is"));

        return new ResolveResult(loadOrder, skipped);
    }

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

    // deps first, ties broken by id so the order is same every run
    private static List<ReadMod> Order(Dictionary<string, ReadMod> alive)
    {
        // how many deps each mod is still waiting on
        var waiting = alive.Values.ToDictionary(
            m => m.Manifest.Id,
            m => m.Manifest.Dependencies.Count,
            StringComparer.OrdinalIgnoreCase);

        // depId -> mods that need it
        var dependents = new Dictionary<string, List<ReadMod>>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in alive.Values)
        {
            foreach (var dep in mod.Manifest.Dependencies)
            {
                if (!dependents.TryGetValue(dep.Id, out var list))
                    dependents[dep.Id] = list = [];

                list.Add(mod);
            }
        }

        // mods with nothing left to wait, smallest id first
        var ready = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, count) in waiting)
        {
            if (count == 0) ready.Add(id);
        }

        var order = new List<ReadMod>();
        while (ready.Count > 0)
        {
            var id = ready.Min!;
            ready.Remove(id);
            order.Add(alive[id]);

            if (!dependents.TryGetValue(id, out var needers)) continue;
            foreach (var needer in needers)
            {
                if (--waiting[needer.Manifest.Id] == 0)
                    ready.Add(needer.Manifest.Id);
            }
        }

        return order;
    }

    private static string CycleReason(ReadMod start, Dictionary<string, ReadMod> stuck)
    {
        var path = new List<string>();
        var current = start;

        while (true)
        {
            var id = current.Manifest.Id;
            var seenAt = path.FindIndex(p => string.Equals(p, id, StringComparison.OrdinalIgnoreCase));

            // arrived back to the start, start is inside the loop
            if (seenAt == 0)
                return $"dependency cycle: {string.Join(" -> ", path.Append(id))}";

            // looped somewhere further along, start just needs a mod that is in a cycle
            if (seenAt > 0)
                return $"needs {path[1]}, which can't load because of a dependency cycle";

            path.Add(id);
            var next = current.Manifest.Dependencies.First(d => stuck.ContainsKey(d.Id));
            current = stuck[next.Id];
        }
    }
}