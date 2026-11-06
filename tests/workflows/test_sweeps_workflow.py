"""Contract tests for .github/workflows/sweeps.yml and infra/sweeps/ (spec 013, issue #13: full-scale sweeps).

The sweeps themselves run on GitHub's runners when the workflow is dispatched. These tests pin down what
that depends on: manual trigger only, least privilege, pinned actions, one job per sweep, result branches
that never touch main, and the trimming that keeps exactly what the decision memo reads.

Run: python3 -m unittest discover -s tests/workflows -v   (needs PyYAML)
"""

import importlib.util
import json
import subprocess
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
        self.assertEqual("${{ fromJSON(needs.plan.outputs.sweeps) }}", job["strategy"]["matrix"]["sweep"])
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

    def test_plan_lists_every_sweep_spec_but_the_memo_rules(self):
        script = WORKFLOW["jobs"]["plan"]["steps"][1]["run"]
        lines = script.splitlines()
        self.assertIn("<<'PY'", lines[0])
        body = "\n".join(lines[1:lines.index("PY")])
        out = subprocess.run(["python3", "-c", body], cwd=ROOT, capture_output=True, text=True, check=True,
                             env={"REQUESTED": "", "PATH": "/usr/bin:/bin"})
        sweeps = json.loads(out.stdout.strip().split("=", 1)[1])
        bad = subprocess.run(["python3", "-c", body], cwd=ROOT, capture_output=True, text=True,
                             env={"REQUESTED": "nowhere", "PATH": "/usr/bin:/bin"})
        twice = subprocess.run(["python3", "-c", body], cwd=ROOT, capture_output=True, text=True, check=True,
                               env={"REQUESTED": "spacing spacing air-layer", "PATH": "/usr/bin:/bin"})
        self.assertEqual(["spacing", "air-layer"], json.loads(twice.stdout.strip().split("=", 1)[1]))
        expected = sorted(p.stem for p in (ROOT / "simulator" / "sweeps").glob("*.json") if p.stem != "memo-rules")
        self.assertEqual(expected, sweeps)
        self.assertIn("infantry-radius", sweeps)
        self.assertNotEqual(0, bad.returncode)


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
            refused = subprocess.run(["python3", str(INFRA / "trim.py"), folder, folder + "/out"], capture_output=True, text=True)
            partial = subprocess.run(["python3", str(INFRA / "trim.py"), "--partial", folder, folder + "/out"], capture_output=True, text=True)
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
