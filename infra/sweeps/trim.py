#!/usr/bin/env python3
"""Copies a sweep result folder keeping only what `regionizer-sim memo` and `harness load-sweeps` read:
sweep.json and cells.csv at the top, and manifest.json and summary.json in each run folder.

Usage: trim.py <sweep-results-dir> <out-dir>
"""
import os
import shutil
import sys

KEEP_TOP = {"sweep.json", "cells.csv"}
KEEP_RUN = {"manifest.json", "summary.json"}


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
    if len(argv) != 3:
        print(__doc__, file=sys.stderr)
        return 2
    if not os.path.isfile(os.path.join(argv[1], "sweep.json")):
        print(f"{argv[1]} is not a sweep results folder (no sweep.json)", file=sys.stderr)
        return 2
    print(f"kept {trim(argv[1], argv[2])} files")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
