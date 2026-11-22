"""Contract tests for .github/workflows/simcore.yml and its auto-merge listener (spec 015-shared-simulation-core).

The SimCore workflow's `agreement` job runs the shared simulation core's agreement suite on GitHub's
runners: it publishes the prebuilt WebAssembly build, prepares a work directory with the committed
streams and 50 generated seeds, runs the build in Node with the jiterpreter on and off and in browser
Workers, and gates the pull request on `simcore agreement compare`. These tests pin the configuration
that job depends on:

- the triggers and path filters, so a change to the core, its analyzer, its recorded streams or its
  build runs the suite (US2.7, FR-027), and the read-only token;
- the SHA-pinned actions, the exact Playwright pin and the toolchains;
- the agreement steps, their order and the compare gate (US2.7, SC-001), the 10-minute timeout
  (SC-006), each step's own bound inside it, and the three browser projects;
- the `"SimCore"` entry in auto-merge.yml's workflow_run list, so the pull request waits for the gate,
  and that auto-merge's own check filter holds the merge on a failed or pending `agreement` check.

The suite itself (the per-stream, batch, perturbation, refusal and tables checks) is
tools/agreement/node/agreement.test.mjs and tools/agreement/specs/worker.spec.mjs.

Run: python3 -m unittest tests/workflows/test_simcore_workflow.py -v   (needs PyYAML)
 or: python3 -m unittest discover -s tests/workflows -v
"""

import importlib.util
import json
import os
import re
import subprocess
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

import yaml

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW_PATH = ROOT / ".github" / "workflows" / "simcore.yml"
WORKFLOW = yaml.safe_load(WORKFLOW_PATH.read_text())
# PyYAML reads the `on:` key as True.
TRIGGERS = WORKFLOW.get("on", WORKFLOW.get(True))
AUTO_MERGE = yaml.safe_load((ROOT / ".github" / "workflows" / "auto-merge.yml").read_text())
AGREEMENT_TOOLS = ROOT / "tools" / "agreement"

# The repo's evaluator for GitHub expressions, loaded by path so that this file runs both on its own
# (`python3 -m unittest tests/workflows/test_simcore_workflow.py`) and under `discover`.
_GH_EXPR_SPEC = importlib.util.spec_from_file_location("gh_expr", Path(__file__).resolve().parent / "gh_expr.py")
gh_expr = importlib.util.module_from_spec(_GH_EXPR_SPEC)
_GH_EXPR_SPEC.loader.exec_module(gh_expr)

# The eleven path filters of FR-027: the core and its builds, their tests and benchmarks, the
# agreement runners and tools, the recorded streams and content, and the build files.
SIMCORE_PATHS = (
    "src/MmoEngine.SimCore*/**",
    "tests/MmoEngine.SimCore*/**",
    "benchmarks/MmoEngine.SimCore*/**",
    "tools/agreement/**",
    "tools/simcore/**",
    "simcore/**",
    "Directory.Build.props",
    "Directory.Packages.props",
    "global.json",
    "MmoEngine.sln",
    ".github/workflows/simcore.yml",
)

# The runs of the agreement runners: the Node runner and the Playwright Worker runner.
RUNNER_RUNS = ("node --test tools/agreement/node/agreement.test.mjs", "npx playwright test")

# The runners' unit tests: the tables comparison, the agreement server and the record helpers.
RUNNER_UNIT_TESTS = ("node --test tools/agreement/node/tables.test.mjs tools/agreement/node/server.test.mjs"
                     " tools/agreement/node/runtime.test.mjs")

# The actions that set up the job's toolchains; every step after them has its own timeout-minutes.
TOOLCHAIN_ACTIONS = ("actions/checkout@", "actions/setup-dotnet@", "actions/setup-node@")

REQUIRED_BUILDS = "node-interp,node-interp-nojit,chromium-worker"
SHA_PINNED = r"^[\w.-]+/[\w.-]+@[0-9a-f]{40}$"


def steps(job):
    return WORKFLOW["jobs"][job]["steps"]


def runs(job):
    return "\n".join(step.get("run", "") for step in steps(job))


def all_steps():
    """(job name, step) for every step of every job."""
    for name, job in WORKFLOW["jobs"].items():
        for step in job.get("steps", []):
            yield name, step


