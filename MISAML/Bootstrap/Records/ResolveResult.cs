using System.Collections.Generic;

namespace MISAML.Bootstrap.Records;

// a mod the resolver decided not to load and why
public record SkippedMod(ReadMod Mod, string Reason);

//what the resolver decided
public record ResolveResult(IReadOnlyList<ReadMod> LoadOrder, IReadOnlyList<SkippedMod> Skipped);

// what VersionSelector hands back, one mod per id that carries on to the gate
// and resolver, along with mods that lost and reason (why)
public record SelectionResult(IReadOnlyList<ReadMod> Selected, IReadOnlyList<SkippedMod> Unselected);