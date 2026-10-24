#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")"

log() { echo "[build] $*"; }
err() { echo "[build] ERROR: $*" >&2; }

LINUX_SHIM="target/release/libmisaml_shim.so"
LINUX_CLI="target/release/misaml-cli"

CORE_PUBLISH="MISAML/bin/Release/net8.0/publish"
BOOTSTRAP_PUBLISH="MISAML/Bootstrap/bin/Release/net8.0/publish"
GODOTAPI_PUBLISH="MISAML/GodotAPI/bin/Release/net8.0/publish"

CSHARP_OUT="build/csharp"
CSHARP_DLLS=(MISAML.dll MISAML.Bootstrap.dll MISAML.GodotAPI.dll)

DOTNET_ARGS=()

need_files() {
    for f in "$@"; do
        [ -f "$f" ] || { err "Missing: $f"; exit 1; }
    done
}

find_ilrepack() {
    local ilrepack
    ilrepack=$(find "$HOME/.nuget/packages/ilrepack" -name "ILRepack.exe" 2>/dev/null | sort -V | tail -1 || true)
    [ -n "$ilrepack" ] || { err "ILRepack.exe not found in NuGet cache, did 'dotnet add package ILRepack' run inside MISAML?"; exit 1; }
    echo "$ilrepack"
}

compile_csharp() {
    log "Publishing MISAML.Bootstrap..."
    (cd MISAML/Bootstrap && dotnet publish -c Release "${DOTNET_ARGS[@]}")

    log "Publishing MISAML..."
    (cd MISAML && dotnet publish -c Release "${DOTNET_ARGS[@]}")

    log "Publishing MISAML.GodotAPI..."
    (cd MISAML/GodotAPI && dotnet publish -c Release "${DOTNET_ARGS[@]}")

    for d in "$CORE_PUBLISH" "$BOOTSTRAP_PUBLISH" "$GODOTAPI_PUBLISH"; do
        [ -d "$d" ] || { err "Missing: $d"; exit 1; }
    done

    local ilrepack
    ilrepack=$(find_ilrepack)

    rm -rf "$CSHARP_OUT"
    mkdir -p "$CSHARP_OUT"
    cp "$GODOTAPI_PUBLISH/MISAML.GodotAPI.dll" "$CSHARP_OUT/"

    log "Merging Tomlyn.dll, Semver.dll and Microsoft.Extensions.Primitives into MISAML.Bootstrap.dll..."
    dotnet "$ilrepack" \
        /target:library \
        /lib:"$BOOTSTRAP_PUBLISH" \
        /out:"$CSHARP_OUT/MISAML.Bootstrap.dll" \
        "$BOOTSTRAP_PUBLISH/MISAML.Bootstrap.dll" \
        "$BOOTSTRAP_PUBLISH/Tomlyn.dll" \
        "$BOOTSTRAP_PUBLISH/Semver.dll" \
        "$BOOTSTRAP_PUBLISH/Microsoft.Extensions.Primitives.dll"

    log "Merging 0Harmony.dll into MISAML.dll..."
    dotnet "$ilrepack" \
        /target:library \
        /lib:MISAML/vendor \
        /out:"$CSHARP_OUT/MISAML.dll" \
        "$CORE_PUBLISH/MISAML.dll" \
        "$CORE_PUBLISH/0Harmony.dll"

    for dll in "${CSHARP_DLLS[@]}"; do
        need_files "$CSHARP_OUT/$dll"
    done
}

compile_rust() {
    log "Building Rust..."
    cargo build --release
    need_files "$LINUX_SHIM" "$LINUX_CLI"
}

stage() {
    log "Staging dist..."
    mkdir -p dist/MISAML
    cp "$LINUX_CLI" dist/
    cp "$LINUX_SHIM" dist/MISAML/
    for dll in "${CSHARP_DLLS[@]}"; do
        cp "$CSHARP_OUT/$dll" dist/MISAML/
    done
}

do_build() {
    compile_csharp
    compile_rust
    stage

    log "dist/ ready, run with: ./dist/misaml-cli"
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
    rm -rf build
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
  build          Build everything, into dist/
  run            Launch ./dist/misaml-cli
  clean          Remove all code build artifacts (clean-rust + clean-csharp)
  clean-rust     Remove Rust build artifacts (target/)
  clean-csharp   Remove C# build artifacts (bin/, obj/ of each project, and build/)
  clean-runtime  Remove the bundled .NET runtime
  clean-all      Remove everything (clean + dist/)
EOF
}

case "${1:-}" in
    build)          do_build ;;
    run)            do_run ;;
    clean)          do_clean ;;
    clean-rust)     do_clean_rust ;;
    clean-csharp)   do_clean_csharp ;;
    clean-runtime)  do_clean_runtime ;;
    clean-all)      do_clean_all ;;
    *)              usage; exit 1 ;;
esac