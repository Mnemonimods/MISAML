# Building MISAML

**The game is not required to build, only to run MISAML.**

## What you need

**Linux:** [Rust](https://rustup.rs) (1.85 or higher), the [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0), and a C compiler (`build-essential` on Debian/Ubuntu, `base-devel` on Arch).

**Windows:** [Rust](https://rustup.rs) (use the default MSVC option), the [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0), and [Visual Studio Build Tools](https://visualstudio.microsoft.com/downloads/) with the "Desktop development with C++" workload ticked.

Check with `rustc --version` and `dotnet --version` (8.x).

## Build

**Linux:**

```
./build-linux.sh build
```

**Windows (PowerShell):**

```
.\build-windows.ps1
```

Both scripts build the Rust and C# parts, merge the C# dependencies into a single `MISAML.dll`, and put everything in `dist/`:

```
misaml-cli(.exe)
MISAML/
    libmisaml_shim.so (Linux only)
    misaml_shim.dll (Windows only)
    MISAML.dll
    MISAML.GodotAPI.dll
```

## Run

Run `./dist/misaml-cli` (or `.\dist\misaml-cli.exe` in PowerShell). The first launch downloads the .NET runtime it needs into `MISAML/runtime/`, and creates an example `misaml.toml`. If the game isn't found automatically, set `game_dir` in `misaml.toml`.

## Other script commands (Linux)

`./build-linux.sh run` starts the launcher. `clean`, `clean-rust`, `clean-csharp`, `clean-runtime` and `clean-all` remove build output. If a build misbehaves after switching branches, run `clean-csharp` and build again.

The Windows script keeps `misaml.toml` and `MISAML/runtime` in `dist/` between builds. Add `-Run` to start the launcher after building.

## Cross-compiling for Windows from Linux

Install MinGW (Arch: `mingw-w64-gcc`, Debian/Ubuntu: `gcc-mingw-w64-x86-64`), then:

```
rustup target add x86_64-pc-windows-gnu
cargo build --release --target x86_64-pc-windows-gnu
```

The Windows files land in `target/x86_64-pc-windows-gnu/release/` (`misaml-cli.exe` and `misaml_shim.dll`). Build the C# part with `./build-linux.sh build` (the DLLs are the same on every OS), then put the files together as in the layout above.