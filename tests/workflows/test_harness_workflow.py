"""Contract tests for .github/workflows/harness.yml and infra/harness/ (spec 014-telemetry-test-harness, US6).

The cluster itself is exercised by the workflow's `stack` job on GitHub's runners. These tests pin down
the configuration that job depends on: triggers, permissions, pinned actions, the one command run twice,
the smoke checks, and the scripts' argument handling.

Run: python3 -m unittest discover -s tests/workflows -v   (needs PyYAML)
"""

import os
import re
import subprocess
import unittest
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW_PATH = ROOT / ".github" / "workflows" / "harness.yml"
WORKFLOW = yaml.safe_load(WORKFLOW_PATH.read_text())
# PyYAML reads the `on:` key as True.
TRIGGERS = WORKFLOW.get("on", WORKFLOW.get(True))
INFRA = ROOT / "infra" / "harness"


def steps(job):
    return WORKFLOW["jobs"][job]["steps"]


def runs(job):
    return "\n".join(step.get("run", "") for step in steps(job))


class TriggersAndPermissions(unittest.TestCase):
    def test_runs_on_pull_requests_and_main_for_harness_paths(self):
        for event in ("pull_request", "push"):
            paths = TRIGGERS[event]["paths"]
            for path in ("infra/harness/**", "harness/**", "src/**", ".github/workflows/harness.yml"):
                self.assertIn(path, paths, f"{event} must trigger on {path}")
        self.assertEqual(["main"], TRIGGERS["push"]["branches"])

    def test_full_calibration_is_a_manual_input(self):
        self.assertEqual("boolean", TRIGGERS["workflow_dispatch"]["inputs"]["calibrate"]["type"])

    def test_token_is_read_only(self):
        self.assertEqual({"contents": "read"}, WORKFLOW["permissions"])

    def test_every_action_is_pinned_to_a_commit(self):
        for name, job in WORKFLOW["jobs"].items():
            for step in job["steps"]:
                if "uses" in step:
                    self.assertRegex(step["uses"], r"^[\w.-]+/[\w.-]+@[0-9a-f]{40}$", f"{name}: {step['uses']}")

    def test_checkouts_do_not_persist_credentials(self):
        for name, job in WORKFLOW["jobs"].items():
            for step in job["steps"]:
                if step.get("uses", "").startswith("actions/checkout@"):
                    self.assertIs(False, step["with"]["persist-credentials"], name)

    def test_runner_class_and_pinned_intrinsics_are_set(self):
        self.assertTrue(WORKFLOW["env"]["MMO_RUNNER_CLASS"])
        self.assertIn("DOTNET_EnableAVX512F", WORKFLOW["env"])


class StackJob(unittest.TestCase):
    """US6 scenarios 1 to 5, as the stack job runs them."""

    def test_the_one_command_runs_twice_for_idempotence(self):
        self.assertEqual(2, len(re.findall(r"^infra/harness/up\.sh \|", runs("stack"), re.M)))

    def test_core_smoke_then_observability_profile(self):
        text = runs("stack")
        self.assertRegex(text, r"infra/harness/smoke\.sh\s*$|infra/harness/smoke\.sh\n")
        self.assertIn("infra/harness/up.sh --observability", text)
        self.assertIn("infra/harness/smoke.sh --observability", text)

    def test_kind_is_installed_at_a_pinned_version(self):
        self.assertRegex(runs("stack"), r"go install sigs\.k8s\.io/kind@v\d+\.\d+\.\d+")


class GoldenAndNetemJobs(unittest.TestCase):
    def test_golden_job_replays_and_uploads_candidates(self):
        self.assertIn("infra/harness/golden.sh", runs("golden"))
        uploads = [s for s in steps("golden") if s.get("uses", "").startswith("actions/upload-artifact@")]
        self.assertEqual("golden-candidates", uploads[0]["with"]["name"])

    def test_netem_job_loads_the_module_and_calibrates(self):
        text = runs("netem")
        self.assertIn("modprobe sch_netem", text)
        self.assertIn("infra/harness/calibrate.sh --seconds 30 LAN", text)
        self.assertIn("infra/harness/calibrate.sh --seconds 300 --write-calibrated", text)


class Scripts(unittest.TestCase):
    def test_scripts_are_executable_and_parse(self):
        for script in ("up.sh", "down.sh", "smoke.sh", "golden.sh", "calibrate.sh"):
            path = INFRA / script
            self.assertTrue(os.access(path, os.X_OK), f"{script} is not executable")
            subprocess.run(["bash", "-n", str(path)], check=True)

    def test_up_refuses_an_unknown_argument_with_exit_2(self):
        result = subprocess.run([str(INFRA / "up.sh"), "--bogus"], capture_output=True, text=True)
        self.assertEqual(2, result.returncode)
        self.assertIn("unknown argument", result.stderr)

    def test_up_refuses_an_unknown_cluster_tool_with_exit_2(self):
        env = dict(os.environ, HARNESS_CLUSTER="minikube", PATH=os.environ["PATH"])
        result = subprocess.run([str(INFRA / "up.sh")], capture_output=True, text=True, env=env)
        # Exit 2 either for the missing tools this machine lacks or for the unknown cluster tool.
        self.assertEqual(2, result.returncode)

    def test_manifests_never_hold_the_clickhouse_password(self):
        for path in (INFRA / "manifests").rglob("*.yaml"):
            text = path.read_text()
            self.assertNotRegex(text, r"CLICKHOUSE_PASSWORD\s*\n\s*value:", str(path))

    def test_collector_does_not_create_its_schema(self):
        config = yaml.safe_load((INFRA / "manifests" / "base" / "otel-collector-config.yaml").read_text())
        self.assertIs(False, config["exporters"]["clickhouse"]["create_schema"])
        pipelines = config["service"]["pipelines"]
        self.assertEqual(["clickhouse"], pipelines["logs"]["exporters"])
        self.assertEqual(["clickhouse"], pipelines["traces"]["exporters"])
        self.assertEqual(["prometheus"], pipelines["metrics"]["exporters"])

    def test_core_profile_has_no_prometheus_or_grafana(self):
        base = yaml.safe_load((INFRA / "manifests" / "base" / "kustomization.yaml").read_text())
        observability = yaml.safe_load((INFRA / "manifests" / "observability" / "kustomization.yaml").read_text())
        self.assertNotIn("prometheus.yaml", base["resources"])
        self.assertIn("prometheus.yaml", observability["resources"])
        self.assertIn("grafana.yaml", observability["resources"])


if __name__ == "__main__":
    unittest.main()
