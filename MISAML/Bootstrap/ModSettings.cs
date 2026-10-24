using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using MISAML.Bootstrap.Interfaces;
using MISAML.Bootstrap.Records;
using MISAML.Interfaces;
using Tomlyn;
using Tomlyn.Model;

namespace MISAML.Bootstrap;


// reads the [mods] table of misaml.toml, each entry is bool, or table like:
// { enabled = true, priority = 50, version = "0.0.0" }
public class ModSettings(ILogger<ModSettings> logger) : IModSettings
{
    private const int MaxPriority = 65535;

    // returns mod id -> setting (ids compared ignoring case), a missing file
    // or no [mods] table has no issues, just means no settings, returns null
    // only when the file exists but cannot be read, so the caller can refuse
    // to guess which mods the user turned off
    public Dictionary<string, ModSetting>? Load(string path)
    {
        var result = new Dictionary<string, ModSetting>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path))
        {
            logger.LogInformation($"no settings file at {path}, every mod is enabled");
            return result;
        }

        TomlTable root;
        try
        {
            root = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path))
                ?? throw new Exception("file is empty");
        }
        catch (Exception e)
        {
            logger.LogError($"could not read {path} ({e.GetType().Name}: {e.Message})");
            return null;
        }

        if (!root.TryGetValue("mods", out var modsValue) || modsValue is not TomlTable mods)
        {
            return result;
        }

        foreach (var (id, value) in mods)
        {
            if (value is bool enabled)
            {
                result[id] = new ModSetting(enabled, null, null);
            }
            else if (value is TomlTable entry)
            {
                // enabled is optional in table, a mod listed is on unless said otherwise
                var on = true;
                if (entry.TryGetValue("enabled", out var enabledValue))
                {
                    if (enabledValue is bool b) on = b;
                    else logger.LogWarning($"mods.{id}: enabled must be true or false, ignoring...");
                }

                int? priority = null;
                if (entry.TryGetValue("priority", out var priorityValue))
                {
                    if (priorityValue is long n && n >= 0 && n <= MaxPriority) priority = (int)n;
                    else logger.LogWarning($"mods.{id}: priority must be a whole number from 0 to {MaxPriority}, ignoring...");
                }

                //TODO: ADD ON TO THIS SHIT LATER I SWEAR TO GOD PLEASEEEEEEEEEEEEEEE MORE SEMVER
                string? version = null;
                if (entry.TryGetValue("version", out var versionValue))
                {
                    if (versionValue is string v) version = v;
                    else logger.LogInformation($"mods.{id}: version must be text in quotes, like \"1.2.0\", ignoring...");
                }

                result[id] = new ModSetting(on, priority, version);
            }
            else
            {
                logger.LogWarning($"mods.{id}: expected true, false or a table, ignoring...");
            }
        }

        return result;
    }
}