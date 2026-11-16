"""Contract tests for running a sweep as an indexed Kubernetes Job (spec 014-telemetry-test-harness, FR-032;
issue #62): infra/sweeps/Dockerfile, job-entrypoint.sh, manifests/sweep-job.yaml and k8s-sweep.sh.

The Job itself runs on a cluster in the Harness workflow's `stack` job (infra/harness/smoke.sh step 4, on kind
and on k3d). These tests pin down what that run depends on: the completion index mapped to `--shard k/n`, the
results each pod leaves, the rendered Job, and the script's argument handling.

Run: python3 -m unittest discover -s tests/workflows -v   (needs PyYAML)
"""

import json
import os
import re
import string
import subprocess
import tempfile
import unittest
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]
SWEEPS = ROOT / "infra" / "sweeps"
ENTRYPOINT = SWEEPS / "job-entrypoint.sh"
SCRIPT = SWEEPS / "k8s-sweep.sh"
TEMPLATE = SWEEPS / "manifests" / "sweep-job.yaml"


def stub(folder, name, body):
    path = Path(folder) / name
    path.write_text("#!/bin/sh\n" + body)
    path.chmod(0o755)


class Entrypoint(unittest.TestCase):
    """job-entrypoint.sh, with a stub `dotnet` that records its arguments and exits with $STUB_CODE."""

    def run_entrypoint(self, index, shards, code):
        work = tempfile.mkdtemp()
        bin_dir = Path(work) / "bin"
        bin_dir.mkdir()
        stub(bin_dir, "dotnet", 'echo "$@" > "$STUB_ARGS"\nexit "$STUB_CODE"\n')
        results = Path(work) / "results"
        # A retried pod finds its earlier attempt's files; the shard must start over.
        (results / f"shard-{index + 1}").mkdir(parents=True)
        (results / f"shard-{index + 1}" / "stale").write_text("x")
        env = dict(os.environ, PATH=f"{bin_dir}:{os.environ['PATH']}", JOB_COMPLETION_INDEX=str(index),
                   SWEEP_SHARDS=str(shards), SWEEP_RUN_PARALLELISM="2", SWEEP_RESULTS=str(results), STUB_CODE=str(code),
                   STUB_ARGS=str(Path(work) / "args"))
        result = subprocess.run(["sh", str(ENTRYPOINT)], env=env, capture_output=True, text=True)
        args = (Path(work) / "args").read_text().split()
        return result, args, results / f"shard-{index + 1}"

    def test_completion_index_runs_shard_index_plus_one_of_the_shards(self):
        result, args, shard = self.run_entrypoint(index=1, shards=3, code=0)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["/app/regionizer-sim.dll", "sweep", "/sweep/spec.json", "--out", str(shard),
                          "--scenarios", "/app/scenarios", "--shard", "2/3", "--parallelism", "2"], args)
        self.assertEqual("0", (shard / "exit-code").read_text().strip())
        self.assertFalse((shard / "stale").exists(), "a retried shard must start over")

    def test_a_violation_keeps_the_results_and_records_exit_3(self):
        result, _, shard = self.run_entrypoint(index=0, shards=2, code=3)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("3", (shard / "exit-code").read_text().strip())

    def test_any_other_failure_fails_the_pod(self):
        result, _, shard = self.run_entrypoint(index=0, shards=2, code=1)
        self.assertEqual(1, result.returncode)
        self.assertEqual("1", (shard / "exit-code").read_text().strip())

    def test_refuses_to_run_outside_an_indexed_job(self):
        env = {k: v for k, v in os.environ.items() if k != "JOB_COMPLETION_INDEX"}
        env.update(SWEEP_SHARDS="2", SWEEP_RUN_PARALLELISM="1")
        result = subprocess.run(["sh", str(ENTRYPOINT)], env=env, capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("indexed Job", result.stderr)


class JobManifest(unittest.TestCase):
    VALUES = {"SWEEP_NAME": "spacing", "NAMESPACE": "sweeps", "SHARDS": "4", "PARALLELISM": "2",
              "IMAGE": "mmo-engine/regionizer-sim:local", "RUNNER_CLASS": "k8s-kind", "CPU": "2",
              "MEMORY": "512Mi", "STORAGE": "5Gi", "ACCESS_MODE": "ReadWriteOnce"}

    def render(self, template=TEMPLATE):
        # k8s-sweep.sh renders with string.Template.substitute, which fails on a placeholder it lacks.
        text = string.Template(template.read_text()).substitute(self.VALUES)
        return {doc["kind"]: doc for doc in yaml.safe_load_all(text)}

    def test_the_reader_pod_mounts_the_jobs_results_volume_read_only(self):
        reader = self.render(SWEEPS / "manifests" / "sweep-reader.yaml")["Pod"]
        self.assertEqual("sweep-spacing-reader", reader["metadata"]["name"])
        claim = reader["spec"]["volumes"][0]["persistentVolumeClaim"]["claimName"]
        self.assertEqual(self.render()["PersistentVolumeClaim"]["metadata"]["name"], claim)
        self.assertEqual([{"name": "results", "mountPath": "/results", "readOnly": True}],
                         reader["spec"]["containers"][0]["volumeMounts"])

    def test_the_job_is_indexed_with_one_completion_per_shard(self):
        job = self.render()["Job"]["spec"]
        self.assertEqual("Indexed", job["completionMode"])
        self.assertEqual(4, job["completions"])
        self.assertEqual(2, job["parallelism"])
        self.assertEqual("Never", job["template"]["spec"]["restartPolicy"])
        container = job["template"]["spec"]["containers"][0]
        self.assertEqual("mmo-engine/regionizer-sim:local", container["image"])
        env = {e["name"]: e["value"] for e in container["env"]}
        self.assertEqual({"SWEEP_SHARDS": "4", "SWEEP_RUN_PARALLELISM": "2", "MMO_RUNNER_CLASS": "k8s-kind",
                          "DOTNET_EnableAVX512F": "0", "DOTNET_TieredPGO": "0"}, env)
        # Runs at once match the pod's CPUs, and the limit equals the request, so pods sharing a node don't
        # oversubscribe it (the wall-clock columns stay comparable).
        self.assertEqual({"cpu": "2"}, container["resources"]["limits"])
        self.assertEqual("2", container["resources"]["requests"]["cpu"])

    def test_pods_read_the_spec_configmap_and_write_the_shared_volume(self):
        docs = self.render()
        spec = docs["Job"]["spec"]["template"]["spec"]
        volumes = {v["name"]: v for v in spec["volumes"]}
        self.assertEqual("sweep-spacing-spec", volumes["spec"]["configMap"]["name"])
        self.assertEqual("sweep-spacing-results", volumes["results"]["persistentVolumeClaim"]["claimName"])
        self.assertEqual("sweep-spacing-results", docs["PersistentVolumeClaim"]["metadata"]["name"])
        mounts = {m["name"]: m["mountPath"] for m in spec["containers"][0]["volumeMounts"]}
        self.assertEqual({"spec": "/sweep", "results": "/results"}, mounts)

    def test_the_image_runs_the_entrypoint_with_the_repos_sdk(self):
        dockerfile = (SWEEPS / "Dockerfile").read_text()
        self.assertIn('ENTRYPOINT ["/app/job-entrypoint.sh"]', dockerfile)
        sdk = json.loads((ROOT / "global.json").read_text())["sdk"]["version"]
        self.assertIn(f"ARG SDK_VERSION={sdk}", dockerfile)
        # The image has no repository: the revision it was built from reaches manifest.json's gitSha.
        self.assertIn("ENV DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true MMO_GIT_SHA=${GIT_SHA}", dockerfile)
        self.assertIn('--build-arg "GIT_SHA=$GIT_SHA"', SCRIPT.read_text())
        self.assertTrue(os.access(ENTRYPOINT, os.X_OK), "job-entrypoint.sh must be executable")
        self.assertTrue(os.access(SCRIPT, os.X_OK), "k8s-sweep.sh must be executable")


class Script(unittest.TestCase):
    """k8s-sweep.sh with stub kubectl and docker: argument handling, and how it gives up on a Job that can't run.

    The stub kubectl logs every call to $STUB_LOG and answers `get jobs,pods` with the JSON in $STUB_STATE.
    """

    def run_script(self, *args, state=None, **env):
        work = tempfile.mkdtemp()
        bin_dir = Path(work) / "bin"
        bin_dir.mkdir()
        stub(bin_dir, "kubectl", 'printf "%s\\n" "$*" >> "$STUB_LOG"\n'
                                 'case "$*" in *"get jobs,pods"*) cat "$STUB_STATE" ;; *"-f -"*) cat >/dev/null ;; esac\n')
        for tool in ("docker", "kind", "k3d"):
            stub(bin_dir, tool, f"echo unexpected {tool} call >&2\nexit 99\n")
        state_file = Path(work) / "state.json"
        state_file.write_text(json.dumps({"items": state or []}))
        self.log = Path(work) / "kubectl.log"
        self.log.write_text("")
        full_env = dict(os.environ, PATH=f"{bin_dir}:{os.environ['PATH']}", STUB_LOG=str(self.log),
                        STUB_STATE=str(state_file), **env)
        return subprocess.run(["bash", str(SCRIPT), *args], env=full_env, capture_output=True, text=True,
                              timeout=60)

    def test_a_pod_that_cannot_get_its_image_fails_the_run_at_once_and_everything_is_removed(self):
        pod = {"kind": "Pod", "metadata": {"name": "sweep-infantry-radius-0-abc"},
               "status": {"phase": "Pending", "containerStatuses": [
                   {"state": {"waiting": {"reason": "ImagePullBackOff"}}}]}}
        job = {"kind": "Job", "status": {}}
        result = self.run_script("infantry-radius", "2", tempfile.mkdtemp(), state=[job, pod],
                                 HARNESS_CLUSTER="none", SWEEP_SKIP_BUILD="1")
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertIn("Stuck sweep-infantry-radius-0-abc ImagePullBackOff", result.stderr)
        deletes = [line for line in self.log.read_text().splitlines()
                   if line.startswith("-n sweeps delete job/sweep-infantry-radius pod/sweep-infantry-radius-reader "
                                      "pvc/sweep-infantry-radius-results configmap/sweep-infantry-radius-spec")]
        # Once before the run (an earlier run's leftovers) and once on the way out.
        self.assertEqual(2, len(deletes), self.log.read_text())

    def test_a_job_whose_pods_never_start_fails_after_the_start_timeout(self):
        pod = {"kind": "Pod", "metadata": {"name": "sweep-infantry-radius-0-abc"}, "status": {"phase": "Pending"}}
        result = self.run_script("infantry-radius", "2", tempfile.mkdtemp(), state=[pod],
                                 HARNESS_CLUSTER="none", SWEEP_SKIP_BUILD="1", SWEEP_START_TIMEOUT="0")
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertIn("Stuck: no pod started in 0s", result.stderr)

    def test_sweep_keep_leaves_the_job_and_its_volume(self):
        result = self.run_script("infantry-radius", "2", tempfile.mkdtemp(), state=[{"kind": "Job", "status": {
            "conditions": [{"type": "Failed", "status": "True"}]}}],
            HARNESS_CLUSTER="none", SWEEP_SKIP_BUILD="1", SWEEP_KEEP="1")
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertIn("job/sweep-infantry-radius: Failed", result.stderr)
        self.assertEqual(1, self.log.read_text().count("delete job/sweep-infantry-radius"))

    def test_every_kubectl_call_uses_the_clusters_context_and_its_own_runner_class(self):
        text = SCRIPT.read_text()
        self.assertIn('CONTEXT=(--context "$TOOL-$CLUSTER")', text)
        bare = [line for line in text.splitlines()
                if re.search(r"(^|[|;&]\s*)kubectl\b", line.strip()) and "k() {" not in line]
        self.assertEqual([], bare, "call kubectl through k(), which adds the context")
        # The Harness workflow sets MMO_RUNNER_CLASS for host runs; pods get their own class.
        self.assertIn('RUNNER_CLASS="${SWEEP_RUNNER_CLASS:-k8s-$TOOL}"', text)
        self.assertNotIn("MMO_RUNNER_CLASS", text)

    def test_refuses_more_shards_than_seeds(self):
        result = self.run_script("infantry-radius", "11", tempfile.mkdtemp())
        self.assertEqual(2, result.returncode)
        self.assertIn("shards must be 1 to the sweep's 10 seeds, was 11", result.stderr)

    def test_refuses_an_unknown_sweep(self):
        result = self.run_script("no-such-sweep", "1", tempfile.mkdtemp())
        self.assertEqual(2, result.returncode)
        self.assertIn("no sweep spec no-such-sweep", result.stderr)

    def test_refuses_a_fractional_cpu(self):
        result = self.run_script("infantry-radius", "2", tempfile.mkdtemp(), SWEEP_CPU="500m")
        self.assertEqual(2, result.returncode)
        self.assertIn("SWEEP_CPU must be a whole number of CPUs", result.stderr)

    def test_refuses_an_output_folder_that_holds_files(self):
        out = tempfile.mkdtemp()
        Path(out, "x").write_text("x")
        result = self.run_script("infantry-radius", "2", out)
        self.assertEqual(2, result.returncode)
        self.assertIn("is not empty", result.stderr)

    def test_refuses_an_unknown_cluster_tool_before_building_the_image(self):
        # docker is a stub that fails: the check comes before the build.
        result = self.run_script("infantry-radius", "2", tempfile.mkdtemp(), HARNESS_CLUSTER="minikube")
        self.assertEqual(2, result.returncode)
        self.assertIn("HARNESS_CLUSTER must be kind, k3d or none, was minikube", result.stderr)
        self.assertNotIn("unexpected docker call", result.stderr)

    def test_merges_the_trimmed_shards_and_exits_with_the_sweeps_code(self):
        text = SCRIPT.read_text()
        self.assertIn('infra/sweeps/trim.py" "$shard/$NAME"', text)
        self.assertIn('infra/sweeps/merge-shards.py" "$OUT/$NAME" "${trimmed[@]}"', text)
        self.assertRegex(text, r'if \[ "\$shard_code" = 3 \]; then\n.*\n\s*code=3')

    def test_smoke_runs_a_sweep_as_a_job_and_compares_it_with_a_plain_run(self):
        smoke = (ROOT / "infra" / "harness" / "smoke.sh").read_text()
        self.assertIn('"$ROOT/infra/sweeps/k8s-sweep.sh" "$OUT/sweep-spec.json" 2 "$OUT/job"', smoke)
        self.assertIn('sim sweep "$OUT/sweep-spec.json" --out "$OUT/plain"', smoke)
        self.assertIn('open(f"{job}/sweep.json").read() == open(f"{plain}/sweep.json").read()', smoke)
        self.assertTrue(re.search(r"assert job_rows == plain_rows", smoke))
        self.assertIn('manifest["gitSha"] == sys.argv[2]', smoke)


if __name__ == "__main__":
    unittest.main()
