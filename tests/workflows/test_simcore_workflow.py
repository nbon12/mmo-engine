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
  and that auto-merge's own check filter holds the merge on a failed or pending `agreement` check;
- the `measure` job (US7.2, US7-IT, SC-007, FR-030, FR-031): the server benchmarks on the runner class,
  the prebuilt, relinked and AOT publishes in the contract's order, each mode recorded as produced or
  with its reason (a publish that does not finish within its `timeout` included), the relinked and AOT
  agreement gate, which fails the job when a runner or compare fails and still reports after a failed
  run, each step's own bound inside the job's 30 minutes, the exhaustive trig sweeps that run only
  there, and the measurement report validated and then uploaded.

The suite itself (the per-stream, batch, perturbation, refusal and tables checks) is
tools/agreement/node/agreement.test.mjs and tools/agreement/specs/worker.spec.mjs. The measurement
runner's own tests are tools/agreement/node/measure.test.mjs.

Run: python3 -m unittest tests/workflows/test_simcore_workflow.py -v   (needs PyYAML)
 or: python3 -m unittest discover -s tests/workflows -v
"""

import importlib.util
import json
import os
import re
import subprocess
import sys
import tempfile
import time
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

# The measure job's steps that feed the report, in the order of
# contracts/measurement-report.md#how-the-report-is-produced.
MEASURE_ORDER = (
    "Run the server benchmarks",
    "Publish the prebuilt interpreted build",
    "Install the wasm-tools workload",
    "Publish the relinked interpreted build",
    "Publish the AOT build",
    "Prepare the agreement work directory",
    "Agreement on the relinked build",
    "Agreement on the AOT build",
    "Compare the new builds with the server",
    "Measure the WebAssembly builds",
    "Validate the measurement report",
    "Upload the measurement report",
)

MEASURE_STEP = "Measure the WebAssembly builds"
MEASURE_RUNNER = "tools/agreement/lib/measure.mjs"
SERVER_BENCHMARKS = "BenchmarkDotNet.Artifacts/results/simcore-server-benchmarks.json"
MEASUREMENT_REPORT = "$RUNNER_TEMP/simcore-measurement.json"
PREBUILT_OUT = "publish/prebuilt"

# The reason recorded for the relinked and AOT modes when the workload did not install, so their
# publishes were skipped and wrote no outputs.
WORKLOAD_REASON = "dotnet workload install wasm-tools failed; see the install-workload step log"

# The two modes the job publishes after the workload: the publish step, its id, its output folder under
# RUNNER_TEMP, the minutes `timeout` gives its `dotnet publish` and the reason lines it writes when the
# publish fails or does not finish, and the agreement build and step that check it.
NEW_MODES = (
    SimpleNamespace(mode="interp-relinked", publish="Publish the relinked interpreted build", publish_id="publish-relinked",
                    out="publish/relinked", build="node-interp-relinked", agreement="Agreement on the relinked build",
                    aot=False, bound=4,
                    reason_line="reason=dotnet publish exited ${code}; see the publish-relinked step log",
                    timeout_reason_line="reason=dotnet publish did not finish within 4 minutes; see the publish-relinked step log"),
    SimpleNamespace(mode="aot", publish="Publish the AOT build", publish_id="publish-aot",
                    out="publish/aot", build="node-aot", agreement="Agreement on the AOT build",
                    aot=True, bound=5,
                    reason_line="reason=dotnet publish -p:RunAOTCompilation=true exited ${code}; see the publish-aot step log",
                    timeout_reason_line=("reason=dotnet publish -p:RunAOTCompilation=true did not finish within 5 minutes;"
                                         " see the publish-aot step log")),
)

# The exit status of `timeout` when the command it runs does not finish within its bound.
TIMEOUT_EXIT = 124

# A stand-in for dotnet, node, npm and npx in the step runs below: it appends its name and arguments to
# $FAKE_TOOL_LOG as one JSON line, sleeps $FAKE_<NAME>_SLEEP seconds (default 0) and exits with
# $FAKE_<NAME>_EXIT (default 0).
FAKE_TOOL = """#!{python}
import json, os, sys, time
name = os.path.basename(sys.argv[0])
with open(os.environ["FAKE_TOOL_LOG"], "a", encoding="utf-8") as log:
    log.write(json.dumps([name] + sys.argv[1:]) + "\\n")
time.sleep(float(os.environ.get("FAKE_" + name.upper() + "_SLEEP", "0")))
sys.exit(int(os.environ.get("FAKE_" + name.upper() + "_EXIT", "0")))
"""
FAKE_TOOLS = ("dotnet", "node", "npm", "npx")


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


# GitHub's status check functions. An `if` that calls none of them runs as `success() && (<if>)`.
STATUS_FUNCTIONS = re.compile(r"\b(success|failure|cancelled|always)\(\s*\)")


def would_run(step, steps_context, failed=False, cancelled=False):
    """Whether GitHub runs `step` after earlier steps left `steps_context`: `failed` says an earlier step
    failed (without continue-on-error), `cancelled` that the run was cancelled. The step's `if`, or
    `success()` when it has none, gets the implicit `success() &&` when it calls no status function, and
    each status function is then decided as GitHub does: success() when nothing failed or was cancelled,
    failure() when something failed, cancelled() on a cancel, always() always."""
    text = condition(step) or "success()"
    if not STATUS_FUNCTIONS.search(text):
        text = f"success()&&({text})"
    status = {"success": not failed and not cancelled, "failure": failed, "cancelled": cancelled, "always": True}
    text = STATUS_FUNCTIONS.sub(lambda m: "true" if status[m.group(1)] else "false", text)
    return evaluate(text, {"steps": steps_context})


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


def step_named(job, name):
    """(index, step) of the job's one step called `name`; fails naming the step when there is none."""
    found = [i for i, step in enumerate(steps(job)) if step.get("name") == name]
    if len(found) != 1:
        raise AssertionError(f"expected exactly one {job} step named {name!r}, found {len(found)}")
    return found[0], steps(job)[found[0]]


