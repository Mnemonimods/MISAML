use std::{
    path::{Component, Path, PathBuf},
    process::Command,
};

#[cfg(unix)]
use std::os::unix::process::CommandExt;

use anyhow::{Context, Result, anyhow, bail};
use serde::Deserialize;

#[cfg(target_os = "windows")]
mod injector;

#[derive(Deserialize, Default)]
#[serde(default)]
struct Config {
    debug: bool,
    force: bool,
    game_dir: Option<PathBuf>,
}

// Known hashes of the game's DLLs
const DLL_HASHES: &[(&str, &[&str])] = &[
    (
        "Mnemonimov.dll",
        &[
            "5111b0825cb5919c5a8fcc30717b2c0f", /* 2026-10-12 */
            "8b06c0c6ea2b6f209aecd5e51ae49d58", /* 2026-10-14 */
        ],
    ),
    (
        "Asm.dll",
        &[
            "173d587f1dd59340138f8bb06f7f1f7c", /* 2026-10-12 */
            "ea2de8c656ae06817763bd56fd547c02", /* 2026-10-14 */
        ],
    ),
    (
        "GodotSharp.dll",
        &[
            "16dbe046b79ca898d2debefb1709906d", /* 2026-10-12 and 2026-10-14 */
        ],
    ),
];

const MNEMONIMOV_APP_ID: u32 = 3854110;
const BUILD_ID: &str = env!("CARGO_PKG_VERSION");

// strings libcoreclr.so | sed -nE 's/.*Version (8\.0)\.([0-9]+)[0-9]{2}\..*/\1.\2/p'
const RUNTIME_VERSION: &str = "8.0.31";

#[cfg(target_os = "windows")]
mod platform {
    pub const RUNTIME_PACKAGE: &str = "win-x64.zip";
    pub const GAME_EXE: &str = "mnemonimov.exe";
    pub const GAME_EXE_HASHES: &[&str] =
        &["63fcd750c72581592ef20c8b4ea031cc" /* 2026-10-20 */];
    pub const SHIM_NAME: &str = "misaml_shim.dll";
    pub const DATA_DIR: &str = "data_Mnemonimov_windows_x86_64";
}

#[cfg(target_os = "linux")]
mod platform {
    pub const RUNTIME_PACKAGE: &str = "linux-x64.tar.gz";
    pub const GAME_EXE: &str = "mnemonimov.x86_64";
    pub const GAME_EXE_HASHES: &[&str] = &[
        "6c9c48cb4cf2f00d7bec39d0c491c32d", /* 2026-10-12 and 2026-10-14 */
    ];
    pub const SHIM_NAME: &str = "libmisaml_shim.so";
    pub const DATA_DIR: &str = "data_Mnemonimov_linuxbsd_x86_64";
}

macro_rules! debug_log {
    ($($arg:tt)*) => {
        if std::env::var_os("MISAML_DEBUG").is_some() {
            eprintln!("[misaml {BUILD_ID}] {}", format_args!($($arg)*));
        }
    };
}

macro_rules! log {
    ($($arg:tt)*) => {
        eprintln!("[misaml {BUILD_ID}] {}", format_args!($($arg)*));
    };
}

const DEFAULT_CONFIG: &str = "\
# MISAML Configuration

# Verbose logs for debugging.
debug = false

# Force-launch the game even if untested.
force = false

# Where Mnemonimov is installed. Normally found through Steam automatically,
# uncomment and set this if that fails or you want to use a different copy.
# game_dir = \"/home/you/.local/share/Steam/steamapps/common/Mnemonimov\"

# Every installed mod gets a line here, set a line to false to turn off the mod.
# The name is the \"modId\" from the mod's manifest.
# A mod can also be a table, for example:
#   example-mod = { enabled = true, priority = 10, version = \"0.0.0\" }
# Priority is a whole number from 0 to 65535, lower runs first (default 100).
# It decides whose hook runs first when two mods hook the same thing.
[mods]
";

fn main() -> Result<()> {
    let args: Vec<String> = std::env::args().collect();
    let force = args.iter().any(|a| a == "--force");

    match args.get(1).map(String::as_str) {
        Some("inspect-pck") => cmd_inspect_pck(args.get(2)),
        Some("extract-pck") => cmd_extract_pck(args.get(2), args.get(3)),
        _ => cmd_launch(force),
    }
}

