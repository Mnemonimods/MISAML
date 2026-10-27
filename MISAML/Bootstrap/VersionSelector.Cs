using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using MISAML.Bootstrap.Interfaces;
using MISAML.Bootstrap.Records;
using Semver;

namespace MISAML.Bootstrap;

// when several folders hold the same mod id, picks the one that will load
public class VersionSelector(IVersionReq versionReq) : IVersionSelector
{
    public SelectionResult Select(
        IReadOnlyList<ReadMod> mods,
        IReadOnlyDictionary<string, ModSetting> settings
    )
    {
        var selected = new List<ReadMod>();
        var unselected = new List<SkippedMod>();

        foreach (var group in mods.GroupBy(m => m.Manifest.Id, StringComparer.OrdinalIgnoreCase))
        {
            var candidates = group.ToList();

            // a verison in misaml.toml narrows chocie
            if (candidates.Count > 1
                && settings.TryGetValue(group.Key, out var setting)
                && setting.Version is { } pin 
                && versionReq.TryParse(pin, out _) is { } range)
            {
                var matching = candidates
                    .Where(c => versionReq.ParseVersion(c.Manifest.Version) is { } v && range.Contains(v))
                    .ToList();
                
                // nothing matches, keep them all, so pin check reports
                if (matching.Count > 0) candidates = matching;
            }

            // valid vers first, then highest, then folder name
            var best = candidates
                .OrderByDescending(c => versionReq.ParseVersion(c.Manifest.Version) != null)
                .ThenByDescending(c => versionReq.ParseVersion(c.Manifest.Version), Comparer<SemVersion?>.Create(ComparePrecedence))
                .ThenBy(c => c.Dir, StringComparer.Ordinal)
                .First();

            selected.Add(best);

            foreach (var other in group.Where(m => !ReferenceEquals(m, best)))
            {
                unselected.Add(new SkippedMod(other,
                    $"mod id '{group.Key}' has several versions installed, using {Path.GetFileName(best.Dir)} ({best.Manifest.Version}). " +
                    $"Set a version for '{group.Key}' in misaml.toml to choose a different one"));
            }
        }

        return new SelectionResult(selected, unselected);
    }

    // semver precedence, with a missing version counting as lowest
    private static int ComparePrecedence(SemVersion? a, SemVersion? b)
        => a is null ? (b is null ? 0 : -1) : a.ComparePrecedenceTo(b);
}