def contains_sequence(items, sequence):
    """Whether `sequence` occurs in `items` as consecutive elements."""
    n = len(sequence)
    return any(list(items[i:i + n]) == list(sequence) for i in range(len(items) - n + 1))


def option_value(args, name):
    """The one value given after `name` in an argument list."""
    found = [args[i + 1] for i, arg in enumerate(args[:-1]) if arg == name]
    if len(found) != 1:
        raise AssertionError(f"expected {name} exactly once with a value in {args}, found {len(found)}")
    return found[0]


def if_else_blocks(script):
    """The (then, else) branch texts of each `if` ... `else` ... `fi` block of a bash script, read line by
    line; an `elif` starts the other branch. A nested block's lines belong to its enclosing branches too."""
    blocks, stack = [], []
    for raw in script.splitlines():
        line = raw.strip()
        if re.match(r"if\b", line):
            for frame in stack:
                frame[frame["in"]].append(line)
            stack.append({"then": [], "else": [], "in": "then"})
            after = re.split(r";\s*then\b", line, maxsplit=1)
            if len(after) == 2 and after[1].strip():
                stack[-1]["then"].append(after[1].strip())
        elif stack and re.match(r"(else|elif)\b", line):
            for frame in stack[:-1]:
                frame[frame["in"]].append(line)
            stack[-1]["in"] = "else"
            rest = re.sub(r"^else\b", "", line).strip()
            if line.startswith("else") and rest:
                stack[-1]["else"].append(rest)
        elif stack and re.match(r"fi\b", line):
            frame = stack.pop()
            blocks.append(("\n".join(frame["then"]), "\n".join(frame["else"])))
            for outer in stack:
                outer[outer["in"]].append(line)
        else:
            for frame in stack:
                frame[frame["in"]].append(line)
    return blocks


def run_step(job, name, steps_context=None, env=None, exits=None, setup=None, sleeps=None, rewrite=None):
    """Runs one step's `run` under bash as the runner does (`bash --noprofile --norc -eo pipefail`), with a
    temporary RUNNER_TEMP, GITHUB_ENV, GITHUB_OUTPUT and GITHUB_STEP_SUMMARY, and FAKE_TOOL standing in for
    dotnet, node, npm and npx. `${{ }}` in the run and in the step's env is evaluated against the `steps`
    context given, `runner.temp` and the env, as the runner would.

    `env(runner_temp)` gives the variables earlier steps appended to $GITHUB_ENV, `exits` maps a tool to
    its exit code, `sleeps` to the seconds it takes before exiting, and `setup(runner_temp, repo)` writes
    the files earlier steps would have left. `rewrite(script)`, when given, changes the step's evaluated
    run before it runs (a test shortens a bound with it).

    Returns the step's exit code and stderr, the RUNNER_TEMP and step summary file it ran with, the env it
    was given, each tool call as [tool, *args], the outputs and env variables it appended, and, for each
    appended variable that names a directory, whether that directory was empty afterwards.
    """
    index, step = step_named(job, name)
    with tempfile.TemporaryDirectory() as temp:
        temp = Path(temp)
        runner_temp = str(temp / "runner-temp")
        os.mkdir(runner_temp)
        repo, tools = temp / "repo", temp / "bin"
        repo.mkdir()
        tools.mkdir()
        for tool in FAKE_TOOLS:
            (tools / tool).write_text(FAKE_TOOL.format(python=sys.executable))
            (tools / tool).chmod(0o755)
        files = {key: temp / key for key in ("github-env", "github-output", "step-summary", "tool-log")}
        for path in files.values():
            path.write_text("")
        if setup:
            setup(runner_temp, repo)
        given = env(runner_temp) if env else {}
        variables = {**WORKFLOW.get("env", {}), **WORKFLOW["jobs"][job].get("env", {}), **given}
        context = {"runner": {"temp": runner_temp}, "env": variables, "steps": steps_context or {}}
        step_env = {key: render(value if isinstance(value, str) else gh_expr.to_str(value), context)
                    for key, value in step.get("env", {}).items()}
        process_env = {str(key): value if isinstance(value, str) else gh_expr.to_str(value)
                       for key, value in {**variables, **step_env}.items()}
        process_env.update({
            "PATH": f"{tools}{os.pathsep}{os.environ['PATH']}",
            "HOME": str(temp),
            "RUNNER_TEMP": runner_temp,
            "GITHUB_ENV": str(files["github-env"]),
            "GITHUB_OUTPUT": str(files["github-output"]),
            "GITHUB_STEP_SUMMARY": str(files["step-summary"]),
            "FAKE_TOOL_LOG": str(files["tool-log"]),
        })
        for tool, code in (exits or {}).items():
            process_env[f"FAKE_{tool.upper()}_EXIT"] = str(code)
        for tool, seconds in (sleeps or {}).items():
            process_env[f"FAKE_{tool.upper()}_SLEEP"] = str(seconds)
        script = render(step["run"], context)
        if rewrite:
            script = rewrite(script)
        result = subprocess.run(["bash", "--noprofile", "--norc", "-eo", "pipefail", "-c", script],
                                env=process_env, cwd=repo, capture_output=True, text=True, timeout=60)
        appended = parse_github_env(files["github-env"].read_text())
        empty_dirs = {key: not any(Path(value).iterdir()) for key, value in appended.items()
                      if os.path.isabs(value) and Path(value).is_dir()}
        return SimpleNamespace(index=index, step=step, returncode=result.returncode, stderr=result.stderr,
                               runner_temp=runner_temp, summary_path=str(files["step-summary"]), given=given,
                               calls=[json.loads(line) for line in files["tool-log"].read_text().splitlines()],
                               outputs=parse_github_env(files["github-output"].read_text()), env=appended,
                               empty_dirs=empty_dirs)


