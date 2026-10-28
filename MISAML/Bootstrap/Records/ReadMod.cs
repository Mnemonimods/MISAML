using MISAML.Core;

namespace MISAML.Bootstrap.Records;

/// <summary>
/// A mod folder with its parsed manifest, as read.
/// </summary>
/// <param name="Dir">The mod's directory.</param>
/// <param name="Manifest">The parsed mod.json.</param>
/// <param name="Priority">The mod's hook priority.</param>
public record ReadMod(
    string Dir,
    ModManifest Manifest,
    int Priority = Hooks.DefaultPriority
);