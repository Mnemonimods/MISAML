using System;
using System.Collections.Generic;
using System.Linq;
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
                var problems = FindProblems(mod, alive, gone);
                if (problems.Count == 0) continue;

                var skip = new SkippedMod(mod, problems);
                alive.Remove(mod.Manifest.Id);
                gone[mod.Manifest.Id] = skip.Reason;
                skipped.Add(skip);
                changed = true;
            }
        } while (changed);

        var loadOrder = Order(alive);
        var stuck = alive.Values
            .Where(m => !loadOrder.Contains(m))
            .ToDictionary(m => m.Manifest.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var mod in stuck.Values)
            skipped.Add(new SkippedMod(mod, CycleReason(mod, stuck)));

        return new ResolveResult(loadOrder, skipped);
    }

    private List<string> FindProblems(
        ReadMod mod,
        Dictionary<string, ReadMod> alive,
        IReadOnlyDictionary<string, string> unavailable)
    {
        var problems = new List<string>();

        foreach (var dep in mod.Manifest.Dependencies)
        {
            // only used when the dependency has a source
            var hint = dep.Source == null ? "" : $". Get it from {dep.Source}";

            // dependency isn't alive
            if (!alive.TryGetValue(dep.Id, out var depMod))
            {
                if (unavailable.TryGetValue(dep.Id, out var why))
                    problems.Add($"requires {dep.Id}, which was skipped: {why}");
                else if (dep.Source == null)
                    problems.Add($"requires {dep.Id} {dep.RawRange}, which isn't installed (install manually)");
                else
                    problems.Add($"requires {dep.Id} {dep.RawRange}, which isn't installed{hint}");

                continue;
            }

            // dependency is alive but version is out of range
            var depVer = versionReq.ParseVersion(depMod.Manifest.Version);
            if (depVer == null)
                problems.Add($"requires {dep.Id} {dep.RawRange}, but its version '{depMod.Manifest.Version}' is not valid semantic versioning.");
            else if (!dep.Range.Contains(depVer))
                problems.Add($"requires {dep.Id} {dep.RawRange}, but {depMod.Manifest.Version} is installed{hint}");
        }

        return problems;
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

        // mods with nothing left to wait for, lowest priority number first, then id
        var ready = new SortedSet<ReadMod>(Comparer<ReadMod>.Create((a, b) =>
        {
            var byPriority = a.Priority.CompareTo(b.Priority);

            return byPriority != 0
                ? byPriority
                : string.Compare(a.Manifest.Id, b.Manifest.Id, StringComparison.OrdinalIgnoreCase);
        }));

        foreach (var (id, count) in waiting)
        {
            if (count == 0) ready.Add(alive[id]);
        }

        var order = new List<ReadMod>();

        while (ready.Count > 0)
        {
            var mod = ready.Min!;
            ready.Remove(mod);
            order.Add(mod);

            if (!dependents.TryGetValue(mod.Manifest.Id, out var needers)) continue;

            foreach (var needer in needers)
            {
                if (--waiting[needer.Manifest.Id] == 0)
                    ready.Add(needer);
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

            // looped somewhere further along, start just requires a mod that is in a cycle
            if (seenAt > 0)
                return $"requires {path[1]}, which can't load because of a dependency cycle";

            path.Add(id);
            var next = current.Manifest.Dependencies.First(d => stuck.ContainsKey(d.Id));
            current = stuck[next.Id];
        }
    }
}