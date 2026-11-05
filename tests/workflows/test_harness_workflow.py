"""Contract tests for .github/workflows/harness.yml and infra/harness/ (spec 014-telemetry-test-harness, US6).

The cluster itself is exercised by the workflow's `stack` job on GitHub's runners. These tests pin down
the configuration that job depends on: triggers, permissions, pinned actions, the one command run twice,
the smoke checks, and the scripts' argument handling.

Run: python3 -m unittest discover -s tests/workflows -v   (needs PyYAML)
"""

import importlib.util
import os
import re
import subprocess
import sys
import tempfile
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

    def test_steps_run_with_pipefail(self):
        # `shell: bash` runs `bash --noprofile --norc -eo pipefail {0}`, so `up.sh | tail -1` keeps up.sh's failure.
        self.assertEqual("bash", WORKFLOW["defaults"]["run"]["shell"])

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
        self.assertEqual(2, len(re.findall(r"^\s*infra/harness/up\.sh \|", runs("stack"), re.M)))

    def test_the_rerun_asserts_nothing_was_recreated(self):
        """US6 scenario 2: identity (cluster node, namespaces, secret and password hash, workload UIDs) is unchanged."""
        step = next(s for s in steps("stack") if "idempotent" in s["name"])
        text = step["run"]
        self.assertLess(text.index("identity.sh > \"$RUNNER_TEMP/identity-before\""), text.index("infra/harness/up.sh"))
        self.assertLess(text.index("infra/harness/up.sh"), text.index("identity.sh > \"$RUNNER_TEMP/identity-after\""))
        self.assertIn('diff -u "$RUNNER_TEMP/identity-before" "$RUNNER_TEMP/identity-after"', text)

    def test_core_smoke_then_observability_profile(self):
        text = runs("stack")
        self.assertRegex(text, r"infra/harness/smoke\.sh\s*$|infra/harness/smoke\.sh\n")
        self.assertIn("infra/harness/up.sh --observability", text)
        self.assertIn("infra/harness/smoke.sh --observability", text)

    def test_kind_is_installed_at_a_pinned_version(self):
        self.assertRegex(runs("stack"), r"go install sigs\.k8s\.io/kind@v\d+\.\d+\.\d+")


class GoldenAndNetemJobs(unittest.TestCase):
    def test_golden_job_replays_twice_per_config_and_writes_no_recordings(self):
        self.assertIn("infra/harness/golden.sh", runs("golden"))
        uploads = [s for s in steps("golden") if s.get("uses", "").startswith("actions/upload-artifact@")]
        self.assertEqual([], uploads)
        script = (INFRA / "golden.sh").read_text()
        self.assertIn('--out "$WORK/$name-reference"', script)
        self.assertIn('--out "$WORK/$name-replay"', script)
        self.assertNotRegex(script, r"--out \"?\$ROOT/harness/golden")

    def test_netem_job_loads_the_module_and_calibrates(self):
        text = runs("netem")
        self.assertIn("modprobe sch_netem", text)
        self.assertIn("infra/harness/calibrate.sh --seconds 30 LAN", text)
        self.assertIn('infra/harness/calibrate.sh --seconds "$CALIBRATE_SECONDS" --write-calibrated', text)
        step = next(s for s in steps("netem") if s.get("name") == "Calibrate every profile (5 minutes each)")
        self.assertEqual("${{ inputs.calibrate_seconds }}", step["env"]["CALIBRATE_SECONDS"])
        self.assertEqual(300, TRIGGERS["workflow_dispatch"]["inputs"]["calibrate_seconds"]["default"])
        self.assertGreaterEqual(WORKFLOW["jobs"]["netem"]["timeout-minutes"], 120)


