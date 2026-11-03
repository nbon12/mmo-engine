"""Tests for tools/coverage_gate.py, the changed-file line coverage gate (spec 013, T003).

Reports are synthetic Cobertura XML in the shape coverlet writes. Changed-file lists are given
directly, or come from a throwaway git repository for the `git diff` part.

Run: python3 -m unittest discover -s tests/workflows -v
"""

import contextlib
import io
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
import uuid
from pathlib import Path
from xml.sax.saxutils import quoteattr

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))

import coverage_gate  # noqa: E402  (imported after the path is set)


def unique_dir(test):
    """A fresh temp directory for one test, removed (best effort) when it ends."""
    path = Path(tempfile.gettempdir()) / "mmo-tests" / uuid.uuid4().hex
    path.mkdir(parents=True)
    test.addCleanup(shutil.rmtree, path, True)
    return path


def lines_xml(lines, indent):
    return "".join(f'{indent}<line number="{n}" hits="{h}" branch="False" />\n'
                   for n, h in sorted(lines.items()))


def cobertura(classes, sources=()):
    """A coverlet-shaped Cobertura report. `classes` is a list of (filename, {line: hits})."""
    body = []
    for i, (filename, lines) in enumerate(classes):
        body.append(
            f'        <class name="C{i}" filename={quoteattr(filename)} line-rate="0" branch-rate="0">\n'
            '          <methods>\n'
            '            <method name="M" signature="()" line-rate="0" branch-rate="0">\n'
            '              <lines>\n'
            f'{lines_xml(lines, "                ")}'
            '              </lines>\n'
            '            </method>\n'
            '          </methods>\n'
            '          <lines>\n'
            f'{lines_xml(lines, "            ")}'
            '          </lines>\n'
            '        </class>\n')
    source_xml = "".join(f"    <source>{s}</source>\n" for s in sources)
    return ('<?xml version="1.0" encoding="utf-8"?>\n'
            '<coverage line-rate="0" branch-rate="0" version="1.9" timestamp="1">\n'
            f'  <sources>\n{source_xml}  </sources>\n'
            '  <packages>\n    <package name="P" line-rate="0" branch-rate="0">\n      <classes>\n'
            f'{"".join(body)}'
            '      </classes>\n    </package>\n  </packages>\n</coverage>\n')


def write_report(directory, classes, sources=("/repo/",)):
    """Writes a report where coverlet's collector would: <dir>/<guid>/coverage.cobertura.xml."""
    path = Path(directory) / uuid.uuid4().hex / "coverage.cobertura.xml"
    path.parent.mkdir(parents=True)
    path.write_text(cobertura(classes, sources), encoding="utf-8")
    return path


def hits(covered, coverable):
    """{line: hits} with `coverable` lines, the first `covered` of them hit."""
    return {n: (3 if n <= covered else 0) for n in range(1, coverable + 1)}


def run_main(argv):
    out, err = io.StringIO(), io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
        code = coverage_gate.main(argv)
    return code, out.getvalue(), err.getvalue()


class SelectingChangedFiles(unittest.TestCase):
    def test_keeps_only_cs_files_under_src_sorted_and_unique(self):
        paths = ["src/B/B.cs", "src/A/A.cs", "src/B/B.cs", "tests/A.Tests/ATests.cs",
                 "benchmarks/X/X.cs", "src/A/A.csproj", "src/A/README.md", "srcx/A.cs",
                 "tools/coverage_gate.py", "lib/src/A.cs", "src\\C\\C.cs", ""]
        self.assertEqual(coverage_gate.select_source_files(paths),
                         ["src/A/A.cs", "src/B/B.cs", "src/C/C.cs"])


class ThresholdRule(unittest.TestCase):
    def test_pass_or_fail_at_the_95_percent_boundary(self):
        cases = [  # (covered, coverable, passes, shown)
            (20, 20, True, "100.00%"),
            (19, 20, True, "95.00%"),
            (1900, 2000, True, "95.00%"),
            (1899, 2000, False, "94.95%"),
            (18999, 20000, False, "94.99%"),  # rounded down, never shown as 95.00%
            (189, 200, False, "94.50%"),
            (0, 7, False, "0.00%"),
            (0, 0, True, "n/a (no coverable lines)"),
        ]
        for covered, coverable, passes, shown in cases:
            with self.subTest(covered=covered, coverable=coverable):
                result = coverage_gate.FileCoverage("src/A.cs", covered, coverable)
                self.assertIs(result.passes(95.0), passes)
                self.assertEqual(result.percent_text(), shown)

    def test_threshold_must_be_in_range(self):
        for bad in ("0", "-5", "100.5", "abc"):
            with self.subTest(threshold=bad):
                with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                    coverage_gate.parse_args(["--base", "origin/main", "--threshold", bad])


