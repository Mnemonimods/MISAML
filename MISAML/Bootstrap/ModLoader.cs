using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MISAML.Bootstrap.Interfaces;
using MISAML.Bootstrap.Records;
using MISAML.Core;
using MISAML.Exceptions.Manifests;
using MISAML.Interfaces;

namespace MISAML.Bootstrap;

// scans mods/<mod-id>/mod.json + a DLL (or more) per mod, loads each enabled
// one into the Default AssemblyLoadContext, mods only ever call Hooks.Register,
// they never touch live Godot types directly. A broken mod's exception is caught
// and logged, never allowed to stop the rest of the mods from loading.
//
// A mod DLL's starting point is named in mod.json by "entryMethod", written as
// Namespace.Class.Method. The method must be public static with no parameters.
public class ModLoader(IGodotBridge godotBridge, IHooks hooks, IModSettings modSettings, IVersionReq versionReq, ILogger<ModLoader> logger) : IModLoader
{
    // populated once by LoadMods(), read-only "snapshot" I suppose.
    public static IReadOnlyList<ModStatus> Mods => _mods;
    private static readonly List<ModStatus> _mods = [];

    // every mod folder, sorted so load order is the same every time ran
    private static readonly List<string> _modDirs = [];

    // assembly name -> full path, for every DLL in every mod folder 
    // incl. subfolders. before any mod loads, the Resolving hook looks
    // up names here
    private static readonly Dictionary<string, string> _dllIndex = new(StringComparer.OrdinalIgnoreCase);

    private static bool _resolvingHooked = false;

    // the game's own assembly load context, a mod that references the game's
    // DLLs must get the game's copy, a second copy is a different set of types
    public AssemblyLoadContext? GameContext { get; set; }