class Scripts(unittest.TestCase):
    def test_scripts_are_executable_and_parse(self):
        for script in ("up.sh", "down.sh", "smoke.sh", "golden.sh", "calibrate.sh", "identity.sh"):
            path = INFRA / script
            self.assertTrue(os.access(path, os.X_OK), f"{script} is not executable")
            subprocess.run(["bash", "-n", str(path)], check=True)

    def test_up_refuses_an_unknown_argument_with_exit_2(self):
        result = subprocess.run([str(INFRA / "up.sh"), "--bogus"], capture_output=True, text=True)
        self.assertEqual(2, result.returncode)
        self.assertIn("unknown argument", result.stderr)

    def test_up_refuses_an_unknown_cluster_tool_with_exit_2(self):
        # Stub the tools up.sh needs first, so the only way to exit 2 is the unknown cluster tool.
        with tempfile.TemporaryDirectory() as stubs:
            for tool in ("docker", "kubectl", "helm", "dotnet"):
                stub = Path(stubs) / tool
                stub.write_text("#!/bin/sh\nexit 0\n")
                stub.chmod(0o755)
            env = dict(os.environ, HARNESS_CLUSTER="minikube", PATH=f"{stubs}:{os.environ['PATH']}")
            result = subprocess.run([str(INFRA / "up.sh")], capture_output=True, text=True, env=env)
        self.assertEqual(2, result.returncode)
        self.assertIn("HARNESS_CLUSTER must be kind or k3d, was minikube", result.stderr)

    def test_stack_job_runs_on_kind_and_k3d_with_a_verified_k3d(self):
        job = WORKFLOW["jobs"]["stack"]
        self.assertEqual(["kind", "k3d"], job["strategy"]["matrix"]["cluster"])
        self.assertEqual("${{ matrix.cluster }}", job["env"]["HARNESS_CLUSTER"])
        self.assertRegex(runs("stack"), r"k3d/releases/download/v\d+\.\d+\.\d+/k3d-linux-amd64")
        self.assertRegex(runs("stack"), r"[0-9a-f]{64}  \$RUNNER_TEMP/k3d\" \| sha256sum -c -")

    def test_paths_cover_the_files_the_jobs_read(self):
        for trigger in ("pull_request", "push"):
            for path in ("specs/014-telemetry-test-harness/contracts/store-ddl.md",
                         "tests/MmoEngine.Harness.Tests/Fixtures/runs/**", "global.json",
                         "Directory.Build.props", "Directory.Packages.props"):
                self.assertIn(path, TRIGGERS[trigger]["paths"])

    def test_scripts_build_once_through_lib_with_visible_output(self):
        lib = (INFRA / "lib.sh").read_text()
        self.assertIn(">&2", lib)
        for script in ("up.sh", "smoke.sh", "golden.sh", "calibrate.sh"):
            text = (INFRA / script).read_text()
            self.assertIn("infra/harness/lib.sh", text, script)
            self.assertNotIn("dotnet run --project", text, script)

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

    def test_identity_includes_pods_and_up_reuses_an_installed_chaos_mesh(self):
        self.assertIn("get pods", (INFRA / "identity.sh").read_text())
        up = (INFRA / "up.sh").read_text()
        self.assertIn("reusing Chaos Mesh", up)
        self.assertLess(up.index("helm -n chaos-mesh list --deployed"), up.index("helm upgrade --install"))

    def test_core_profile_removes_an_earlier_observability_install(self):
        up = (INFRA / "up.sh").read_text()
        self.assertIn("delete deployment/prometheus deployment/grafana", up)

    def test_smoke_checks_the_contract_tables_and_prometheus(self):
        script = (INFRA / "smoke.sh").read_text()
        self.assertIn("contracts/store-ddl.md", script)
        self.assertIn('METRICS="$(curl', script)
        self.assertIn("api/v1/query?query=harness_smoke_total", script)

    def test_scripts_clean_up_what_they_start(self):
        calibrate = (INFRA / "calibrate.sh").read_text()
        self.assertIn('"$HARNESS_BUILD_DIR/harness/harness.dll" probe serve', calibrate)
        self.assertIn('sudo ip netns del "$NS"', calibrate)
        self.assertNotIn("dotnet run --project", calibrate)
        self.assertIn('docker rm -f "$CONTAINER"', calibrate)
        self.assertIn("trap cleanup EXIT", calibrate)
        self.assertIn("""trap 'rm -rf "$WORK"' EXIT""", (INFRA / "golden.sh").read_text())

    def test_up_refuses_a_port_it_does_not_own_and_calibrate_waits_for_the_server(self):
        self.assertIn("/dev/tcp/127.0.0.1/", (INFRA / "up.sh").read_text())
        self.assertIn("listening", (INFRA / "calibrate.sh").read_text())

    def test_smoke_checks_every_table_schema_apply_lists(self):
        text = (INFRA / "smoke.sh").read_text()
        self.assertIn("harness schema apply", text)
        self.assertNotIn("for table in runs sim_ticks", text)

    def test_calibrate_uses_repo_paths_and_reports_storage_failures(self):
        text = (INFRA / "calibrate.sh").read_text()
        self.assertIn('PROFILES_DIR="$ROOT/harness/profiles"', text)
        self.assertNotIn("$WRITE >/dev/null || true", text)
        self.assertIn("storing calibrated settings", text)

    def test_calibrated_profiles_upload_does_not_depend_on_a_copy_step(self):
        uploads = [s for s in steps("netem") if s.get("uses", "").startswith("actions/upload-artifact@")]
        self.assertEqual("harness/profiles/*.json", uploads[0]["with"]["path"])
        self.assertNotIn("cp harness/profiles", runs("netem"))

    def test_dashboard_panels_bind_to_the_provisioned_clickhouse_uid(self):
        import json
        datasources = yaml.safe_load((INFRA / "manifests" / "observability" / "grafana-datasources.yaml").read_text())
        uids = {d["type"]: d.get("uid") for d in datasources["datasources"]}
        dashboard = json.loads((INFRA / "manifests" / "observability" / "dashboards" / "harness-runs.json").read_text())
        for panel in dashboard["panels"]:
            self.assertEqual(uids["grafana-clickhouse-datasource"], panel["datasource"]["uid"])
            self.assertTrue(panel["datasource"]["uid"])

    def test_core_profile_has_no_prometheus_or_grafana(self):
        base = yaml.safe_load((INFRA / "manifests" / "base" / "kustomization.yaml").read_text())
        observability = yaml.safe_load((INFRA / "manifests" / "observability" / "kustomization.yaml").read_text())
        self.assertNotIn("prometheus.yaml", base["resources"])
        self.assertIn("prometheus.yaml", observability["resources"])
        self.assertIn("grafana.yaml", observability["resources"])


