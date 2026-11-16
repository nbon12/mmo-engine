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
import signal
import string
import subprocess
import tempfile
import time
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

    def test_sigterm_reaches_the_simulator_and_the_pod_stops_with_it(self):
        work = tempfile.mkdtemp()
        bin_dir = Path(work) / "bin"
        bin_dir.mkdir()
        marker = Path(work) / "terminated"
        # A stub simulator that runs until SIGTERM, then records it and exits 143.
        stub(bin_dir, "dotnet", f'trap \'touch "{marker}"; exit 143\' TERM\nwhile :; do sleep 0.1; done\n')
        env = dict(os.environ, PATH=f"{bin_dir}:{os.environ['PATH']}", JOB_COMPLETION_INDEX="0", SWEEP_SHARDS="1",
                   SWEEP_RUN_PARALLELISM="1", SWEEP_RESULTS=str(Path(work) / "results"))
        process = subprocess.Popen(["sh", str(ENTRYPOINT)], env=env)
        try:
            deadline = time.monotonic() + 10
            while not (Path(work) / "results" / "shard-1").exists() and time.monotonic() < deadline:
                time.sleep(0.05)
            time.sleep(0.3)
            process.send_signal(signal.SIGTERM)
            code = process.wait(timeout=10)
        finally:
            process.kill()
        self.assertTrue(marker.exists(), "the simulator never got SIGTERM")
        self.assertEqual(143, code)
        self.assertEqual("143", (Path(work) / "results" / "shard-1" / "exit-code").read_text().strip())

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
        self.assertEqual("Failed", job["podReplacementPolicy"])
        # A non-zero exit fails its index at once (the simulator is deterministic); disruptions are retried.
        rules = job["podFailurePolicy"]["rules"]
        self.assertEqual({"action": "Ignore", "onPodConditions": [{"type": "DisruptionTarget"}]}, rules[0])
        self.assertEqual({"action": "FailIndex", "onExitCodes": {"containerName": "sweep", "operator": "NotIn",
                                                                 "values": [0]}}, rules[1])
        container = job["template"]["spec"]["containers"][0]
        self.assertEqual("mmo-engine/regionizer-sim:local", container["image"])
        env = {e["name"]: e["value"] for e in container["env"]}
        self.assertEqual({"SWEEP_SHARDS": "4", "SWEEP_RUN_PARALLELISM": "2", "MMO_RUNNER_CLASS": "k8s-kind",
                          "DOTNET_EnableAVX512F": "0", "DOTNET_TieredPGO": "0"}, env)
        # Runs at once match the pod's CPUs, and the limit equals the request, so pods sharing a node don't
        # oversubscribe it (the wall-clock columns stay comparable).
        # Memory too, so the pod is Guaranteed QoS.
        self.assertEqual({"cpu": "2", "memory": "512Mi"}, container["resources"]["limits"])
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
        self.assertRegex(dockerfile, rf"FROM mcr\.microsoft\.com/dotnet/sdk:{re.escape(sdk)}@sha256:[0-9a-f]{{64}} AS build")
        self.assertRegex(dockerfile, r"FROM mcr\.microsoft\.com/dotnet/runtime:10\.0\.\d+@sha256:[0-9a-f]{64}\n")
        # The image has no repository: the revision it was built from reaches manifest.json's gitSha.
        self.assertIn("ENV DOTNET_NOLOGO=true DOTNET_CLI_TELEMETRY_OPTOUT=true MMO_GIT_SHA=${GIT_SHA}", dockerfile)
        self.assertIn('--build-arg "GIT_SHA=$GIT_SHA"', SCRIPT.read_text())
        # StackedAreas and Soak name simulator/maps/stacked-base.json, resolved upward from /app.
        self.assertIn("COPY simulator/maps/ /app/simulator/maps/", dockerfile)
        self.assertIn("WORKDIR /app", dockerfile)
        self.assertTrue(os.access(ENTRYPOINT, os.X_OK), "job-entrypoint.sh must be executable")

    def test_dependabot_proposes_runtime_patches_only(self):
        config = yaml.safe_load((ROOT / ".github" / "dependabot.yml").read_text())
        docker = next(u for u in config["updates"] if u["package-ecosystem"] == "docker")
        self.assertEqual("/infra/sweeps", docker["directory"])
        ignored = {i["dependency-name"]: i.get("update-types") for i in docker["ignore"]}
        self.assertIsNone(ignored["mcr.microsoft.com/dotnet/sdk"], "the SDK follows global.json")
        self.assertEqual(["version-update:semver-major", "version-update:semver-minor"],
                         ignored["mcr.microsoft.com/dotnet/runtime"])
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
                                 'case "$*" in *"get jobs,pods"*) cat "$STUB_STATE" ;; *"get pvc/"*) printf "%s" "${STUB_KEPT:-}" ;;'
                                 ' *"-f -"*) cat >/dev/null ;; esac\n')
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

    @staticmethod
    def waiting_pod(reason):
        return {"kind": "Pod", "metadata": {"name": "sweep-infantry-radius-0-abc"},
                "status": {"phase": "Pending", "containerStatuses": [{"state": {"waiting": {"reason": reason}}}]}}

    def test_a_pod_whose_image_can_never_be_pulled_fails_the_run_at_once_and_everything_is_removed(self):
        job = {"kind": "Job", "status": {}}
        result = self.run_script("infantry-radius", "2", tempfile.mkdtemp(),
                                 state=[job, self.waiting_pod("ErrImageNeverPull")],
                                 HARNESS_CLUSTER="none", SWEEP_SKIP_BUILD="1")
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertIn("Stuck sweep-infantry-radius-0-abc ErrImageNeverPull", result.stderr)
        deletes = [line for line in self.log.read_text().splitlines()
                   if line.startswith("-n sweeps delete job/sweep-infantry-radius pod/sweep-infantry-radius-reader "
                                      "configmap/sweep-infantry-radius-spec pvc/sweep-infantry-radius-results")]
        # Once before the run (an earlier run's leftovers) and once on the way out.
        self.assertEqual(2, len(deletes), self.log.read_text())

    def test_a_failing_pull_gets_a_grace_period_before_it_fails_the_run(self):
        started = time.monotonic()
        result = self.run_script("infantry-radius", "2", tempfile.mkdtemp(),
                                 state=[{"kind": "Job", "status": {}}, self.waiting_pod("ImagePullBackOff")],
                                 HARNESS_CLUSTER="none", SWEEP_SKIP_BUILD="1", SWEEP_PULL_GRACE="3")
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertIn("Stuck sweep-infantry-radius-0-abc ImagePullBackOff for 3s", result.stderr)
        # Not on the first poll: it kept polling through the grace period.
        self.assertGreaterEqual(time.monotonic() - started, 3)

    def test_a_stuck_pod_does_not_hide_the_shards_that_already_ran(self):
        done = {"kind": "Pod", "metadata": {"name": "sweep-infantry-radius-1-def"}, "status": {"phase": "Succeeded"}}
        result = self.run_script("infantry-radius", "2", tempfile.mkdtemp(),
                                 state=[{"kind": "Job", "status": {}}, done, self.waiting_pod("CreateContainerError")],
                                 HARNESS_CLUSTER="none")
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertIn("Stuck sweep-infantry-radius-0-abc CreateContainerError", result.stderr)
        self.assertIn("-n sweeps cp sweep-infantry-radius-reader:/results", self.log.read_text(),
                      "the finished shard must be copied out")

    def test_a_job_whose_pods_never_start_fails_after_the_start_timeout(self):
        pod = {"kind": "Pod", "metadata": {"name": "sweep-infantry-radius-0-abc"}, "status": {"phase": "Pending"}}
        result = self.run_script("infantry-radius", "2", tempfile.mkdtemp(), state=[pod],
                                 HARNESS_CLUSTER="none", SWEEP_SKIP_BUILD="1", SWEEP_START_TIMEOUT="0")
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertIn("Stuck: no pod started in 0s", result.stderr)

    def test_a_failed_job_copies_out_the_shards_that_ran_before_removing_the_volume(self):
        result = self.run_script("infantry-radius", "2", tempfile.mkdtemp(), state=[{"kind": "Job", "status": {
            "conditions": [{"type": "Failed", "status": "True"}]}}],
            HARNESS_CLUSTER="none", SWEEP_SKIP_BUILD="1")
        self.assertEqual(1, result.returncode, result.stderr)
        calls = self.log.read_text().splitlines()
        copy = next(i for i, c in enumerate(calls) if c.startswith("-n sweeps cp sweep-infantry-radius-reader:/results "))
        last_delete = max(i for i, c in enumerate(calls) if "pvc/sweep-infantry-radius-results" in c)
        self.assertLess(copy, last_delete, "the shards must be copied out before the volume is deleted")
        self.assertIn("kept the shards that ran", result.stderr)

    def test_a_volume_whose_shards_could_not_be_copied_out_is_kept(self):
        work = tempfile.mkdtemp()
        bin_dir = Path(work) / "bin"
        bin_dir.mkdir()
        log = Path(work) / "kubectl.log"
        state = Path(work) / "state.json"
        state.write_text(json.dumps({"items": [{"kind": "Job", "status": {
            "conditions": [{"type": "Failed", "status": "True"}]}}]}))
        stub(bin_dir, "kubectl", f'printf "%s\\n" "$*" >> "{log}"\n'
                                 f'case "$*" in *"get jobs,pods"*) cat "{state}" ;; *" cp "*) exit 1 ;; *"-f -"*) cat >/dev/null ;; esac\n')
        env = dict(os.environ, PATH=f"{bin_dir}:{os.environ['PATH']}", HARNESS_CLUSTER="none")
        result = subprocess.run(["bash", str(SCRIPT), "infantry-radius", "2", tempfile.mkdtemp()], env=env,
                                capture_output=True, text=True, timeout=60)
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertIn("kept pvc/sweep-infantry-radius-results", result.stderr)
        self.assertTrue(any(c.startswith("-n sweeps annotate --overwrite pvc/sweep-infantry-radius-results mmo-engine/kept=")
                            for c in log.read_text().splitlines()), "a kept volume must be marked")
        calls = log.read_text().splitlines()
        copy = next(i for i, c in enumerate(calls) if " cp " in f" {c} ")
        self.assertFalse(any("delete" in c and "pvc/" in c for c in calls[copy:]), "the volume must survive a failed copy")

    def test_a_failed_poll_is_retried_rather_than_ending_the_run(self):
        work = tempfile.mkdtemp()
        # The first `get jobs,pods` fails; the next one reports a stuck pod, which ends the run as usual.
        counter = Path(work) / "polls"
        pod = {"kind": "Pod", "metadata": {"name": "p-0"}, "status": {"phase": "Pending", "containerStatuses": [
            {"state": {"waiting": {"reason": "ErrImageNeverPull"}}}]}}
        bin_dir = Path(work) / "bin"
        bin_dir.mkdir()
        (Path(work) / "state.json").write_text(json.dumps({"items": [pod]}))
        stub(bin_dir, "kubectl", f'case "$*" in *"get jobs,pods"*)\n'
                                 f'  if [ ! -f "{counter}" ]; then touch "{counter}"; echo "connection refused" >&2; exit 1; fi\n'
                                 f'  cat "{work}/state.json" ;;\n'
                                 f'  *"-f -"*) cat >/dev/null ;;\nesac\n')
        env = dict(os.environ, PATH=f"{bin_dir}:{os.environ['PATH']}", HARNESS_CLUSTER="none", SWEEP_SKIP_BUILD="1")
        result = subprocess.run(["bash", str(SCRIPT), "infantry-radius", "2", tempfile.mkdtemp()], env=env,
                                capture_output=True, text=True, timeout=60)
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertIn("Stuck p-0 ErrImageNeverPull", result.stderr)

    def test_a_volume_an_earlier_run_kept_is_never_deleted_without_being_told(self):
        result = self.run_script("infantry-radius", "2", tempfile.mkdtemp(), HARNESS_CLUSTER="none",
                                 STUB_KEPT="2026-11-16T11:00:00Z")
        self.assertEqual(2, result.returncode, result.stderr)
        self.assertIn("was kept by an earlier run (2026-11-16T11:00:00Z)", result.stderr)
        self.assertNotIn("delete", self.log.read_text())

    def test_sweep_discard_kept_lets_the_run_replace_a_kept_volume(self):
        result = self.run_script("infantry-radius", "2", tempfile.mkdtemp(), state=[{"kind": "Job", "status": {
            "conditions": [{"type": "Failed", "status": "True"}]}}],
            HARNESS_CLUSTER="none", STUB_KEPT="2026-11-16T11:00:00Z", SWEEP_DISCARD_KEPT="1", SWEEP_KEEP="1")
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertIn("delete job/sweep-infantry-radius pod/sweep-infantry-radius-reader "
                      "configmap/sweep-infantry-radius-spec pvc/sweep-infantry-radius-results", self.log.read_text())

    def test_sweep_keep_leaves_the_job_and_its_volume(self):
        result = self.run_script("infantry-radius", "2", tempfile.mkdtemp(), state=[{"kind": "Job", "status": {
            "conditions": [{"type": "Failed", "status": "True"}]}}],
            HARNESS_CLUSTER="none", SWEEP_SKIP_BUILD="1", SWEEP_KEEP="1")
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertIn("job/sweep-infantry-radius: Failed", result.stderr)
        self.assertIn("SWEEP_KEEP=1: left job/sweep-infantry-radius", result.stderr)
        # Only the clean-up before the run deletes anything: the failed Job and its volume are left as they are.
        self.assertEqual(1, self.log.read_text().count("delete job/sweep-infantry-radius"))
        self.assertNotIn(" cp ", self.log.read_text())

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

    def test_refuses_a_shard_count_with_a_leading_zero(self):
        # YAML would read 010 as octal 8 completions.
        result = self.run_script("infantry-radius", "010", tempfile.mkdtemp())
        self.assertEqual(2, result.returncode)
        self.assertIn("shards must be 1 to the sweep's 10 seeds, was 010", result.stderr)

    def test_refuses_timeouts_and_pod_counts_that_are_not_whole_numbers(self):
        for env, message in ((dict(SWEEP_TIMEOUT="6h"), "SWEEP_TIMEOUT must be whole seconds, was 6h"),
                             (dict(SWEEP_PULL_GRACE="2m"), "SWEEP_PULL_GRACE must be whole seconds, was 2m"),
                             (dict(SWEEP_PARALLELISM="0"), "SWEEP_PARALLELISM must be a whole number of pods, was 0")):
            result = self.run_script("infantry-radius", "2", tempfile.mkdtemp(), **env)
            self.assertEqual(2, result.returncode, env)
            self.assertIn(message, result.stderr)
            self.assertEqual("", self.log.read_text(), "nothing may reach the cluster")

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

    @staticmethod
    def volume(root, runs_by_shard, codes):
        """The results volume of a finished 2-cell, 2-seed infantry-radius Job: shard-<k>/infantry-radius/ holding
        that shard's runs (full results, ticks.csv included) and shard-<k>/exit-code."""
        for k, runs in runs_by_shard.items():
            folder = Path(root) / f"shard-{k}" / "infantry-radius"
            index = {"formatVersion": 1, "name": "infantry-radius", "scenario": "Baseline", "mode": "OneAtATime",
                     "sweepSeed": 3, "seeds": [{"index": 0, "seed": 10}, {"index": 1, "seed": 11}], "cells": []}
            rows = []
            for cell in ("c000", "c001"):
                mine = [i for c, i in runs if c == cell]
                index["cells"].append({"cellId": cell, "parameters": {"p": cell}, "runs": [
                    {"seedIndex": i, "seed": 10 + i, "folder": f"{cell}/seed-{i}", "completed": True} for i in mine]})
                for i in mine:
                    run = folder / cell / f"seed-{i}"
                    run.mkdir(parents=True)
                    for name in ("summary.json", "manifest.json", "ticks.csv"):
                        (run / name).write_text(f"{cell}-{i} {name}")
                    rows.append(f"{cell},{i},{10 + i},run-{cell}-{i}")
            (folder / "sweep.json").write_text(json.dumps(index))
            (folder / "cells.csv").write_text("cellId,seedIndex,seed,runId\n" + "".join(r + "\n" for r in rows))
            (Path(root) / f"shard-{k}" / "exit-code").write_text(f"{codes[k]}\n")

    def run_complete_job(self, codes):
        """Runs the script against a stub cluster whose Job completed; `kubectl cp` copies a fake volume."""
        work = tempfile.mkdtemp()
        volume = Path(work) / "volume"
        self.volume(volume, {1: [("c000", 0), ("c001", 0)], 2: [("c000", 1), ("c001", 1)]}, codes)
        bin_dir = Path(work) / "bin"
        bin_dir.mkdir()
        state = Path(work) / "state.json"
        state.write_text(json.dumps({"items": [{"kind": "Job", "status": {
            "conditions": [{"type": "Complete", "status": "True"}]}}]}))
        stub(bin_dir, "kubectl", f'case "$*" in\n'
                                 f'  *"get jobs,pods"*) cat "{state}" ;;\n'
                                 f'  *" cp "*) eval "dest=\\${{$#}}"; cp -r "{volume}" "$dest" ;;\n'
                                 f'  *"-f -"*) cat >/dev/null ;;\nesac\n')
        out = Path(work) / "out"
        env = dict(os.environ, PATH=f"{bin_dir}:{os.environ['PATH']}", HARNESS_CLUSTER="none")
        result = subprocess.run(["bash", str(SCRIPT), "infantry-radius", "2", str(out)], env=env,
                                capture_output=True, text=True, timeout=60)
        return result, out

    def test_a_completed_job_is_copied_out_trimmed_and_merged_into_one_sweep(self):
        result, out = self.run_complete_job({1: 0, 2: 0})
        self.assertEqual(0, result.returncode, result.stderr)
        merged = out / "infantry-radius"
        index = json.loads((merged / "sweep.json").read_text())
        self.assertEqual([[0, 1], [0, 1]], [[r["seedIndex"] for r in c["runs"]] for c in index["cells"]])
        self.assertEqual(["cellId,seedIndex,seed,runId", "c000,0,10,run-c000-0", "c000,1,11,run-c000-1",
                          "c001,0,10,run-c001-0", "c001,1,11,run-c001-1"],
                         (merged / "cells.csv").read_text().splitlines())
        # Trimmed: what the memo reads, without per-tick files; the full shards stay under shards/.
        self.assertEqual(["manifest.json", "summary.json"], sorted(p.name for p in (merged / "c001" / "seed-1").iterdir()))
        self.assertTrue((out / "_shards" / "shard-2" / "infantry-radius" / "c001" / "seed-1" / "ticks.csv").exists())

    def test_a_shard_that_exited_3_still_merges_and_the_script_exits_3(self):
        result, out = self.run_complete_job({1: 0, 2: 3})
        self.assertEqual(3, result.returncode, result.stderr)
        # On stderr, so callers that discard stdout (smoke.sh) still show it.
        self.assertIn("shard 2/2 exited 3", result.stderr)
        self.assertTrue((out / "infantry-radius" / "sweep.json").exists())

    def test_smoke_runs_a_sweep_as_a_job_and_compares_it_with_a_plain_run(self):
        smoke = (ROOT / "infra" / "harness" / "smoke.sh").read_text()
        self.assertIn('"$ROOT/infra/sweeps/k8s-sweep.sh" "$OUT/sweep-spec.json" 2 "$OUT/job"', smoke)
        self.assertIn('sim sweep "$OUT/sweep-spec.json" --out "$OUT/plain"', smoke)
        self.assertIn('open(f"{job}/sweep.json").read() == open(f"{plain}/sweep.json").read()', smoke)
        self.assertTrue(re.search(r"assert job_rows == plain_rows", smoke))
        self.assertIn('manifest["gitSha"] == sys.argv[2]', smoke)


if __name__ == "__main__":
    unittest.main()