# How a publish of the relinked or AOT build ended, as the measure job's later steps see it.
PRODUCED = ("produced", None)
SKIPPED = ("skipped", None)


def failed_with(reason):
    return ("failed", reason)


def publish_context(state):
    """The `steps` context entry a publish step leaves: it exits 0 whether or not dotnet publish did, and
    a skipped step (the workload did not install) leaves no outputs."""
    kind, reason = state
    if kind == "skipped":
        return {"outcome": "skipped", "conclusion": "skipped", "outputs": {}}
    outputs = {"produced": "true"} if kind == "produced" else {"produced": "false", "reason": reason}
    return {"outcome": "success", "conclusion": "success", "outputs": outputs}


def measure_context(relinked, aot):
    """The `steps` context after the workload install and the relinked and AOT publishes. The install
    continues on error, so only its outcome says it failed; both publishes are then skipped."""
    installed = SKIPPED not in (relinked, aot)
    return {"install-workload": {"outcome": "success" if installed else "failure", "conclusion": "success",
                                 "outputs": {}},
            "publish-relinked": publish_context(relinked),
            "publish-aot": publish_context(aot)}


def expected_mode_argument(state, runner_temp, out):
    """(option, value) that measure.mjs must be given for a mode whose publish ended in `state`."""
    kind, reason = state
    if kind == "produced":
        return ("--mode", f"{runner_temp}/{out}")
    return ("--unproduced", reason if kind == "failed" else WORKLOAD_REASON)


def run_measure(relinked, aot):
    """Runs the "Measure the WebAssembly builds" step after the given publish outcomes, with WORK as the
    prepare step appended it. Returns the run, measure.mjs's options as {option: [values]}, and each
    `--mode` or `--unproduced` it was given as (mode, option, value)."""
    def setup(runner_temp, repo):
        export = repo / SERVER_BENCHMARKS
        export.parent.mkdir(parents=True)
        export.write_text("{}")
        for state, out in ((PRODUCED, PREBUILT_OUT), (relinked, NEW_MODES[0].out), (aot, NEW_MODES[1].out)):
            if state == PRODUCED:
                (Path(runner_temp) / out / "wwwroot" / "_framework").mkdir(parents=True)

    def prepared(runner_temp):
        work = f"{runner_temp}/simcore-work.prepared"
        os.mkdir(work)
        return {"WORK": work, "AGREEMENT_WORK": work}

    run = run_step("measure", MEASURE_STEP, measure_context(relinked, aot), env=prepared, setup=setup)
    calls = [call for call in run.calls if call[:2] == ["node", MEASURE_RUNNER]]
    if len(calls) != 1:
        raise AssertionError(f"expected one `node {MEASURE_RUNNER}` call, found {run.calls} (stderr: {run.stderr})")
    options, modes, args = {}, [], iter(calls[0][2:])
    for arg in args:
        value = next(args, None)
        if arg in ("--mode", "--unproduced"):
            mode, _, rest = (value or "").partition("=")
            modes.append((mode, arg, rest))
        else:
            options.setdefault(arg, []).append(value)
    return run, options, modes


