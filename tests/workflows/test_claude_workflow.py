"""Contract tests for .github/workflows/claude.yml (spec 001-claude-mention-agent).

The job's `if:` condition is evaluated against event payloads by a small evaluator for the GitHub
Actions expression subset the condition uses (see `Expr`), so each Given/When/Then scenario that
decides whether a run starts is tested as behavior, not as text. The rest of the workflow is
asserted as configuration.

Run: python3 -m unittest discover -s tests/workflows -v   (needs PyYAML)
"""

import json
import re
import unittest
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parents[2]
WORKFLOW_PATH = ROOT / ".github" / "workflows" / "claude.yml"
AUTO_MERGE_PATH = ROOT / ".github" / "workflows" / "auto-merge.yml"
WORKFLOW_TEXT = WORKFLOW_PATH.read_text()
WORKFLOW = yaml.safe_load(WORKFLOW_TEXT)
JOB = WORKFLOW["jobs"]["claude"]


# --- A GitHub Actions expression evaluator, for the subset claude.yml's condition uses -----------

TOKEN = re.compile(
    r"\s*(?:(?P<str>'(?:[^']|'')*')|(?P<num>\d+)|(?P<op>&&|\|\||==|!=|[!().,*\[\]])"
    r"|(?P<ident>[A-Za-z_][A-Za-z0-9_-]*))"
)


class Expr:
    """Recursive-descent evaluator with GitHub's semantics for what the condition uses:
    `||`, `&&`, `!`, `==`, `!=`, parentheses, property access (including the `.*.` filter),
    string literals, `contains()` and `fromJSON()`. Comparisons and `contains()` ignore case;
    `||` and `&&` return an operand, as in GitHub."""

    def __init__(self, text, context):
        self.tokens = []
        pos = 0
        text = text.strip()
        while pos < len(text):
            m = TOKEN.match(text, pos)
            if not m or m.end() == pos:
                raise ValueError(f"cannot tokenize at: {text[pos:pos + 30]!r}")
            kind = m.lastgroup
            self.tokens.append((kind, m.group(kind)))
            pos = m.end()
            while pos < len(text) and text[pos].isspace():
                pos += 1
        self.i = 0
        self.ctx = context

    def evaluate(self):
        value = self.or_()
        if self.i != len(self.tokens):
            raise ValueError(f"unexpected token {self.tokens[self.i]}")
        return value

    def peek(self):
        return self.tokens[self.i][1] if self.i < len(self.tokens) else None

    def take(self, expected=None):
        kind, value = self.tokens[self.i]
        if expected is not None and value != expected:
            raise ValueError(f"expected {expected!r}, got {value!r}")
        self.i += 1
        return kind, value

    def or_(self):
        left = self.and_()
        while self.peek() == "||":
            self.take()
            right = self.and_()
            left = left if truthy(left) else right
        return left

    def and_(self):
        left = self.cmp()
        while self.peek() == "&&":
            self.take()
            right = self.cmp()
            left = right if truthy(left) else left
        return left

    def cmp(self):
        left = self.unary()
        while self.peek() in ("==", "!="):
            op = self.take()[1]
            right = self.unary()
            equal = loose_equal(left, right)
            left = equal if op == "==" else not equal
        return left

    def unary(self):
        if self.peek() == "!":
            self.take()
            return not truthy(self.unary())
        return self.primary()

    def primary(self):
        kind, value = self.take()
        if value == "(":
            inner = self.or_()
            self.take(")")
            return inner
        if kind == "str":
            return value[1:-1].replace("''", "'")
        if kind == "num":
            return int(value)
        if kind != "ident":
            raise ValueError(f"unexpected {value!r}")
        if value in ("true", "false"):
            return value == "true"
        if value == "null":
            return None
        if self.peek() == "(":
            return self.call(value)
        return self.path(self.ctx.get(value))

    def call(self, name):
        self.take("(")
        args = [self.or_()]
        while self.peek() == ",":
            self.take()
            args.append(self.or_())
        self.take(")")
        if name == "contains":
            haystack, needle = args
            if isinstance(haystack, list):
                return any(loose_equal(item, needle) for item in haystack)
            return to_str(needle).lower() in to_str(haystack).lower()
        if name == "fromJSON":
            return json.loads(args[0])
        raise ValueError(f"unsupported function {name}")

    def path(self, value):
        while self.peek() == ".":
            self.take()
            kind, name = self.take()
            if name == "*":
                value = value if isinstance(value, list) else []
                self.take(".")
                _, field = self.take()
                value = [item.get(field) for item in value if isinstance(item, dict)]
            elif isinstance(value, list):
                value = [item.get(name) for item in value if isinstance(item, dict)]
            else:
                value = value.get(name) if isinstance(value, dict) else None
        return value


def truthy(value):
    return value not in (None, False, 0, "") and value == value


def to_str(value):
    if value is None:
        return ""
    if isinstance(value, bool):
        return "true" if value else "false"
    return str(value)


def loose_equal(a, b):
    if isinstance(a, str) and isinstance(b, str):
        return a.lower() == b.lower()
    return a == b


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
        script = yaml.safe_load(AUTO_MERGE_PATH.read_text())["jobs"]["auto-merge"]["steps"][0]["run"]
        self.assertIn("""grep -qF 'claude[bot]' <<<"$head_by\"""", script)
        self.assertIn('reasons+=("its head commit is from claude[bot]")', script)

    def test_fr010_no_toolchains_or_shell_commands_while_repo_is_docs_only(self):
        uses = " ".join(step.get("uses", "") for step in JOB["steps"])
        self.assertNotRegex(uses, r"setup-(dotnet|node|python)")
        self.assertNotIn("--allowedTools", UserStory3Credentials.action_inputs(self)["claude_args"])


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
