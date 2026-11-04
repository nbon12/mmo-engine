#!/usr/bin/env bash
# Golden-run determinism check (spec 014, US3, FR-018 to FR-020).
#
# For every config in harness/golden/<name>/:
#   1. Run the simulator twice, in separate processes, on this machine (one runner class).
#      The first run is recorded as a transient reference in a temp folder; the second must
#      match it tick by tick (`harness golden check`, exit 0). Any divergence fails (exit 7).
#      This is what proves "the same seed gives the same per-tick state hash" on the runner
#      class the job runs on. Nothing is written into harness/golden/ (FR-020).
#   2. If a committed recording (golden.json) exists, compare the second run with it too:
#      0 passes, 7 (diverged) fails, 8 (recorded on another runner class) is a warning,
#      because hosted runners are not one CPU model (research R5, R9).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK="$(mktemp -d)"
# The transient references are discarded when the script ends (FR-020).
trap 'rm -rf "$WORK"' EXIT
sim() { dotnet run --project "$ROOT/src/MmoEngine.Regionizer.Simulator.Cli" -c Release -- "$@"; }
harness() { dotnet run --project "$ROOT/src/MmoEngine.Harness.Cli" -c Release -- "$@"; }
status=0
# Keep the most serious status: a divergence (7) outranks any other failure.
fail() { if [ "$1" -eq 7 ] || [ "$status" -eq 0 ]; then status="$1"; fi; }

for dir in "$ROOT"/harness/golden/*/; do
  name="$(basename "$dir")"
  set +e
  sim run "$dir/config.json" --out "$WORK/$name-reference" >/dev/null &&
    sim run "$dir/config.json" --out "$WORK/$name-replay" >/dev/null &&
    harness golden record --run "$WORK/$name-reference" --config "$dir/config.json" --out "$WORK/$name-golden" --name "$name" >/dev/null
  code=$?
  set -e
  if [ "$code" -ne 0 ]; then
    echo "::error::golden $name: running or recording the reference failed (exit $code)"
    fail 1
    continue
  fi

  set +e
  harness golden check --golden "$WORK/$name-golden" --run "$WORK/$name-replay"
  code=$?
  set -e
  case "$code" in
    0) echo "golden $name: two runs on this runner class match tick for tick" ;;
    7) echo "::error::golden $name: two runs of the same seed diverged on one machine"; fail 7 ;;
    *) echo "::error::golden $name: the replay check failed with exit $code"; fail "$code" ;;
  esac

  if [ -f "$dir/golden.json" ]; then
    set +e
    harness golden check --golden "$dir" --run "$WORK/$name-replay"
    code=$?
    set -e
    case "$code" in
      0) echo "golden $name: matches the committed recording" ;;
      7) echo "::error::golden $name diverged from the committed recording"; fail 7 ;;
      8) echo "::warning::golden $name: the committed recording is from another runner class; not compared" ;;
      *) echo "::error::golden $name: the committed-recording check failed with exit $code"; fail "$code" ;;
    esac
  fi
done
exit "$status"
