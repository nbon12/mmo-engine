#!/usr/bin/env python3
"""One step of calibrate.sh's search for per-direction netem settings (spec 014, FR-026, research R7).

Usage: calibration_search.py <probe-result.json> <target-rtt-ms> <target-jitter-ms> <delay-ms> <jitter-ms>

Reads the result `harness probe run` printed for a short run with the given per-direction delay and
jitter. Prints "done <delay> <jitter>" when the round trip and jitter are within tolerance, or
"next <delay> <jitter>" with the settings to try next:

- jitter scales by target / measured, limited to x0.5..x2 per step, because keeping packets in order
  clips the delay distribution and netem's jitter doesn't map one to one onto round-trip jitter;
- delay moves by half the round-trip error, never below zero.

Loss and reordering aren't searched: netem's loss is exact in expectation, and reordering follows from
the profile.
"""
import json
import sys


def step(result, target_rtt, target_jitter, delay, jitter):
    timing = [f for f in result.get("failures", []) if f.startswith(("round trip", "jitter"))]
    if not timing:
        return "done", delay, jitter
    median = float(result["medianRoundTripMs"])
    measured = float(result["jitterMs"])
    if target_jitter > 0 and any(f.startswith("jitter") for f in timing):
        ratio = target_jitter / max(measured, 0.01)
        jitter = jitter * min(2.0, max(0.5, ratio))
    if any(f.startswith("round trip") for f in timing):
        delay = max(0.0, delay + (target_rtt - median) / 2)
    return "next", delay, jitter


def main(argv):
    if len(argv) != 6:
        print(__doc__, file=sys.stderr)
        return 2
    lines = [line for line in open(argv[1], encoding="utf-8").read().splitlines() if line.startswith("{")]
    result = json.loads(lines[-1])
    verdict, delay, jitter = step(result, float(argv[2]), float(argv[3]), float(argv[4]), float(argv[5]))
    print(f"{verdict} {delay:.3f} {jitter:.3f}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