def command(step):
    """A step's `run` with backslash-continued lines joined and runs of blanks collapsed, so a command
    matches however the workflow wraps it."""
    text = re.sub(r"\\\n\s*", " ", step.get("run", ""))
    return re.sub(r"[ \t]+", " ", text)


def indexes(job, text):
    """The indexes of the job's steps whose run contains `text`."""
    return [i for i, step in enumerate(steps(job)) if text in command(step)]


def only_step(job, text):
    """(index, step) of the one step of the job whose run contains `text`."""
    found = indexes(job, text)
    if len(found) != 1:
        raise AssertionError(f"expected exactly one {job} step running {text!r}, found {len(found)}")
    return found[0], steps(job)[found[0]]


def runner_indexes():
    """The indexes of the agreement job's runner steps, ascending."""
    found = sorted(i for text in RUNNER_RUNS for i in indexes("agreement", text))
    if not found:
        raise AssertionError("the agreement job runs no agreement runner")
    return found


def condition(step):
    """A step's `if` without the optional `${{ }}` wrapper and without blanks."""
    text = str(step.get("if", "")).strip()
    wrapped = re.fullmatch(r"\$\{\{(.*)\}\}", text, re.S)
    return re.sub(r"\s+", "", wrapped.group(1) if wrapped else text)


def playwright_test_timeout_ms():
    """The `timeout` of tools/agreement/playwright.config.mjs in milliseconds, written as a product of
    integers (`2 * 60 * 1000`)."""
    config = (AGREEMENT_TOOLS / "playwright.config.mjs").read_text()
    found = re.findall(r"^\s*timeout\s*:\s*([0-9_][0-9_\s*]*),", config, re.M)
    if len(found) != 1:
        raise AssertionError(f"expected one `timeout:` in playwright.config.mjs, found {found}")
    value = 1
    for factor in found[0].split("*"):
        value *= int(factor.strip().replace("_", ""))
    return value


def run_jq(filter_, document):
    result = subprocess.run(["jq", "-r", filter_], input=json.dumps(document), capture_output=True,
                            text=True, check=True, timeout=30)
    return result.stdout


def run_awk(program, text):
    """Runs one of auto-merge.yml's `awk -F'\\t' '<program>'` lines on `text`, as bash would."""
    result = subprocess.run(["awk", "-F", "\\t", program], input=text, capture_output=True, text=True,
                            check=True, timeout=30)
    return result.stdout


def evaluate(expression, context):
    return gh_expr.truthy(gh_expr.Expr(expression, context).evaluate())


def render(template, context):
    """Substitutes each `${{ ... }}` in a workflow string, as the runner would."""
    return re.sub(r"\$\{\{(.*?)\}\}", lambda m: gh_expr.to_str(gh_expr.Expr(m.group(1), context).evaluate()), template)


def expand(text, runner_temp, env=None):
    """A workflow value with `${{ runner.temp }}`, `${{ env.X }}` and `$RUNNER_TEMP` replaced."""
    text = render(text, {"runner": {"temp": runner_temp}, "env": env or {}})
    return re.sub(r"\$\{?RUNNER_TEMP\}?", runner_temp, text)


def matches_glob(pattern, path):
    """GitHub's path filter glob: `**` matches any characters, `*` and `?` anything but `/`."""
    regex = re.sub(r"\\\*\\\*|\\\*|\\\?", lambda m: {r"\*\*": ".*", r"\*": "[^/]*", r"\?": "[^/]"}[m.group(0)],
                   re.escape(pattern))
    return re.fullmatch(regex, path) is not None


def triggers_on(paths, changed):
    """Whether a change to `changed` passes the `paths` filter (a later `!pattern` excludes again)."""
    selected = False
    for pattern in paths:
        if pattern.startswith("!"):
            selected = selected and not matches_glob(pattern[1:], changed)
        elif matches_glob(pattern, changed):
            selected = True
    return selected


def parse_github_env(text):
    """The variables a step appended to $GITHUB_ENV, in `NAME=value` or `NAME<<DELIMITER` form."""
    values, lines = {}, iter(text.splitlines())
    for line in lines:
        name, sep, delimiter = line.partition("<<")
        if sep and "=" not in name:
            body = []
            for inner in lines:
                if inner == delimiter:
                    break
                body.append(inner)
            values[name] = "\n".join(body)
        elif "=" in line:
            name, value = line.split("=", 1)
            values[name] = value
    return values


