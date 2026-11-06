#!/usr/bin/env python3
"""Copies a sweep result folder keeping only what `regionizer-sim memo` reads: sweep.json and cells.csv at
the top, and manifest.json, summary.json and any _incomplete marker in each run folder.

`harness load` loads a sweep's sweep_cells rows from sweep.json and cells.csv alone, but it also loads every
run folder that exists, and a trimmed run folder has no ticks.csv. Load a copy without the run folders (as
docs/simulator/results/ holds them).

With --partial (a sweep cut short), a folder without sweep.json is accepted: the runs that finished are kept.

Usage: trim.py [--partial] <sweep-results-dir> <out-dir>
"""
import os
import shutil
import sys

KEEP_TOP = {"sweep.json", "cells.csv"}
# _incomplete marks a run that never finished; the memo refuses such a run, so the marker must survive.
KEEP_RUN = {"manifest.json", "summary.json", "_incomplete"}


def trim(source, target):
    copied = 0
    for root, _, files in os.walk(source):
        top = os.path.abspath(root) == os.path.abspath(source)
        for name in files:
            if name in (KEEP_TOP if top else KEEP_RUN):
                destination = os.path.join(target, os.path.relpath(root, source))
                os.makedirs(destination, exist_ok=True)
                shutil.copy2(os.path.join(root, name), destination)
                copied += 1
    return copied


def main(argv):
    partial = "--partial" in argv
    args = [a for a in argv[1:] if a != "--partial"]
    if len(args) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    source, target = args
    if not os.path.isdir(source) or (not partial and not os.path.isfile(os.path.join(source, "sweep.json"))):
        print(f"{source} is not a sweep results folder (no sweep.json)", file=sys.stderr)
        return 2
    if os.path.isdir(target) and os.listdir(target):
        # Merging into an earlier result would leave its stale run folders next to the new ones.
        print(f"{target} is not empty; trim into a new folder", file=sys.stderr)
        return 2
    print(f"kept {trim(source, target)} files")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
