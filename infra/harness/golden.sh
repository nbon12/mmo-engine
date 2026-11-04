#!/usr/bin/env bash
# Replays every golden run in harness/golden/ and compares it tick by tick (spec 014, US3, FR-019).
#   - golden.json present: `harness golden check`. Exit 0 match, 7 diverged (fails), 8 not comparable.
#   - Not comparable, or golden.json missing: records a candidate on this runner class into
#     $GOLDEN_CANDIDATES/<name>/ (default out/golden-candidates), for CI to upload and an agent to commit.
#     That is reported as a warning, never as a pass of the comparison.
# Recording is never written into harness/golden/ by this script (FR-020).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CANDIDATES="${GOLDEN_CANDIDATES:-$ROOT/out/golden-candidates}"
WORK="$(mktemp -d)"
sim() { dotnet run --project "$ROOT/src/MmoEngine.Regionizer.Simulator.Cli" -c Release -- "$@"; }
harness() { dotnet run --project "$ROOT/src/MmoEngine.Harness.Cli" -c Release -- "$@"; }
status=0
for dir in "$ROOT"/harness/golden/*/; do
  name="$(basename "$dir")"
  sim run "$dir/config.json" --out "$WORK/$name" >/dev/null
  if [ -f "$dir/golden.json" ]; then
    set +e
    harness golden check --golden "$dir" --run "$WORK/$name"
    code=$?
    set -e
    case "$code" in
      0) echo "golden $name: match" ;;
      7) echo "::error::golden $name diverged (see the report above)"; status=7 ;;
      8) echo "::warning::golden $name was recorded on another runner class; recording a candidate for this one"
         harness golden record --run "$WORK/$name" --config "$dir/config.json" --out "$CANDIDATES/$name" --name "$name" >/dev/null ;;
      *) echo "::error::golden $name check failed with exit $code"; status=$code ;;
    esac
  else
    echo "::warning::golden $name has no recording yet; recording a candidate on this runner class"
    harness golden record --run "$WORK/$name" --config "$dir/config.json" --out "$CANDIDATES/$name" --name "$name" >/dev/null
  fi
done
# Also print each candidate into the log, so it can be committed from the log when the artifact store can't
# be reached (one line: a gzip tarball in base64, between markers).
if [ -d "$CANDIDATES" ]; then
  for candidate in "$CANDIDATES"/*/; do
    [ -d "$candidate" ] || continue
    name="$(basename "$candidate")"
    echo "golden-candidate-begin $name"
    tar -czf - -C "$CANDIDATES" "$name" | base64 -w0
    echo
    echo "golden-candidate-end $name"
  done
fi
exit "$status"
