using MISAML.Core.Records;

namespace MISAML.Interfaces;

public interface IIdScanner
{
    // reads the game's own maps and enums
    UsedIds Scan();
}