def run_env_step():
    """Runs the agreement job's step that appends AGREEMENT_APP to $GITHUB_ENV under bash, as the runner
    does (`bash --noprofile --norc -eo pipefail`), with a temporary RUNNER_TEMP and GITHUB_ENV.

    Returns its index, the RUNNER_TEMP it ran with, the variables it appended, and whether
    AGREEMENT_WORK was still absent or an empty directory afterwards (`agreement prepare` refuses
    anything else).
    """
    found = [i for i, step in enumerate(steps("agreement"))
             if "AGREEMENT_APP=" in step.get("run", "") and "GITHUB_ENV" in step.get("run", "")]
    if len(found) != 1:
        raise AssertionError(f"expected exactly one agreement step appending AGREEMENT_APP to $GITHUB_ENV, found {len(found)}")
    step = steps("agreement")[found[0]]
    if re.search(r"\b(dotnet|npm|npx|node)\b", step["run"]):
        raise AssertionError("the step that sets AGREEMENT_APP and AGREEMENT_WORK must only set them")
    with tempfile.TemporaryDirectory() as temp:
        runner_temp = str(Path(temp) / "runner-temp")
        os.mkdir(runner_temp)
        github_env = Path(temp) / "github-env"
        github_env.write_text("")
        script = render(step["run"], {"runner": {"temp": runner_temp}, "env": {}})
        subprocess.run(["bash", "--noprofile", "--norc", "-eo", "pipefail", "-c", script],
                       env={"PATH": os.environ["PATH"], "HOME": temp, "RUNNER_TEMP": runner_temp,
                            "GITHUB_ENV": str(github_env)},
                       cwd=temp, check=True, capture_output=True, text=True, timeout=30)
        values = parse_github_env(github_env.read_text())
        work = Path(values.get("AGREEMENT_WORK", runner_temp))
        work_fresh = not work.exists() or (work.is_dir() and not any(work.iterdir()))
    return SimpleNamespace(index=found[0], runner_temp=runner_temp, values=values, work_fresh=work_fresh)


class TriggersAndPaths(unittest.TestCase):
    """US2.7: "**Given** a pull request that changes the shared core or its recorded streams, **When** CI
    runs, **Then** the agreement suite runs and its result gates the pull request." FR-027: "The
    agreement suite MUST run in CI on every pull request that changes the core, its analyzer, its
    recorded streams or its build, and its result MUST gate the pull request." This class is the
    "runs" half; AgreementJob.test_compare_gates_the_job and AutoMerge are the gate."""

    def test_runs_on_pull_requests_and_main_for_simcore_paths(self):
        for event in ("pull_request", "push"):
            paths = TRIGGERS[event]["paths"]
            for path in SIMCORE_PATHS:
                self.assertIn(path, paths, f"{event} must trigger on {path}")
            # A change to each part FR-027 names passes the filter (no later `!` pattern drops it).
            for part, changed in (
                    ("the core", "src/MmoEngine.SimCore/Movement/CharacterMover.cs"),
                    ("its analyzer", "src/MmoEngine.SimCore.Analyzers/DeterminismAnalyzer.cs"),
                    ("the server build", "src/MmoEngine.SimCore.Server/StreamGenerator.cs"),
                    ("the WebAssembly host", "src/MmoEngine.SimCore.Wasm/SimCoreExports.cs"),
                    ("its recorded streams", "simcore/streams/flat-run-30hz.mmrs"),
                    ("its content", "simcore/content/test-world.mmcd"),
                    ("its tests", "tests/MmoEngine.SimCore.Tests/Batch/CrossBuildTablesTests.cs"),
                    ("the agreement runners", "tools/agreement/web/worker.mjs"),
                    ("its build", "Directory.Packages.props"),
                    ("its SDK", "global.json"),
                    ("this workflow", ".github/workflows/simcore.yml")):
                with self.subTest(event=event, part=part):
                    self.assertTrue(triggers_on(paths, changed), f"{event}: a change to {part} ({changed}) must run the suite")
        self.assertEqual(["main"], TRIGGERS["push"]["branches"])

    def test_workflow_is_named_simcore(self):
        # The name auto-merge.yml's workflow_run trigger listens for (AutoMerge below).
        self.assertEqual("SimCore", WORKFLOW["name"])

    def test_token_is_read_only(self):
        self.assertEqual({"contents": "read"}, WORKFLOW["permissions"])
        for name, job in WORKFLOW["jobs"].items():
            self.assertNotIn("write", json.dumps(job.get("permissions", {})), f"{name} must not widen the token")