class MergingReports(unittest.TestCase):
    def setUp(self):
        self.dir = unique_dir(self)

    def coverage_of(self, changed):
        reports = coverage_gate.find_reports(self.dir)
        merged = coverage_gate.merge_line_hits(reports, changed)
        return {r.path: (r.covered, r.coverable) for r in coverage_gate.evaluate(changed, merged)}

    def test_line_hits_merge_across_reports(self):
        # Each test project covers half the file; together they cover all of it.
        write_report(self.dir, [("src/Core/A.cs", {1: 2, 2: 1, 3: 0, 4: 0})])
        write_report(self.dir, [("src/Core/A.cs", {1: 0, 2: 0, 3: 5, 4: 1})])
        self.assertEqual(self.coverage_of(["src/Core/A.cs"]), {"src/Core/A.cs": (4, 4)})

    def test_a_line_missed_by_every_report_stays_uncovered(self):
        write_report(self.dir, [("src/Core/A.cs", {1: 1, 2: 0})])
        write_report(self.dir, [("src/Core/A.cs", {1: 0, 2: 0, 3: 4})])
        self.assertEqual(self.coverage_of(["src/Core/A.cs"]), {"src/Core/A.cs": (2, 3)})

    def test_classes_of_one_file_merge_within_a_report(self):
        # Partial and nested classes are separate <class> elements with the same file name.
        write_report(self.dir, [("src/Core/A.cs", {1: 1, 2: 0}), ("src/Core/A.cs", {3: 1, 4: 1}),
                                ("src/Core/A.cs", {2: 0})])
        self.assertEqual(self.coverage_of(["src/Core/A.cs"]), {"src/Core/A.cs": (3, 4)})

    def test_method_lines_are_not_counted_twice(self):
        write_report(self.dir, [("src/Core/A.cs", hits(3, 5))])
        self.assertEqual(self.coverage_of(["src/Core/A.cs"]), {"src/Core/A.cs": (3, 5)})

    def test_reports_are_found_recursively_and_only_cobertura_files(self):
        nested = Path(self.dir) / "a" / "b"
        nested.mkdir(parents=True)
        (nested / "x.cobertura.xml").write_text(cobertura([("src/A.cs", {1: 1})], ["/r/"]))
        (nested / "coverage.json").write_text("{}")
        (Path(self.dir) / "notes.xml").write_text("<nope/>")
        self.assertEqual(coverage_gate.find_reports(self.dir), [nested / "x.cobertura.xml"])
        self.assertEqual(coverage_gate.find_reports(Path(self.dir) / "missing"), [])


class MatchingReportPaths(unittest.TestCase):
    """Coverlet writes file names relative to one of its <source> roots, or absolute."""

    def matched(self, filename, sources, changed):
        return coverage_gate.match_changed(coverage_gate.candidate_paths(filename, sources), changed)

    def test_report_paths_resolve_to_changed_repo_paths(self):
        changed = ["src/MmoEngine.Regionizer/Regionizer.cs", "src/A/Program.cs"]
        cases = [  # (filename, sources, expected match)
            ("MmoEngine.Regionizer/Regionizer.cs", ["/home/runner/work/x/x/src/"],
             "src/MmoEngine.Regionizer/Regionizer.cs"),
            ("src/MmoEngine.Regionizer/Regionizer.cs", ["/home/runner/work/x/x/"],
             "src/MmoEngine.Regionizer/Regionizer.cs"),
            ("/Users/me/repo/src/MmoEngine.Regionizer/Regionizer.cs", [],
             "src/MmoEngine.Regionizer/Regionizer.cs"),
            ("/_/src/MmoEngine.Regionizer/Regionizer.cs", ["/elsewhere/"],
             "src/MmoEngine.Regionizer/Regionizer.cs"),
            ("MmoEngine.Regionizer\\Regionizer.cs", ["D:\\a\\x\\x\\src\\"],
             "src/MmoEngine.Regionizer/Regionizer.cs"),
            ("A/Program.cs", ["/other/root/", "/repo/src/"], "src/A/Program.cs"),
            ("Regionizer.cs", ["/repo/src/MmoEngine.Regionizer/./"],
             "src/MmoEngine.Regionizer/Regionizer.cs"),
            ("B/Program.cs", ["/repo/src/"], None),  # same name, different directory
            ("xsrc/A/Program.cs", ["/repo/"], None),  # suffix only at a '/' boundary
            ("tests/A.Tests/Program.cs", ["/repo/"], None),
        ]
        for filename, sources, expected in cases:
            with self.subTest(filename=filename, sources=sources):
                self.assertEqual(self.matched(filename, sources, changed), expected)