    // the game's already loaded copy of an assembly, or null if it has none
    private Assembly? FindInGame(AssemblyName name)
        => GameContext?.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);

    /*
    an example mod.json:

    {
        "modId": "ExampleMod",
        "author": "JJRol",
        "modName": "Example Mod",
        "description": "This is an example description.",
        "version": "0.1.0",
        "source": "https://github.com/user/example-mod",
        "sourceManifest": "https://raw.githubusercontent.com/user/example-mod/main/mod.json",
        "misamlVersion": "^0.1.0",
        "entryAssembly": "ExampleMod.dll",
        "entryMethod": "ExampleMod.Main.Start",
        "entryScript": "main.gd",
        "dependencies": {
            "MISAML.API.Base": "^1.0.0"
        }
    }
    */

    // exactly what is written in mod.json, every field optional
    private record RawManifest(
        string? ModId,
        string? ModName,
        string? Version,
        string? Description,
        string? Author,
        string? Source,
        string? SourceManifest,
        string? MisamlVersion,
        string? EntryAssembly,
        string? EntryMethod,
        string? EntryScript,
        Dictionary<string, string>? Dependencies
    );

    // parsed and tidied version
    private record ModManifest(
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
        IReadOnlyDictionary<string, string> Dependencies
    );

    // match field names ignoring case, so modId and modid both work
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private ModManifest ParseManifest(string json)
    {
        var raw = JsonSerializer.Deserialize<RawManifest>(json, JsonOptions)
            ?? throw new BadModManifestException("mod.json is empty");

        var id = raw.ModId?.Trim();
        if (string.IsNullOrEmpty(id))
            throw new BadModManifestException("mod.json needs a modId");

        return new ModManifest(
            id,
            raw.ModName ?? id,
            raw.Version ?? "0.0.0",
            raw.Description,
            raw.Author,
            raw.Source,
            raw.SourceManifest,
            raw.MisamlVersion,
            raw.EntryAssembly,
            raw.EntryMethod,
            raw.EntryScript,
            // dependency ids are compared ignoring case
            new Dictionary<string, string>(
                raw.Dependencies ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase)
        );
    }

    public void LoadMods(string modsDir)
    {
        if (!Directory.Exists(modsDir))
        {
            logger.LogWarning($"no mods directory at {modsDir}, skipping");
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
                var shared = FindInGame(name);
                if (shared != null) return shared;

                if (name.Name == null || !_dllIndex.TryGetValue(name.Name, out var path)) return null; // not found
                return ctx.LoadFromAssemblyPath(path);
            };
        }

        // pass one, before any mod loads, list every mod folder (sorted)
        // and index every DLL in them, so a mod can use a DLL from a mod that
        // sorts after it
        _modDirs.Clear();
        _modDirs.AddRange(Directory.GetDirectories(modsDir).OrderBy(dir => dir, StringComparer.Ordinal));
        logger.LogInformation("Building DLL index...");
        BuildDllIdx();

        // misaml.toml sits next to the mods folder
        var settings = modSettings.Load(Path.GetFullPath(Path.Combine(modsDir, "..", "misaml.toml")));
        if (settings == null)
        {
            logger.LogError("not loading any mods, because misaml.toml could not be read (fix it, or delete it to get a new one)");
            return;
        }

        // MISAML's own version, read from MISAML.dll itself instead, set in csproj
        var misamlVer = typeof(Hooks).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        var installedMisaml = versionReq.ParseVersion(misamlVer);
        if (installedMisaml == null)
        {
            logger.LogError($"could not read MISAML's own version ('{misamlVer}')");
        }

        // mod id -> the folder that claimed it first
        var idOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var modDir in _modDirs)
        {
            var manifestPath = Path.Combine(modDir, "mod.json");
            if (!File.Exists(manifestPath))
            {
                logger.LogWarning($"skipping {modDir}: no mod manifest");
                continue;
            }

            ModManifest? manifest;
            try
            {
                manifest = ParseManifest(File.ReadAllText(manifestPath));
            }
            catch (BadModManifestException e)
            {
                logger.LogWarning($"skipping {modDir}: bad mod manifest ({e.GetType().Name}: {e.Message})");
                continue;
            }

            if (manifest == null)
            {
                logger.LogWarning($"skipping {modDir}: mod manifest parsed to null");
                continue;
            }

            if (!idOwners.TryAdd(manifest.Id, modDir))
            {
                logger.LogWarning($"skipping {modDir}: mod id '{manifest.Id}' is already used by {idOwners[manifest.Id]}");
                continue;
            }

            settings.TryGetValue(manifest.Id, out var setting);

            if (setting is { Enabled: false })
            {
                _mods.Add(new ModStatus(manifest.Id, manifest.Name, manifest.Version, false, false, null, manifest.Description));
                logger.LogWarning($"{manifest.Id}: disabled, not loading");
                continue;
            }

            // a mod made for another MISAML ver only gets a warning, still loads
            if (manifest.MisamlVersion is { } wanted && installedMisaml != null)
            {
                var wantedRange = versionReq.TryParse(wanted, out var wantedError);
                if (wantedRange == null)
                    logger.LogWarning($"{manifest.Id}: misamlVersion {wantedError}, can't check it");
                else if (!wantedRange.Contains(installedMisaml))
                    logger.LogWarning($"{manifest.Id}: WARNING: made for MISAML {wanted}, but this is MISAML {installedMisaml}, issues may occur.");
            }

            // misaml.toml can pin which version of a mod the user wants
            if (setting?.Version is { } pin)
            {
                var range = versionReq.TryParse(pin, out var pinError);
                var installed = versionReq.ParseVersion(manifest.Version);

                string? problem =
                    range == null ? $"the version in misaml.toml {pinError}" :
                    installed == null ? $"mod version '{manifest.Version}' is not a version like 1.2.3, so it can't be checked against '{pin}'" :
                    !range.Contains(installed) ? $"installed version {manifest.Version} does not match '{pin}' from misaml.toml" :
                    null;

                if (problem != null)
                {
                    logger.LogWarning($"{manifest.Id}: not loading, {problem}");
                    _mods.Add(new ModStatus(manifest.Id, manifest.Name, manifest.Version, true, false, problem, manifest.Description));
                    continue;
                }
            }

            // the user's priority for this mod, used for every hook it registers
            if (setting?.Priority is { } priority)
            {
                hooks.SetModPriority(manifest.Id, priority);
                logger.LogInformation($"{manifest.Id}: priority {priority} from misaml.toml");
            }

            // a mod is a dll (entryAssembly), a GDScript (script), or both
            string? err = null;

            try
            {
                if (manifest.EntryAssembly == null && manifest.EntryScript == null)
                    throw new ModManifestAssemblyException("mod manifest requires an entry assembly or script!");

                // everything the mod registers while this block runs is owned
                // by its modId, so Hooks can name the mod in the log
                using (hooks.EnterMod(manifest.Id))
                {
                    if (manifest.EntryAssembly != null) LoadDll(modDir, manifest);
                    if (manifest.EntryScript != null) LoadScript(modDir, manifest);
                }
            }
            catch (Exception e)
            {
                // one broken mod never takes others down with it, Invoke()
                // wraps errors, show the real one
                var ue = e is TargetInvocationException { InnerException: { } inner } ? inner : e;
                err = $"{ue.GetType().Name}: {ue.Message.Trim()}";
            }

            _mods.Add(new ModStatus(manifest.Id, manifest.Name, manifest.Version, true, err == null, err, manifest.Description));
            logger.LogInformation(err == null ? $"{manifest.Id}: loaded OK" : $"{manifest.Id}: failed to load - {err}");
        }
    }

    /// <summary>
    /// Builds an index of mod assemblies by filename, excluding build output and
    /// resolving duplicate names by keeping the first assembly encountered.
    /// </summary>
    /// <param name="log">Receives warnings for duplicate assemblies and
    /// directories that could not be read.</param>
    private void BuildDllIdx()
    {
        foreach (var modDir in _modDirs)
        {
            try
            {

                var dlls = Directory
                    .EnumerateFiles(modDir, "*.dll", SearchOption.AllDirectories) /* find all dlls in the mod dir and subdirs */
                    .Where(path => !IsBuildOutput(modDir, path)) /* skip build output dirs */
                    .OrderBy(path => path.Count(c => c == Path.DirectorySeparatorChar)) /* load dlls from shallower dirs first */
                    .ThenBy(path => path, StringComparer.Ordinal); /* sort alphabetically when at same depth */

                foreach (var path in dlls)
                {
                    var name = Path.GetFileNameWithoutExtension(path);

                    if (!_dllIndex.TryAdd(name, path))
                        logger.LogWarning($"duplicate assembly '{name}': keeping {_dllIndex[name]}, ignoring {path}");
                }
            }
            catch (Exception e)
            {
                // one unreadable folder never stops the rest from being read
                logger.LogWarning($"could not read {modDir} for DLLs ({e.GetType().Name}: {e.Message})");
            }
        }

        logger.LogInformation($"indexed {_dllIndex.Count} assemblies from {_modDirs.Count} mod folders");
    }

    /// <summary>
    /// Determines whether a DLL is located inside a build output directory, which
    /// should be excluded from assembly discovery.
    /// </summary>
    /// <param name="modDir">The root directory of the mod being scanned.</param>
    /// <param name="path">The full path of the DLL being checked.</param>
    /// <returns><see langword="true"/> if the DLL is inside an <c>obj</c>
    /// directory, otherwise, <see langword="false"/>.</returns>
    protected virtual bool IsBuildOutput(string modDir, string path)
        => Path.GetRelativePath(modDir, path)
            .Split(Path.DirectorySeparatorChar)
            .SkipLast(1)
            .Any(part => part.Equals("obj", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Loads the mod's entry assembly and invokes the method specified by
    /// <see cref="ModManifest.EntryMethod"/>. The entry method must be public,
    /// static, and have no parameters.</summary>
    /// <param name="modDir">The root directory of the mod.</param>
    /// <param name="manifest">The manifest specifying the entry assembly and
    /// the method.</param>
    /// <exception cref="FileNotFoundException">The entry assembly could not
    /// be found.</exception>
    /// <exception cref="Exception">The entry method is missing or invalid,
    /// the specified class cannot be found, or the method doesn't exist with
    /// the required specifications/signature.</exception>
    private void LoadDll(string modDir, ModManifest manifest)
    {
        var dllPath = Path.Combine(modDir, manifest.EntryAssembly!);
        if (!File.Exists(dllPath))
            throw new FileNotFoundException($"assembly not found: {dllPath}");

        var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(dllPath);

        if (manifest.EntryMethod == null)
            throw new ModManifestAssemblyException("entryAssembly needs entryMethod in mod.json (Hint: Namespace.Class.Method)");

        // split at the last dot, before it is the class, after it's the method
        var split = manifest.EntryMethod.LastIndexOf('.');
        if (split <= 0 || split == manifest.EntryMethod.Length - 1)
            throw new ModManifestAssemblyException($"entryMethod '{manifest.EntryMethod}' must look like Namespace.Class.Method (or Class.Method with no namespace)");

        var className = manifest.EntryMethod[..split];
        var methodName = manifest.EntryMethod[(split + 1)..];

        var entryType = asm.GetType(className)
            ?? throw new ModManifestAssemblyException($"class '{className}' not found in {manifest.EntryAssembly} (entryMethod is Namespace.Class.Method)");

        var initMethod = entryType.GetMethod(
                methodName, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)
            ?? throw new ModManifestAssemblyException($"{className}.{methodName}() not found (must be public static, no parameters)");

        initMethod.Invoke(null, null);
    }

    /// <summary>
    /// Keeps instantiated mod scripts alive for the lifetime of the loader,
    /// including scripts that are not attached to the Godot scene tree and
    /// would otherwise be freed/dropped.
    /// </summary>
    private static readonly List<object> _scriptInstances = [];

    /// <summary>
    /// Loads and instantiates a mod's GDScript entry point once Godot is ready.
    /// Configures the instance with the mod directory and script API, then adds
    /// it to the Godot scene tree if it extends <c>Node</c>.
    /// Script compilation failures and non-Node instances are reported through
    /// the logger provided.
    /// </summary>
    /// <param name="modDir">The root directory of the mod.</param>
    /// <param name="manifest">The manifest specifying the entry script.</param>
    /// <exception cref="FileNotFoundException">
    /// The entry script could not be found at the specified path.
    /// </exception>
    private void LoadScript(string modDir, ModManifest manifest)
    {
        var path = Path.Combine(modDir, manifest.EntryScript!);
        if (!File.Exists(path))
            throw new FileNotFoundException($"script not found: {path}");

        // so a bad path fails at load time
        var source = File.ReadAllText(path);

        // mods load before the game is running, so the actual work waits for OnReady
        godotBridge.OnReady(() =>
        {
            var script = godotBridge.Instantiate("GDScript");
            godotBridge.Set(script, "source_code", source);

            var result = Convert.ToInt64(godotBridge.Call(script, "reload"));
            if (result != 0)
            {
                logger.LogError($"{manifest.Id}: GDScript failed to compile (error {result}), see the game console");
                return;
            }

            var instance = godotBridge.Call(script, "new")!;
            _scriptInstances.Add(instance);
            godotBridge.Call(instance, "set_meta", "mod_dir", modDir);
            godotBridge.Call(instance, "set_meta", "misaml", godotBridge.ScriptApi);

            if (Equals(godotBridge.Call(instance, "is_class", "Node"), true))
            {
                var tree = godotBridge.CallStatic("Godot.Engine", "GetMainLoop")!;
                var root = godotBridge.Get(tree, "root")!;
                godotBridge.Call(root, "add_child", instance);
            }
            else
            {
                logger.LogWarning($"{manifest.Id}: script loaded but it isn't a Node, so it won't run _ready/_process (use 'extends Node')");
            }
        });
    }
}
