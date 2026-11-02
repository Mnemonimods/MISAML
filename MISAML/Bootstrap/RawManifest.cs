using System.Collections.Generic;

namespace MISAML.Bootstrap;

// exactly what is written in mod.json, every field optional
public class RawManifest
{
    public string? ModId { get; set; }
    public string? ModName { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string? Source { get; set; }
    public string? SourceManifest { get; set; }
    public string? MisamlVersion { get; set; }
    public string? EntryAssembly { get; set; }
    public string? EntryMethod { get; set; }
    public string? EntryScript { get; set; }
    public Dictionary<string, RawDependency>? Dependencies { get; set; }
}