class GateDecision(unittest.TestCase):
    """main() over synthetic reports, with the changed-file list from a stubbed git diff."""

    def setUp(self):
        self.dir = unique_dir(self)
        self.changed = []
        original = coverage_gate.changed_source_files
        coverage_gate.changed_source_files = lambda base, repo=".": list(self.changed)
        self.addCleanup(setattr, coverage_gate, "changed_source_files", original)

    def gate(self, *extra):
        return run_main(["--base", "origin/main", "--coverage-dir", str(self.dir), *extra])

    def test_all_changed_files_at_or_above_threshold_exit_0(self):
        self.changed = ["src/A/Full.cs", "src/A/Exactly95.cs"]
        write_report(self.dir, [("src/A/Full.cs", hits(12, 12)), ("src/A/Exactly95.cs", hits(19, 20))])
        code, out, _ = self.gate()
        self.assertEqual(code, 0)
        self.assertIn("| `src/A/Exactly95.cs` | 19 | 20 | 95.00% | pass |", out)
        self.assertIn("| `src/A/Full.cs` | 12 | 12 | 100.00% | pass |", out)

    def test_a_changed_file_under_threshold_fails_with_a_table_exit_1(self):
        self.changed = ["src/A/Good.cs", "src/A/Low.cs"]
        write_report(self.dir, [("src/A/Good.cs", hits(10, 10)), ("src/A/Low.cs", hits(18, 20))])
        code, out, err = self.gate()
        self.assertEqual(code, 1)
        self.assertIn("| File | Covered lines | Coverable lines | Line coverage | Result |", out)
        self.assertIn("| `src/A/Low.cs` | 18 | 20 | 90.00% | FAIL |", out)
        self.assertIn("| `src/A/Good.cs` | 10 | 10 | 100.00% | pass |", out)
        self.assertIn("1 of 2 changed file(s) are under 95% line coverage", err)

    def test_files_with_no_coverable_lines_pass(self):
        # An enum-only file is in no report; an interface can appear with no lines.
        self.changed = ["src/A/Enums.cs", "src/A/IThing.cs"]
        write_report(self.dir, [("src/A/IThing.cs", {}), ("src/A/Other.cs", hits(1, 2))])
        code, out, _ = self.gate()
        self.assertEqual(code, 0)
        self.assertIn("| `src/A/Enums.cs` | 0 | 0 | n/a (no coverable lines) | pass |", out)
        self.assertIn("| `src/A/IThing.cs` | 0 | 0 | n/a (no coverable lines) | pass |", out)

    def test_unchanged_files_are_not_gated(self):
        self.changed = ["src/A/Changed.cs"]
        write_report(self.dir, [("src/A/Changed.cs", hits(5, 5)), ("src/A/Untouched.cs", hits(0, 50))])
        code, out, _ = self.gate()
        self.assertEqual(code, 0)
        self.assertNotIn("Untouched.cs", out)

    def test_merged_reports_decide_the_result(self):
        self.changed = ["src/A/Split.cs"]
        write_report(self.dir, [("src/A/Split.cs", {1: 1, 2: 1, 3: 0, 4: 0})])
        write_report(self.dir, [("src/A/Split.cs", {1: 0, 2: 0, 3: 1, 4: 1})])
        code, out, _ = self.gate()
        self.assertEqual(code, 0)
        self.assertIn("| `src/A/Split.cs` | 4 | 4 | 100.00% | pass |", out)

    def test_custom_threshold_is_applied(self):
        self.changed = ["src/A/Mid.cs"]
        write_report(self.dir, [("src/A/Mid.cs", hits(8, 10))])
        self.assertEqual(self.gate("--threshold", "80")[0], 0)
        self.assertEqual(self.gate("--threshold", "81")[0], 1)

    def test_no_changed_source_files_passes_without_reports(self):
        self.changed = []
        code, out, _ = self.gate()
        self.assertEqual(code, 0)
        self.assertIn("no C# files under src/ were added or changed", out)

    def test_changed_files_but_no_reports_is_an_error_exit_2(self):
        self.changed = ["src/A/A.cs"]
        code, _, err = self.gate()
        self.assertEqual(code, 2)
        self.assertIn("no Cobertura reports", err)

    def test_unreadable_reports_are_an_error_exit_2(self):
        self.changed = ["src/A/A.cs"]
        cases = {
            "malformed XML": "<coverage><sources>",
            "not Cobertura": "<report />",
            "bad line number": cobertura([("src/A/A.cs", {1: 1})]).replace('number="1"', 'number="x"'),
        }
        for name, text in cases.items():
            with self.subTest(case=name):
                directory = unique_dir(self)
                (directory / "coverage.cobertura.xml").write_text(text, encoding="utf-8")
                code, _, err = run_main(["--base", "origin/main", "--coverage-dir", str(directory)])
                self.assertEqual(code, 2)
                self.assertIn("coverage gate: error:", err)

    def test_summary_file_gets_the_markdown_table(self):
        self.changed = ["src/A/Low.cs"]
        write_report(self.dir, [("src/A/Low.cs", hits(1, 4))])
        summary = self.dir / "summary.md"
        summary.write_text("earlier step\n", encoding="utf-8")
        code, _, _ = self.gate("--summary", str(summary))
        self.assertEqual(code, 1)
        text = summary.read_text(encoding="utf-8")
        self.assertTrue(text.startswith("earlier step\n### Changed-file line coverage (threshold 95%"))
        self.assertIn("| `src/A/Low.cs` | 1 | 4 | 25.00% | FAIL |", text)


