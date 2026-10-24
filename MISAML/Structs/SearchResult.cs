namespace MISAML.Debug;

public readonly record struct SearchResult(
    int TypesScanned,
    int Patchable,
    int Skipped,
    int Failed
);