class Pins(unittest.TestCase):
    """Every action is pinned to a commit, and the Playwright the runners use is pinned exactly."""

    def test_every_action_is_pinned_to_a_commit(self):
        uses = [(name, step["uses"]) for name, step in all_steps() if "uses" in step]
        self.assertTrue(uses, "the workflow uses no action")
        for name, ref in uses:
            self.assertRegex(ref, SHA_PINNED, f"{name}: {ref}")

    def test_checkouts_do_not_persist_credentials(self):
        checkouts = [(name, step) for name, step in all_steps() if step.get("uses", "").startswith("actions/checkout@")]
        self.assertIn("agreement", [name for name, _ in checkouts], "the agreement job must check out the repository")
        for name, step in checkouts:
            self.assertIs(False, step.get("with", {}).get("persist-credentials"), name)

    def test_playwright_is_pinned_exactly(self):
        package = json.loads((AGREEMENT_TOOLS / "package.json").read_text())
        self.assertEqual({"@playwright/test": "1.63.0"}, package["devDependencies"])
        self.assertFalse(package.get("dependencies"), "the runners have no runtime dependencies")
        lock = json.loads((AGREEMENT_TOOLS / "package-lock.json").read_text())
        self.assertEqual(3, lock["lockfileVersion"])
        self.assertEqual("1.63.0", lock["packages"][""]["devDependencies"]["@playwright/test"])
        self.assertEqual("1.63.0", lock["packages"]["node_modules/@playwright/test"]["version"])

    def test_npm_ci_installs_from_the_lockfile(self):
        ci = [i for i, step in enumerate(steps("agreement"))
              if re.search(r"(^|[\n;&|]\s*)npm ci(\s|$)", command(step))
              and step.get("working-directory") == "tools/agreement"]
        self.assertTrue(ci, "a step must run `npm ci` with working-directory: tools/agreement")
        # npx then resolves the lockfile's Playwright, not a download of the latest one.
        npx = indexes("agreement", "npx playwright")
        self.assertTrue(npx)
        self.assertLess(ci[0], min(npx))
        for name, step in all_steps():
            self.assertNotRegex(command(step), r"\bnpm\s+(install|i|add)\b", f"{name}: {step.get('name')}")

    def test_toolchains_come_from_global_json_and_node_22(self):
        dotnet = [(name, step) for name, step in all_steps() if step.get("uses", "").startswith("actions/setup-dotnet@")]
        node = [(name, step) for name, step in all_steps() if step.get("uses", "").startswith("actions/setup-node@")]
        self.assertIn("agreement", [name for name, _ in dotnet])
        self.assertIn("agreement", [name for name, _ in node])
        for name, step in dotnet:
            self.assertEqual("global.json", step.get("with", {}).get("global-json-file"), name)
        for name, step in node:
            self.assertEqual("22", step.get("with", {}).get("node-version"), name)


