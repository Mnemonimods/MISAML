# MISAML

MISAML is a mod loader for [Mneminomov](https://store.steampowered.com/app/3854110/Mnemonimov/), a game based on low-level programming with a fantasy console.

MISAML lets you hook into and modify almost any function within the game, whether it be syscalls, the lexer, and more, using either C# or GDScript, without modifying any of the games files. Examples of what have been done so far are adding sound, colour, and additional syscalls to the game, as well as custom GUI's on top of the game, and a headless interface for the virtual machine.

## What you get

- Mods are plain folders in `mods/` (next to the game executable). Each mod has a manifest, `mod.json`.
- Mods can depend on other mods (with semantic version ranges), with dependency resolving, and helpful logs to tell you when your dependencies are out of order. In the future there will be automatic updating, and more.
- The game's own files are never modified, everything is patched at runtime with Harmony.

## Requirements

To build it yourself:

- **Rust**, tested on rustc 1.99.0 (the project uses edition 2024, which needs at least 1.85).
- **.NET SDK** (8.0 is required, 10.0 will not work, this is due to Mnemonimov being built on .NET 8.0).
- **C compiler** (`gcc` or `clang` on Linux). Required for misaml-shim.
- **An internet connection for the first build/run**, MISAML automatically downloads the .NET runtime required for the modloader to function. Also required for some NuGet packages (namely ILRepack).

On Windows you also need:

- **Visual Studio Build Tools** with the "Desktop development with C++" workload.

Building the Windows version from Linux (cross-compiling) also needs MinGW (`x86_64-w64-mingw32-gcc`).

## Build

Linux:

```sh
./build-linux.sh build
```

Windows (PowerShell):

```powershell
.\build-windows.ps1
```

Both output to `dist/`. The contents of this folder can be moved next to your game executable, `mnemonimov.exe` (Windows) or `mneminomov.x86_64` (Linux).

## Run

Start the game through the launcher, not through Steam:

```sh
./misaml-cli
```

(this may require `chmod +x ./misaml-cli` to make the file executable)

or, Windows:

```powershell
.\misaml-cli.exe
```

This will work even if not placed next to the executable, but placing it next to the executable is preferred.

If the game files have changed (likely due to an update), and it refuses to start, launch with the `--force` flag (can also be enabled in the config file) to launch anyways at your own risk.

## Configuration

`misaml.toml` sits next to the launcher, and it is the configuration file.

- `[mods]` turns each mod on or off by its id.
- A mod can have its own priority and a version pin (lower priority numbers load first).
- `game_dir` sets the game folder by hand, if Steam is not found.
- `debug` and `force` do the same as the launcher flags.

## Mods

Put a mod folder in `/mods/`:

```
dist/mods/
  examplemod/
    mod.json
    examplemod.dll
    main.gd
```

Then enable it in `misaml.toml`. See the modding documentation (WIP) for how to write one. If not enabled manually, new mods will be automatically enabled on launch, and added to `misaml.toml`.

## Logs and debugging

- `misaml.log`, next to `MISAML.dll`, is the loader's log.
- Set `MISAML_DEBUG=1` for more detail.

## Things to know

- **Mods are not sandboxed.** A mod runs with your user's permissions, the same as any program you start. Only install mods you trust.
- **Antivirus.** On Windows the launcher starts the game and loads a DLL into it. Some antivirus software flags that pattern. If yours does, it is a false positive, but check the source if you are unsure.
- The loader only uses the network to install the runtime, after that it will not use the network. If anything is using the network, check your mods.

## Status

0.1.0 is the first release. Loading, dependencies and priorities work. A command-line mod installer, a shared mod API and more are planned, none of it is in this release.