fn ensure_config(misaml_dir: &Path) {
    let path = misaml_dir.join("misaml.toml");

    if !path.exists() {
        match std::fs::write(&path, DEFAULT_CONFIG) {
            Ok(()) => {
                log!("created default config file at {}", path.display());
            }
            Err(e) => {
                log!("could not create {} ({e}), using defaults", path.display());
                return;
            }
        }
    }

    if let Err(e) = sync_mods(&path, &misaml_dir.join("mods")) {
        log!("could not update {}: {e:#}", path.display());
    }
}

// reads misaml.toml, defaults if it could not be created
fn load_config(misaml_dir: &Path) -> Result<Config> {
    let path = misaml_dir.join("misaml.toml");

    if !path.exists() {
        return Ok(Config::default());
    }

    log!("config: {}", path.display());

    let text = std::fs::read_to_string(&path)?;

    toml::from_str(&text).with_context(|| format!("bad config at {}", path.display()))
}

// makes sure every installed mod has a line in the [mods] table of misaml.toml,
// new mods are added as `true`.
fn sync_mods(config_path: &Path, mods_dir: &Path) -> Result<()> {
    let text = std::fs::read_to_string(config_path)?;
    let mut doc: toml_edit::DocumentMut = text
        .parse()
        .with_context(|| format!("bad config at {}", config_path.display()))?;

    if !doc.contains_key("mods") {
        doc["mods"] = toml_edit::table();
    }
    let mods = doc["mods"]
        .as_table_mut()
        .context("[mods] in misaml.toml must be a table")?;

    // the id of every mod folder that has a readable mod.json
    let mut installed = Vec::new();
    if mods_dir.is_dir() {
        for entry in std::fs::read_dir(mods_dir)? {
            let manifest = entry?.path().join("mod.json");
            if !manifest.is_file() {
                continue;
            }

            let id = std::fs::read_to_string(&manifest)
                .ok()
                .and_then(|json| serde_json::from_str::<serde_json::Value>(&json).ok())
                .and_then(|v| v["modId"].as_str().map(String::from));

            match id {
                Some(id) => installed.push(id),
                None => {
                    log!("could not read a mod id from {}", manifest.display());
                }
            }
        }
    }
    installed.sort();

    let mut added = Vec::new();
    for id in &installed {
        if !mods.contains_key(id) {
            mods.insert(id, toml_edit::value(true));
            added.push(id.as_str());
        }
    }

    // listed in the file but no longer installed, tell the user, don't delete
    for (key, _) in mods.iter() {
        if !installed.iter().any(|id| id == key) {
            log!("misaml.toml lists '{key}' but no installed mod has that id");
        }
    }

    if !added.is_empty() {
        std::fs::write(config_path, doc.to_string())?;
        log!("Added to misaml.toml: {}", added.join(", "));
    }

    Ok(())
}

fn check_file(path: &Path, name: &str, known: &[&str], mismatch: &mut Vec<String>) -> Result<()> {
    require_exists(path, name)?;

    let hash = format!("{:x}", md5::compute(std::fs::read(path)?));

    if known.contains(&hash.as_str()) {
        debug_log!("{name} matches");
    } else {
        mismatch.push(format!("{name} (found {hash})"));
    }

    Ok(())
}

fn check_game_ver(game_dir: &Path, force: bool) -> Result<()> {
    let data_dir = game_dir.join(platform::DATA_DIR);
    let mut mismatch = Vec::new();

    check_file(
        &game_dir.join(platform::GAME_EXE),
        platform::GAME_EXE,
        platform::GAME_EXE_HASHES,
        &mut mismatch,
    )?;

    // the dlls sit in the data folder
    for &(name, known) in DLL_HASHES {
        check_file(&data_dir.join(name), name, known, &mut mismatch)?;
    }

    if mismatch.is_empty() {
        return Ok(());
    }

    if force {
        log!(
            "WARNING: untested game files, launcing anyways: {}",
            mismatch.join(", ")
        );
        return Ok(());
    }

    bail!(
        "this version of Mnemonimov isn't supported by MISAML {BUILD_ID}. \
         These files changed: {}. The game probably updated. \
         Run with --force to launch anyway at your own risk.",
        mismatch.join(", ")
    );
}

