namespace MISAML.Bootstrap.Records;

public record ModStatus(
    string Id, 
    string Name, 
    string Version, 
    bool Enabled, 
    bool Loaded, 
    string? Error, 
    string? Description = null
);