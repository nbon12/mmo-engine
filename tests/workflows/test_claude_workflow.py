"""Contract tests for .github/workflows/claude.yml (spec 001-claude-mention-agent).

The job's `if:` condition is evaluated against event payloads by `gh_expr.Expr`, an evaluator for
the GitHub Actions expression subset the condition uses, so each Given/When/Then scenario that
decides whether a run starts is tested as behavior, not as text. The rest of the workflow is
asserted as configuration.

Run: python3 -m unittest discover -s tests/workflows -v   (needs PyYAML)
"""

import json
import re
import subprocess
import unittest
from pathlib import Path

import yaml

from gh_expr import Expr, truthy

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW_PATH = ROOT / ".github" / "workflows" / "claude.yml"
AUTO_MERGE_PATH = ROOT / ".github" / "workflows" / "auto-merge.yml"
WORKFLOW_TEXT = WORKFLOW_PATH.read_text()
WORKFLOW = yaml.safe_load(WORKFLOW_TEXT)
JOB = WORKFLOW["jobs"]["claude"]


def job_runs(event_name, payload):
    """Whether the `claude` job's condition passes for this event, as GitHub would decide."""
    return truthy(Expr(JOB["if"], {"github": {"event_name": event_name, "event": payload}}).evaluate())


# --- Event payloads -------------------------------------------------------------------------------

def issue(title="A request", body="", association="OWNER", labels=()):
    return {"title": title, "body": body, "author_association": association,
            "labels": [{"name": n} for n in labels]}


def pull_request(labels=()):
    return {"labels": [{"name": n} for n in labels]}


def issue_comment(body, association="OWNER", labels=()):
    return "issue_comment", {"comment": {"body": body, "author_association": association},
                             "issue": issue(labels=labels)}


def review_comment(body, association="OWNER", labels=()):
    return "pull_request_review_comment", {
        "comment": {"body": body, "author_association": association},
        "pull_request": pull_request(labels)}


def review(body, association="OWNER", labels=()):
    return "pull_request_review", {"review": {"body": body, "author_association": association},
                                   "pull_request": pull_request(labels)}


def issue_opened(title="A request", body="", association="OWNER", labels=()):
    return "issues", {"action": "opened", "issue": issue(title, body, association, labels)}


MENTION = "@claude please summarise this thread in one line."
WRITE_ACCESS = ("OWNER", "MEMBER", "COLLABORATOR")
NO_WRITE_ACCESS = ("CONTRIBUTOR", "FIRST_TIME_CONTRIBUTOR", "FIRST_TIMER", "NONE", "MANNEQUIN")
ALL_PATHS = {
    "issue_comment": lambda body, association="OWNER", labels=(): issue_comment(body, association, labels),
    "pull_request_review_comment": review_comment,
    "pull_request_review": review,
    "issues": lambda body, association="OWNER", labels=(): issue_opened(
        body=body, association=association, labels=labels),
}


# --- User Story 1: hand an issue or PR to Claude by mentioning it ---------------------------------

class UserStory1MentionStartsARun(unittest.TestCase):
    def test_us1_1_write_access_user_opens_issue_mentioning_claude_in_body_or_title(self):
        for association in WRITE_ACCESS:
            with self.subTest(association=association, where="body"):
                self.assertIs(job_runs(*issue_opened(body=MENTION, association=association)), True)
            with self.subTest(association=association, where="title"):
                self.assertIs(job_runs(*issue_opened(title="@claude: fix the typo",
                                                     association=association)), True)

    def test_us1_2_write_access_user_comments_mentioning_claude_on_issue_or_pr(self):
        for association in WRITE_ACCESS:
            with self.subTest(association=association):
                self.assertIs(job_runs(*issue_comment(MENTION, association)), True)

    def test_us1_3_write_access_user_mentions_claude_in_pr_review_or_review_comment(self):
        for association in WRITE_ACCESS:
            with self.subTest(association=association, event="review"):
                self.assertIs(job_runs(*review(MENTION, association)), True)
            with self.subTest(association=association, event="review comment"):
                self.assertIs(job_runs(*review_comment(MENTION, association)), True)

    def test_us1_3_mention_matches_in_any_letter_case(self):
        self.assertIs(job_runs(*issue_comment("@Claude can you look?")), True)

    def test_us1_4_no_mention_means_the_job_is_skipped(self):
        for name, make in ALL_PATHS.items():
            with self.subTest(event=name):
                self.assertIs(job_runs(*make("Looks good to me.")), False)
        self.assertIs(job_runs("issues", {"action": "opened", "issue": issue(body=None)}), False)

    def test_us1_4_other_events_never_start_a_run(self):
        self.assertIs(job_runs("push", {"head_commit": {"message": MENTION}}), False)

    def test_us1_5_job_has_no_merge_step(self):
        # The job's only steps are checkout and the action: nothing here merges or pushes to main.
        uses = [step.get("uses", "").split("@")[0] for step in JOB["steps"]]
        self.assertEqual(uses, ["actions/checkout", "anthropics/claude-code-action"])
        self.assertEqual([step for step in JOB["steps"] if "run" in step], [])
        self.assertNotRegex(WORKFLOW_TEXT, r"gh pr merge|git push")