fn cmd_launch(force: bool) -> Result<()> {
    let misaml_dir = own_install_dir().context("could not find misaml-cli directory")?;

    ensure_config(&misaml_dir);

    let config = load_config(&misaml_dir)?;

    let game_dir = resolve_game_dir(&config)?;

    let exe_path = game_dir.join(platform::GAME_EXE);
    require_exists(&exe_path, platform::GAME_EXE)?;

    check_game_ver(&game_dir, force || config.force)?;

    let payload_dir = misaml_dir.join("MISAML");

    ensure_runtime(&payload_dir)?;

    let shim_path = payload_dir.join(platform::SHIM_NAME);
    let framework_dir =
        find_framework_dir(&payload_dir).context("could not find bundled .NET runtime")?;

    require_exists(&shim_path, "shim library")?;
    require_exists(&payload_dir.join("MISAML.dll"), "MISAML.dll")?;

    log!("Launching Mnemonimov with MISAML...");
    log!("  shim: {}", shim_path.display());
    log!("  core: {}", payload_dir.display());
    log!("  framework: {}", framework_dir.display());

    let mut cmd = Command::new(&exe_path);
    cmd.current_dir(&game_dir)
        .env("MISAML_CORE_DIR", &payload_dir)
        .env("MISAML_FRAMEWORK_DIR", &framework_dir);
    if config.debug {
        cmd.env("MISAML_DEBUG", "1");
    }

    launch(cmd, &shim_path, &exe_path)
}

// Linux, LD_PRELOAD
#[cfg(target_os = "linux")]
fn launch(mut cmd: Command, shim_path: &Path, exe_path: &Path) -> Result<()> {
    let err = cmd.env("LD_PRELOAD", shim_path).exec();

    Err(anyhow!(err).context(format!("failed to launch {}", exe_path.display())))
}

// Windows, start the game suspended, make it load the shim, let it run, wait
// for it to exit
#[cfg(target_os = "windows")]
fn launch(cmd: Command, shim_path: &Path, exe_path: &Path) -> Result<()> {
    let status = injector::launch(cmd, shim_path)
        .with_context(|| format!("failed to launch {}", exe_path.display()))?;

    if !status.success() {
        log!("game exited with {status}");
    }

    Ok(())
}

// a game_dir that is set, but wrong is an error, not silent fallback to Steam,
// otherwise a typo would quietly launch another copy
fn resolve_game_dir(config: &Config) -> Result<PathBuf> {
    // an empty game_dir = "" counts as not set
    let configured = config
        .game_dir
        .as_deref()
        .filter(|dir| !dir.as_os_str().is_empty());

    if let Some(dir) = configured {
        let dir = expand_home(dir);

        if !dir.is_dir() {
            bail!(
                "game_dir in misaml.toml is not a folder: {}. \
                 Fix the path, or remove the line to use Steam detection.",
                dir.display()
            );
        }

        log!("Using game_dir from misaml.toml: {}", dir.display());
        return Ok(dir);
    }

    let dir = find_game_dir().context(
        "could not find Mnemonimov, is it installed via Steam? \
         You can also set game_dir in misaml.toml",
    )?;

    log!("Found Mnemonimov at: {}", dir.display());
    Ok(dir)
}

// toml does not expand "~", so uh, fix
fn expand_home(path: &Path) -> PathBuf {
    let home = std::env::var_os("HOME").or_else(|| std::env::var_os("USERPROFILE"));

    if let (Ok(rest), Some(home)) = (path.strip_prefix("~"), home) {
        return PathBuf::from(home).join(rest);
    }

    path.to_path_buf()
}

fn find_game_dir() -> Result<PathBuf> {
    let steam = steamlocate::SteamDir::locate().context("could not locate Steam")?;

    let Some((app, library)) = steam.find_app(MNEMONIMOV_APP_ID)? else {
        bail!("Mnemonimov (app id {MNEMONIMOV_APP_ID}) is not installed!");
    };

    Ok(library.resolve_app_dir(&app))
}

fn own_install_dir() -> Result<PathBuf> {
    std::env::current_exe()?
        .parent()
        .map(Path::to_path_buf)
        .ok_or_else(|| anyhow!("could not get executable directory"))
}

