using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace MISAML.Bootstrap;

// scans mods/<mod-id>/mod.json + a DLL (or more) per mod, loads each enabled
// one into the Default AssemblyLoadContext, mods only ever call Hooks.Register,
// they never touch live Godot types directly. A broken mod's exception is caught
// and logged, never allowed to stop the rest of the mods from loading.
//
// Convention a mod DLL must follow: exactly one type named "ModEntry",
// with a public static Initialize() method. That's the entire contract.
public static class ModLoader
{
    // populated once by LoadMods(), read-only "snapshot" I suppose.
    public static IReadOnlyList<ModStatus> Mods => _mods;
    private static readonly List<ModStatus> _mods = [];

    // every mod folder seen so far, so the Resolving hook can find a
    // sibling DLL by name regardless of which mod entry assembly asks
    private static readonly List<string> _modDirs = [];
    private static bool _resolvingHooked = false;

    /*
    an example mod.json:

    {
      "id": "example-mod",
      "name": "example mod",
      "version": "1.0.0",
      "entryAssembly": "examplemod.dll",
      "enabled": true
    }
    */
    private record ModManifest(string Id, string Name, string Version, string EntryAssembly, bool Enabled);

    public static void LoadMods(string modsDir, Action<string> log)
    {
        if (!Directory.Exists(modsDir))
        {
            log($"no mods directory at {modsDir}, skipping");
            return;
        }

        // hook this once to resolve any dependency DLL a mod entry assembly
        // (or another dependency) references, by looking for <name>.dll
        // alongside it in any known mod folder!
        if (!_resolvingHooked)
        {
            _resolvingHooked = true;
            AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                foreach (var dir in _modDirs)
                {
                    var candidate = Path.Combine(dir, name.Name + ".dll");
                    if (File.Exists(candidate))
                    {
                        log($"resolved dependency '{name.Name}' -> {candidate}");
                        return ctx.LoadFromAssemblyPath(candidate);
                    }
                }
                return null; // not found
            };
        }

        foreach (var modDir in Directory.GetDirectories(modsDir))
        {
            // track every mod folder up front, even ones that end up skipped/
            // failed, covers a dependency being asked for before its own manifest 
            // has even been read yet
            _modDirs.Add(modDir);

            var manifestPath = Path.Combine(modDir, "mod.json");
            if (!File.Exists(manifestPath))
            {
                log($"skipping {modDir}: no mod manifest");
                continue;
            }

            ModManifest? manifest;
            try
            {
                var json = File.ReadAllText(manifestPath);

                // json deserializer, makes sense, it goes to ModManifest record
                manifest = JsonSerializer.Deserialize<ModManifest>(json,
                    /* PropertyNameCaseInsensitive is like #[rename] in rust. */
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception e)
            {
                log($"skipping {modDir}: bad mod manifest ({e.GetType().Name}: {e.Message})");
                continue;
            }

            if (manifest == null)
            {
                log($"skipping {modDir}: mod manifest parsed to null");
                continue;
            }

            if (!manifest.Enabled)
            {
                _mods.Add(new ModStatus(manifest.Id, manifest.Name, manifest.Version, false, false, null));
                log($"{manifest.Id}: disabled, not loading");
                continue;
            }

            var dllPath = Path.Combine(modDir, manifest.EntryAssembly);
            if (!File.Exists(dllPath))
            {
                var err = $"entry assembly not found: {dllPath}";
                _mods.Add(new ModStatus(manifest.Id, manifest.Name, manifest.Version, true, false, err));
                log($"{manifest.Id}: {err}");
                continue;
            }

            try
            {
                var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(dllPath);

                var entryType = asm.GetTypes().FirstOrDefault(t => t.Name == "ModEntry")
                    ?? throw new Exception("no type named 'ModEntry' found in assembly");

                var initMethod = entryType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)
                    ?? throw new Exception("ModEntry.Initialize() not found (must be public static)");

                initMethod.Invoke(null, null);

                _mods.Add(new ModStatus(manifest.Id, manifest.Name, manifest.Version, true, true, null));
                log($"{manifest.Id}: loaded OK");
            }
            catch (Exception e)
            {
                // one broken mod must never take the others down with it
                var err = $"{e.GetType().Name}: {e.Message}";
                _mods.Add(new ModStatus(manifest.Id, manifest.Name, manifest.Version, true, false, err));
                log($"{manifest.Id}: failed to load - {err}");
            }
        }
    }
}
