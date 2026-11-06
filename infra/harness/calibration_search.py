#!/usr/bin/env python3
"""One step of calibrate.sh's search for per-direction netem settings (spec 014, FR-026, research R7).

Usage: calibration_search.py <probe-result.json> <target-rtt-ms> <target-jitter-ms> <delay-ms> <jitter-ms>

Reads the result `harness probe run` printed for a short run with the given per-direction delay and
jitter. Prints "<verdict> <delay> <jitter> <error>":

- "done" with the same settings when the round trip and jitter are within the search's own band;
- "next" with the settings to try next;
- "fail" when the run gave no usable measurement (no result line, or no echoes, so no round trip).

<error> scores the settings that were measured (the larger relative miss of round trip and jitter), so the
caller can fall back to the best measured settings when the search runs out of steps. The band is half the calibration tolerance
(5% of target, or 0.25 ms for a round trip), because a short run is noisier than the full run that
checks the result: a search that stopped just inside tolerance failed its full run (Cross-region, run
42123464780: 60 s within tolerance, then 22.17 ms against 25 ± 2.5 over 5 minutes).

- jitter scales by target / measured, limited to x0.5..x2 per step, because keeping packets in order
  clips the delay distribution and netem's jitter doesn't map one to one onto round-trip jitter;
- delay moves by half the round-trip error, never below zero.

Loss and reordering aren't searched: netem's loss is exact in expectation, and reordering follows from
the profile.
"""
import json
import sys


BAND = 0.05
RTT_FLOOR_MS = 0.25


def error(result, target_rtt, target_jitter):
    """The larger relative miss of the round trip and the jitter (absolute ms for a zero target)."""
    median = float(result["medianRoundTripMs"])
    measured = float(result["jitterMs"])
    rtt_miss = abs(median - target_rtt) / target_rtt if target_rtt > 0 else abs(median)
    jitter_miss = abs(measured - target_jitter) / target_jitter if target_jitter > 0 else 0.0
    return max(rtt_miss, jitter_miss)


def step(result, target_rtt, target_jitter, delay, jitter):
    timing = [f for f in result.get("failures", []) if f.startswith(("round trip", "jitter"))]
    median = float(result["medianRoundTripMs"])
    measured = float(result["jitterMs"])
    jitter_off = target_jitter > 0 and abs(measured - target_jitter) > BAND * target_jitter
    rtt_off = abs(median - target_rtt) > max(BAND * target_rtt, RTT_FLOOR_MS)
    if not timing and not jitter_off and not rtt_off:
        return "done", delay, jitter
    if jitter_off or any(f.startswith("jitter") for f in timing):
        ratio = target_jitter / max(measured, 0.01)
        jitter = jitter * min(2.0, max(0.5, ratio)) if target_jitter > 0 else 0.0
    if rtt_off or any(f.startswith("round trip") for f in timing):
        delay = max(0.0, delay + (target_rtt - median) / 2)
    return "next", delay, jitter


def main(argv):
    if len(argv) != 6:
        print(__doc__, file=sys.stderr)
        return 2
    target_rtt, target_jitter, delay, jitter = (float(a) for a in argv[2:6])
    try:
        lines = [line for line in open(argv[1], encoding="utf-8").read().splitlines() if line.startswith("{")]
        result = json.loads(lines[-1])
        score = error(result, target_rtt, target_jitter)
        verdict, delay_next, jitter_next = step(result, target_rtt, target_jitter, delay, jitter)
    except (OSError, IndexError, KeyError, TypeError, ValueError):
        # No result line, or no echoes (the round trip and jitter are null): nothing to search from.
        print(f"fail {delay:.3f} {jitter:.3f} inf")
        return 0
    print(f"{verdict} {delay_next:.3f} {jitter_next:.3f} {score:.4f}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