class TriggersAndPaths(unittest.TestCase):
    """US2.7: "**Given** a pull request that changes the shared core, its analyzer, its recorded streams,
    its runners or its build, **When** CI runs, **Then** the `SimCore` workflow's `agreement` job runs
    the agreement suite and fails on any divergent step, and its result gates the pull request:
    auto-merge waits for the workflow and does not merge while the `agreement` check has failed or is
    still running." FR-027: "The agreement suite MUST run in CI on every pull request that changes the
    core, its analyzer, its recorded streams or its build (the `SimCore` workflow,
    `.github/workflows/simcore.yml`), and its result MUST gate the pull request." This class is the
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
        # Only the path filter narrows them: a `branches`, `branches-ignore`, `types` or `paths-ignore`
        # key would let a pull request that changes the core, on any base and with the default opened,
        # synchronize and reopened events, skip the suite while every assertion above still held.
        self.assertEqual({"paths"}, set(TRIGGERS["pull_request"]),
                         "pull_request must have no filter but paths, so every pull request that touches them runs the suite")
        self.assertEqual({"branches", "paths"}, set(TRIGGERS["push"]),
                         "push must have no filter but branches and paths")

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
    scenarios (19 are committed), plus generated streams from 50 seeds, gives identical per-step hashes
    in the server build, the WebAssembly build in Node and the WebAssembly build in a browser Worker:
    zero divergent steps." SC-006: "The agreement suite, including the browser Worker run, completes in
    under 10 minutes on a CI runner: the `agreement` job's 10-minute bound fails any run that does
    not." US2.7's gate: `simcore agreement compare` exits non-zero on any divergent
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
        # Where an image has wasm-tools, InvariantGlobalization=true would relink the runtime; the flag
        # keeps node-interp and chromium-worker on the prebuilt runtime pack.
        self.assertIn("-p:UsingBrowserRuntimeWorkload=false", command(step).split(), "the publish keeps the prebuilt runtime pack")
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


class MeasureJob(unittest.TestCase):
    """The `measure` job, which writes and uploads the measurement report.

    US7.2: "**Given** the WebAssembly build, **When** the measurement job runs, **Then** its report records,
    for each build mode the toolchain produced, the download size (raw, Brotli and gzip), the time to the
    first completed step over 10 cold starts and the cost per step, each in Node and in a Chromium Worker,
    and names any mode it could not produce and why." US7-IT: "Run the measurement job on a CI runner and
    check that its report holds every listed measurement with units, the runner class and the build mode
    (`simcore measure validate-report`). Check that the zero-allocation tests pass for every step
    function." SC-007: "The measurement report records the server step
    costs, and the WebAssembly download size, startup time and step cost for each build mode produced, each
    with its runner class." FR-031: the job "MUST name any mode it could not build". FR-030: the server
    build's movement, weapon and projectile steps have benchmarks, which this job runs in full.

    The numbers themselves are checked by `simcore measure validate-report` in the job, and the runner by
    tools/agreement/node/measure.test.mjs; these tests pin the steps that produce, gate, validate and
    upload the report. Steps that only decide what to pass on, and the gate steps, are run under bash with
    stand-in tools, the gate steps with their tool passing and failing, and their conditions are decided
    with GitHub's status functions (would_run)."""

    def test_measure_job_measures_every_mode_and_validates_the_report(self):
        # The steps of contracts/measurement-report.md#how-the-report-is-produced, in its order.
        order = [step_named("measure", name)[0] for name in MEASURE_ORDER]
        for (earlier, before), (later, after) in zip(zip(MEASURE_ORDER, order), zip(MEASURE_ORDER[1:], order[1:])):
            with self.subTest(earlier=earlier, later=later):
                self.assertLess(before, after, f"{earlier!r} must come before {later!r}")

        _, measure = step_named("measure", MEASURE_STEP)
        text, raw = command(measure), measure.get("run", "")
        self.assertIn(f"node {MEASURE_RUNNER}", text)
        self.assertIn('--work "$WORK"', text)
        self.assertIn(f"--server-benchmarks {SERVER_BENCHMARKS}", text)
        self.assertIn(f'--out "{MEASUREMENT_REPORT}"', text)
        # The prebuilt build is always produced: it was published before the workload.
        self.assertEqual(1, text.count("--mode interp-prebuilt="))
        self.assertNotIn("--unproduced interp-prebuilt=", text)
        # Each new mode is passed as produced or as unproduced, never both: the two branches of one if/else.
        blocks = if_else_blocks(raw)
        for mode in NEW_MODES:
            with self.subTest(mode=mode.mode):
                produced, unproduced = f"--mode {mode.mode}=", f"--unproduced {mode.mode}="
                self.assertEqual(1, text.count(produced), produced)
                self.assertEqual(1, text.count(unproduced), unproduced)
                split = [block for block in blocks
                         if (produced in block[0] and unproduced in block[1])
                         or (unproduced in block[0] and produced in block[1])]
                self.assertEqual(1, len(split), f"{produced} and {unproduced} must be the two branches of one if/else")
        # The report is written to the prepared work directory that compare read, not to one of its own.
        self.assertNotIn("WORK", measure.get("env", {}))

        # With every mode produced, each is measured from the folder its publish wrote.
        run, options, modes = run_measure(PRODUCED, PRODUCED)
        self.assertEqual(0, run.returncode, run.stderr)
        self.assertEqual([run.given["WORK"]], options.get("--work"))
        self.assertEqual([SERVER_BENCHMARKS], options.get("--server-benchmarks"))
        self.assertEqual([expand(MEASUREMENT_REPORT, run.runner_temp)], options.get("--out"))
        self.assertCountEqual(["interp-prebuilt", "interp-relinked", "aot"], [mode for mode, _, _ in modes])
        self.assertEqual({"interp-prebuilt": ("--mode", f"{run.runner_temp}/{PREBUILT_OUT}"),
                          "interp-relinked": ("--mode", f"{run.runner_temp}/{NEW_MODES[0].out}"),
                          "aot": ("--mode", f"{run.runner_temp}/{NEW_MODES[1].out}")},
                         {mode: (option, value) for mode, option, value in modes})

        # The report is validated, and nothing turns a failed validation into a pass.
        _, validate = step_named("measure", "Validate the measurement report")
        self.assertIn(f'measure validate-report "{MEASUREMENT_REPORT}"', command(validate))
        self.assertNotIn("continue-on-error", validate)
        self.assertNotIn("|| true", command(validate))
        self.assertNotIn("if", validate)
        self.assertNotIn("continue-on-error", WORKFLOW["jobs"]["measure"])

        # Then uploaded: no `if`, so it runs only when validation passed, and a missing report fails it.
        _, upload = step_named("measure", "Upload the measurement report")
        self.assertRegex(upload.get("uses", ""), r"^actions/upload-artifact@[0-9a-f]{40}$")
        self.assertEqual("simcore-measurement", upload["with"]["name"])
        self.assertEqual("${{ runner.temp }}/simcore-measurement.json", upload["with"]["path"])
        self.assertEqual("error", upload["with"]["if-no-files-found"])
        self.assertNotIn("if", upload)
        self.assertEqual(expand(MEASUREMENT_REPORT, run.runner_temp), expand(upload["with"]["path"], run.runner_temp))

    def test_runner_class_and_pinned_intrinsics_are_set(self):
        env = {**WORKFLOW.get("env", {}), **WORKFLOW["jobs"]["measure"].get("env", {})}
        self.assertEqual("gha-ubuntu-latest-x64", env.get("MMO_RUNNER_CLASS"))
        self.assertEqual("0", env.get("DOTNET_EnableAVX512F"))
        self.assertEqual("0", env.get("DOTNET_TieredPGO"))
        self.assertEqual("ubuntu-latest", WORKFLOW["jobs"]["measure"]["runs-on"])
        # One runner class reaches the benchmarks, measure.mjs and so every report field, and the server
        # benchmarks run with the same intrinsics the report records.
        for step in steps("measure"):
            with self.subTest(step=step.get("name")):
                for name in ("MMO_RUNNER_CLASS", "DOTNET_EnableAVX512F", "DOTNET_TieredPGO"):
                    self.assertNotIn(name, step.get("env", {}))
                self.assertNotRegex(command(step), r"(?<![$\w{])MMO_RUNNER_CLASS=|\bunset\b[^\n;|&]*\bMMO_RUNNER_CLASS\b",
                                    "no step may set or unset MMO_RUNNER_CLASS")

    def test_unproduced_modes_are_recorded_with_their_reason(self):
        install_index, install = step_named("measure", "Install the wasm-tools workload")
        self.assertEqual("install-workload", install.get("id"))
        self.assertIn("dotnet workload install wasm-tools", command(install))
        self.assertIs(True, install.get("continue-on-error"))

        for mode in NEW_MODES:
            index, publish = step_named("measure", mode.publish)
            text = command(publish)
            with self.subTest(step=mode.publish):
                self.assertEqual(mode.publish_id, publish.get("id"))
                self.assertGreater(index, install_index)
                self.assertEqual("steps.install-workload.outcome=='success'", condition(publish))
                for outcome in ("success", "failure", "skipped", "cancelled"):
                    # With continue-on-error a failed install still concludes `success`; only its outcome tells.
                    context = {"steps": {"install-workload": {"outcome": outcome, "conclusion": "success"}}}
                    self.assertIs(outcome == "success", evaluate(condition(publish), context), outcome)
                self.assertNotIn("continue-on-error", publish)
                self.assertIn('"$GITHUB_OUTPUT"', text)
                self.assertIn(mode.reason_line, publish.get("run", ""))
                self.assertIs(mode.aot, "-p:RunAOTCompilation=true" in text)
                # These two need the workload: the flag that keeps the prebuilt runtime pack would undo it.
                self.assertNotIn("UsingBrowserRuntimeWorkload", text)
            # Run the step with dotnet publish succeeding and failing: it records the outcome and exits 0.
            for code in (0, 3):
                with self.subTest(step=mode.publish, dotnet_exit=code):
                    run = run_step("measure", mode.publish,
                                   {"install-workload": {"outcome": "success", "conclusion": "success", "outputs": {}}},
                                   exits={"dotnet": code})
                    self.assertEqual(0, run.returncode, run.stderr)
                    publishes = [call for call in run.calls if call[:2] == ["dotnet", "publish"]]
                    self.assertEqual(1, len(publishes), run.calls)
                    args = publishes[0]
                    self.assertEqual("src/MmoEngine.SimCore.Wasm", args[2])
                    self.assertTrue(contains_sequence(args, ["-c", "Release"]), args)
                    self.assertEqual(f"{run.runner_temp}/{mode.out}", option_value(args, "-o"))
                    self.assertIs(mode.aot, "-p:RunAOTCompilation=true" in args)
                    if code == 0:
                        self.assertEqual("true", run.outputs.get("produced"))
                        self.assertEqual("", run.outputs.get("reason", ""))
                    else:
                        reason = mode.reason_line.removeprefix("reason=").replace("${code}", str(code))
                        self.assertEqual({"produced": "false", "reason": reason}, run.outputs)

        _, measure = step_named("measure", MEASURE_STEP)
        expected_env = {
            "RELINKED_PRODUCED": "${{ steps.publish-relinked.outputs.produced }}",
            "RELINKED_REASON": "${{ steps.publish-relinked.outputs.reason }}",
            "AOT_PRODUCED": "${{ steps.publish-aot.outputs.produced }}",
            "AOT_REASON": "${{ steps.publish-aot.outputs.reason }}",
        }
        for name, value in expected_env.items():
            with self.subTest(env=name):
                self.assertEqual(re.sub(r"\s+", "", value), re.sub(r"\s+", "", str(measure.get("env", {}).get(name, ""))))
        self.assertIn(WORKLOAD_REASON, measure.get("run", ""))

        # Each mode that was not produced is passed with its reason: the publish's own, or the workload's
        # when the publish was skipped.
        for label, relinked, aot in (
                ("the AOT publish failed", PRODUCED,
                 failed_with("dotnet publish -p:RunAOTCompilation=true exited 1; see the publish-aot step log")),
                ("the relinked publish failed", failed_with("dotnet publish exited 1; see the publish-relinked step log"),
                 PRODUCED),
                ("both publishes failed", failed_with("dotnet publish exited 2; see the publish-relinked step log"),
                 failed_with("dotnet publish -p:RunAOTCompilation=true exited 3; see the publish-aot step log")),
                ("the workload did not install", SKIPPED, SKIPPED)):
            with self.subTest(label):
                run, _, modes = run_measure(relinked, aot)
                self.assertEqual(0, run.returncode, run.stderr)
                self.assertCountEqual(["interp-prebuilt", "interp-relinked", "aot"], [mode for mode, _, _ in modes])
                self.assertEqual({"interp-prebuilt": ("--mode", f"{run.runner_temp}/{PREBUILT_OUT}"),
                                  "interp-relinked": expected_mode_argument(relinked, run.runner_temp, NEW_MODES[0].out),
                                  "aot": expected_mode_argument(aot, run.runner_temp, NEW_MODES[1].out)},
                                 {mode: (option, value) for mode, option, value in modes})

    def test_server_benchmarks_run_with_the_runner_class(self):
        index, step = step_named("measure", "Run the server benchmarks")
        self.assertIn("dotnet run -c Release --project benchmarks/MmoEngine.SimCore.Benchmarks -- --filter '*'", command(step))
        self.assertNotIn("continue-on-error", step)
        # The benchmark Main exits 2 without MMO_RUNNER_CLASS (Phase 9), so a missing class fails the job.
        self.assertNotIn("MMO_RUNNER_CLASS", step.get("env", {}))
        self.assertNotRegex(command(step), r"\bMMO_RUNNER_CLASS\b", "the step must neither set nor unset MMO_RUNNER_CLASS")
        # The measure step reads its export, from the same directory BenchmarkDotNet wrote it under.
        measure_index, measure = step_named("measure", MEASURE_STEP)
        self.assertLess(index, measure_index)
        self.assertEqual(step.get("working-directory"), measure.get("working-directory"))

    def test_relinked_and_aot_agreement_gates_the_job(self):
        prepare_index, prepare = step_named("measure", "Prepare the agreement work directory")
        self.assertEqual("prepare", prepare.get("id"))
        self.assertNotIn("if", prepare)
        self.assertIn('echo "AGREEMENT_WORK=$WORK" >> "$GITHUB_ENV"', prepare.get("run", ""))
        # The prepared work directory is the one every later step reads, and `agreement prepare` gets it
        # empty, as it requires.
        prepare_run = run_step("measure", "Prepare the agreement work directory")
        self.assertEqual(0, prepare_run.returncode, prepare_run.stderr)
        work = prepare_run.env.get("WORK")
        self.assertIsNotNone(work, "the step must append WORK to $GITHUB_ENV")
        self.assertEqual(work, prepare_run.env.get("AGREEMENT_WORK"))
        self.assertTrue(work.startswith(prepare_run.runner_temp + "/"), work)
        self.assertIs(True, prepare_run.empty_dirs.get("WORK"), "WORK must be a fresh empty directory")
        prepares = [call for call in prepare_run.calls if call[0] == "dotnet" and contains_sequence(call, ["agreement", "prepare"])]
        self.assertEqual(1, len(prepares), prepare_run.calls)
        self.assertTrue(contains_sequence(prepares[0], ["--seeds", "1-50"]), prepares[0])
        self.assertEqual(work, option_value(prepares[0], "--work"))
        for scope in (WORKFLOW.get("env", {}), WORKFLOW["jobs"]["measure"].get("env", {})):
            self.assertNotIn("WORK", scope)
            self.assertNotIn("AGREEMENT_WORK", scope)

        def prepared(runner_temp):
            os.mkdir(f"{runner_temp}/simcore-work.prepared")
            return {"WORK": f"{runner_temp}/simcore-work.prepared",
                    "AGREEMENT_WORK": f"{runner_temp}/simcore-work.prepared"}

        def after_prepare(context, outcome="success"):
            return {**context, "prepare": {"outcome": outcome, "conclusion": outcome, "outputs": {}}}

        agreement_call = ["node", "--test", "tools/agreement/node/agreement.test.mjs"]
        gates = []
        for mode in NEW_MODES:
            index, step = step_named("measure", mode.agreement)
            gates.append(index)
            with self.subTest(step=mode.agreement):
                # `!cancelled()`, not the implicit `success()`: the AOT run still runs after the relinked
                # run diverged, so one build's divergence does not hide whether the other agrees. A failed
                # or skipped prepare leaves no work directory, so neither runs then.
                self.assertEqual(f"!cancelled()&&steps.prepare.outcome=='success'&&steps.{mode.publish_id}.outputs.produced=='true'",
                                 condition(step))
                self.assertTrue(str(step["if"]).strip().startswith("${{"), step["if"])
                for produced in ("true", "false", None):
                    for prepare_outcome in ("success", "failure", "skipped"):
                        for failed, cancelled in ((False, False), (True, False), (False, True)):
                            outputs = {} if produced is None else {"produced": produced}
                            context = after_prepare({mode.publish_id: {"outcome": "success", "outputs": outputs}}, prepare_outcome)
                            with self.subTest(produced=produced, prepare=prepare_outcome, failed=failed, cancelled=cancelled):
                                self.assertIs(produced == "true" and prepare_outcome == "success" and not cancelled,
                                              would_run(step, context, failed=failed, cancelled=cancelled))
                env = step.get("env", {})
                self.assertEqual(mode.build, env.get("AGREEMENT_BUILD"))
                self.assertEqual(f"${{{{ runner.temp }}}}/{mode.out}/wwwroot", env.get("AGREEMENT_APP"))
                self.assertIn("node --test tools/agreement/node/agreement.test.mjs", command(step))
                self.assertNotIn("||", command(step), "nothing may turn the agreement run's exit code into a pass")
                self.assertEqual("bash", step.get("shell", "bash"))
                self.assertNotIn("AGREEMENT_WORK", env)
                self.assertNotIn("continue-on-error", step)
                self.assertLess(prepare_index, index)
                # It runs the build the publish wrote.
                _, publish = step_named("measure", mode.publish)
                out = re.search(r"\s-o\s+\"?([^\"\s]+)\"?", command(publish))
                self.assertIsNotNone(out, f"{mode.publish} names its output directory with -o")
                with tempfile.TemporaryDirectory() as runner_temp:
                    self.assertEqual(expand(out.group(1), runner_temp) + "/wwwroot",
                                     expand(env.get("AGREEMENT_APP", ""), runner_temp))
                # The step passes when the runner passes, and fails when the runner fails: a diverged build
                # fails the job.
                context = after_prepare(measure_context(PRODUCED, PRODUCED))
                for code in (0, 1):
                    with self.subTest(node_exit=code):
                        run = run_step("measure", mode.agreement, context, env=prepared, exits={"node": code})
                        self.assertEqual(code, run.returncode, run.stderr)
                        self.assertEqual([agreement_call], run.calls)

        compare_index, compare = step_named("measure", "Compare the new builds with the server")
        # It runs after an agreement run failed too (not after a cancel), so its divergence reports reach
        # the job summary and report/, which the failure upload below carries.
        self.assertEqual("!cancelled()&&steps.prepare.outcome=='success'&&"
                         "(steps.publish-relinked.outputs.produced=='true'||steps.publish-aot.outputs.produced=='true')",
                         condition(compare))
        self.assertTrue(str(compare["if"]).strip().startswith("${{"), compare["if"])
        self.assertIn('agreement compare --work "$WORK" --require "$REQUIRE" --summary "$GITHUB_STEP_SUMMARY"', command(compare))
        self.assertNotIn("||", command(compare), "nothing may turn compare's exit code into a pass")
        self.assertEqual("bash", compare.get("shell", "bash"))
        self.assertIn("node-interp-relinked", compare.get("run", ""))
        self.assertIn("node-aot", compare.get("run", ""))
        self.assertNotIn("continue-on-error", compare)
        self.assertNotIn("WORK", compare.get("env", {}))
        self.assertGreater(compare_index, max(gates))
        # It runs when either build was produced, and requires exactly the builds produced.
        for relinked, aot, required in (
                (PRODUCED, PRODUCED, ["node-interp-relinked", "node-aot"]),
                (PRODUCED, failed_with("dotnet publish -p:RunAOTCompilation=true exited 1; see the publish-aot step log"),
                 ["node-interp-relinked"]),
                (failed_with("dotnet publish exited 1; see the publish-relinked step log"), PRODUCED, ["node-aot"]),
                (failed_with("dotnet publish exited 1; see the publish-relinked step log"),
                 failed_with("dotnet publish -p:RunAOTCompilation=true exited 1; see the publish-aot step log"), None),
                (SKIPPED, SKIPPED, None)):
            with self.subTest(relinked=relinked[0], aot=aot[0]):
                context = after_prepare(measure_context(relinked, aot))
                self.assertIs(required is not None, would_run(compare, context))
                # After an agreement run failed it still runs; after a cancel, or without a prepared work
                # directory, it does not.
                self.assertIs(required is not None, would_run(compare, context, failed=True))
                self.assertIs(False, would_run(compare, context, cancelled=True))
                for prepare_outcome in ("failure", "skipped"):
                    self.assertIs(False, would_run(compare, after_prepare(measure_context(relinked, aot), prepare_outcome)))
                if required is None:
                    continue

                # compare's exit code is the step's: 0 when every required build matched, 1 when one
                # diverged or did not run.
                for code in (0, 1):
                    with self.subTest(dotnet_exit=code):
                        run = run_step("measure", "Compare the new builds with the server", context, env=prepared,
                                       exits={"dotnet": code})
                        self.assertEqual(code, run.returncode, run.stderr)
                        compares = [call for call in run.calls if call[0] == "dotnet" and contains_sequence(call, ["agreement", "compare"])]
                        self.assertEqual(1, len(compares), run.calls)
                        self.assertEqual(1, len(run.calls), run.calls)
                        self.assertEqual(run.given["WORK"], option_value(compares[0], "--work"))
                        self.assertEqual(run.summary_path, option_value(compares[0], "--summary"))
                        self.assertCountEqual(required, option_value(compares[0], "--require").split(","))

        for name in (NEW_MODES[0].agreement, NEW_MODES[1].agreement, "Compare the new builds with the server"):
            self.assertNotIn("continue-on-error", step_named("measure", name)[1], name)

        # When an agreement run or compare failed, the job uploads report/, the folder compare writes under
        # the prepared work directory.
        upload_index, upload = step_named("measure", "Upload the agreement report")
        self.assertEqual("failure()", condition(upload))
        self.assertRegex(upload.get("uses", ""), r"^actions/upload-artifact@[0-9a-f]{40}$")
        self.assertEqual("simcore-measure-agreement-report", upload["with"]["name"])
        self.assertGreater(upload_index, compare_index)
        self.assertIs(True, would_run(upload, {}, failed=True))
        self.assertIs(False, would_run(upload, {}))
        self.assertEqual(f"{work}/report/", expand(upload["with"]["path"], prepare_run.runner_temp, prepare_run.env))

        # The agreement job's gate still requires only its own builds.
        _, agreement_compare = only_step("agreement", "agreement compare --work")
        self.assertNotIn("node-interp-relinked", json.dumps(agreement_compare))
        self.assertNotIn("node-aot", json.dumps(agreement_compare))

    def test_prebuilt_build_is_published_before_the_workload(self):
        index, step = step_named("measure", "Publish the prebuilt interpreted build")
        text = command(step)
        # The agreement job's publish command: the flag keeps the prebuilt runtime pack even on an image
        # that ships wasm-tools, so the report's interp-prebuilt mode is the build `node-interp` tested.
        self.assertIn('dotnet publish src/MmoEngine.SimCore.Wasm -c Release -p:UsingBrowserRuntimeWorkload=false '
                      '-o "$RUNNER_TEMP/publish/prebuilt"', text)
        self.assertNotIn("RunAOTCompilation", text)
        install_index, _ = step_named("measure", "Install the wasm-tools workload")
        self.assertLess(index, install_index)
        for earlier in steps("measure")[:index]:
            self.assertNotIn("dotnet workload", json.dumps(earlier), earlier.get("name"))
        self.assertNotIn("continue-on-error", step)

    def test_exhaustive_math_sweeps_run_here_only(self):
        workflows = sorted(path for pattern in ("*.yml", "*.yaml") for path in (ROOT / ".github" / "workflows").glob(pattern))
        self.assertIn(WORKFLOW_PATH, workflows)
        self.assertEqual(["simcore.yml"], [path.name for path in workflows if "MMO_EXHAUSTIVE_MATH" in path.read_text()])
        # Inside simcore.yml, only that step sets it: not the workflow or a job env, nor any other step.
        mentions = [(job, step.get("name")) for job, step in all_steps() if "MMO_EXHAUSTIVE_MATH" in json.dumps(step)]
        self.assertEqual([("measure", "Run the exhaustive math sweeps")], mentions)
        outside = {str(key): value for key, value in WORKFLOW.items() if key != "jobs"}
        outside["jobs"] = {name: {key: value for key, value in job.items() if key != "steps"}
                           for name, job in WORKFLOW["jobs"].items()}
        self.assertNotIn("MMO_EXHAUSTIVE_MATH", json.dumps(outside, default=str))

        index, step = step_named("measure", "Run the exhaustive math sweeps")
        text = command(step)
        self.assertIn('MMO_EXHAUSTIVE_MATH=1 dotnet test tests/MmoEngine.SimCore.Tests -c Release '
                      '--filter "FullyQualifiedName~MmoEngine.SimCore.Tests.Math"', text)
        self.assertIn('>> "$GITHUB_STEP_SUMMARY"', text)
        self.assertIn("SIMMATH", text, "the step copies the sweeps' SIMMATH lines to the job summary")
        # A measurement, not a gate (Phase 5): the fixed trig table in the pull-request tests is the gate.
        self.assertIs(True, step.get("continue-on-error"))
        compare_index, _ = step_named("measure", "Compare the new builds with the server")
        measure_index, _ = step_named("measure", MEASURE_STEP)
        self.assertLess(compare_index, index)
        self.assertLess(index, measure_index)

    def test_job_times_out_after_thirty_minutes(self):
        self.assertEqual(30, WORKFLOW["jobs"]["measure"]["timeout-minutes"])

    def test_every_step_after_the_toolchain_setup_has_its_own_bound(self):
        # A step that hangs fails on its own, or for an optional step is recorded, instead of the job being
        # cancelled at 30 minutes: an unproduced mode keeps its reason (FR-031), compare still reports and
        # the failure upload still runs. The optional bounds are a budget (plan.md): the job's normal run
        # plus the most one hung optional step can add still fits in the 30 minutes.
        job_bound = WORKFLOW["jobs"]["measure"]["timeout-minutes"]
        all_job_steps = steps("measure")
        toolchain = [i for i, step in enumerate(all_job_steps) if step.get("uses", "").startswith(TOOLCHAIN_ACTIONS)]
        self.assertEqual(list(range(3)), toolchain, "checkout and the two toolchain setups come first")
        for step in all_job_steps[3:]:
            with self.subTest(step=step.get("name")):
                bound = step.get("timeout-minutes")
                self.assertIsInstance(bound, int, "timeout-minutes is a whole number of minutes")
                self.assertNotIsInstance(bound, bool)
                self.assertGreater(bound, 0)
                self.assertLess(bound, job_bound)

        # The optional steps continue on error, so a hang there is a failed step the job carries on past.
        for name, bound in (("Install the wasm-tools workload", 5), ("Run the exhaustive math sweeps", 10)):
            with self.subTest(step=name):
                _, step = step_named("measure", name)
                self.assertIs(True, step.get("continue-on-error"))
                self.assertEqual(bound, step.get("timeout-minutes"))

        # The relinked and AOT publishes have no continue-on-error, so `timeout` bounds `dotnet publish`
        # inside the step, below the step's own bound: a publish that does not finish is recorded as not
        # produced, with its reason, and the step exits 0.
        installed = {"install-workload": {"outcome": "success", "conclusion": "success", "outputs": {}}}
        for mode in NEW_MODES:
            _, publish = step_named("measure", mode.publish)
            bounded = f"timeout --kill-after=30s {mode.bound}m dotnet publish"
            reason = mode.timeout_reason_line.removeprefix("reason=")
            with self.subTest(step=mode.publish):
                # The one line that runs dotnet publish (the others only write its reason) is the bounded one.
                publishes = [line.strip() for line in command(publish).splitlines()
                             if re.search(r"\bdotnet publish\b", line) and not line.strip().startswith("echo ")]
                self.assertEqual(1, len(publishes), publishes)
                self.assertTrue(publishes[0].startswith(f"{bounded} src/MmoEngine.SimCore.Wasm "), publishes[0])
                self.assertGreater(publish.get("timeout-minutes"), mode.bound,
                                   "the step's own bound is the backstop, after `timeout` and its kill")
                self.assertIn(mode.timeout_reason_line, publish.get("run", ""))
            # `timeout` exits 124 when the bound ends the publish ...
            with self.subTest(step=mode.publish, dotnet_exit=TIMEOUT_EXIT):
                run = run_step("measure", mode.publish, installed, exits={"dotnet": TIMEOUT_EXIT})
                self.assertEqual(0, run.returncode, run.stderr)
                self.assertEqual({"produced": "false", "reason": reason}, run.outputs)
            # ... and it does end a publish that never finishes: here with the bound shortened to a second.
            with self.subTest(step=mode.publish, stalled=True):
                def shorten(script, bounded=bounded):
                    self.assertEqual(1, script.count(bounded), bounded)
                    return script.replace(bounded, "timeout --kill-after=1s 1s dotnet publish")

                started = time.monotonic()
                run = run_step("measure", mode.publish, installed, sleeps={"dotnet": 30}, rewrite=shorten)
                self.assertLess(time.monotonic() - started, 20, "timeout must end the stalled publish")
                self.assertEqual(0, run.returncode, run.stderr)
                self.assertEqual({"produced": "false", "reason": reason}, run.outputs)
                self.assertEqual(1, len([call for call in run.calls if call[:2] == ["dotnet", "publish"]]), run.calls)

    def test_runner_tests_run_before_the_measurement(self):
        install_index, install = step_named("measure", "Install the agreement tools")
        self.assertRegex(command(install), r"(^|[\n;&|]\s*)npm ci(\s|$)")
        self.assertEqual("tools/agreement", install.get("working-directory"))
        chromium_index, chromium = step_named("measure", "Install Chromium")
        self.assertIn("npx playwright install --with-deps chromium", command(chromium))
        self.assertEqual("tools/agreement", chromium.get("working-directory"))
        # npx then runs the lockfile's Playwright, whose Chromium measure.mjs launches.
        self.assertLess(install_index, chromium_index)
        test_index, test = step_named("measure", "Test the measurement runner")
        self.assertIn("node --test tools/agreement/node/measure.test.mjs tools/agreement/node/server.test.mjs", command(test))
        measure_index, _ = step_named("measure", MEASURE_STEP)
        for index, step in ((install_index, install), (chromium_index, chromium), (test_index, test)):
            with self.subTest(step=step.get("name")):
                self.assertLess(index, measure_index)
                self.assertNotIn("continue-on-error", step)


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
        # run is a reason to leave the pull request alone, and a queued or in-progress one keeps it
        # waiting. A job named `auto-merge` or `claude` would be filtered out, and the gate would never
        # hold a merge.
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
