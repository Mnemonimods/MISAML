namespace MISAML.Bootstrap.Records;

/// <summary>
/// What misaml.toml informs about a single mod, priority is null when the user
/// has not set one, the mod's own (or the default) priority is used then.
/// </summary>
/// <param name="Enabled">Is the mod enabled?</param>
/// <param name="Priority">mod priority</param>
/// <param name="Version">mod version</param>
public record ModSetting(
    bool Enabled,
    int? Priority,
    string? Version
    );