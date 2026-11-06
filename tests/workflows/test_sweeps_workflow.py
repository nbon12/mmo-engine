"""Contract tests for .github/workflows/sweeps.yml and infra/sweeps/ (spec 013, issue #13: full-scale sweeps).

The sweeps themselves run on GitHub's runners when the workflow is dispatched. These tests pin down what
that depends on: manual trigger only, least privilege, pinned actions, one job per sweep, result branches
that never touch main, and the trimming that keeps exactly what the decision memo reads.

Run: python3 -m unittest discover -s tests/workflows -v   (needs PyYAML)
"""

import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = yaml.safe_load((ROOT / ".github" / "workflows" / "sweeps.yml").read_text())
TRIGGERS = WORKFLOW.get("on", WORKFLOW.get(True))
INFRA = ROOT / "infra" / "sweeps"


def runs(job):
    return "\n".join(step.get("run", "") for step in WORKFLOW["jobs"][job]["steps"])


class Workflow(unittest.TestCase):
    def test_manual_trigger_only(self):
        self.assertEqual(["workflow_dispatch"], list(TRIGGERS))
        self.assertEqual("string", TRIGGERS["workflow_dispatch"]["inputs"]["sweeps"]["type"])

    def test_read_only_except_the_sweep_jobs_branch_push(self):
        self.assertEqual({"contents": "read"}, WORKFLOW["permissions"])
        self.assertEqual({"contents": "write"}, WORKFLOW["jobs"]["sweep"]["permissions"])
        self.assertNotIn("permissions", WORKFLOW["jobs"]["plan"])

    def test_every_action_is_pinned_to_a_commit_and_checkouts_keep_no_credentials(self):
        for job in WORKFLOW["jobs"].values():
            for step in job["steps"]:
                uses = step.get("uses")
                if uses:
                    self.assertRegex(uses, r"@[0-9a-f]{40}$", uses)
                if uses and uses.startswith("actions/checkout@"):
                    self.assertIs(False, step["with"]["persist-credentials"])

    def test_one_job_per_sweep_that_keeps_going_when_one_fails(self):
        job = WORKFLOW["jobs"]["sweep"]
        self.assertEqual("${{ fromJSON(needs.plan.outputs.jobs) }}", job["strategy"]["matrix"]["job"])
        self.assertIs(False, job["strategy"]["fail-fast"])
        self.assertLessEqual(job["timeout-minutes"], 360)

    def test_results_go_to_a_per_run_branch_never_main(self):
        text = runs("sweep")
        # The attempt is in the name, so a re-run never collides with a branch an earlier attempt pushed.
        self.assertIn('branch="sweep-results/$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT/$SWEEP$suffix"', text)
        self.assertIn('"HEAD:refs/heads/$branch"', text)
        self.assertNotRegex(text, r"refs/heads/main|push[^\n]*\bmain\b")

    def test_results_are_pushed_even_when_the_sweep_fails_or_times_out(self):
        steps = {s.get("name"): s for s in WORKFLOW["jobs"]["sweep"]["steps"]}
        sweep, push = steps["Run the sweep"], steps["Push the results branch"]
        self.assertEqual("${{ always() }}", push["if"])
        self.assertLess(sweep["timeout-minutes"], WORKFLOW["jobs"]["sweep"]["timeout-minutes"])
        self.assertIn('trim.py --partial "$results/full/$SWEEP"', push["run"])

    def test_inputs_reach_scripts_only_through_the_environment(self):
        for job in WORKFLOW["jobs"].values():
            for step in job["steps"]:
                self.assertNotIn("${{", step.get("run", ""), step.get("name"))

    @staticmethod
    def plan(requested="", shards=None):
        script = WORKFLOW["jobs"]["plan"]["steps"][1]["run"]
        lines = script.splitlines()
        body = "\n".join(lines[1:lines.index("PY")])
        env = {**os.environ, "REQUESTED": requested}
        if shards is not None:
            env["SHARDS"] = shards
        out = subprocess.run([sys.executable, "-c", body], cwd=ROOT, capture_output=True, text=True, env=env)
        jobs = json.loads(out.stdout.strip().split("=", 1)[1]) if out.returncode == 0 else None
        return out.returncode, jobs

    def test_plan_lists_every_sweep_spec_but_the_memo_rules(self):
        self.assertIn("<<'PY'", WORKFLOW["jobs"]["plan"]["steps"][1]["run"].splitlines()[0])
        code, jobs = self.plan()
        expected = sorted(p.stem for p in (ROOT / "simulator" / "sweeps").glob("*.json") if p.stem != "memo-rules")
        self.assertEqual(0, code)
        self.assertEqual([{"sweep": s, "shard": 1, "of": 1} for s in expected], jobs)
        self.assertIn("infantry-radius", [j["sweep"] for j in jobs])
        self.assertNotEqual(0, self.plan("nowhere")[0])
        self.assertEqual(["spacing", "air-layer"], [j["sweep"] for j in self.plan("spacing spacing air-layer")[1]])

    def test_plan_splits_each_sweep_into_the_requested_shards(self):
        code, jobs = self.plan("split-damping", "3")
        self.assertEqual(0, code)
        self.assertEqual([{"sweep": "split-damping", "shard": k, "of": 3} for k in (1, 2, 3)], jobs)
        self.assertNotEqual(0, self.plan("split-damping", "0")[0])
        # More shards than the sweep's 10 seeds would leave cells with no run in some shard.
        self.assertEqual(10, len(self.plan("split-damping", "10")[1]))
        self.assertNotEqual(0, self.plan("split-damping", "11")[0])
        # A fractional count is refused, not rounded down; a whole one written as a float is fine.
        self.assertNotEqual(0, self.plan("split-damping", "2.5")[0])
        self.assertEqual(2, len(self.plan("split-damping", "2.0")[1]))
        self.assertEqual(TRIGGERS["workflow_dispatch"]["inputs"]["shards"]["default"], 1)

    def test_a_sweep_can_have_its_own_shard_count(self):
        code, jobs = self.plan("spacing split-damping:4 split-damping:2", "1")
        self.assertEqual(0, code)
        self.assertEqual([("spacing", 1, 1)] + [("split-damping", k, 4) for k in (1, 2, 3, 4)],
                         [(j["sweep"], j["shard"], j["of"]) for j in jobs])
        self.assertNotEqual(0, self.plan("split-damping:x")[0])
        self.assertNotEqual(0, self.plan("split-damping:11")[0])

    def test_the_plan_never_asks_for_more_jobs_than_a_matrix_holds(self):
        body = WORKFLOW["jobs"]["plan"]["steps"][1]["run"]
        self.assertIn("if len(jobs) > 256:", body)
        # With every sweep at its most shards, the plan still fits.
        code, jobs = self.plan("", "10")
        self.assertEqual(0, code)
        self.assertLessEqual(len(jobs), 256)

    def test_shards_run_their_part_and_push_their_own_branch(self):
        steps = {s.get("name"): s for s in WORKFLOW["jobs"]["sweep"]["steps"]}
        self.assertEqual("${{ matrix.job.shard }}/${{ matrix.job.of }}", steps["Run the sweep"]["env"]["SHARD"])
        self.assertIn('run-sweep.sh "$SWEEP" "$RUNNER_TEMP/results" "$SHARD"', steps["Run the sweep"]["run"])
        push = steps["Push the results branch"]["run"]
        self.assertIn('suffix="-shard-$SHARD_INDEX-of-$SHARD_COUNT"', push)
        self.assertIn('suffix="$suffix-partial"', push)
        self.assertIn('--shard "$SHARD"', (INFRA / "run-sweep.sh").read_text())


