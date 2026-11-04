# shellcheck shell=bash
# Shared helpers for the harness scripts: build each CLI once, with its output on stderr so compile errors
# reach the log, then run the built DLL directly. Sourced, never run.
#
# HARNESS_BUILD_DIR  where the CLIs are built (default: a cache folder under $XDG_CACHE_HOME or ~/.cache)
HARNESS_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
HARNESS_BUILD_DIR="${HARNESS_BUILD_DIR:-${XDG_CACHE_HOME:-$HOME/.cache}/mmo-harness/build}"

# harness_build <harness|sim>: builds that CLI into $HARNESS_BUILD_DIR/<name>, once per script run.
harness_build() {
  local name="$1" project
  case "$name" in
    harness) project="$HARNESS_ROOT/src/MmoEngine.Harness.Cli" ;;
    sim) project="$HARNESS_ROOT/src/MmoEngine.Regionizer.Simulator.Cli" ;;
    *) echo "harness_build: unknown CLI $name" >&2; return 2 ;;
  esac
  local built="HARNESS_BUILT_$name"
  [ -n "${!built:-}" ] && return 0
  dotnet build "$project" -c Release -nologo -v quiet -o "$HARNESS_BUILD_DIR/$name" >&2 || {
    echo "building $project failed (the compiler output is above)" >&2
    return 1
  }
  printf -v "$built" '%s' 1
}

# The harness CLI (`harness ...`) and the simulator CLI (`sim ...`), built on first use.
harness() { harness_build harness && dotnet "$HARNESS_BUILD_DIR/harness/harness.dll" "$@"; }
sim() { harness_build sim && dotnet "$HARNESS_BUILD_DIR/sim/regionizer-sim.dll" "$@"; }
