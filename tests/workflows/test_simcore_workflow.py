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
  (SC-006), each step's own bound inside it, the optional browsers' shared deadline, set by the job's
  first step so the toolchain setup cannot move it, that leaves compare and the failure upload after it
  their own bounds inside the 10 minutes, and the three browser projects;
- the `"SimCore"` entry in auto-merge.yml's workflow_run list, so the pull request waits for the gate,
  that auto-merge's own check filter holds the merge on a registered `agreement` check run that failed
  or is pending, and that its decision step, run under bash with a stand-in gh, leaves the pull request
  alone or keeps it waiting then and merges it only once the check run passed (an `agreement` check run
  that has not registered on the head commit yet is not waited for while auto-merge's REQUIRE_CHECKS
  is "false", the open follow-up of the spec's Assumptions, which US2.7 does not state and nothing here
  claims);
- the `measure` job (US7.2, US7-IT, SC-007, FR-030, FR-031): the server benchmarks on the runner class,
  the prebuilt, relinked and AOT publishes in the contract's order, each mode recorded as produced or
  with its reason (a publish that does not finish within its `timeout` included), the relinked and AOT
  agreement gate, which fails the job when a runner or compare fails and still reports after a failed
  run, each step's own bound, which together fit in the job's 90 minutes, the exhaustive trig sweeps
  that run only there, and the measurement run, its report validated, shown in the job log and summary,
  and then uploaded, after a failed agreement gate too, so a diverged relinked or AOT build fails the
  job while its report still reaches the artifact with that mode's agreement status Diverged, also
  when the build diverged on flat-run-30hz's first record (measure.mjs's cold-start check accepts the
  server's step number with another hash for a build that report/agreement.json lists as Diverged).

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

# The optional browsers' shared deadline (SC-006): the agreement job's directories step, the job's first
# step, sets AGREEMENT_OPTIONAL_DEADLINE this many seconds after it runs, before checkout and the
# toolchain setup. An install must end OPTIONAL_INSTALL_RESERVE seconds before it and starts only with
# OPTIONAL_INSTALL_MINIMUM seconds to spare; a run starts only with OPTIONAL_RUN_MINIMUM seconds left, more
# than Playwright's test timeout, and stops at the deadline. `timeout` kills a stopped step
# OPTIONAL_KILL_GRACE seconds after it asks it to end.
OPTIONAL_DEADLINE = 7 * 60
OPTIONAL_INSTALL_RESERVE = 160
OPTIONAL_INSTALL_MINIMUM = 30
OPTIONAL_RUN_MINIMUM = 150
OPTIONAL_KILL_GRACE = 10
# The budget the deadline is checked against [estimate]: GitHub's own "Set up job", which downloads the
# job's actions before its first step and is the only time the job's 10 minutes count before the
# directories step (seconds on a hosted runner). Checkout, setup-dotnet and setup-node come after the
# directories step, so they need no allowance. Compare's budget and the failure upload's after it are
# their own step bounds.
SETUP_JOB_ALLOWANCE = 30

# The measure job's steps without a bound of their own, GitHub's "Set up job", checkout, setup-dotnet and
# setup-node, are given this many minutes together [estimate: well under a minute on a hosted runner, the
# SDK download included] when every step's bound is summed against the job's timeout.
MEASURE_SETUP_ALLOWANCE_MINUTES = 5
OPTIONAL_BROWSERS = (("firefox", "Firefox"), ("webkit", "WebKit"))
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

# A stand-in for gh in the auto-merge step runs below. It appends its arguments to $FAKE_GH_LOG as one
# JSON line and answers from $FAKE_GH_ANSWERS, a JSON object with the pull request's number, repository
# and fields (`pr`) and the API's responses by `<METHOD> <endpoint>` (`api`). `pr view` prints the fields
# its --json names, `pr merge` prints nothing, and `api` prints the response, through `jq -r` when given
# --jq, as gh does. Any other call, or an endpoint without an answer, exits 1, as a failed API call would.
FAKE_GH = r"""
import json, os, subprocess, sys
args = sys.argv[1:]
with open(os.environ["FAKE_GH_LOG"], "a", encoding="utf-8") as log:
    log.write(json.dumps(args) + "\n")
with open(os.environ["FAKE_GH_ANSWERS"], encoding="utf-8") as file:
    answers = json.load(file)


def fail(message):
    sys.stderr.write("fake gh: " + message + ": " + " ".join(args) + "\n")
    sys.exit(1)


if args[:2] in (["pr", "view"], ["pr", "merge"]):
    if args[2:5] != [answers["number"], "--repo", answers["repo"]]:
        fail("another pull request or repository")
    if args[1] == "view":
        fields = [args[i + 1] for i, arg in enumerate(args[:-1]) if arg == "--json"]
        if len(fields) != 1:
            fail("pr view without one --json")
        print(json.dumps(dict((field, answers["pr"].get(field)) for field in fields[0].split(","))))
elif args[:1] == ["api"]:
    method, jq, endpoints, rest = "GET", None, [], iter(args[1:])
    for arg in rest:
        if arg == "--method":
            method = next(rest)
        elif arg == "--jq":
            jq = next(rest)
        elif arg == "-f":
            next(rest)
        elif arg == "--paginate":
            pass
        elif arg.startswith("-"):
            fail("an option it does not know")
        else:
            endpoints.append(arg)
    key = method + " " + (endpoints[0] if len(endpoints) == 1 else "?")
    if key not in answers["api"]:
        fail("no answer for " + key)
    response = json.dumps(answers["api"][key])
    if jq is None:
        print(response)
    else:
        sys.exit(subprocess.run(["jq", "-r", jq], input=response, text=True).returncode)
else:
    fail("a command it does not know")
"""

# auto-merge.yml's step that decides, and the values the runner gives its `${{ }}` expressions on a
# pull_request_target event for one pull request of this repository.
AUTO_MERGE_STEP = "Merge, wait, or leave it"
AUTO_MERGE_REPO = "nbon12/mmo-engine"
AUTO_MERGE_PR = "74"
AUTO_MERGE_HEAD = "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c"
AUTO_MERGE_EVENT = {
    "secrets.MERGE_TOKEN": "stand-in-merge-token",
    "github.repository": AUTO_MERGE_REPO,
    "github.event.pull_request.number || github.event.workflow_run.pull_requests[0].number": AUTO_MERGE_PR,
    "github.event.repository.default_branch": "main",
}


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