class MergeShards(unittest.TestCase):
    """infra/sweeps/merge-shards.py: the shards of one sweep become one folder the memo reads."""

    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location("merge_shards", INFRA / "merge-shards.py")
        cls.merge = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.merge)

    @staticmethod
    def shard(root, name, runs):
        """A shard folder of a 2-cell, 2-seed sweep holding only `runs` (cellId, seedIndex)."""
        folder = Path(root) / name / "sweep"
        index = {"formatVersion": 1, "name": "s", "scenario": "Migration", "mode": "OneAtATime", "sweepSeed": 3,
                 "seeds": [{"index": 0, "seed": 10}, {"index": 1, "seed": 11}], "cells": []}
        rows = []
        for cell in ("c000", "c001"):
            mine = [i for c, i in runs if c == cell]
            index["cells"].append({"cellId": cell, "parameters": {"p": cell},
                                   "runs": [{"seedIndex": i, "seed": 10 + i, "folder": f"{cell}/seed-{i}", "completed": True} for i in mine]})
            for i in mine:
                (folder / cell / f"seed-{i}").mkdir(parents=True)
                (folder / cell / f"seed-{i}" / "summary.json").write_text(f"{cell}-{i}")
                rows.append(f"{cell},{i},{10 + i},run-{cell}-{i}")
        folder.mkdir(parents=True, exist_ok=True)
        (folder / "sweep.json").write_text(json.dumps(index))
        (folder / "cells.csv").write_text("cellId,seedIndex,seed,runId\n" + "".join(r + "\n" for r in rows))
        return folder

    def test_shards_merge_into_one_index_table_and_set_of_runs(self):
        with tempfile.TemporaryDirectory() as root:
            a = self.shard(root, "a", [("c000", 0), ("c001", 1)])
            b = self.shard(root, "b", [("c000", 1), ("c001", 0)])
            out = Path(root) / "merged"
            runs = self.merge.merge([str(a), str(b)], str(out))
            index = json.loads((out / "sweep.json").read_text())
            csv_rows = (out / "cells.csv").read_text().splitlines()
            summaries = sorted(p.read_text() for p in out.rglob("summary.json"))
        self.assertEqual(4, runs)
        self.assertEqual([[0, 1], [0, 1]], [[r["seedIndex"] for r in c["runs"]] for c in index["cells"]])
        self.assertEqual(["cellId,seedIndex,seed,runId", "c000,0,10,run-c000-0", "c000,1,11,run-c000-1",
                          "c001,0,10,run-c001-0", "c001,1,11,run-c001-1"], csv_rows)
        self.assertEqual(["c000-0", "c000-1", "c001-0", "c001-1"], summaries)

    def test_a_run_in_two_shards_or_shards_of_different_sweeps_are_refused(self):
        with tempfile.TemporaryDirectory() as root:
            a = self.shard(root, "a", [("c000", 0)])
            b = self.shard(root, "b", [("c000", 0)])
            with self.assertRaisesRegex(self.merge.MergeError, "in two shards"):
                self.merge.merge([str(a), str(b)], str(Path(root) / "out1"))
            c = self.shard(root, "c", [("c001", 1)])
            index = json.loads((c / "sweep.json").read_text())
            index["sweepSeed"] = 4
            (c / "sweep.json").write_text(json.dumps(index))
            with self.assertRaisesRegex(self.merge.MergeError, "sweepSeed differs"):
                self.merge.merge([str(a), str(c)], str(Path(root) / "out2"))
            result = subprocess.run([sys.executable, str(INFRA / "merge-shards.py"), str(Path(root) / "out3"), str(a), str(b)],
                                    capture_output=True, text=True)
        self.assertEqual(2, result.returncode)


    def test_a_missing_shard_is_refused_unless_allowed(self):
        with tempfile.TemporaryDirectory() as root:
            a = self.shard(root, "a", [("c000", 0), ("c001", 1)])
            with self.assertRaisesRegex(self.merge.MergeError, "incomplete merge.*c000 has 1 of 2 runs"):
                self.merge.merge([str(a)], str(Path(root) / "out1"))
            self.assertEqual(2, self.merge.merge([str(a)], str(Path(root) / "out2"), allow_missing=True))
            cli = subprocess.run([sys.executable, str(INFRA / "merge-shards.py"), str(Path(root) / "out3"), str(a)],
                                 capture_output=True, text=True)
        self.assertEqual(2, cli.returncode)
        self.assertIn("--allow-missing", cli.stderr)

    def test_a_shard_cut_short_is_refused_unless_partial(self):
        with tempfile.TemporaryDirectory() as root:
            a = self.shard(root, "a", [("c000", 0), ("c001", 1)])
            b = self.shard(root, "b", [("c000", 1), ("c001", 0)])
            (b / "cells.csv").unlink()
            with self.assertRaisesRegex(self.merge.MergeError, "no cells.csv"):
                self.merge.merge([str(a), str(b)], str(Path(root) / "out1"))
            out = Path(root) / "out2"
            self.assertEqual(4, self.merge.merge([str(a), str(b)], str(out), partial=True))
            rows = (out / "cells.csv").read_text().splitlines()
            folders = sorted(str(p.parent.relative_to(out)) for p in out.rglob("summary.json"))
        self.assertEqual(["cellId,seedIndex,seed,runId", "c000,0,10,run-c000-0", "c001,1,11,run-c001-1"], rows)
        self.assertEqual(["c000/seed-0", "c000/seed-1", "c001/seed-0", "c001/seed-1"], folders)

    def test_rows_follow_the_index_cell_order_and_numbers_keep_their_text(self):
        with tempfile.TemporaryDirectory() as root:
            folders = []
            for name, seed in (("a", 0), ("b", 1)):
                folder = Path(root) / name
                folder.mkdir()
                cells = [{"cellId": c, "parameters": {"x": "PLACEHOLDER"},
                          "runs": [{"seedIndex": seed, "seed": 10 + seed, "folder": f"{c}/seed-{seed}", "completed": True}]}
                         for c in ("c999", "c1000")]
                index = {"formatVersion": 1, "name": "s", "scenario": "Migration", "mode": "OneAtATime", "sweepSeed": 3,
                         "seeds": [{"index": 0, "seed": 10}, {"index": 1, "seed": 11}], "cells": cells}
                (folder / "sweep.json").write_text(json.dumps(index).replace('"PLACEHOLDER"', "1E-05"))
                (folder / "cells.csv").write_text(f"cellId,seedIndex\nc1000,{seed}\nc999,{seed}\n")
                folders.append(str(folder))
            out = Path(root) / "out"
            self.merge.merge(folders, str(out))
            rows = (out / "cells.csv").read_text().splitlines()
            text = (out / "sweep.json").read_text()
        self.assertEqual(["cellId,seedIndex", "c999,0", "c999,1", "c1000,0", "c1000,1"], rows)
        self.assertIn('"x": 1E-05', text)


