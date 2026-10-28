using System.Collections.Generic;

namespace MISAML.Bootstrap.Records;

// a mod the resolver decided not to load and why, one mod can have serveral
// problems at once, Reason is all of them in one line
public record SkippedMod(ReadMod Mod, IReadOnlyList<string> Problems)
{
    // for the places that only have one reason
    public SkippedMod(ReadMod mod, string reason) : this(mod, [reason]) { }

    public string Reason => string.Join("; ", Problems);
}

//what the resolver decided
public record ResolveResult(IReadOnlyList<ReadMod> LoadOrder, IReadOnlyList<SkippedMod> Skipped);

// what VersionSelector hands back, one mod per id that carries on to the gate
// and resolver, along with mods that lost and reason (why)
public record SelectionResult(IReadOnlyList<ReadMod> Selected, IReadOnlyList<SkippedMod> Unselected);