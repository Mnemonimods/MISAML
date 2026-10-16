#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"

log() { echo "[build] $*"; }
err() { echo "[build] ERROR: $*" >&2; }

SHIM="target/release/libmisaml_shim.so"
CLI="target/release/misaml-cli"
CORE_PUBLISH="MISAML/bin/Release/net8.0/publish"
BOOTSTRAP_PUBLISH="MISAML/Bootstrap/bin/Release/net8.0/publish"
GODOTAPI_PUBLISH="MISAML/GodotAPI/bin/Release/net8.0/publish"

compile() {
    log "Building Rust..."
    cargo build --release

    log "Publishing MISAML.Bootstrap..."
    (cd MISAML/Bootstrap && dotnet publish -c Release)

    log "Publishing MISAML..."
    (cd MISAML && dotnet publish -c Release)

    log "Publishing MISAML.GodotAPI..."
    (cd MISAML/GodotAPI && dotnet publish -c Release)

    for f in "$SHIM" "$CLI"; do
        [ -f "$f" ] || { err "Missing: $f"; exit 1; }
    done

    [ -d "$CORE_PUBLISH" ] || { err "Missing: $CORE_PUBLISH"; exit 1; }
    [ -d "$BOOTSTRAP_PUBLISH" ] || { err "Missing: $BOOTSTRAP_PUBLISH"; exit 1; }
    [ -d "$GODOTAPI_PUBLISH" ] || { err "Missing: $GODOTAPI_PUBLISH"; exit 1; }
}

merge_dll() {
    local ilrepack
    ilrepack=$(find "$HOME/.nuget/packages/ilrepack" -name "ILRepack.exe" | sort -V | tail -1)
    [ -n "$ilrepack" ] || { err "ILRepack.exe not found in NuGet cache, did 'dotnet add package ILRepack' run inside MISAML?"; exit 1; }

    log "Merging 0Harmony.dll into MISAML.dll..."
    dotnet "$ilrepack" \
        /target:library \
        /lib:MISAML/vendor \
        /out:dist/MISAML/MISAML.dll \
        "$CORE_PUBLISH/MISAML.dll" \
        "$CORE_PUBLISH/0Harmony.dll"
}

stage_bootstrap() {
    local ilrepack
    ilrepack=$(find "$HOME/.nuget/packages/ilrepack" -name "ILRepack.exe" | sort -V | tail -1)
    [ -n "$ilrepack" ] || { err "ILRepack.exe not found in NuGet cache, did 'dotnet add package ILRepack' run inside MISAML?"; exit 1; }

    log "Merging Tomlyn.dll into MISAML.Bootstrap.dll..."
    dotnet "$ilrepack" \
        /target:library \
        /lib:"$BOOTSTRAP_PUBLISH" \
        /out:dist/MISAML/MISAML.Bootstrap.dll \
        "$BOOTSTRAP_PUBLISH/MISAML.Bootstrap.dll" \
        "$BOOTSTRAP_PUBLISH/Tomlyn.dll" \
        "$BOOTSTRAP_PUBLISH/Semver.dll"
}

stage_binaries() {
    mkdir -p dist/MISAML
    cp "$CLI" dist/
    cp "$SHIM" dist/MISAML/
    cp "$GODOTAPI_PUBLISH/MISAML.GodotAPI.dll" dist/MISAML/
    stage_bootstrap
    merge_dll
}

do_build() {
    compile

    log "Staging dist..."
    mkdir -p dist/MISAML
    rm -f dist/misaml-cli dist/MISAML/libmisaml_shim.so dist/MISAML/MISAML.dll dist/MISAML/MISAML.Bootstrap.dll dist/MISAML/MISAML.GodotAPI.dll
    stage_binaries

    log "dist/ ready:"
    find dist -maxdepth 4 | sort

    log "Run with: ./dist/misaml-cli"
}

do_rebuild() {
    [ -d "dist" ] || {
        err "dist/ not found. Run './build.sh build' first."
        exit 1
    }

    compile

    log "Restaging binaries..."
    stage_binaries

    log "Rebuild complete:"
    find dist -maxdepth 4 | sort

    log "Run with: ./dist/misaml-cli"
}

do_run() {
    [ -x "./dist/misaml-cli" ] || {
        err "./dist/misaml-cli not found. Run './build.sh build' first."
        exit 1
    }

    ./dist/misaml-cli
}

do_clean_rust() {
    log "Cleaning Rust build artifacts..."
    cargo clean
}

do_clean_csharp() {
    log "Cleaning C# build artifacts..."
    rm -rf MISAML/bin MISAML/obj
    rm -rf MISAML/Bootstrap/bin MISAML/Bootstrap/obj
    rm -rf MISAML/GodotAPI/bin MISAML/GodotAPI/obj
}

do_clean() {
    do_clean_rust
    do_clean_csharp
}

do_clean_runtime() {
    log "Cleaning bundled .NET runtime..."
    rm -rf dist/MISAML/runtime
}

do_clean_all() {
    do_clean
    rm -rf dist
}

usage() {
    cat <<EOF
Usage: ./build.sh <command>

Commands:
  build          Build Rust + MISAML and stage dist/
  rebuild        Rebuild and restage binaries
  run            Launch ./dist/misaml-cli
  clean          Remove all code build artifacts (clean-rust + clean-csharp)
  clean-rust     Remove Rust build artifacts (target/)
  clean-csharp   Remove C# build artifacts (MISAML/bin, MISAML/obj, MISAML/Bootstrap/bin, MISAML/Bootstrap/obj, etc.)
  clean-runtime  Remove the bundled .NET runtime
  clean-all      Remove everything (clean + dist/)
EOF
}

case "${1:-}" in
    build)          do_build ;;
    rebuild)        do_rebuild ;;
    run)            do_run ;;
    clean)          do_clean ;;
    clean-rust)     do_clean_rust ;;
    clean-csharp)   do_clean_csharp ;;
    clean-runtime)  do_clean_runtime ;;
    clean-all)      do_clean_all ;;
    *)              usage; exit 1 ;;
esac