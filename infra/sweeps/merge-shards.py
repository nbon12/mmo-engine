#!/usr/bin/env python3
"""Merges the shards of one sweep (`regionizer-sim sweep --shard k/n`) into a single sweep folder.

Each shard's sweep.json lists every cell but only its own runs, its cells.csv has only its own rows, and
its run folders (<cellId>/seed-<i>) never collide with another shard's. The merged folder has one
sweep.json listing every run, one cells.csv with every row in cell and seed order, and every run folder.
That is the folder `regionizer-sim memo` and `harness load` read.

The shards must be of the same sweep (name, scenario, mode, sweep seed, seeds and cells), and no run may
appear in two shards.

Usage: merge-shards.py <out-dir> <shard-sweep-dir> [<shard-sweep-dir> ...]
"""
import csv
import json
import os
import shutil
import sys

IDENTITY = ("formatVersion", "name", "scenario", "mode", "sweepSeed", "seeds")


class MergeError(Exception):
    pass


def merge(shards, out):
    if os.path.isdir(out) and os.listdir(out):
        raise MergeError(f"{out} is not empty; merge into a new folder")
    indexes = []
    for shard in shards:
        path = os.path.join(shard, "sweep.json")
        if not os.path.isfile(path):
            raise MergeError(f"{shard} is not a sweep folder (no sweep.json)")
        with open(path, encoding="utf-8") as f:
            indexes.append(json.load(f))

    first = indexes[0]
    merged = {key: first[key] for key in IDENTITY}
    cells = {c["cellId"]: {"cellId": c["cellId"], "parameters": c["parameters"], "runs": {}} for c in first["cells"]}
    for shard, index in zip(shards, indexes):
        for key in IDENTITY:
            if index[key] != first[key]:
                raise MergeError(f"{shard}: {key} differs from {shards[0]}; not shards of one sweep")
        if [c["cellId"] for c in index["cells"]] != list(cells):
            raise MergeError(f"{shard}: its cells differ from {shards[0]}")
        for cell in index["cells"]:
            if cell["parameters"] != cells[cell["cellId"]]["parameters"]:
                raise MergeError(f"{shard}: cell {cell['cellId']} has other parameters than in {shards[0]}")
            for run in cell["runs"]:
                runs = cells[cell["cellId"]]["runs"]
                if run["seedIndex"] in runs:
                    raise MergeError(f"run {run['folder']} is in two shards")
                runs[run["seedIndex"]] = (run, shard)

    merged["cells"] = [
        {"cellId": c["cellId"], "parameters": c["parameters"], "runs": [r for r, _ in (c["runs"][i] for i in sorted(c["runs"]))]}
        for c in cells.values()
    ]

    os.makedirs(out, exist_ok=True)
    header, rows = None, []
    for shard in shards:
        path = os.path.join(shard, "cells.csv")
        if not os.path.isfile(path):
            continue
        with open(path, newline="", encoding="utf-8") as f:
            reader = csv.reader(f)
            shard_header = next(reader)
            if header is None:
                header = shard_header
            elif shard_header != header:
                raise MergeError(f"{shard}/cells.csv: its columns differ from the other shards'")
            rows.extend(reader)
    for c in cells.values():
        for run, shard in c["runs"].values():
            source = os.path.join(shard, run["folder"])
            if os.path.isdir(source):
                shutil.copytree(source, os.path.join(out, run["folder"]))
    with open(os.path.join(out, "sweep.json"), "w", encoding="utf-8") as f:
        json.dump(merged, f, indent=2)
        f.write("\n")
    if header is not None:
        cell_column, seed_column = header.index("cellId"), header.index("seedIndex")
        rows.sort(key=lambda r: (r[cell_column], int(r[seed_column])))
        with open(os.path.join(out, "cells.csv"), "w", newline="", encoding="utf-8") as f:
            writer = csv.writer(f, lineterminator="\n")
            writer.writerow(header)
            writer.writerows(rows)
    return sum(len(c["runs"]) for c in cells.values())


def main(argv):
    if len(argv) < 3:
        print(__doc__, file=sys.stderr)
        return 2
    try:
        runs = merge(argv[2:], argv[1])
    except MergeError as error:
        print(error, file=sys.stderr)
        return 2
    print(f"merged {len(argv) - 2} shards: {runs} runs")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