class ChangedFilesFromGit(unittest.TestCase):
    """changed_source_files runs `git diff <base>...HEAD` in a throwaway repository."""

    ENV = {**os.environ, "GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1"}

    def git(self, *args):
        subprocess.run(["git", "-c", "user.name=Test", "-c", "user.email=test@example.invalid",
                        "-c", "commit.gpgsign=false", *args],
                       cwd=self.repo, env=self.ENV, check=True, capture_output=True, text=True)

    def write(self, path, text):
        target = self.repo / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")

    def setUp(self):
        self.repo = unique_dir(self)
        self.git("init", "-q", "-b", "base")
        for path in ("src/A/Keep.cs", "src/A/Edit.cs", "src/A/Gone.cs", "tests/T/T.cs"):
            self.write(path, f"// {path}\nclass X {{ }}\n")
        self.write("src/A/Move.cs", "".join(f"// line {i}\n" for i in range(40)))
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "base")
        self.git("checkout", "-q", "-b", "feature")
        self.write("src/A/Edit.cs", "// edited\n")
        self.write("src/B/New.cs", "// new\n")
        self.write("src/B/notes.md", "# not code\n")
        self.write("tests/T/New.cs", "// a test\n")
        (self.repo / "src/A/Gone.cs").unlink()
        self.git("mv", "src/A/Move.cs", "src/B/Moved.cs")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "feature")
        # A later commit on the base branch is not part of the feature's diff (three-dot).
        self.git("checkout", "-q", "base")
        self.write("src/A/OnBaseOnly.cs", "// base moved on\n")
        self.git("add", "-A")
        self.git("commit", "-q", "-m", "base moves on")
        self.git("checkout", "-q", "feature")

    def test_added_modified_and_renamed_src_cs_files_since_the_merge_base(self):
        self.assertEqual(coverage_gate.changed_source_files("base", self.repo),
                         ["src/A/Edit.cs", "src/B/Moved.cs", "src/B/New.cs"])

    def test_unknown_base_is_an_error(self):
        with self.assertRaises(coverage_gate.GateError) as caught:
            coverage_gate.changed_source_files("no-such-ref", self.repo)
        self.assertIn("no-such-ref", str(caught.exception))

    def test_main_gates_the_files_git_reports(self):
        coverage = unique_dir(self)
        write_report(coverage, [("src/A/Edit.cs", hits(1, 1)), ("src/B/New.cs", hits(1, 1)),
                                ("src/B/Moved.cs", hits(3, 40)), ("src/A/Keep.cs", hits(0, 9))],
                     sources=[str(self.repo) + "/"])
        code, out, _ = run_main(["--base", "base", "--repo", str(self.repo),
                                 "--coverage-dir", str(coverage)])
        self.assertEqual(code, 1)
        self.assertIn("| `src/B/Moved.cs` | 3 | 40 | 7.50% | FAIL |", out)
        self.assertIn("| `src/A/Edit.cs` | 1 | 1 | 100.00% | pass |", out)
        self.assertNotIn("Keep.cs", out)

    def test_main_with_an_unknown_base_exits_2(self):
        code, _, err = run_main(["--base", "no-such-ref", "--repo", str(self.repo),
                                 "--coverage-dir", str(unique_dir(self))])
        self.assertEqual(code, 2)
        self.assertIn("git diff against 'no-such-ref' failed", err)


if __name__ == "__main__":
    unittest.main()
