using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using MISAML.Core;

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
    private static readonly List<string> ModDirs = [];
    private static bool _resolvingHooked = false;

    /*
    an example mod.json:

    {
      "id": "example-mod",
      "name": "example mod",
      "version": "1.0.0",
      "description": "Hi this is a description :3",
      "entryAssembly": "examplemod.dll",
      "entryScript": "main.gd"
    }
    */
    private record ModManifest(
        string Id,
        string Name,
        string Version,
        string? Description,
        string? EntryAssembly,
        string? EntryScript
    );

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
                foreach (var dir in ModDirs)
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

        var disabled = (Environment.GetEnvironmentVariable("MISAML_DISABLED_MODS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (var modDir in Directory.GetDirectories(modsDir))
        {
            // track every mod folder up front, even ones that end up skipped/
            // failed, covers a dependency being asked for before its own manifest 
            // has even been read yet
            ModDirs.Add(modDir);

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

            if (disabled.Contains(manifest.Id))
            {
                _mods.Add(new ModStatus(manifest.Id, manifest.Name, manifest.Version, false, false, null, manifest.Description));
                log($"{manifest.Id}: disabled, not loading");
                continue;
            }

            // a mod is a dll (entryAssembly), a GDScript (script), or both
            string? err = null;

            try
            {
                if (manifest.EntryAssembly == null && manifest.EntryScript == null)
                    throw new Exception("mod manifest requires an entry assembly or script!");

                if (manifest.EntryAssembly != null) LoadDll(modDir, manifest);
                if (manifest.EntryScript != null) LoadScript(modDir, manifest, log);
            }
            catch (Exception e)
            {
                // one broken mod never takes others down with it, Invoke()
                // wraps errors, show the real one
                var ue = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
                err = $"{ue.GetType().Name}: {ue.Message}";
            }

            _mods.Add(new ModStatus(manifest.Id, manifest.Name, manifest.Version, true, err == null, err, manifest.Description));
            log(err == null ? $"{manifest.Id}: loaded OK" : $"{manifest.Id}: failed to load - {err}");
        }
    }

    private static void LoadDll(string modDir, ModManifest manifest)
    {
        var dllPath = Path.Combine(modDir, manifest.EntryAssembly!);
        if (!File.Exists(dllPath))
            throw new FileNotFoundException($"assembly not found: {dllPath}");

        var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(dllPath);

        var entryType = asm.GetTypes().FirstOrDefault(t => t.Name == "ModEntry")
            ?? throw new Exception("no type named 'ModEntry' found in assembly");

        var initMethod = entryType.GetMethod("Initialize", BindingFlags.Public | BindingFlags.Static)
            ?? throw new Exception("ModEntry.Initialize() not found (must be public static)");

        initMethod.Invoke(null, null);
    }

    // keeps script instances alive (a script that is not a Node would be freed)
    private static readonly List<object> _scriptInstances = [];

    private static void LoadScript(string modDir, ModManifest manifest, Action<string> log)
    {
        var path = Path.Combine(modDir, manifest.EntryScript!);
        if (!File.Exists(path))
            throw new FileNotFoundException($"script not found: {path}");

        // so a bad path fails at load time
        var source = File.ReadAllText(path);

        // mods load before the game is running, so the actual work waits for OnReady
        GodotBridge.OnReady(() =>
        {
            var script = GodotBridge.Instantiate("GDScript");
            GodotBridge.Set(script, "source_code", source);

            var result = Convert.ToInt64(GodotBridge.Call(script, "reload"));
            if (result != 0)
            {
                log($"{manifest.Id}: GDScript failed to compile (error {result}), see the game console");
                return;
            }

            var instance = GodotBridge.Call(script, "new")!;
            _scriptInstances.Add(instance);
            GodotBridge.Call(instance, "set_meta", "mod_dir", modDir);
            GodotBridge.Call(instance, "set_meta", "misaml", GodotBridge.ScriptApi);

            if (Equals(GodotBridge.Call(instance, "is_class", "Node"), true))
            {
                var tree = GodotBridge.CallStatic("Godot.Engine", "GetMainLoop")!;
                var root = GodotBridge.Get(tree, "root")!;
                GodotBridge.Call(root, "add_child", instance);
            }
            else
            {
                log($"{manifest.Id}: script loaded but it isn't a Node, so it won't run _ready/_process (use 'extends Node')");
            }
        });
    }
}
