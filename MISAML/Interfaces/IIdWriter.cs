using System.Collections.Generic;
using MISAML.Core.Records;

namespace MISAML.Interfaces;

public interface IIdWriter
{
    // writes the allocated words into the game's assembler and hooks the VM
    void Apply(IReadOnlyList<Allocation> allocations);
}