fn find_framework_dir(root: &Path) -> Result<PathBuf> {
    let root = root.join("runtime/shared/Microsoft.NETCore.App");

    let mut versions = std::fs::read_dir(&root)?
        .filter_map(|entry| entry.ok())
        .map(|entry| entry.path())
        .filter(|path| path.is_dir());

    let Some(version) = versions.next() else {
        bail!("no .NET runtime found in {}", root.display());
    };

    if versions.next().is_some() {
        bail!("multiple .NET runtimes found in {}", root.display());
    }

    Ok(version)
}

fn require_exists(path: &Path, name: &str) -> Result<()> {
    if path.exists() {
        return Ok(());
    }

    bail!("{name} not found at {}", path.display());
}

fn cmd_inspect_pck(path: Option<&String>) -> Result<()> {
    let Some(path) = path else {
        bail!("usage: misaml-cli inspect-pck <path-to.pck>");
    };

    let pck = misaml_pck::open(path)?;

    log!(
        "Godot {}.{}.{} - profile: {} - {} entries",
        pck.header.godot_version.0,
        pck.header.godot_version.1,
        pck.header.godot_version.2,
        pck.matched_profile.map_or("none", |profile| profile.name),
        pck.entries.len(),
    );

    for entry in &pck.entries {
        println!(
            "{:#010x} {:8} flags={:#x} {}",
            entry.abs_off, entry.size, entry.flags, entry.path,
        );
    }

    Ok(())
}

fn cmd_extract_pck(path: Option<&String>, out_dir: Option<&String>) -> Result<()> {
    let Some(path) = path else {
        bail!("usage: misaml-cli extract-pck <path-to.pck> [out_dir]");
    };

    let out_dir = PathBuf::from(out_dir.map(String::as_str).unwrap_or("extracted"));

    let pck = misaml_pck::open(path)?;
    let key = pck
        .matched_profile
        .context("PCK has no matching profile")?
        .eff_key();

    let mut ok = 0;
    let mut failed = 0;

    for entry in &pck.entries {
        let out_path = out_dir.join(&entry.path);

        if let Some(parent) = out_path.parent() {
            std::fs::create_dir_all(parent)?;
        }

        match misaml_pck::extract(Path::new(path), entry, &key) {
            Ok(data) => {
                std::fs::write(out_path, data)?;
                ok += 1;
            }
            Err(e) => {
                eprintln!("failed to extract {}: {e}", entry.path);
                failed += 1;
            }
        }
    }

    log!("Extracted {ok} files, {failed} failed.");

    Ok(())
}

fn wanted_runtime_file(path: &Path) -> Option<String> {
    let mut parts: Vec<String> = Vec::new();

    for component in path.components() {
        match component {
            Component::CurDir => {} /* archives mod from '.' start with './' */
            Component::Normal(part) => parts.push(part.to_string_lossy().into_owned()),
            _ => return None, /* '..' abs paths */
        }
    }

    match parts.as_slice() {
        [shared, framework, version, name]
            if shared == "shared"
                && framework == "Microsoft.NETCore.App"
                && version == RUNTIME_VERSION
                && name.to_ascii_lowercase().ends_with(".dll") =>
        {
            Some(name.clone())
        }
        _ => None,
    }
}

fn ensure_runtime(payload_dir: &Path) -> Result<()> {
    let runtime_dir = payload_dir.join("runtime");
    let framework_dir = runtime_dir
        .join("shared/Microsoft.NETCore.App")
        .join(RUNTIME_VERSION);

    if framework_dir.is_dir() {
        debug_log!("Runtime {RUNTIME_VERSION} already present!");
        return Ok(());
    }

    log!("Runtime {RUNTIME_VERSION} not found, downloading...");

    let url = format!(
        "https://builds.dotnet.microsoft.com/dotnet/Runtime/{RUNTIME_VERSION}/dotnet-runtime-{RUNTIME_VERSION}-{}",
        platform::RUNTIME_PACKAGE
    );

    // Download into a staging directory first, then rename in place.
    // If the download fails, the final dir never exists, so the next launch
    // retries instead of treating an incomplete download as complete, keep
    // this alongside, not inside, runtime/shared so find_framework_dir never
    // sees this
    let staging_dir = runtime_dir.join(format!("staging-{RUNTIME_VERSION}"));

    if staging_dir.exists() {
        std::fs::remove_dir_all(&staging_dir).context("could not clear an old staging download")?;
    }

    std::fs::create_dir_all(&staging_dir).context("could not create runtime directory")?;

    let kept = match download_runtime(&url, &staging_dir) {
        Ok(kept) => kept,
        Err(e) => {
            let _ = std::fs::remove_dir_all(&staging_dir);
            return Err(e);
        }
    };

    let parent = framework_dir
        .parent()
        .context("runtime folder has no parent directory")?;

    std::fs::create_dir_all(parent)?;
    std::fs::rename(&staging_dir, &framework_dir).with_context(|| {
        format!(
            "could not move the runtime into {}",
            framework_dir.display()
        )
    })?;

    log!("Runtime {RUNTIME_VERSION} ready! ({kept} framework DLLs)");

    Ok(())
}

