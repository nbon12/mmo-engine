"""Contract tests for .github/workflows/dotnet.yml (spec 013-regionizer-simulator, T003).

The workflow builds, checks formatting, runs the tests with coverage and runs the changed-file
coverage gate. The gate's base ref is an Actions expression, evaluated here with `gh_expr.Expr`
against pull_request and push contexts; the rest is asserted as configuration.

Run: python3 -m unittest discover -s tests/workflows -v   (needs PyYAML)
"""

import re
import unittest
from pathlib import Path

import yaml

from gh_expr import Expr, to_str

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW_PATH = ROOT / ".github" / "workflows" / "dotnet.yml"
WORKFLOW_TEXT = WORKFLOW_PATH.read_text()
WORKFLOW = yaml.safe_load(WORKFLOW_TEXT)
JOB = WORKFLOW["jobs"]["dotnet"]
STEPS = JOB["steps"]

SHA_PINNED = re.compile(r"^[\w.-]+/[\w.-]+@[0-9a-f]{40}$")
EXPRESSION = re.compile(r"\$\{\{(.*?)\}\}")


def render(template, context):
    """Substitutes each `${{ ... }}` in a workflow string, as the runner would."""
    return EXPRESSION.sub(lambda m: to_str(Expr(m.group(1), context).evaluate()), template)


def step_index(predicate, what):
    matches = [i for i, step in enumerate(STEPS) if predicate(step)]
    if len(matches) != 1:
        raise AssertionError(f"expected exactly one {what} step, found {len(matches)}")
    return matches[0]


def run_step(pattern):
    regex = re.compile(pattern)
    return step_index(lambda s: bool(regex.search(s.get("run", ""))), pattern)


def uses_step(action):
    return step_index(lambda s: s.get("uses", "").split("@")[0] == action, action)


class Triggers(unittest.TestCase):
    def test_runs_on_every_pull_request_and_on_push_to_main(self):
        triggers = WORKFLOW.get("on", WORKFLOW.get(True))  # PyYAML reads the bare key `on` as True
        self.assertEqual(set(triggers), {"pull_request", "push"})
        self.assertIn(triggers["pull_request"], (None, {}))  # no path filter: every PR is gated
        self.assertEqual(triggers["push"], {"branches": ["main"]})


class Permissions(unittest.TestCase):
    def test_workflow_token_is_read_only(self):
        self.assertEqual(WORKFLOW["permissions"], {"contents": "read"})
        for name, job in WORKFLOW["jobs"].items():
            with self.subTest(job=name):
                for scope, level in (job.get("permissions") or {}).items():
                    self.assertIn(level, ("read", "none"), scope)

    def test_checkout_does_not_keep_the_token(self):
        self.assertIs(STEPS[uses_step("actions/checkout")]["with"]["persist-credentials"], False)


class PinnedActions(unittest.TestCase):
    def test_every_uses_is_pinned_to_a_full_commit_sha(self):
        uses = [step["uses"] for job in WORKFLOW["jobs"].values()
                for step in job["steps"] if "uses" in step]
        self.assertTrue(uses)
        for value in uses:
            with self.subTest(uses=value):
                self.assertRegex(value, SHA_PINNED)

    def test_every_uses_line_carries_its_version_comment(self):
        lines = [line for line in WORKFLOW_TEXT.splitlines() if re.match(r"\s*(-\s*)?uses:", line)]
        self.assertTrue(lines)
        for line in lines:
            with self.subTest(line=line.strip()):
                self.assertRegex(line, r"uses:\s*[\w.-]+/[\w.-]+@[0-9a-f]{40}\s+#\s*v\d")

    def test_checkout_and_setup_dotnet_pins_and_inputs(self):
        checkout = STEPS[uses_step("actions/checkout")]
        self.assertEqual(checkout["uses"], "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1")
        self.assertEqual(checkout["with"]["fetch-depth"], 0)  # the gate diffs against the base
        setup = STEPS[uses_step("actions/setup-dotnet")]
        self.assertEqual(setup["uses"], "actions/setup-dotnet@0f4a354dd5547a28c70b7853dbd95d5b67886e73")
        self.assertEqual(setup["with"], {"global-json-file": "global.json"})


class Job(unittest.TestCase):
    def test_job_is_named_and_bounded(self):
        self.assertEqual(JOB["name"], "dotnet")
        self.assertEqual(JOB["runs-on"], "ubuntu-latest")
        self.assertIsInstance(JOB["timeout-minutes"], int)
        self.assertTrue(0 < JOB["timeout-minutes"] <= 60)

    def test_build_format_test_and_gate_steps_run_in_order(self):
        order = [
            uses_step("actions/checkout"),
            uses_step("actions/setup-dotnet"),
            run_step(r"^dotnet restore\b"),
            run_step(r"^dotnet build MmoEngine\.sln\b"),
            run_step(r"^dotnet format MmoEngine\.sln\b"),
            run_step(r"^dotnet test MmoEngine\.sln\b"),
            run_step(r"-p \"test_coverage_gate\.py\""),
            run_step(r"^python3 tools/coverage_gate\.py\b"),
        ]
        self.assertEqual(order, sorted(order))

    def test_build_treats_warnings_as_errors(self):
        self.assertEqual(STEPS[run_step(r"^dotnet build\b")]["run"],
                         "dotnet build MmoEngine.sln -warnaserror --no-restore")

    def test_format_only_verifies(self):
        self.assertEqual(STEPS[run_step(r"^dotnet format\b")]["run"],
                         "dotnet format MmoEngine.sln --verify-no-changes --no-restore")

    def test_tests_collect_cobertura_coverage_where_the_gate_reads_it(self):
        test = STEPS[run_step(r"^dotnet test\b")]["run"]
        self.assertEqual(test, 'dotnet test MmoEngine.sln --no-build --collect:"XPlat Code Coverage" '
                               "--results-directory coverage")
        gate = STEPS[run_step(r"^python3 tools/coverage_gate\.py\b")]["run"]
        self.assertIn("--coverage-dir coverage", gate)
        self.assertTrue((ROOT / "tools" / "coverage_gate.py").is_file())

    def test_gate_reports_to_the_job_summary(self):
        gate = STEPS[run_step(r"^python3 tools/coverage_gate\.py\b")]["run"]
        self.assertIn('--summary "$GITHUB_STEP_SUMMARY"', gate)

    def test_no_expression_is_expanded_inside_a_script(self):
        # Event data reaches scripts only through env, never by text substitution into `run`.
        for step in STEPS:
            with self.subTest(step=step.get("name")):
                self.assertNotIn("${{", step.get("run", ""))


class GateBaseRef(unittest.TestCase):
    """The gate diffs against the pull request's base branch, or main on a push."""

    def base_ref(self, event_name, base_ref):
        step = STEPS[run_step(r"^python3 tools/coverage_gate\.py\b")]
        self.assertIn('--base "$BASE_REF"', step["run"])
        context = {"github": {"event_name": event_name, "base_ref": base_ref}}
        return render(step["env"]["BASE_REF"], context)

    def test_base_ref_per_event(self):
        cases = [  # (event, github.base_ref as Actions sets it, expected base)
            ("pull_request", "main", "origin/main"),
            ("pull_request", "release/1.0", "origin/release/1.0"),
            ("push", "", "origin/main"),
        ]
        for event, base_ref, expected in cases:
            with self.subTest(event=event, base_ref=base_ref):
                self.assertEqual(self.base_ref(event, base_ref), expected)


if __name__ == "__main__":
    unittest.main()
