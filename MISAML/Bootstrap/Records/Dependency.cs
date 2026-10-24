using Semver;

namespace MISAML.Bootstrap.Records;

/// <summary>
/// A validated dependency of a mod
/// </summary>
/// <param name="Id">The dependencies modId</param>
/// <param name="RawRange">Version text as written</param>
/// <param name="Range">Parsed version range</param>
/// <param name="Source">Source URL</param>
public record Dependency(
    string Id,
    string RawRange,
    SemVersionRange Range,
    string? Source
);