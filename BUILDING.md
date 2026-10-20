**The game is not required to build, only to run MISAML.**

## Dependencies

**Linux:** [Rust](https://rustup.rs), [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0), and a C compiler (`build-essential` on Debian/Ubuntu, `base-devel` on Arch).

**Windows:** [Rust](https://rustup.rs) (use the default MSVC option), the [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0), and [Visual Studio Build Tools](https://visualstudio.microsoft.com/downloads/) with the "Desktop development with C++" workload ticked.

Check `rustc --version` (1.85 or higher) and `dotnet --version` (8.x).

## Building Rust

`cargo build --release`

`misaml-cli` and the shim are build to `target/release/`.

Linux should have `misaml-cli` and `libmisaml_shim.so`, Windows should have `misaml-cli.exe` and `misaml_shim.dll`.

## Building C#

`dotnet publish -c Release` from the root.

## Merge the dependencies into single DLLs

ILRepack should come down with the publish step find it using:

Linux: `find ~/.nuget/packages/ilrepack -name ILRepack.exe`

Windows (PowerShell): `Get-ChildItem "$env:USERPROFILE\.nuget\packages\ilrepack" -Recurse -Filter ILRepack.exe`

Call the full path (ILREPACK) for the following commands, after making an output folder `build/csharp`:

```
dotnet ILREPACK /target:library /lib:MISAML/Bootstrap/bin/Release/net8.0/publish \
    /out:build/csharp/MISAML.Bootstrap.dll \
    MISAML/Bootstrap/bin/Release/net8.0/publish/MISAML.Bootstrap.dll \
    MISAML/Bootstrap/bin/Release/net8.0/publish/Tomlyn.dll \
    MISAML/Bootstrap/bin/Release/net8.0/publish/Semver.dll \
    MISAML/Bootstrap/bin/Release/net8.0/publish/Microsoft.Extensions.Primitives.dll
```

```
dotnet ILREPACK /target:library /lib:MISAML/vendor \
    /out:build/csharp/MISAML.dll \
    MISAML/bin/Release/net8.0/publish/MISAML.dll \
    MISAML/bin/Release/net8.0/publish/0Harmony.dll
```

Then copy `MISAML/GodotAPI/bin/Release/net8.0/publish/MISAML.GodotAPI.dll` into `build/csharp/`.

## Put it all together

Make a folder (e.g. `/dist`) like this:

```
misaml-cli(.exe)
MISAML/
    libmisaml_shim.so (Only on Linux)
    misaml_shim.dll (Only on Windows)
    MISAML.dll (from build/csharp/)
    MISAML.Bootstrap.dll (from build/csharp/)
    MISAML.GodotAPI.dll (from build/csharp/)
```

## Run

Run `./misaml-cli` or `misaml-cli.exe` (in PowerShell) from the `dist` folder. On first launch it will download the required .NET runtime into `MISAML/runtime/`. If the game isn't found automatically, set `game_dir` in `misaml.toml`. Starting `misaml-cli` for the first time will create an example `misaml.toml`.

## Cross-compiling for Windows from Linux

Install MinGW (Arch: `mingw-w64-gcc`, Debian/Ubuntu: `gcc-mingw-w64-x86-64`), then:

```
rustup target add x86_64-pc-windows-gnu
cargo build --release --target x86_64-pc-windows-gnu
```

The Windows files land in `target/x86_64-pc-windows-gnu/release/`. Do steps 3 to 6 as above, using `misaml-cli.exe` and `misaml_shim.dll`. The C# DLLs are the same on every OS.