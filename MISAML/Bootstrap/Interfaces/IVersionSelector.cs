using System.Collections.Generic;
using MISAML.Bootstrap.Records;

namespace MISAML.Bootstrap.Interfaces;

// when several folders hold the same mod id, pick which one loads, a version
// pinned or other in misaml.toml wins, otherwise the highest version does
public interface IVersionSelector
{
    SelectionResult Select(
        IReadOnlyList<ReadMod> mods,
        IReadOnlyDictionary<string, ModSetting> settings
    );
}