def simulate_job(job, outcomes=None, outputs=None, cancel_after=None):
    """Steps through the job as GitHub's runner does, deciding each step's `if` with would_run after the
    steps before it. A step that runs ends with `outcomes[name]` (default "success") and, when it has an
    id, leaves `outputs[id]` (default none); a step that does not run is "skipped" and leaves no outputs.
    A failed step without continue-on-error makes failure() true and fails the job; one with
    continue-on-error fails only itself (its conclusion is still "success"). The run is cancelled once
    the step named `cancel_after` has ended.

    Returns the names of the steps that ran, in order, the `steps` context they left and the job's
    conclusion: "cancelled", "failure" or "success"."""
    outcomes, outputs = outcomes or {}, outputs or {}
    names = [step.get("name") for step in steps(job)]
    for name in [*outcomes, *([cancel_after] if cancel_after else [])]:
        if name not in names:
            raise AssertionError(f"the {job} job has no step named {name!r}")
    context, ran, failed, cancelled = {}, [], False, False
    for step in steps(job):
        name = step.get("name")
        if would_run(step, context, failed=failed, cancelled=cancelled):
            ran.append(name)
            outcome, left = outcomes.get(name, "success"), outputs.get(step.get("id"), {})
        else:
            outcome, left = "skipped", {}
        tolerated = outcome == "failure" and step.get("continue-on-error") is True
        if outcome == "failure" and not tolerated:
            failed = True
        if "id" in step:
            context[step["id"]] = {"outcome": outcome, "conclusion": "success" if tolerated else outcome,
                                   "outputs": dict(left)}
        if name == cancel_after:
            cancelled = True
    return SimpleNamespace(ran=ran, steps=context,
                           conclusion="cancelled" if cancelled else "failure" if failed else "success")


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
    does (`bash --noprofile --norc -eo pipefail`), with a temporary RUNNER_TEMP and GITHUB_ENV. The same
    step appends AGREEMENT_WORK and the optional browsers' AGREEMENT_OPTIONAL_DEADLINE.

    Returns its index, the RUNNER_TEMP it ran with, the variables it appended, the epoch seconds just
    before and just after it ran, and whether AGREEMENT_WORK was still absent or an empty directory
    afterwards (`agreement prepare` refuses anything else).
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
        started = int(time.time())
        subprocess.run(["bash", "--noprofile", "--norc", "-eo", "pipefail", "-c", script],
                       env={"PATH": os.environ["PATH"], "HOME": temp, "RUNNER_TEMP": runner_temp,
                            "GITHUB_ENV": str(github_env)},
                       cwd=temp, check=True, capture_output=True, text=True, timeout=30)
        ended = int(time.time())
        values = parse_github_env(github_env.read_text())
        work = Path(values.get("AGREEMENT_WORK", runner_temp))
        work_fresh = not work.exists() or (work.is_dir() and not any(work.iterdir()))
    return SimpleNamespace(index=found[0], runner_temp=runner_temp, values=values, started=started, ended=ended,
                           work_fresh=work_fresh)


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

    Returns the step's exit code, stdout (where the runner reads `::warning::` and `::error::` lines) and
    stderr, the RUNNER_TEMP and step summary file it ran with and the text the step left in that file,
    the env it was given, each tool call as [tool, *args], the outputs and env variables it appended, and,
    for each appended variable that names a directory, whether that directory was empty afterwards.
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
        return SimpleNamespace(index=index, step=step, returncode=result.returncode, stdout=result.stdout, stderr=result.stderr,
                               runner_temp=runner_temp, summary_path=str(files["step-summary"]),
                               summary=files["step-summary"].read_text(), given=given,
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


def auto_merge_step(name):
    """auto-merge.yml's one step called `name`; fails naming the step when there is none."""
    found = [step for step in AUTO_MERGE["jobs"]["auto-merge"]["steps"] if step.get("name") == name]
    if len(found) != 1:
        raise AssertionError(f"expected exactly one auto-merge step named {name!r}, found {len(found)}")
    return found[0]


def render_event(text):
    """Substitutes each `${{ ... }}` of an auto-merge.yml string with its AUTO_MERGE_EVENT value."""
    def substitute(match):
        expression = match.group(1).strip()
        if expression not in AUTO_MERGE_EVENT:
            raise AssertionError(f"AUTO_MERGE_EVENT has no value for the expression {expression!r}")
        return AUTO_MERGE_EVENT[expression]

    return re.sub(r"\$\{\{(.*?)\}\}", substitute, text)


def run_auto_merge_step(check_runs, labels=(), auto_merge=None):
    """Runs auto-merge.yml's "Merge, wait, or leave it" step under bash, as the runner runs a step without
    a `shell` (`bash -e`), with the step's own env and FAKE_GH standing in for gh.

    The pull request qualifies for a merge in every way but its check runs: it is open, ready for review,
    nbon12's, from this repository and based on main, with no review decision and no `hold` label. Its
    head commit is an owner commit, its three changed files are none of GUARDED_PATHS, the commit has no
    statuses, main has no required checks, and GitHub reads it as CLEAN. `check_runs` are the head
    commit's check runs, `labels` the pull request's label names and `auto_merge` its autoMergeRequest
    (None: auto-merge is off).

    Returns the exit code, stdout, stderr, the text the step left in $GITHUB_STEP_SUMMARY and each gh call
    as its argument list.
    """
    step = auto_merge_step(AUTO_MERGE_STEP)
    repo, head = AUTO_MERGE_REPO, AUTO_MERGE_HEAD
    answers = {
        "number": AUTO_MERGE_PR,
        "repo": repo,
        "pr": {"state": "OPEN", "author": {"login": "nbon12"}, "isCrossRepository": False, "isDraft": False,
               "baseRefName": "main", "headRefOid": head, "labels": [{"name": name} for name in labels],
               "autoMergeRequest": auto_merge, "reviewDecision": "", "mergeStateStatus": "CLEAN"},
        "api": {
            f"GET repos/{repo}/commits/{head}": {
                "sha": head, "author": {"login": "nbon12"}, "committer": {"login": "web-flow"},
                "commit": {"author": {"name": "nbon12", "email": "nbon12@users.noreply.github.com"},
                           "committer": {"name": "GitHub", "email": "noreply@github.com"}}},
            f"GET repos/{repo}/compare/main...{head}": {"files": [
                {"filename": "src/MmoEngine.SimCore/Movement/CharacterMover.cs", "status": "modified"},
                {"filename": "tools/agreement/node/agreement.test.mjs", "status": "modified"},
                {"filename": "simcore/streams/flat-run-30hz.mmrs", "status": "renamed",
                 "previous_filename": "simcore/streams/flat-run.mmrs"}]},
            f"GET repos/{repo}/commits/{head}/check-runs?per_page=100": {"total_count": len(check_runs),
                                                                       "check_runs": list(check_runs)},
            f"GET repos/{repo}/commits/{head}/status": {"state": "pending", "statuses": [], "total_count": 0},
            f"GET repos/{repo}/branches/main": {
                "name": "main", "protected": False,
                "protection": {"enabled": False,
                               "required_status_checks": {"enforcement_level": "off", "contexts": [], "checks": []}}},
            f"GET repos/{repo}/rules/branches/main": [],
            f"POST repos/{repo}/issues/{AUTO_MERGE_PR}/labels": [{"name": "auto-merge"}],
            f"DELETE repos/{repo}/issues/{AUTO_MERGE_PR}/labels/auto-merge": [],
        },
    }
    with tempfile.TemporaryDirectory() as temp:
        temp = Path(temp)
        tools = temp / "bin"
        tools.mkdir()
        (tools / "gh").write_text(f"#!{sys.executable}\n{FAKE_GH}")
        (tools / "gh").chmod(0o755)
        files = {key: temp / key for key in ("step-summary", "gh-log", "gh-answers")}
        files["step-summary"].write_text("")
        files["gh-log"].write_text("")
        files["gh-answers"].write_text(json.dumps(answers))
        process_env = {key: render_event(value if isinstance(value, str) else gh_expr.to_str(value))
                       for key, value in step.get("env", {}).items()}
        process_env.update({
            "PATH": f"{tools}{os.pathsep}{os.environ['PATH']}",
            "HOME": str(temp),
            "GITHUB_STEP_SUMMARY": str(files["step-summary"]),
            "FAKE_GH_LOG": str(files["gh-log"]),
            "FAKE_GH_ANSWERS": str(files["gh-answers"]),
        })
        script = render_event(step["run"])
        result = subprocess.run(["bash", "-e", "-c", script], env=process_env, cwd=temp, capture_output=True,
                                text=True, timeout=60)
        return SimpleNamespace(returncode=result.returncode, stdout=result.stdout, stderr=result.stderr,
                               summary=files["step-summary"].read_text(),
                               calls=[json.loads(line) for line in files["gh-log"].read_text().splitlines()])


class TriggersAndPaths(unittest.TestCase):
    """US2.7: "**Given** a pull request that changes the shared core, its analyzer, its recorded streams,
    its runners or its build, **When** CI runs, **Then** the `SimCore` workflow's `agreement` job runs
    the agreement suite and fails on any divergent step, and its result gates the pull request:
    auto-merge waits for the workflow and does not merge while an `agreement` check run on the head
    commit has failed or is still queued or running." FR-027: "The agreement suite MUST run in CI on
    every pull request that changes the core, its analyzer, its recorded streams or its build (the
    `SimCore` workflow, `.github/workflows/simcore.yml`), and its result MUST gate the pull request."
    This class is the "runs" half; AgreementJob.test_compare_gates_the_job and AutoMerge are the gate.
    A check run that has not registered on the head commit yet is the spec's Assumptions, not US2.7."""

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
        # compare (`!cancelled()`) still runs and the report is still uploaded (`failure()`). The
        # directories step, which sets the optional browsers' deadline, is the job's first step, before
        # checkout and the two toolchain setups (test_optional_browsers_share_a_deadline_that_leaves_compare_its_time).
        job_bound = WORKFLOW["jobs"]["agreement"]["timeout-minutes"]
        all_job_steps = steps("agreement")
        toolchain = [i for i, step in enumerate(all_job_steps) if step.get("uses", "").startswith(TOOLCHAIN_ACTIONS)]
        self.assertEqual([1, 2, 3], toolchain, "checkout and the two toolchain setups come right after the directories step")
        self.assertEqual(0, run_env_step().index, "the directories step is the job's first step")
        for step in all_job_steps[:1] + all_job_steps[4:]:
            with self.subTest(step=step.get("name")):
                bound = step.get("timeout-minutes")
                self.assertIsInstance(bound, int, "timeout-minutes is a whole number of minutes")
                self.assertGreater(bound, 0)
                self.assertLess(bound, job_bound)

    def test_optional_browsers_install_after_the_required_runs_within_three_minutes(self):
        # A slow Firefox or WebKit install cannot use up the required runs' time, and one that times
        # out fails only itself (continue-on-error), so its run is skipped and compare reports it as
        # not run. The three minutes are each step's backstop; the shared deadline below bounds the four
        # together.
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

    def test_optional_browsers_share_a_deadline_that_leaves_compare_its_time(self):
        # Whatever Firefox and WebKit do, their four steps end by one deadline: an install must end
        # OPTIONAL_INSTALL_RESERVE seconds before it, a run starts only with OPTIONAL_RUN_MINIMUM seconds
        # left and is stopped at it. So the job is not cancelled by optional steps that are slow or hang,
        # and compare (`!cancelled()`) still reports inside the 10 minutes (SC-006), and so does the
        # failure upload after it.
        job_seconds = WORKFLOW["jobs"]["agreement"]["timeout-minutes"] * 60
        env = run_env_step()
        deadline = env.values.get("AGREEMENT_OPTIONAL_DEADLINE", "")
        self.assertRegex(deadline, r"^[0-9]+$", "AGREEMENT_OPTIONAL_DEADLINE is a whole number of epoch seconds")
        self.assertGreaterEqual(int(deadline), env.started + OPTIONAL_DEADLINE)
        self.assertLessEqual(int(deadline), env.ended + OPTIONAL_DEADLINE)
        # Anchored to the job's start: the step that sets the deadline is the job's first, so checkout,
        # setup-dotnet and setup-node, which have no bound of their own, run after it and however long
        # they take cannot move the deadline later. Only GitHub's "Set up job" comes before it.
        self.assertEqual(0, env.index, "the deadline is set by the job's first step")
        self.assertNotIn("uses", steps("agreement")[0], "the first step is the directories step, not an action")
        compare_index, compare = only_step("agreement", "agreement compare --work")
        # The worst case before compare: GitHub's job set-up, then the deadline, by which every step
        # between the directories step and compare that can still be running has ended (the four optional
        # steps are cut off by it; a required step that is still running then has kept the optional ones
        # from starting and is the run SC-006 fails), then `timeout`'s kill. Compare's own bound after that,
        # and then the bound of the failure upload, which runs when compare fails or reaches its bound,
        # fit in the job's 10 minutes, so each can use all of its bound and the job is still not cancelled.
        compare_seconds = compare["timeout-minutes"] * 60
        upload_index, upload = step_named("agreement", "Upload the agreement report")
        self.assertEqual("failure()", condition(upload))
        self.assertEqual(compare_index + 1, upload_index, "the failure upload comes right after compare")
        self.assertEqual(upload_index, len(steps("agreement")) - 1, "the failure upload is the job's last step")
        upload_seconds = upload["timeout-minutes"] * 60
        after_deadline = SETUP_JOB_ALLOWANCE + OPTIONAL_DEADLINE + OPTIONAL_KILL_GRACE + compare_seconds + upload_seconds
        self.assertLessEqual(after_deadline, job_seconds)
        self.assertEqual(580, after_deadline,
                         "30 s of job set-up, the 420 s deadline, the 10 s kill, compare's 60 s and the upload's 60 s")
        optional = [step_named("agreement", name)[0] for _, title in OPTIONAL_BROWSERS
                    for name in (f"Install {title}", f"Agreement in a {title} Worker")]
        self.assertEqual(compare_index - 1, max(optional), "compare comes right after the last optional step")
        # A run that starts has more time than Playwright's own test timeout, so Playwright reports a
        # stalled run before the deadline stops it, and an install that ends in time leaves its run that much.
        self.assertGreater(OPTIONAL_RUN_MINIMUM * 1000, playwright_test_timeout_ms())
        self.assertGreater(OPTIONAL_INSTALL_RESERVE, OPTIONAL_RUN_MINIMUM)

        def deadline_in(seconds):
            return lambda runner_temp: {"AGREEMENT_OPTIONAL_DEADLINE": str(int(time.time()) + seconds)}

        for browser, title in OPTIONAL_BROWSERS:
            install_name, run_name = f"Install {title}", f"Agreement in a {title} Worker"
            install_index, install = step_named("agreement", install_name)
            run_index, test = step_named("agreement", run_name)
            self.assertLess(env.index, install_index)
            self.assertLess(install_index, run_index)
            # Each npx runs under `timeout` with the time left, which is what stops it at its bound.
            bounded = f'timeout --kill-after={OPTIONAL_KILL_GRACE}s "${{left}}s" npx playwright'
            self.assertIn(f"{bounded} install --with-deps {browser}", install["run"])
            self.assertIn(f"{bounded} test --project {browser}", test["run"])
            install_call = ["npx", "playwright", "install", "--with-deps", browser]
            run_call = ["npx", "playwright", "test", "--project", browser]
            not_run = f"compare reports {browser}-worker as not run"

            with self.subTest(step=install_name, case="time to spare"):
                run = run_step("agreement", install_name, env=deadline_in(OPTIONAL_INSTALL_RESERVE + 300))
                self.assertEqual(0, run.returncode, run.stderr)
                self.assertEqual([install_call], run.calls)
            with self.subTest(step=install_name, case="the install fails"):
                run = run_step("agreement", install_name, env=deadline_in(OPTIONAL_INSTALL_RESERVE + 300), exits={"npx": 1})
                self.assertEqual(1, run.returncode, "continue-on-error then skips the run")
                self.assertEqual([install_call], run.calls)
            with self.subTest(step=install_name, case="no time to spare"):
                run = run_step("agreement", install_name,
                               env=deadline_in(OPTIONAL_INSTALL_RESERVE + OPTIONAL_INSTALL_MINIMUM - 5))
                self.assertNotEqual(0, run.returncode, "an install that does not start fails, so its run is skipped")
                self.assertEqual([], run.calls)
                self.assertIn("::warning::", run.stdout)
                self.assertIn(not_run, run.stdout)
            with self.subTest(step=install_name, case="no deadline"):
                run = run_step("agreement", install_name)
                self.assertNotEqual(0, run.returncode)
                self.assertEqual([], run.calls)
                self.assertIn("AGREEMENT_OPTIONAL_DEADLINE", run.stderr)
            with self.subTest(step=install_name, case="stalled"):
                # The minimum is lowered so the install's bound, OPTIONAL_INSTALL_RESERVE seconds before
                # the deadline, comes in seconds rather than half a minute.
                def lower_install_minimum(script):
                    self.assertEqual(1, script.count(f'-lt {OPTIONAL_INSTALL_MINIMUM} ]'), script)
                    self.assertEqual(1, script.count(f"- {OPTIONAL_INSTALL_RESERVE} ))"), script)
                    return script.replace(f'-lt {OPTIONAL_INSTALL_MINIMUM} ]', '-lt 1 ]')

                started = time.monotonic()
                run = run_step("agreement", install_name, env=deadline_in(OPTIONAL_INSTALL_RESERVE + 3),
                               sleeps={"npx": 30}, rewrite=lower_install_minimum)
                self.assertLess(time.monotonic() - started, 20, "the deadline must stop the stalled install")
                self.assertEqual(TIMEOUT_EXIT, run.returncode, run.stderr)
                self.assertEqual([install_call], run.calls)

            with self.subTest(step=run_name, case="time to run"):
                run = run_step("agreement", run_name, env=deadline_in(OPTIONAL_RUN_MINIMUM + 100))
                self.assertEqual(0, run.returncode, run.stderr)
                self.assertEqual([run_call], run.calls)
            with self.subTest(step=run_name, case="the build disagrees"):
                run = run_step("agreement", run_name, env=deadline_in(OPTIONAL_RUN_MINIMUM + 100), exits={"npx": 1})
                self.assertEqual(1, run.returncode, "a build that installs and then disagrees fails the job")
                self.assertNotIn("::error::", run.stdout)
            with self.subTest(step=run_name, case="no time to run"):
                run = run_step("agreement", run_name, env=deadline_in(OPTIONAL_RUN_MINIMUM - 10))
                self.assertEqual(0, run.returncode, "an optional run without time is not run, not a failure")
                self.assertEqual([], run.calls)
                self.assertIn("::warning::", run.stdout)
                self.assertIn(not_run, run.stdout)
            with self.subTest(step=run_name, case="no deadline"):
                run = run_step("agreement", run_name)
                self.assertNotEqual(0, run.returncode)
                self.assertEqual([], run.calls)
                self.assertIn("AGREEMENT_OPTIONAL_DEADLINE", run.stderr)
            with self.subTest(step=run_name, case="stalled"):
                # The minimum is lowered so the deadline comes in seconds rather than minutes.
                def lower_run_minimum(script):
                    self.assertEqual(1, script.count(f'-lt {OPTIONAL_RUN_MINIMUM} ]'), script)
                    return script.replace(f'-lt {OPTIONAL_RUN_MINIMUM} ]', '-lt 1 ]')

                started = time.monotonic()
                run = run_step("agreement", run_name, env=deadline_in(3), sleeps={"npx": 30}, rewrite=lower_run_minimum)
                self.assertLess(time.monotonic() - started, 20, "the deadline must stop the stalled run")
                self.assertEqual(TIMEOUT_EXIT, run.returncode, "a run stopped at the deadline fails the job")
                self.assertEqual([run_call], run.calls)
                self.assertIn(f"::error::the {title} Worker run did not finish by the optional browsers' deadline", run.stdout)

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

        # The report is validated whenever the measure step wrote it, after a failed agreement gate too
        # (test_the_report_is_written_and_uploaded_after_a_failed_agreement_gate), and nothing turns a
        # failed validation into a pass.
        _, validate = step_named("measure", "Validate the measurement report")
        self.assertIn(f'measure validate-report "{MEASUREMENT_REPORT}"', command(validate))
        self.assertNotIn("continue-on-error", validate)
        self.assertNotIn("|| true", command(validate))
        self.assertEqual("validate", validate.get("id"))
        self.assertEqual("!cancelled()&&steps.measure.outcome=='success'", condition(validate))
        self.assertEqual("measure", measure.get("id"))
        self.assertNotIn("continue-on-error", WORKFLOW["jobs"]["measure"])

        # Then uploaded only when validation passed, and a missing report fails it.
        _, upload = step_named("measure", "Upload the measurement report")
        self.assertRegex(upload.get("uses", ""), r"^actions/upload-artifact@[0-9a-f]{40}$")
        self.assertEqual("simcore-measurement", upload["with"]["name"])
        self.assertEqual("${{ runner.temp }}/simcore-measurement.json", upload["with"]["path"])
        self.assertEqual("error", upload["with"]["if-no-files-found"])
        self.assertEqual("!cancelled()&&steps.validate.outcome=='success'", condition(upload))
        for outcome in ("success", "failure", "skipped"):
            with self.subTest(upload_after_validation=outcome):
                self.assertIs(outcome == "success", would_run(upload, {"validate": {"outcome": outcome}}))
                self.assertIs(outcome == "success", would_run(upload, {"validate": {"outcome": outcome}}, failed=True))
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
        self.assertEqual("failure()&&steps.prepare.outcome=='success'", condition(upload))
        self.assertTrue(str(upload["if"]).strip().startswith("${{"), upload["if"])
        self.assertRegex(upload.get("uses", ""), r"^actions/upload-artifact@[0-9a-f]{40}$")
        self.assertEqual("simcore-measure-agreement-report", upload["with"]["name"])
        self.assertGreater(upload_index, compare_index)
        self.assertEqual(f"{work}/report/", expand(upload["with"]["path"], prepare_run.runner_temp, prepare_run.env))
        # Only prepare puts WORK in the environment, so before it succeeded the path would be /report/:
        # the upload then does not run. After a later step failed, it does.
        self.assertEqual("/report/", expand(upload["with"]["path"], prepare_run.runner_temp, {}))
        for label, prepare_outcome, failed, cancelled, runs_it in (
                ("a step before prepare failed (the benchmarks or the prebuilt publish)", "skipped", True, False, False),
                ("prepare failed", "failure", True, False, False),
                ("a step after prepare failed (an agreement run or compare)", "success", True, False, True),
                ("nothing failed", "success", False, False, False),
                ("the run was cancelled after prepare", "success", False, True, False)):
            with self.subTest(upload=label):
                self.assertIs(runs_it, would_run(upload, after_prepare({}, prepare_outcome), failed=failed, cancelled=cancelled))

        # The agreement job's gate still requires only its own builds.
        _, agreement_compare = only_step("agreement", "agreement compare --work")
        self.assertNotIn("node-interp-relinked", json.dumps(agreement_compare))
        self.assertNotIn("node-aot", json.dumps(agreement_compare))

    def test_the_report_is_written_and_uploaded_after_a_failed_agreement_gate(self):
        """A relinked or AOT build that disagrees with the server fails the job, and is still measured: the
        sweeps and the steps that write, validate, show and upload the report run after the failed gate
        step (`!cancelled()` and their own inputs, not the default success()), so when the measurement
        writes its report the simcore-measurement artifact exists on that run and its mode's
        `agreement.status` is the `Diverged` that compare wrote to report/agreement.json
        (AgreementCommandTests.CompareExitsOneOnADivergedBuildAndWritesItsReport pins compare's file;
        measure.mjs's assembleReport copies the entry's status, as 'assembled report has exactly the
        contract fields' pins). That holds too for a build whose first committed step of flat-run-30hz
        has another hash than the server's first record: measure.mjs's cold-start check accepts the
        server's step number with another hash for a mode whose build report/agreement.json lists as
        Diverged (measure.test.mjs, 'a build that agreement.json lists as Diverged is measured though
        its first step has another hash'), so the row for that case runs the measurement, validation,
        the report and both uploads, and the job fails on its gate. They do not run after a cancel, nor
        when a step before the gate failed, and each waits for the steps whose output it reads."""
        produced = {"publish-relinked": {"produced": "true"}, "publish-aot": {"produced": "true"}}
        relinked, aot, compare = (NEW_MODES[0].agreement, NEW_MODES[1].agreement, "Compare the new builds with the server")
        sweeps, tools, chromium, runner_tests = ("Run the exhaustive math sweeps", "Install the agreement tools",
                                                 "Install Chromium", "Test the measurement runner")
        validate, show, upload = ("Validate the measurement report", "Show the measurement report",
                                  "Upload the measurement report")
        agreement_upload = "Upload the agreement report"
        report_steps = (sweeps, tools, chromium, runner_tests, MEASURE_STEP, validate, show, upload)
        every = [step.get("name") for step in steps("measure")]

        def all_but(*names):
            for name in names:
                self.assertIn(name, every)
            return [name for name in every if name not in names]

        def through(name):
            return every[:every.index(name) + 1]

        # The gate steps fail the job: neither they nor the job continue on error.
        for name in (relinked, aot, compare):
            self.assertNotIn("continue-on-error", step_named("measure", name)[1], name)
        self.assertNotIn("continue-on-error", WORKFLOW["jobs"]["measure"])
        diverged = {relinked: "failure", compare: "failure"}
        for label, outcomes, outputs, cancel_after, ran, conclusion in (
                ("every build agrees", {}, produced, None, all_but(agreement_upload), "success"),
                ("the relinked build diverged", diverged, produced, None, every, "failure"),
                ("the AOT build diverged", {aot: "failure", compare: "failure"}, produced, None, every, "failure"),
                ("only compare failed (a tables mismatch)", {compare: "failure"}, produced, None, every, "failure"),
                ("the AOT build diverged and the relinked one was not produced", {aot: "failure", compare: "failure"},
                 {"publish-relinked": {"produced": "false", "reason": "dotnet publish exited 1; see the publish-relinked step log"},
                  "publish-aot": {"produced": "true"}}, None, all_but(relinked), "failure"),
                ("the workload did not install", {"Install the wasm-tools workload": "failure"}, {}, None,
                 all_but(NEW_MODES[0].publish, NEW_MODES[1].publish, relinked, aot, compare, agreement_upload), "success"),
                ("the relinked build diverged and the measurement failed", {**diverged, MEASURE_STEP: "failure"},
                 produced, None, all_but(validate, show, upload), "failure"),
                # measure.mjs accepts the server's first step number of flat-run-30hz with another hash
                # for a build that compare's report/agreement.json lists as Diverged, so a build that
                # diverges there is still measured, and its report validated, shown and uploaded; the
                # failed gate still fails the job.
                ("the AOT build diverged at flat-run-30hz's first step, which is still measured",
                 {aot: "failure", compare: "failure"}, produced, None, every, "failure"),
                # The measurement is not a step that may fail quietly: it fails the job on its own.
                ("every build agrees and the measurement failed", {MEASURE_STEP: "failure"},
                 produced, None, all_but(validate, show, upload), "failure"),
                ("the relinked build diverged and its report is invalid", {**diverged, validate: "failure"},
                 produced, None, all_but(show, upload), "failure"),
                ("the relinked build diverged and the agreement tools did not install", {**diverged, tools: "failure"},
                 produced, None, all_but(chromium, runner_tests, MEASURE_STEP, validate, show, upload), "failure"),
                ("the relinked build diverged and Chromium did not install", {**diverged, chromium: "failure"},
                 produced, None, all_but(MEASURE_STEP, validate, show, upload), "failure"),
                ("the relinked build diverged and the runner's tests failed", {**diverged, runner_tests: "failure"},
                 produced, None, all_but(MEASURE_STEP, validate, show, upload), "failure"),
                ("cancelled during the relinked agreement run", {relinked: "cancelled"}, produced, relinked,
                 through(relinked), "cancelled"),
                ("the benchmarks failed", {"Run the server benchmarks": "failure"}, produced, None,
                 through("Run the server benchmarks"), "failure"),
                ("the prebuilt publish failed", {"Publish the prebuilt interpreted build": "failure"}, produced, None,
                 through("Publish the prebuilt interpreted build"), "failure"),
                ("prepare failed", {"Prepare the agreement work directory": "failure"}, produced, None,
                 through("Prepare the agreement work directory"), "failure")):
            with self.subTest(label):
                job = simulate_job("measure", outcomes, outputs, cancel_after)
                self.assertEqual(ran, job.ran)
                self.assertEqual(conclusion, job.conclusion)

        # Each step's own condition, for the states the rows above pass through. After a failed gate every
        # one of them runs once its inputs succeeded; after a cancel none does.
        _, measure = step_named("measure", MEASURE_STEP)
        inputs = ("benchmarks", "publish-prebuilt", "prepare", "chromium", "runner-tests")
        ready = {step_id: {"outcome": "success"} for step_id in inputs}
        self.assertEqual("!cancelled()&&" + "&&".join(f"steps.{step_id}.outcome=='success'" for step_id in inputs),
                         condition(measure))
        self.assertIs(True, would_run(measure, ready))
        self.assertIs(True, would_run(measure, ready, failed=True), "after a failed agreement gate")
        self.assertIs(False, would_run(measure, ready, failed=True, cancelled=True))
        for step_id in inputs:
            for outcome in ("failure", "skipped"):
                with self.subTest(measure_input=step_id, outcome=outcome):
                    self.assertIs(False, would_run(measure, {**ready, step_id: {"outcome": outcome}}, failed=True))
        gated = {sweeps: "prepare", tools: "prepare", chromium: "agreement-tools", runner_tests: "agreement-tools",
                 validate: "measure", show: "validate", upload: "validate"}
        for name, needs in gated.items():
            _, step = step_named("measure", name)
            with self.subTest(step=name):
                self.assertEqual(f"!cancelled()&&steps.{needs}.outcome=='success'", condition(step))
                self.assertTrue(str(step["if"]).strip().startswith("${{"), step["if"])
                for outcome in ("success", "failure", "skipped"):
                    self.assertIs(outcome == "success", would_run(step, {needs: {"outcome": outcome}}, failed=True), outcome)
                self.assertIs(False, would_run(step, {needs: {"outcome": "success"}}, failed=True, cancelled=True))
        self.assertEqual(set(report_steps), set(gated) | {MEASURE_STEP})

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

    def test_job_times_out_after_ninety_minutes(self):
        # The sum of every step's own bound and the allowance for the steps without one
        # (test_every_step_bound_fits_in_the_job_timeout), not a target for the run: a normal run takes
        # about 23 minutes.
        self.assertEqual(90, WORKFLOW["jobs"]["measure"]["timeout-minutes"])

    def test_every_step_bound_fits_in_the_job_timeout(self):
        # The job is never cancelled by its own steps: even if every step ran to its own bound in one run,
        # the bounds together, with MEASURE_SETUP_ALLOWANCE_MINUTES for GitHub's job set-up, checkout,
        # setup-dotnet and setup-node (the only steps without a bound), end inside the job's
        # timeout-minutes. So a hung optional step is recorded (an unproduced mode with its reason, a sweep
        # without its summary), and compare, the measurement and both uploads still run, however many
        # steps are slow.
        job_bound = WORKFLOW["jobs"]["measure"]["timeout-minutes"]
        all_job_steps = steps("measure")
        unbounded = [step.get("name") for step in all_job_steps if "timeout-minutes" not in step]
        self.assertEqual(["Checkout repository", "Set up .NET", "Set up Node"], unbounded,
                         "only checkout and the two toolchain setups have no bound of their own")
        for step in all_job_steps:
            if step.get("uses", "").startswith(TOOLCHAIN_ACTIONS):
                continue
            with self.subTest(step=step.get("name")):
                self.assertIsInstance(step["timeout-minutes"], int)
                self.assertNotIsInstance(step["timeout-minutes"], bool)
        bounds = sum(step.get("timeout-minutes", 0) for step in all_job_steps)
        self.assertEqual(82, bounds, "the benchmarks 15, the three publishes 5, 5 and 6, the workload 5, prepare 4, "
                                     "the agreement runs and compare 3 each, the sweeps 10, the tools 2, Chromium 4, "
                                     "the runner tests 2, the measurement 8, validation 2, the report 1 and the uploads 2 each")
        self.assertLessEqual(bounds + MEASURE_SETUP_ALLOWANCE_MINUTES, job_bound)
        # The header comment states the same budget.
        header = re.sub(r"\s*#\s*", " ", WORKFLOW_PATH.read_text().split("\non:", 1)[0])
        self.assertIn(f"inside the job's {job_bound} minutes", header)
        self.assertIn(f"every step's own bound together ({bounds} minutes) and {MEASURE_SETUP_ALLOWANCE_MINUTES} minutes",
                      header)

    def test_every_step_after_the_toolchain_setup_has_its_own_bound(self):
        # A step that hangs fails on its own, or for an optional step is recorded, instead of the job being
        # cancelled: an unproduced mode keeps its reason (FR-031), compare still reports and the failure
        # upload still runs. All the bounds together fit in the job's timeout
        # (test_every_step_bound_fits_in_the_job_timeout), so no number of hung steps cancels it.
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

    def test_the_report_is_shown_in_the_job_log_and_summary(self):
        # The numbers are readable without downloading the artifact: the validated report is printed to
        # the job log and appended to the job summary as a fenced json block.
        validate_index, _ = step_named("measure", "Validate the measurement report")
        index, step = step_named("measure", "Show the measurement report")
        upload_index, _ = step_named("measure", "Upload the measurement report")
        self.assertEqual(validate_index + 1, index, "it comes right after validation")
        self.assertLess(index, upload_index)
        self.assertEqual(1, step.get("timeout-minutes"))
        # Only after validation passed, also when the agreement gate failed before it, never after a
        # cancel, and nothing turns its own failure into a pass.
        self.assertEqual("!cancelled()&&steps.validate.outcome=='success'", condition(step))
        self.assertNotIn("continue-on-error", step)
        validated, invalid = {"validate": {"outcome": "success"}}, {"validate": {"outcome": "failure"}}
        self.assertIs(True, would_run(step, validated))
        self.assertIs(True, would_run(step, validated, failed=True), "after a failed agreement gate")
        self.assertIs(False, would_run(step, invalid, failed=True), "after a failed validation")
        self.assertIs(False, would_run(step, {"validate": {"outcome": "skipped"}}, failed=True), "without a report")
        self.assertIs(False, would_run(step, validated, cancelled=True))
        # No expression is interpolated into the script; it reads the report through $RUNNER_TEMP.
        self.assertNotIn("${{", step["run"])
        self.assertNotIn("env", step)
        text = command(step)
        self.assertEqual(2, text.count(f'cat "{MEASUREMENT_REPORT}"'), "once to the log, once to the summary")
        self.assertIn('>> "$GITHUB_STEP_SUMMARY"', text)

        # Run on a report, with and without its final newline: the log is the report, and the summary is
        # the heading and the report in a json block whose closing fence starts its own line.
        report = json.dumps({"formatVersion": 1, "kind": "simcore-measurement", "runnerClass": "gha-ubuntu-latest-x64"},
                            indent=2)
        for label, written in (("ends with a newline", report + "\n"), ("no final newline", report)):
            with self.subTest(report=label):
                def setup(runner_temp, repo, written=written):
                    Path(expand(MEASUREMENT_REPORT, runner_temp)).write_text(written)

                run = run_step("measure", "Show the measurement report", setup=setup)
                self.assertEqual(0, run.returncode, run.stderr)
                self.assertEqual(written, run.stdout)
                self.assertEqual(f"### simcore-measurement\n\n```json\n{report}\n```\n", run.summary)
                self.assertEqual([], run.calls)
        # A missing report fails the step (validation, which the upload also waits for, fails on it first).
        run = run_step("measure", "Show the measurement report")
        self.assertNotEqual(0, run.returncode)


class AutoMerge(unittest.TestCase):
    """US2.7's gate on the pull request: auto-merge.yml merges once every check run registered on the
    head commit has passed, and hears about a finished workflow only through its workflow_run list.
    Without "SimCore" there, a pull request whose last check to finish is the agreement job's would not
    be decided again when it reports.
    test_the_merge_step_does_not_merge_while_the_agreement_check_failed_or_runs runs the step that
    decides, so what is pinned is the script's handling of the check run, not only its filter.

    These tests cover `agreement` check runs that have registered on the head commit: one that has
    failed or was cancelled, is queued or is in progress, or has passed. They do not cover, and US2.7
    no longer claims, a check run that has not registered yet: while auto-merge's REQUIRE_CHECKS is
    "false" no check counts as missing, so auto-merge does not wait for it (the spec's Assumptions:
    the window predates this feature, auto-merge.yml is not changed here, and it closes once the check
    reports on every pull request, is made required and REQUIRE_CHECKS is "true")."""

    def test_auto_merge_waits_for_simcore(self):
        run = AUTO_MERGE.get("on", AUTO_MERGE.get(True))["workflow_run"]
        self.assertIn("SimCore", run["workflows"])
        self.assertIn("Workflow tests", run["workflows"])
        self.assertIn(WORKFLOW["name"], run["workflows"])
        self.assertEqual(["completed"], run["types"])

    def test_a_failed_or_pending_agreement_check_holds_the_merge(self):
        """The agreement job's registered check run, under the name GitHub gives it (the job's `name`),
        goes through auto-merge.yml's own CHECK_RUNS_JQ filter and its `failed=` and `pending=` awk lines:
        a failed run is a reason to leave the pull request alone, and a queued or in-progress one keeps it
        waiting. A job named `auto-merge` or `claude` would be filtered out, and the gate would never
        hold a merge. Every row is a check run that the head commit's check-runs list holds; one that
        has not registered is not in that list, and nothing here waits for it."""
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

    def test_the_merge_step_does_not_merge_while_the_agreement_check_failed_or_runs(self):
        """auto-merge.yml's "Merge, wait, or leave it" step itself, run under bash with a stand-in gh, on a
        pull request that qualifies in every other way: the `agreement` check run registered on the head
        commit decides. A failed or cancelled run is a reason to leave it alone, a queued or in-progress
        one keeps it waiting, and where this workflow had turned auto-merge on, it turns it off, since
        GitHub's auto-merge waits only for required checks. Only a passed run merges. Each row's check
        run is in the head commit's check-runs answer; a check run that has not registered yet is the
        open follow-up of the class docstring, which no row claims to cover."""
        check = WORKFLOW["jobs"]["agreement"].get("name", "agreement")
        pr, repo, head = AUTO_MERGE_PR, AUTO_MERGE_REPO, AUTO_MERGE_HEAD
        check_runs_jq = auto_merge_step(AUTO_MERGE_STEP)["env"]["CHECK_RUNS_JQ"]
        # The SimCore measure job passed, and this job's own check (still running) is not counted.
        others = [{"name": "measure", "status": "completed", "conclusion": "success"},
                  {"name": "auto-merge", "status": "in_progress", "conclusion": None}]
        failure = {"status": "completed", "conclusion": "failure"}
        cancelled = {"status": "completed", "conclusion": "cancelled"}
        queued = {"status": "queued", "conclusion": None}
        in_progress = {"status": "in_progress", "conclusion": None}
        # Auto-merge this workflow turned on: it carries the workflow's label.
        turned_on = {"enabledBy": {"login": "nbon12"}, "mergeMethod": "SQUASH"}
        disable = ["pr", "merge", pr, "--repo", repo, "--disable-auto"]
        for label, agreement, auto_merge, summary, merges in (
                ("failure", failure, None, f"Left #{pr} alone because these checks failed: {check}.", []),
                ("cancelled", cancelled, None, f"Left #{pr} alone because these checks failed: {check}.", []),
                ("queued", queued, None, f"#{pr} merges once these checks pass: {check}.", []),
                ("in progress", in_progress, None, f"#{pr} merges once these checks pass: {check}.", []),
                ("failure, auto-merge on", failure, turned_on,
                 f"Auto-merge turned off for #{pr} because these checks failed: {check}.", [disable]),
                ("cancelled, auto-merge on", cancelled, turned_on,
                 f"Auto-merge turned off for #{pr} because these checks failed: {check}.", [disable]),
                ("queued, auto-merge on", queued, turned_on,
                 f"Auto-merge paused for #{pr} until these checks finish: {check}.", [disable]),
                ("in progress, auto-merge on", in_progress, turned_on,
                 f"Auto-merge paused for #{pr} until these checks finish: {check}.", [disable])):
            with self.subTest(agreement=label):
                run = run_auto_merge_step([{"name": check, **agreement}, *others],
                                          labels=("auto-merge",) if auto_merge else (), auto_merge=auto_merge)
                self.assertEqual(0, run.returncode, run.stderr)
                pr_merges = [call for call in run.calls if call[:2] == ["pr", "merge"]]
                self.assertEqual([], [call for call in pr_merges if "--squash" in call or "--auto" in call],
                                 "the step must neither merge nor turn auto-merge on")
                self.assertEqual(merges, pr_merges)
                self.assertEqual(summary + "\n", run.summary)
                self.assertIn(["api", "--paginate", f"repos/{repo}/commits/{head}/check-runs?per_page=100",
                               "--jq", check_runs_jq], run.calls)

        # The control: with the agreement run passed, the same pull request squash-merges at its head.
        run = run_auto_merge_step([{"name": check, "status": "completed", "conclusion": "success"}, *others])
        self.assertEqual(0, run.returncode, run.stderr)
        self.assertEqual([["pr", "merge", pr, "--repo", repo, "--squash", "--match-head-commit", head]],
                         [call for call in run.calls if call[:2] == ["pr", "merge"]])
        self.assertEqual(f"Squash-merged #{pr}: every check passed.\n", run.summary)


if __name__ == "__main__":
    unittest.main()