class Scripts(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location("trim", INFRA / "trim.py")
        cls.trim = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.trim)

    def test_run_sweep_parses_and_refuses_an_unknown_sweep_with_exit_2(self):
        subprocess.run(["bash", "-n", str(INFRA / "run-sweep.sh")], check=True)
        result = subprocess.run([str(INFRA / "run-sweep.sh"), "nowhere", "/tmp/unused"], capture_output=True, text=True)
        self.assertEqual(2, result.returncode)
        self.assertIn("no sweep named nowhere", result.stderr)

    def test_trim_keeps_what_the_memo_reads_and_drops_per_tick_files(self):
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / "air-layer"
            run = source / "c000" / "seed-0"
            run.mkdir(parents=True)
            for name in ("sweep.json", "cells.csv"):
                (source / name).write_text("x")
            for name in ("manifest.json", "summary.json", "ticks.csv", "events.csv"):
                (run / name).write_text("x")
            target = Path(folder) / "out"
            copied = self.trim.trim(str(source), str(target))
            kept = sorted(str(p.relative_to(target)) for p in target.rglob("*") if p.is_file())
        self.assertEqual(4, copied)
        self.assertEqual(["c000/seed-0/manifest.json", "c000/seed-0/summary.json", "cells.csv", "sweep.json"], kept)

    def test_trim_refuses_a_target_that_already_holds_files(self):
        with tempfile.TemporaryDirectory() as folder:
            source = Path(folder) / "sweep"
            source.mkdir()
            (source / "sweep.json").write_text("x")
            target = Path(folder) / "out"
            target.mkdir()
            (target / "stale.json").write_text("old")
            result = subprocess.run([sys.executable, str(INFRA / "trim.py"), str(source), str(target)], capture_output=True, text=True)
        self.assertEqual(2, result.returncode)
        self.assertIn("not empty", result.stderr)

    def test_trim_keeps_the_incomplete_marker_so_the_memo_refuses_an_unfinished_run(self):
        with tempfile.TemporaryDirectory() as folder:
            run = Path(folder) / "sweep" / "c000" / "seed-0"
            run.mkdir(parents=True)
            (Path(folder) / "sweep" / "sweep.json").write_text("x")
            for name in ("manifest.json", "_incomplete", "ticks.csv"):
                (run / name).write_text("x")
            self.trim.trim(str(Path(folder) / "sweep"), str(Path(folder) / "out"))
            kept = sorted(p.name for p in (Path(folder) / "out" / "c000" / "seed-0").iterdir())
        self.assertEqual(["_incomplete", "manifest.json"], kept)

    def test_trim_refuses_a_folder_without_sweep_json_unless_partial(self):
        with tempfile.TemporaryDirectory() as folder:
            run = Path(folder) / "c000" / "seed-0"
            run.mkdir(parents=True)
            (run / "summary.json").write_text("x")
            refused = subprocess.run([sys.executable, str(INFRA / "trim.py"), folder, folder + "/out"], capture_output=True, text=True)
            partial = subprocess.run([sys.executable, str(INFRA / "trim.py"), "--partial", folder, folder + "/out"], capture_output=True, text=True)
            kept = (Path(folder) / "out" / "c000" / "seed-0" / "summary.json").exists()
        self.assertEqual(2, refused.returncode)
        self.assertEqual(0, partial.returncode)
        self.assertTrue(kept)

    def test_run_sweep_keeps_results_when_a_run_stops_and_exits_with_the_sweeps_code(self):
        text = (INFRA / "run-sweep.sh").read_text()
        sweep_line = text.index('sim sweep "$SPEC"')
        self.assertLess(text.rindex("set +e", 0, sweep_line), sweep_line)
        self.assertLess(sweep_line, text.index('trim.py" "$FULL/$NAME"'))
        self.assertTrue(text.rstrip().endswith('exit "$code"'))


if __name__ == "__main__":
    unittest.main()