if __name__ == "__main__":
    unittest.main()


class CalibrationSearch(unittest.TestCase):
    """calibrate.sh's search step (infra/harness/calibration_search.py)."""

    @classmethod
    def setUpClass(cls):
        spec = importlib.util.spec_from_file_location("calibration_search", INFRA / "calibration_search.py")
        cls.search = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(cls.search)

    def test_within_tolerance_keeps_the_settings(self):
        self.assertEqual(("done", 40, 7.071), self.search.step({"failures": []}, 80, 10, 40, 7.071))

    def test_low_jitter_scales_the_netem_jitter_up(self):
        verdict, delay, jitter = self.search.step(
            {"failures": ["jitter: measured 5.8 ms"], "medianRoundTripMs": 80, "jitterMs": 5.8}, 80, 10, 40, 7.071)
        self.assertEqual(("next", 40), (verdict, delay))
        self.assertAlmostEqual(7.071 * 10 / 5.8, jitter, places=6)

    def test_a_step_never_more_than_doubles_or_halves_the_jitter(self):
        _, _, up = self.search.step({"failures": ["jitter: x"], "medianRoundTripMs": 80, "jitterMs": 1}, 80, 10, 40, 7)
        _, _, down = self.search.step({"failures": ["jitter: x"], "medianRoundTripMs": 80, "jitterMs": 100}, 80, 10, 40, 7)
        self.assertEqual((14, 3.5), (up, down))

    def test_round_trip_error_moves_the_delay_by_half_and_never_below_zero(self):
        _, delay, _ = self.search.step({"failures": ["round trip: x"], "medianRoundTripMs": 90, "jitterMs": 10}, 80, 10, 40, 7)
        self.assertEqual(35, delay)
        _, floor, _ = self.search.step({"failures": ["round trip: x"], "medianRoundTripMs": 5, "jitterMs": 0}, 1, 0, 0.5, 0)
        self.assertEqual(0, floor)

    def test_loss_and_reordering_failures_are_not_searched(self):
        result = {"failures": ["loss up: x", "reordering: x"], "medianRoundTripMs": 80, "jitterMs": 10}
        self.assertEqual(("done", 40, 7), self.search.step(result, 80, 10, 40, 7))

    def test_cli_reads_the_last_json_line(self):
        with tempfile.TemporaryDirectory() as folder:
            out = Path(folder) / "probe.out"
            out.write_text('log line\n{"failures": [], "medianRoundTripMs": 80, "jitterMs": 10}\n')
            result = subprocess.run([sys.executable, str(INFRA / "calibration_search.py"), str(out), "80", "10", "40", "7.071"],
                                    capture_output=True, text=True, check=True)
        self.assertEqual("done 40.000 7.071", result.stdout.strip())
