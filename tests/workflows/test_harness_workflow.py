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
        self.assertIn("infra/harness/calibrate.sh --seconds 300 --write-calibrated", text)


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
