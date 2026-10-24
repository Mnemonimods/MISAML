using System.Collections.Generic;

namespace MISAML.Bootstrap.Records;

// a mod the resolver decided not to load and why
public record SkippedMod(ReadMod Mod, string Reason);

// wja tge resolver tedeicded
public record ResolveResult(IReadOnlyList<ReadMod> LoadOrder, IReadOnlyList<SkippedMod> Skipped);