class AgreementJob(unittest.TestCase):
    """The gating `agreement` job.

    SC-001: "Every committed recorded stream, at least 10 named movement, weapon and projectile
    scenarios plus generated streams from 50 seeds, gives identical per-step hashes in the server
    build, the WebAssembly build in Node and the WebAssembly build in a browser Worker: zero divergent
    steps." SC-006: "The agreement suite, including the browser Worker run, completes in under 10
    minutes on a CI runner." US2.7's gate: `simcore agreement compare` exits non-zero on any divergent
    step or any required build that did not run, and nothing turns that exit into a pass."""

    def test_compare_gates_the_job(self):
        index, step = only_step("agreement", "agreement compare --work")
        text = command(step)
        self.assertIn('agreement compare --work "$AGREEMENT_WORK"', text)
        self.assertIn('--summary "$GITHUB_STEP_SUMMARY"', text)
        self.assertNotIn("continue-on-error", step)
        self.assertNotIn("continue-on-error", WORKFLOW["jobs"]["agreement"])
        # `!cancelled()`, not `always()` or the implicit `success()`: it reports after a runner failed.
        self.assertEqual("!cancelled()", condition(step))
        self.assertTrue(str(step["if"]).strip().startswith("${{"), step["if"])
        self.assertNotIn("||", text, "nothing may turn compare's exit code into a pass")
        requires = [value.strip("'\"") for value in re.findall(r"--require(?:=|\s+)(\S+)", text)]
        self.assertIn(requires, ([], [REQUIRED_BUILDS]))
        self.assertGreater(index, max(runner_indexes()))
        # Every runner writes where compare reads: none points AGREEMENT_WORK elsewhere.
        for i in runner_indexes():
            self.assertNotIn("AGREEMENT_WORK", steps("agreement")[i].get("env", {}), steps("agreement")[i].get("name"))

    def test_agreement_job_times_out_after_ten_minutes(self):
        self.assertEqual(10, WORKFLOW["jobs"]["agreement"]["timeout-minutes"])

    def test_prebuilt_wasm_is_published_before_any_workload(self):
        index, step = only_step("agreement", "dotnet publish src/MmoEngine.SimCore.Wasm -c Release")
        for other in steps("agreement"):
            self.assertNotIn("dotnet workload", json.dumps(other), other.get("name"))
        self.assertLess(index, runner_indexes()[0])
        # The runners load AGREEMENT_APP, the served root of this publish output.
        out = re.search(r"\s-o\s+\"?([^\"\s]+)\"?", command(step))
        self.assertIsNotNone(out, "the publish names its output directory with -o")
        env = run_env_step()
        self.assertEqual(expand(out.group(1), env.runner_temp) + "/wwwroot", env.values["AGREEMENT_APP"])

    def test_prepare_writes_fifty_seeds_to_a_fresh_work_directory(self):
        env = run_env_step()
        self.assertIn("AGREEMENT_APP", env.values)
        self.assertIn("AGREEMENT_WORK", env.values)
        app, work = env.values["AGREEMENT_APP"], env.values["AGREEMENT_WORK"]
        self.assertRegex(app, rf"^{re.escape(env.runner_temp)}/.+/wwwroot$")
        self.assertRegex(work, rf"^{re.escape(env.runner_temp)}/[^/].*$")
        publish_dir = str(Path(app).parent)
        self.assertFalse(work == publish_dir or work.startswith(publish_dir + "/"),
                         "the work directory must not be inside the publish output")
        self.assertTrue(env.work_fresh, "AGREEMENT_WORK must be absent or empty for agreement prepare")
        index, _ = only_step("agreement", 'agreement prepare --seeds 1-50 --work "$AGREEMENT_WORK"')
        self.assertGreater(index, env.index)
        self.assertLess(index, runner_indexes()[0])

    def test_runner_unit_tests_run_before_the_agreement(self):
        # runtime.test.mjs pins firstDifferingStep, which decides the Worker's `stream` and `batch` events.
        index, step = only_step("agreement", RUNNER_UNIT_TESTS)
        self.assertLess(index, runner_indexes()[0])
        self.assertNotIn("continue-on-error", step)
        self.assertNotIn("if", step)

    def test_every_step_after_the_toolchain_setup_has_its_own_bound(self):
        # A step that hangs then fails on its own instead of the job being cancelled at 10 minutes, so
        # compare (`!cancelled()`) still runs and the report is still uploaded (`failure()`).
        job_bound = WORKFLOW["jobs"]["agreement"]["timeout-minutes"]
        all_job_steps = steps("agreement")
        toolchain = [i for i, step in enumerate(all_job_steps) if step.get("uses", "").startswith(TOOLCHAIN_ACTIONS)]
        self.assertEqual(list(range(3)), toolchain, "checkout and the two toolchain setups come first")
        for step in all_job_steps[3:]:
            with self.subTest(step=step.get("name")):
                bound = step.get("timeout-minutes")
                self.assertIsInstance(bound, int, "timeout-minutes is a whole number of minutes")
                self.assertGreater(bound, 0)
                self.assertLess(bound, job_bound)

    def test_optional_browsers_install_after_the_required_runs_within_three_minutes(self):
        # A slow Firefox or WebKit install cannot use up the required runs' time, and one that times
        # out fails only itself (continue-on-error), so its run is skipped and compare reports it as
        # not run.
        required = [i for text in ("node --test tools/agreement/node/agreement.test.mjs",
                                   "npx playwright test --project chromium")
                    for i in indexes("agreement", text)]
        self.assertEqual(3, len(required))
        compare, _ = only_step("agreement", "agreement compare --work")
        for browser in ("firefox", "webkit"):
            with self.subTest(browser=browser):
                install_index, install = only_step("agreement", f"npx playwright install --with-deps {browser}")
                test_index, test = only_step("agreement", f"npx playwright test --project {browser}")
                self.assertGreater(install_index, max(required))
                self.assertLess(install_index, test_index)
                self.assertLess(test_index, compare)
                self.assertEqual(3, install.get("timeout-minutes"))
                self.assertIs(True, install.get("continue-on-error"))
                self.assertEqual(3, test.get("timeout-minutes"))

    def test_playwright_fails_a_stalled_run_before_its_step_bound(self):
        # Playwright's own test timeout ends first, so its failure and the Worker's events attachment
        # reach the log before the step is killed.
        timeout_ms = playwright_test_timeout_ms()
        self.assertEqual(2 * 60 * 1000, timeout_ms)
        for browser in ("chromium", "firefox", "webkit"):
            with self.subTest(browser=browser):
                _, step = only_step("agreement", f"npx playwright test --project {browser}")
                self.assertGreater(step["timeout-minutes"] * 60 * 1000, timeout_ms)
        for step in (steps("agreement")[i] for i in indexes("agreement", "node --test tools/agreement/node/agreement.test.mjs")):
            with self.subTest(step=step.get("name")):
                self.assertEqual(2, step.get("timeout-minutes"))

    def test_node_runs_with_and_without_the_jiterpreter(self):
        found = [steps("agreement")[i] for i in indexes("agreement", "node --test tools/agreement/node/agreement.test.mjs")]
        self.assertEqual(2, len(found))
        self.assertCountEqual(["node-interp", "node-interp-nojit"],
                              [step.get("env", {}).get("AGREEMENT_BUILD") for step in found])
        for step in found:
            self.assertNotIn("continue-on-error", step, step.get("name"))
            self.assertNotIn("if", step, step.get("name"))

    def test_three_browser_projects_run_and_optional_ones_may_not_install(self):
        # Each Playwright step runs where `npm ci` installed the pinned 1.63.0, so the browsers it
        # installs are the ones that version's tests launch.
        _, chromium_install = only_step("agreement", "npx playwright install --with-deps chromium")
        self.assertNotIn("continue-on-error", chromium_install)
        self.assertEqual("tools/agreement", chromium_install.get("working-directory"))
        install_ids = {}
        for browser in ("firefox", "webkit"):
            _, install = only_step("agreement", f"npx playwright install --with-deps {browser}")
            self.assertTrue(install.get("id"), f"the {browser} install step needs an id")
            self.assertIs(True, install.get("continue-on-error"), browser)
            self.assertEqual("tools/agreement", install.get("working-directory"), browser)
            install_ids[browser] = install["id"]
        self.assertEqual(2, len(set(install_ids.values())))

        tests = {}
        for browser in ("chromium", "firefox", "webkit"):
            _, tests[browser] = only_step("agreement", f"npx playwright test --project {browser}")
            self.assertNotIn("continue-on-error", tests[browser], browser)
            self.assertEqual("tools/agreement", tests[browser].get("working-directory"), browser)
        self.assertNotIn("if", tests["chromium"], "the Chromium Worker run is required and unconditional")
        for browser, step_id in install_ids.items():
            self.assertIn("if", tests[browser], browser)
            for outcome in ("success", "failure", "skipped", "cancelled"):
                # With continue-on-error a failed install still concludes `success`; only its outcome tells.
                context = {"steps": {sid: {"outcome": "success", "conclusion": "success"} for sid in install_ids.values()}}
                context["steps"][step_id]["outcome"] = outcome
                with self.subTest(browser=browser, outcome=outcome):
                    self.assertIs(outcome == "success", evaluate(condition(tests[browser]), context))

        config = (AGREEMENT_TOOLS / "playwright.config.mjs").read_text()
        self.assertIn("projects", config)
        projects = config[config.index("projects"):]
        self.assertEqual(["chromium", "firefox", "webkit"],
                         sorted(re.findall(r"\bname\s*:\s*['\"]([^'\"]+)['\"]", projects)))

    def test_runner_class_and_pinned_intrinsics_are_set(self):
        env = WORKFLOW["env"]
        self.assertEqual("gha-ubuntu-latest-x64", env["MMO_RUNNER_CLASS"])
        self.assertEqual("0", env["DOTNET_EnableAVX512F"])
        self.assertEqual("0", env["DOTNET_TieredPGO"])
        self.assertEqual("ubuntu-latest", WORKFLOW["jobs"]["agreement"]["runs-on"])
        pinned = ("MMO_RUNNER_CLASS", "DOTNET_EnableAVX512F", "DOTNET_TieredPGO")
        overrides = [WORKFLOW["jobs"]["agreement"].get("env", {})] + [step.get("env", {}) for step in steps("agreement")]
        for override in overrides:
            for name in pinned:
                self.assertNotIn(name, override, f"{name} must reach every agreement step unchanged")

    def test_report_is_uploaded_when_the_job_fails(self):
        uploads = [step for step in steps("agreement")
                   if step.get("uses", "").startswith("actions/upload-artifact@") and condition(step) == "failure()"]
        self.assertEqual(1, len(uploads), "one actions/upload-artifact step with if: failure()")
        path = uploads[0]["with"]["path"]
        self.assertTrue(path.endswith("/report/"), path)
        # It is the report folder `agreement compare` writes under the prepared work directory.
        env = run_env_step()
        self.assertEqual(env.values["AGREEMENT_WORK"] + "/report/", expand(path, env.runner_temp, env.values))

    def test_steps_run_with_pipefail(self):
        # `shell: bash` runs `bash --noprofile --norc -eo pipefail {0}`, so a failing command fails its step.
        self.assertEqual("bash", WORKFLOW["defaults"]["run"]["shell"])
        for step in steps("agreement"):
            self.assertEqual("bash", step.get("shell", "bash"), step.get("name"))