# --- User Story 2: outsiders cannot start a run -----------------------------------------------------

class UserStory2OutsidersCannotStartARun(unittest.TestCase):
    def test_us2_1_author_without_write_access_is_skipped_on_every_path(self):
        for name, make in ALL_PATHS.items():
            for association in NO_WRITE_ACCESS:
                with self.subTest(event=name, association=association):
                    self.assertIs(job_runs(*make(MENTION, association)), False)

    def test_us2_1_missing_association_is_skipped(self):
        event, payload = issue_comment(MENTION)
        del payload["comment"]["author_association"]
        self.assertIs(job_runs(event, payload), False)

    def test_us2_2_dependabot_comment_on_its_own_pr_is_skipped(self):
        # Dependabot comments as CONTRIBUTOR, and its comments can quote release notes.
        self.assertIs(job_runs(*issue_comment(
            "Bumps anthropics/claude-code-action. Release notes: @claude now ...", "CONTRIBUTOR")), False)


# --- Sweep-log guard (Edge Cases; hoa_management_system#290) ----------------------------------------

class SweepLogGuard(unittest.TestCase):
    def test_owner_mention_on_sweep_log_issue_or_pr_is_skipped_on_every_path(self):
        for name, make in ALL_PATHS.items():
            with self.subTest(event=name):
                self.assertIs(job_runs(*make(MENTION, "OWNER", labels=("sweep-log",))), False)

    def test_other_labels_do_not_block(self):
        self.assertIs(job_runs(*issue_comment(MENTION, labels=("bug", "story"))), True)


# --- User Story 3: credentials ----------------------------------------------------------------------

class UserStory3Credentials(unittest.TestCase):
    def action_inputs(self):
        step = next(s for s in JOB["steps"] if s.get("uses", "").startswith("anthropics/claude-code-action@"))
        return step["with"]

    def test_us3_2_either_secret_is_wired_from_repository_secrets(self):
        inputs = self.action_inputs()
        self.assertEqual(inputs["claude_code_oauth_token"], "${{ secrets.CLAUDE_CODE_OAUTH_TOKEN }}")
        self.assertEqual(inputs["anthropic_api_key"], "${{ secrets.ANTHROPIC_API_KEY }}")

    def test_no_credential_is_hard_coded(self):
        self.assertNotRegex(WORKFLOW_TEXT, r"sk-ant-")


# --- Functional requirements that are configuration --------------------------------------------------

class FunctionalRequirements(unittest.TestCase):
    def test_fr001_triggers(self):
        triggers = WORKFLOW.get("on", WORKFLOW.get(True))  # PyYAML reads the bare key `on` as True
        self.assertEqual(triggers, {
            "issue_comment": {"types": ["created"]},
            "pull_request_review_comment": {"types": ["created"]},
            "issues": {"types": ["opened"]},
            "pull_request_review": {"types": ["submitted"]},
        })

    def test_fr004_least_privilege_permissions(self):
        self.assertEqual(JOB["permissions"], {
            "contents": "write", "pull-requests": "write", "issues": "write",
            "id-token": "write", "actions": "read"})
        self.assertNotIn("permissions", WORKFLOW)

    def test_fr005_every_action_pinned_to_full_sha_with_version_comment(self):
        lines = [line for line in WORKFLOW_TEXT.splitlines() if re.match(r"\s*(-\s*)?uses:", line)]
        self.assertTrue(lines)
        for line in lines:
            with self.subTest(line=line.strip()):
                self.assertRegex(line, r"uses:\s*[\w.-]+/[\w.-]+@[0-9a-f]{40}\s+#\s*v\d")

    def test_fr005_dependabot_updates_github_actions(self):
        config = yaml.safe_load((ROOT / ".github" / "dependabot.yml").read_text())
        ecosystems = {u["package-ecosystem"]: u for u in config["updates"]}
        self.assertEqual(ecosystems["github-actions"]["directory"], "/")

    def test_fr006_serialized_per_thread_without_cancelling_in_progress(self):
        self.assertEqual(JOB["concurrency"], {
            "group": "claude-${{ github.event.issue.number || github.event.pull_request.number }}",
            "cancel-in-progress": False})

    def test_fr007_bounded_runs(self):
        self.assertEqual(JOB["timeout-minutes"], 60)
        args = UserStory3Credentials.action_inputs(self)["claude_args"]
        self.assertRegex(args, r"--max-turns 100\b")

    def test_fr009_auto_merge_never_merges_a_claude_bot_head_commit(self):
        step = AUTO_MERGE_STEP
        self.assertIn('head_by="$(gh api "repos/${REPO}/commits/${head}" --jq "$HEAD_BY_JQ")"', step["run"])
        self.assertIn("grep -qF 'claude[bot]' <<<\"$head_by\"", step["run"])
        self.assertIn('reasons+=("its head commit is from claude[bot]")', step["run"])

    def test_fr010_no_toolchains_or_shell_commands_while_repo_is_docs_only(self):
        uses = " ".join(step.get("uses", "") for step in JOB["steps"])
        self.assertNotRegex(uses, r"setup-(dotnet|node|python)")
        self.assertNotIn("--allowedTools", UserStory3Credentials.action_inputs(self)["claude_args"])


