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
                   SWEEP_SHARDS=str(shards), SWEEP_RESULTS=str(results), STUB_CODE=str(code),
                   STUB_ARGS=str(Path(work) / "args"))
        result = subprocess.run(["sh", str(ENTRYPOINT)], env=env, capture_output=True, text=True)
        args = (Path(work) / "args").read_text().split()
        return result, args, results / f"shard-{index + 1}"

    def test_completion_index_runs_shard_index_plus_one_of_the_shards(self):
        result, args, shard = self.run_entrypoint(index=1, shards=3, code=0)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["/app/regionizer-sim.dll", "sweep", "/sweep/spec.json", "--out", str(shard),
                          "--scenarios", "/app/scenarios", "--shard", "2/3"], args)
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
        env["SWEEP_SHARDS"] = "2"
        result = subprocess.run(["sh", str(ENTRYPOINT)], env=env, capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("indexed Job", result.stderr)


class JobManifest(unittest.TestCase):
    VALUES = {"SWEEP_NAME": "spacing", "NAMESPACE": "sweeps", "SHARDS": "4", "PARALLELISM": "2",
              "IMAGE": "mmo-engine/regionizer-sim:local", "RUNNER_CLASS": "k8s-kind", "CPU": "1",
              "MEMORY": "512Mi", "STORAGE": "5Gi", "ACCESS_MODE": "ReadWriteOnce"}

    def render(self):
        # k8s-sweep.sh renders with string.Template.substitute, which fails on a placeholder it lacks.
        text = string.Template(TEMPLATE.read_text()).substitute(self.VALUES)
        return {doc["kind"]: doc for doc in yaml.safe_load_all(text)}

    def test_the_job_is_indexed_with_one_completion_per_shard(self):
        job = self.render()["Job"]["spec"]
        self.assertEqual("Indexed", job["completionMode"])
        self.assertEqual(4, job["completions"])
        self.assertEqual(2, job["parallelism"])
        self.assertEqual("Never", job["template"]["spec"]["restartPolicy"])
        container = job["template"]["spec"]["containers"][0]
        self.assertEqual("mmo-engine/regionizer-sim:local", container["image"])
        env = {e["name"]: e["value"] for e in container["env"]}
        self.assertEqual({"SWEEP_SHARDS": "4", "MMO_RUNNER_CLASS": "k8s-kind", "DOTNET_EnableAVX512F": "0",
                          "DOTNET_TieredPGO": "0"}, env)

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
        self.assertTrue(os.access(ENTRYPOINT, os.X_OK), "job-entrypoint.sh must be executable")
        self.assertTrue(os.access(SCRIPT, os.X_OK), "k8s-sweep.sh must be executable")


class Script(unittest.TestCase):
    """k8s-sweep.sh's argument handling, before it touches a cluster (kubectl is a stub that fails)."""

    def run_script(self, *args, **env):
        bin_dir = tempfile.mkdtemp()
        stub(bin_dir, "kubectl", "echo unexpected kubectl call >&2\nexit 99\n")
        full_env = dict(os.environ, PATH=f"{bin_dir}:{os.environ['PATH']}", **env)
        return subprocess.run(["bash", str(SCRIPT), *args], env=full_env, capture_output=True, text=True)

    def test_refuses_more_shards_than_seeds(self):
        result = self.run_script("infantry-radius", "11", tempfile.mkdtemp())
        self.assertEqual(2, result.returncode)
        self.assertIn("shards must be 1 to the sweep's 10 seeds, was 11", result.stderr)

    def test_refuses_an_unknown_sweep(self):
        result = self.run_script("no-such-sweep", "1", tempfile.mkdtemp())
        self.assertEqual(2, result.returncode)
        self.assertIn("no sweep spec no-such-sweep", result.stderr)

    def test_refuses_an_output_folder_that_holds_files(self):
        out = tempfile.mkdtemp()
        Path(out, "x").write_text("x")
        result = self.run_script("infantry-radius", "2", out)
        self.assertEqual(2, result.returncode)
        self.assertIn("is not empty", result.stderr)

    def test_refuses_an_unknown_cluster_tool(self):
        result = self.run_script("infantry-radius", "2", tempfile.mkdtemp(), HARNESS_CLUSTER="minikube",
                                 SWEEP_SKIP_BUILD="1")
        self.assertEqual(2, result.returncode)
        self.assertIn("HARNESS_CLUSTER must be kind, k3d or none, was minikube", result.stderr)

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


if __name__ == "__main__":
    unittest.main()
