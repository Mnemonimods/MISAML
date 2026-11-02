using System.Collections.Generic;

namespace MISAML.Bootstrap.Records;

/// <summary>
/// A mod the resolver decided not to load, and why. One mod can have several
/// problems at once, so they're kept as a list.
/// </summary>
/// <param name="Mod">The mod that was skipped.</param>
/// <param name="Problems">Every reason for skipping the mod.</param>
public record SkippedMod(ReadMod Mod, IReadOnlyList<string> Problems)
{
    /// <summary>
    /// For places that have only one reason.
    /// </summary>
    public SkippedMod(ReadMod mod, string reason) : this(mod, [reason]) { }

    /// <summary>
    /// All the problems on line line, for the mod's status and for mods that
    /// depend on it.
    /// </summary>
    public string Reason => string.Join("; ", Problems);
}

/// <summary>
/// What the dependency resolver decided, which mods load, in what order,
/// and which are skipped.
/// </summary>
/// <param name="LoadOrder">Mods to load, dependencies first.</param>
/// <param name="Skipped">Mods that won't load, each with its problems.</param>
public record ResolveResult(IReadOnlyList<ReadMod> LoadOrder, IReadOnlyList<SkippedMod> Skipped);

/// <summary>
/// What VersionSelector hands back when several folders hold the same mod id,
/// the mod that carries on to the resolver and the mod(s) that do not, and why.
/// </summary>
/// <param name="Selected">One mod per id, which carries on to the gate and resolver.</param>
/// <param name="Unselected">Mods that lost to another version of the same id, with the reason why.</param>
public record SelectionResult(IReadOnlyList<ReadMod> Selected, IReadOnlyList<SkippedMod> Unselected);