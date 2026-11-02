using System.Collections.Generic;

namespace MISAML.Core.Records;

// everything the assembler already uses filled in by IIdScanner
public record UsedIds(Dictionary<IdSpace, HashSet<int>> Numbers, HashSet<string> Words)
{
    public HashSet<int> Of(IdSpace space) => Numbers[space];
}