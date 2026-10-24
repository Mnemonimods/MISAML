using System.Collections.Generic;
using MISAML.Bootstrap.Records;

namespace MISAMl.Bootstrap.Records;

// parsed version of RawManifest
public record ModManifest(
    string Id,
    string Name,
    string Version,
    string? Description,
    string? Author,
    string? Source,
    string? SourceManifest,
    string? MisamlVersion,
    string? EntryAssembly,
    string? EntryMethod,
    string? EntryScript,
    IReadOnlyList<Dependency> Dependencies
);