AUTO_MERGE_STEP = yaml.safe_load(AUTO_MERGE_PATH.read_text())["jobs"]["auto-merge"]["steps"][0]


def run_jq(filter_, document):
    result = subprocess.run(["jq", "-r", filter_], input=json.dumps(document), capture_output=True,
                            text=True, check=True)
    return result.stdout


class AutoMergeFiltersOnRealApiShapes(unittest.TestCase):
    """auto-merge.yml's jq filters, run with jq against GitHub API response shapes (FR-009)."""

    def head_by(self, commit):
        return run_jq(AUTO_MERGE_STEP["env"]["HEAD_BY_JQ"], commit)

    def test_claude_code_action_git_commit_is_recognised(self):
        # What a claude-code-action v1.0.265 git push looks like: bot_id 41898282 is
        # github-actions[bot], so both logins resolve to it; only the git identity says claude[bot].
        commit = {
            "author": {"login": "github-actions[bot]"}, "committer": {"login": "github-actions[bot]"},
            "commit": {"author": {"name": "claude[bot]",
                                  "email": "41898282+claude[bot]@users.noreply.github.com"},
                       "committer": {"name": "claude[bot]",
                                     "email": "41898282+claude[bot]@users.noreply.github.com"}}}
        self.assertIn("claude[bot]", self.head_by(commit))

    def test_api_signed_commit_with_unmapped_logins_is_recognised(self):
        commit = {"author": {"login": "claude[bot]"}, "committer": None,
                  "commit": {"author": {"name": "claude[bot]", "email": "x"},
                             "committer": {"name": "GitHub", "email": "noreply@github.com"}}}
        self.assertIn("claude[bot]", self.head_by(commit))

    def test_owner_commit_is_not_flagged(self):
        commit = {"author": {"login": "nbon12"}, "committer": {"login": "web-flow"},
                  "commit": {"author": {"name": "Nicholas", "email": "n@example.com"},
                             "committer": {"name": "GitHub", "email": "noreply@github.com"}}}
        self.assertNotIn("claude[bot]", self.head_by(commit))

    def test_claude_and_auto_merge_check_runs_never_count_as_failed_or_pending(self):
        runs = {"check_runs": [
            {"name": "claude", "status": "completed", "conclusion": "cancelled"},
            {"name": "claude", "status": "in_progress", "conclusion": None},
            {"name": "auto-merge", "status": "completed", "conclusion": "failure"},
            {"name": "workflow-tests", "status": "completed", "conclusion": "success"},
            {"name": "other", "status": "queued", "conclusion": None}]}
        self.assertEqual(run_jq(AUTO_MERGE_STEP["env"]["CHECK_RUNS_JQ"], runs),
                         "workflow-tests\tsuccess\nother\tpending\n")
        self.assertIn('--jq "$CHECK_RUNS_JQ"', AUTO_MERGE_STEP["run"])


class ExpressionEvaluator(unittest.TestCase):
    """The evaluator's own semantics, so the scenario tests above can be trusted."""

    def ev(self, text, **ctx):
        return Expr(text, ctx).evaluate()

    def test_contains_ignores_case_for_strings_and_array_items(self):
        self.assertIs(self.ev("contains('Hi @CLAUDE', '@claude')"), True)
        self.assertIs(self.ev("contains(fromJSON('[\"OWNER\"]'), 'owner')"), True)
        self.assertIs(self.ev("contains(fromJSON('[\"OWNER\"]'), 'NONE')"), False)

    def test_star_filter_collects_fields_and_tolerates_missing_arrays(self):
        ctx = {"e": {"labels": [{"name": "a"}, {"name": "sweep-log"}]}}
        self.assertIs(self.ev("contains(e.labels.*.name, 'sweep-log')", **ctx), True)
        self.assertIs(self.ev("contains(e.missing.*.name, 'sweep-log')", **ctx), False)

    def test_logical_operators_and_null_properties(self):
        self.assertIs(self.ev("!contains(e.x, 'y') && 'a' == 'A'", e={}), True)
        self.assertIsNone(self.ev("e.a.b", e={}))
        self.assertEqual(self.ev("e.a || 'fallback'", e={}), "fallback")


if __name__ == "__main__":
    unittest.main()
