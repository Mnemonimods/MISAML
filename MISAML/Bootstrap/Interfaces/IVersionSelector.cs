using System.Collections.Generic;
using MISAML.Bootstrap.Records;

namespace MISAML.Bootstrap.Interfaces;

// when several folders hold the same mod id, pick which one loads, a version
// pinned or other in misaml.toml wins, otherwise the highest version does
public interface IVersionSelector
{
    /// <summary>
    /// Picks one mod per mod id. If a mod id appears in more than one folder,
    /// a version pinned in misaml.toml wins, otherwise the highest version does.
    /// Mods with a unique id pass through unchanged.
    /// </summary>
    /// <param name="mods">Every mod that was read, including duplicates.</param>
    /// <param name="settings">The user's per-mod settings from misaml.toml,
    /// by mod id.</param>
    /// <returns>The mods that continue loading, one per id, and the mods lost,
    /// each with a reason.</returns>
    SelectionResult Select(
        IReadOnlyList<ReadMod> mods,
        IReadOnlyDictionary<string, ModSetting> settings
    );
}