class AutoMerge(unittest.TestCase):
    """US2.7's gate on the pull request: auto-merge.yml merges once every check on the pull request has
    passed, and hears about a finished workflow only through its workflow_run list. Without "SimCore"
    there, a pull request could merge before the agreement job reports."""

    def test_auto_merge_waits_for_simcore(self):
        run = AUTO_MERGE.get("on", AUTO_MERGE.get(True))["workflow_run"]
        self.assertIn("SimCore", run["workflows"])
        self.assertIn("Workflow tests", run["workflows"])
        self.assertIn(WORKFLOW["name"], run["workflows"])
        self.assertEqual(["completed"], run["types"])

    def test_a_failed_or_pending_agreement_check_holds_the_merge(self):
        # The agreement job's check run, under the name GitHub gives it (the job's `name`), goes through
        # auto-merge.yml's own CHECK_RUNS_JQ filter and its `failed=` and `pending=` awk lines: a failed
        # run is a reason to leave the pull request alone, and a queued one keeps it waiting. A job
        # named `auto-merge` or `claude` would be filtered out, and the gate would never hold a merge.
        check = WORKFLOW["jobs"]["agreement"].get("name", "agreement")
        merge_steps = [step for step in AUTO_MERGE["jobs"]["auto-merge"]["steps"]
                       if "CHECK_RUNS_JQ" in step.get("env", {})]
        self.assertEqual(1, len(merge_steps), "one auto-merge step filters the check runs")
        step = merge_steps[0]
        self.assertIn('--jq "$CHECK_RUNS_JQ"', step["run"])
        programs = {}
        for variable in ("failed", "pending"):
            found = re.findall(rf"^\s*{variable}=\"\$\(awk -F'\\t' '([^']*)' <<<\"\$checks\"\)\"\s*$", step["run"], re.M)
            self.assertEqual(1, len(found), f"one `{variable}=` awk line in auto-merge.yml")
            programs[variable] = found[0]

        for conclusion, rows, failed, pending in (
                ("failure", [{"status": "completed", "conclusion": "failure"}], check, ""),
                ("cancelled", [{"status": "completed", "conclusion": "cancelled"}], check, ""),
                ("queued", [{"status": "queued", "conclusion": None}], "", check),
                ("in progress", [{"status": "in_progress", "conclusion": None}], "", check),
                ("success", [{"status": "completed", "conclusion": "success"}], "", "")):
            with self.subTest(conclusion=conclusion):
                checks = run_jq(step["env"]["CHECK_RUNS_JQ"], {"check_runs": [{"name": check, **row} for row in rows]})
                expected_state = "pending" if pending else rows[0]["conclusion"]
                self.assertEqual(f"{check}\t{expected_state}\n", checks)
                self.assertEqual(failed, run_awk(programs["failed"], checks).strip())
                self.assertEqual(pending, run_awk(programs["pending"], checks).strip())


if __name__ == "__main__":
    unittest.main()