// downloads the runtime archive and saves JUST the framework DLLs into
// dest_dir, returns how many it kept
fn download_runtime(url: &str, dest_dir: &Path) -> Result<usize> {
    log!("Downloading from {url}...");

    let mut resp = ureq::get(url)
        .call()
        .with_context(|| format!("failed to download .NET runtime from {url}"))?;

    let kept = extract_runtime_archive(resp.body_mut().as_reader(), dest_dir)?;

    // if MS ever changes the archive layout, say so, don't quietly end up
    // with an empty runtime folder
    if kept == 0 {
        bail!(
            "downloaded the .NET runtime, but found no framework DLLs in it \
             (expected shared/Microsoft.NETCore.App/{RUNTIME_VERSION}/*.dll), \
             the archive layout may have changed."
        );
    }

    Ok(kept)
}

fn extract_runtime_archive(reader: impl std::io::Read, dest_dir: &Path) -> Result<usize> {
    let mut kept = 0;

    #[cfg(target_os = "windows")]
    {
        // zip need random access, so this has to be held for it
        let mut reader = reader;
        let mut data = Vec::new();

        reader.read_to_end(&mut data)?;

        let mut zip = zip::ZipArchive::new(std::io::Cursor::new(data))
            .context("failed to open .NET runtime zip archive")?;

        for i in 0..zip.len() {
            let mut file = zip.by_index(i)?;

            if !file.is_file() {
                continue;
            }

            let Some(name) = wanted_runtime_file(Path::new(&*file.name()?)) else {
                continue;
            };

            // read the file into mem so it can be checked before saving
            let mut bytes = Vec::new();
            std::io::Read::read_to_end(&mut file, &mut bytes)
                .with_context(|| format!("could not read {name}"))?;

            // native DLLs (coreclr and others) must not end up in the assembly list
            if !is_managed_assembly(&bytes) {
                continue;
            }

            std::fs::write(dest_dir.join(&name), &bytes)
                .with_context(|| format!("could not write {name}"))?;

            kept += 1;
        }
    }

    #[cfg(target_os = "linux")]
    {
        let mut archive = tar::Archive::new(flate2::read::GzDecoder::new(reader));

        for entry in archive
            .entries()
            .context("failed to read .NET runtime tar.gz archive")?
        {
            let mut entry = entry.context("failed to read an entry of the .NET runtime archive")?;

            if !entry.header().entry_type().is_file() {
                continue;
            }

            let Some(name) = wanted_runtime_file(&entry.path()?) else {
                continue;
            };

            // unpack to a path built, never one from the archive
            entry
                .unpack(dest_dir.join(&name))
                .with_context(|| format!("could not extract {name}"))?;

            kept += 1;
        }
    }

    Ok(kept)
}

// .NET assemblies have CLR header in the PE data dirs, native DLLs do not
#[cfg(target_os = "windows")]
fn is_managed_assembly(bytes: &[u8]) -> bool {
    let rd32 = |off: usize| {
        bytes
            .get(off..off + 4)
            .map(|b| u32::from_le_bytes([b[0], b[1], b[2], b[3]]))
    };

    let Some(nt) = rd32(0x3C).map(|v| v as usize) else {
        return false;
    };
    if rd32(nt) != Some(0x0000_4550) {
        return false; // not "PE\0\0"
    }

    // 64-bit PE, CLR header is data directory entry 14
    rd32(nt + 0xF8).is_some_and(|rva| rva != 0)
}
