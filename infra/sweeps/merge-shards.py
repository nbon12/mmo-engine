#!/usr/bin/env python3
"""Merges the shards of one sweep (`regionizer-sim sweep --shard k/n`) into a single sweep folder.

Each shard's sweep.json lists every cell but only its own runs, its cells.csv has only its own rows, and
its run folders (<cellId>/seed-<i>) never collide with another shard's. The merged folder has one
sweep.json listing every run, one cells.csv with every row in cell and seed order, and every run folder.
That is the folder `regionizer-sim memo` and `harness load` read.

The shards must be of the same sweep (name, scenario, mode, sweep seed, seeds and cells), and no run may
appear in two shards. Every cell must end up with every seed's run, so a forgotten shard is refused, unless
--allow-missing is given. A shard cut short (a -partial branch) has no cells.csv; it is refused unless
--partial is given, and then its runs are listed but have no cells.csv rows.

Numbers are written back as the simulator wrote them, so the merged sweep.json reads exactly like an
unsharded one (harness load stores each cell's parameters as JSON text).

Usage: merge-shards.py [--allow-missing] [--partial] <out-dir> <shard-sweep-dir> [<shard-sweep-dir> ...]
"""
import csv
import json
import os
import re
import shutil
import sys

# Numbers are kept as their original text: json round-trips would rewrite 1E-05 as 1e-05.
_RAW = "\u0000raw:"


class _Raw(str):
    pass


def _load(f):
    return json.load(f, parse_float=_Raw, parse_int=_Raw, parse_constant=_Raw)


def _dumps(value):
    def mark(v):
        if isinstance(v, _Raw):
            return _RAW + v + "\u0000"
        if isinstance(v, dict):
            return {k: mark(x) for k, x in v.items()}
        if isinstance(v, list):
            return [mark(x) for x in v]
        return v

    text = json.dumps(mark(value), indent=2)
    return re.sub(r'"\\u0000raw:([^"\\]*)\\u0000"', r"\1", text)

IDENTITY = ("formatVersion", "name", "scenario", "mode", "sweepSeed", "seeds")


class MergeError(Exception):
    pass


def merge(shards, out, allow_missing=False, partial=False):
    if os.path.isdir(out) and os.listdir(out):
        raise MergeError(f"{out} is not empty; merge into a new folder")
    indexes = []
    for shard in shards:
        path = os.path.join(shard, "sweep.json")
        if not os.path.isfile(path):
            raise MergeError(f"{shard} is not a sweep folder (no sweep.json)")
        with open(path, encoding="utf-8") as f:
            indexes.append(_load(f))
        if not partial and not os.path.isfile(os.path.join(shard, "cells.csv")):
            raise MergeError(f"{shard} has no cells.csv (a shard cut short); pass --partial to merge it anyway")

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
                if int(run["seedIndex"]) in runs:
                    raise MergeError(f"run {run['folder']} is in two shards")
                runs[int(run["seedIndex"])] = (run, shard)

    seeds = len(first["seeds"])
    missing = [f"{c['cellId']} has {len(c['runs'])} of {seeds} runs" for c in cells.values() if len(c["runs"]) != seeds]
    if missing and not allow_missing:
        raise MergeError("incomplete merge (a shard missing?): " + "; ".join(missing[:5]) + "; pass --allow-missing to merge anyway")

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
        f.write(_dumps(merged) + "\n")
    if header is not None:
        cell_column, seed_column = header.index("cellId"), header.index("seedIndex")
        # In the index's cell order (c999 before c1000), then seed order.
        order = {cell: i for i, cell in enumerate(cells)}
        rows.sort(key=lambda r: (order.get(r[cell_column], len(order)), int(r[seed_column])))
        with open(os.path.join(out, "cells.csv"), "w", newline="", encoding="utf-8") as f:
            writer = csv.writer(f, lineterminator="\n")
            writer.writerow(header)
            writer.writerows(rows)
    return sum(len(c["runs"]) for c in cells.values())


def main(argv):
    flags = {a for a in argv[1:] if a.startswith("--")}
    args = [a for a in argv[1:] if not a.startswith("--")]
    unknown = flags - {"--allow-missing", "--partial"}
    if len(args) < 2 or unknown:
        print(__doc__, file=sys.stderr)
        return 2
    try:
        runs = merge(args[1:], args[0], allow_missing="--allow-missing" in flags, partial="--partial" in flags)
    except MergeError as error:
        print(error, file=sys.stderr)
        return 2
    print(f"merged {len(args) - 1} shards: {runs} runs")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
