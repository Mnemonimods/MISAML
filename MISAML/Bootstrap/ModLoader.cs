using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;
using MISAML.Bootstrap.Interfaces;
using MISAML.Bootstrap.Records;
using MISAML.Core;
using MISAML.Interfaces;
using Serilog;

namespace MISAML.Bootstrap;

// scans mods/<mod-id>/mod.json + a DLL (or more) per mod, loads each enabled
// one into the Default AssemblyLoadContext, mods only ever call Hooks.Register,
// they never touch live Godot types directly. A broken mod's exception is caught
// and logged, never allowed to stop the rest of the mods from loading.
//
// A mod DLL's starting point is named in mod.json by "entryMethod", written as
// Namespace.Class.Method. The method must be public static with no parameters.
public class ModLoader(
    IGodotBridge godotBridge,
    IHooks hooks,
    IModSettings modSettings,
    IVersionReq versionReq,
    IDependencyResolver resolver,
    IIdScanner idScanner,
    IIdRegistry idRegistry,
    IIdWriter idWriter,
    IIds ids,
    ILogger<ModLoader> logger) : IModLoader
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

    // match field names ignoring case, so modId and modid both work
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // mod ids & dep ids, letters, digits '.', '_', '-', max 64
    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$");

    private static string CleanSource(string depId, string source)
    {
        if (!Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new Exception($"dependency '{depId}' has an invalid source '{source}'");

        var path = uri.AbsolutePath.Trim('/');
        if (path.EndsWith(".git")) path = path[..^4];

        if (path.Split('/').Length != 2)
            throw new Exception($"dependency '{depId}' has an invalid source '{source}'");

        if (path.Split('/').Any(string.IsNullOrEmpty))
            throw new Exception($"dependency '{depId}' has an invalid source '{source}'");

        return $"https://github.com/{path}";
    }

    private ModManifest ParseManifest(string json)
    {
        var raw = JsonSerializer.Deserialize<RawManifest>(json, JsonOptions)
                  ?? throw new Exception("mod.json is empty");

        var id = raw.ModId?.Trim();
        if (string.IsNullOrEmpty(id))
            throw new Exception("mod.json requires a modId");

        if (!IdPattern.IsMatch(id))
            throw new Exception($"modId '{id}' is not valid (use letters, digits, '.', '_' or '-', max 64 characters)");

        var dependencies = new List<Dependency>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (depId, rawDep) in raw.Dependencies ?? [])
        {
            // ids ignore case
            if (!seen.Add(depId))
                throw new Exception($"dependencies lists '{depId}' twice");

            if (!IdPattern.IsMatch(depId))
                throw new Exception($"depId '{depId}' is not valid (use letters, digits, '.', '_' or '-', max 64 characters)");

            foreach (var key in rawDep.UnknownKeys)
            {
                logger.LogWarning(key.Equals("sourceManifest", StringComparison.OrdinalIgnoreCase)
                    ? $"{id}: dependency '{depId}': sourceManifest is unused on dependencies, ignoring it"
                    : $"{id}: dependency '{depId}': unknown key '{key}', ignoring it");
            }

            if (string.IsNullOrWhiteSpace(rawDep.Version))
                throw new Exception($"dependency '{depId}' requires a version");

            var range = versionReq.TryParse(rawDep.Version, out var rangeErr)
                        ?? throw new Exception($"dependency '{depId}' has a bad version: {rangeErr}");

            dependencies.Add(new Dependency(
                depId,
                rawDep.Version,
                range,
                rawDep.Source == null ? null : CleanSource(depId, rawDep.Source)
            ));
        }

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
            [.. dependencies.OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase)]
        );
    }

    public void LoadMods(string modsDir)
    {
        _ = ids;

        if (!Directory.Exists(modsDir))
        {
            logger.LogWarning($"no mods directory at {modsDir}, skipping");
            return;
        }

        // assembly name -> full path, for every DLL in every mod that will
        // load, incl. subfolders, before any mod loads, the Resolving hook
        // looks up names here
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
        _modDirs.Clear();
        _modDirs.AddRange(Directory.GetDirectories(modsDir)
            .Where(dir => !Path.GetFileName(dir).StartsWith('.'))
            .OrderBy(dir => dir, StringComparer.Ordinal));

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

        var candidates = new List<ReadMod>();
        var unavailable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var invalid = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var read = ReadMods(invalid);

        // a dep on a mod that is installed but broken
        foreach (var (badId, why) in invalid)
            unavailable.TryAdd(badId, why);

        foreach (var (modDir, manifest, _) in read)
        {
            settings.TryGetValue(manifest.Id, out var setting);

            if (setting is { Enabled: false })
            {
                _mods.Add(new ModStatus(manifest.Id, manifest.Name, manifest.Version, false, false, null, manifest.Description));
                unavailable[manifest.Id] = "disabled in misaml.toml";
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
                    unavailable[manifest.Id] = problem;
                    continue;
                }
            }

            // the user's priority for this mod, used for every hook it registers
            if (setting?.Priority is { } priority)
            {
                hooks.SetModPriority(manifest.Id, priority);
                logger.LogInformation($"{manifest.Id}: priority {priority} from misaml.toml");
            }

            candidates.Add(new ReadMod(modDir, manifest, setting?.Priority ?? Hooks.DefaultPriority));
        }

        var result = resolver.Resolve(candidates, unavailable);

        // mods the resolver refused to load
        foreach (var skipped in result.Skipped)
        {
            var m = skipped.Mod.Manifest;
            _mods.Add(new ModStatus(m.Id, m.Name, m.Version, true, false, skipped.Reason, m.Description));

            if (skipped.Problems.Count == 1)
            {
                logger.LogWarning($"{m.Id}: not loading, {skipped.Problems[0]}");
            }
            else
            {
                logger.LogWarning($"{m.Id}: not loading, {skipped.Problems.Count} problems:");
                foreach (var problem in skipped.Problems)
                    logger.LogWarning($"{m.Id}:   - {problem}");
            }
        }

        // only mods that will actually load get dlls idx'ed
        _dllIndex.Clear();
        BuildDllIdx([.. result.LoadOrder.Select(m => m.Dir)]);

        var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in result.LoadOrder)
        {
            var m = mod.Manifest;

            // deps come first in LoadOrder, so it's already known if each one worked
            var broken = m.Dependencies.FirstOrDefault(d => failed.ContainsKey(d.Id));
            if (broken != null)
            {
                var reason = $"requires {broken.Id}, which failed to load: {failed[broken.Id]}";
                _mods.Add(new ModStatus(m.Id, m.Name, m.Version, true, false, reason, m.Description));
                logger.LogWarning($"{m.Id}: not loading, {reason}");
                failed[m.Id] = reason;
                continue;
            }

            var err = LoadMod(mod.Dir, m);
            if (err != null) failed[m.Id] = err;
        }

        var loaded = _mods.Count(s => s.Loaded);
        var notLoaded = _mods.Count(s => s.Enabled && !s.Loaded);
        var disabled = _mods.Count(s => !s.Enabled);

        logger.LogInformation($"{loaded} of {_mods.Count} mods loaded ({notLoaded} not loaded, {disabled} disabled)");

        godotBridge.OnReady(AllocateIds);
    }

    // runs once, after every mod has loaded and registered, gives each registered
    // word its numbers and writes them into the game
    private void AllocateIds()
    {
        var used = idScanner.Scan();
        var spaces = used.Numbers.Select(n => $"{n.Key} {n.Value.Count} (highest {n.Value.DefaultIfEmpty(-1).Max()})");
        logger.LogInformation($"assembler uses {string.Join(", ", spaces)}, {used.Words.Count} words");

        idWriter.Apply(idRegistry.Allocate(used));
    }

    // the modId from a manifest that failed to parse, null if it cannot be read
    private static string? TryReadId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("modId", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    var id = property.Value.GetString()?.Trim();
                    return string.IsNullOrEmpty(id) ? null : id;
                }
            }
        }
        catch (JsonException) { }

        return null;
    }

    private List<ReadMod> ReadMods(Dictionary<string, string> invalid)
    {
        var result = new List<ReadMod>();

        // mod id to folder claimed it first
        var idOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var modDir in _modDirs)
        {
            var manifestPath = Path.Combine(modDir, "mod.json");
            if (!File.Exists(manifestPath))
            {
                logger.LogWarning($"skipping {modDir}: no mod manifest");
                invalid.TryAdd(Path.GetFileName(modDir), "has no mod.json");
                continue;
            }

            string? json = null;
            ModManifest manifest;
            try
            {
                json = File.ReadAllText(manifestPath);
                manifest = ParseManifest(json);
            }
            catch (Exception e)
            {
                logger.LogWarning($"skipping {modDir}: invalid mod manifest ({e.GetType().Name}: {e.Message})");

                // if the id cannot be read, the folder name is best guess
                var badId = (json == null ? null : TryReadId(json)) ?? Path.GetFileName(modDir);

                invalid.TryAdd(badId, $"invalid mod manifest ({e.Message})");

                continue;
            }

            if (!idOwners.TryAdd(manifest.Id, modDir))
            {
                logger.LogWarning($"skipping {modDir}: mod id '{manifest.Id}' is already used by {idOwners[manifest.Id]}");
                continue;
            }

            result.Add(new ReadMod(modDir, manifest));
        }

        return result;
    }

    private string? LoadMod(string modDir, ModManifest manifest)
    {
        // a mod is a dll (entryAssembly), a GDScript (script), or both
        string? err = null;

        try
        {
            if (manifest.EntryAssembly == null && manifest.EntryScript == null)
                throw new Exception("mod manifest requires an entry assembly or script");


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
        return err;
    }

    /// <summary>
    /// Builds an index of mod assemblies by filename, excluding build output and
    /// resolving duplicate names by keeping the first assembly encountered.
    /// </summary>
    /// <param name="dirs">The mod folders to scan for assemblies.</param>
    private void BuildDllIdx(IReadOnlyList<string> dirs)
    {
        foreach (var modDir in dirs)
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
                    {
                        var kept = _dllIndex[name];
                        var keptVer = VersionOf(kept);
                        var otherVer = VersionOf(path);

                        logger.LogWarning(keptVer != null && otherVer != null && keptVer != otherVer
                            ? $"assembly '{name}' exists in two versions ({keptVer} in {kept} & {otherVer} in {path}), using {keptVer}. A mod built against {otherVer} may break"
                            : $"duplicate assembly '{name}': keeping {kept}, ignoring {path}");
                    }
                }
            }
            catch (Exception e)
            {
                // one unreadable folder never stops the rest from being read
                logger.LogWarning($"could not read {modDir} for DLLs ({e.GetType().Name}: {e.Message})");
            }
        }

        logger.LogInformation($"indexed {_dllIndex.Count} assemblies from {dirs.Count} mod folders");
    }

    /// <summary>
    /// Gets the version of an assembly from its file metadata
    /// </summary>
    /// <param name="path">The path to the assembly file.</param>
    /// <returns>The assembly's version, or <see langword="null"/> if the version cannot be retrieved.</returns>
    private static Version? VersionOf(string path)
    {
        try { return AssemblyName.GetAssemblyName(path).Version; }
        catch (Exception) { return null; }
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
    {
        var parts = Path.GetRelativePath(modDir, path)
            .Split(Path.DirectorySeparatorChar)
            .SkipLast(1)
            .ToArray();

        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Equals("obj", StringComparison.OrdinalIgnoreCase))
                return true;

            if (parts[i].Equals("bin", StringComparison.OrdinalIgnoreCase)
                && i + 1 < parts.Length
                && (parts[i + 1].Equals("Debug", StringComparison.OrdinalIgnoreCase)
                    || parts[i + 1].Equals("Release", StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

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
            throw new Exception("entryAssembly requires entryMethod in mod.json (Hint: Namespace.Class.Method)");

        // split at the last dot, before it is the class, after it's the method
        var split = manifest.EntryMethod.LastIndexOf('.');
        if (split <= 0 || split == manifest.EntryMethod.Length - 1)
            throw new Exception($"entryMethod '{manifest.EntryMethod}' must look like Namespace.Class.Method (or Class.Method with no namespace)");

        var className = manifest.EntryMethod[..split];
        var methodName = manifest.EntryMethod[(split + 1)..];

        var entryType = asm.GetType(className)
                        ?? throw new Exception($"class '{className}' not found in {manifest.EntryAssembly} (entryMethod is Namespace.Class.Method)");

        var initMethod = entryType.GetMethod(
                             methodName, BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null)
                         ?? throw new Exception($"{className}.{methodName}() not found (must be public static, no parameters)");

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