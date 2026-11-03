#!/usr/bin/env python3
"""Changed-file line coverage gate.

The constitution (Engineering Standards, CI/CD and code quality) requires at least 95% coverage for
every relevant file a pull request adds or changes. Until Codecov is set up, CI enforces that rule
with this script (specs/013-regionizer-simulator, T003).

It lists the C# files under src/ that were added or modified since the merge base with the base ref
(`git diff --name-only --no-renames --diff-filter=AM <base>...HEAD`, so a renamed file counts as
added). It then merges the line hits of every Cobertura report under the coverage directory and
prints one table row per file.

Usage:
    python3 tools/coverage_gate.py --base origin/main [--coverage-dir coverage]
                                   [--threshold 95] [--repo .] [--summary FILE]

Exit codes:
    0  every changed file is at or above the threshold. A file with no coverable lines passes.
    1  at least one changed file is below the threshold.
    2  the gate could not run: git failed, or a report is missing or unreadable.

Python 3 standard library only. Tests: tests/workflows/test_coverage_gate.py.
"""

import argparse
import posixpath
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

DEFAULT_THRESHOLD = 95.0
REPORT_GLOB = "*.cobertura.xml"
_WINDOWS_ABSOLUTE = re.compile(r"^[A-Za-z]:/")


class GateError(Exception):
    """The gate could not run. main() prints the message and exits 2."""


@dataclass(frozen=True)
class FileCoverage:
    """Merged line coverage of one changed file."""

    path: str
    covered: int
    coverable: int
    # In no report although its source holds executable code: no test project covers it.
    unreported: bool = False

    def passes(self, threshold):
        if self.unreported:
            return False
        # Integer-exact: covered / coverable >= threshold / 100.
        return self.coverable == 0 or self.covered * 100 >= threshold * self.coverable

    def percent_text(self):
        if self.unreported:
            return "not in any coverage report"
        if self.coverable == 0:
            return "n/a (no coverable lines)"
        # Rounded down, so a failing file never displays as the threshold.
        hundredths = (self.covered * 10000) // self.coverable
        return f"{hundredths // 100}.{hundredths % 100:02d}%"


# --- Changed files -------------------------------------------------------------------------------

def select_source_files(paths):
    """The C# files under src/, with forward slashes, sorted and without duplicates."""
    selected = set()
    for path in paths:
        path = path.strip().replace("\\", "/")
        if path.startswith("src/") and path.endswith(".cs"):
            selected.add(path)
    return sorted(selected)


def changed_source_files(base, repo="."):
    """The C# files under src/ added or modified on HEAD since its merge base with `base`."""
    command = ["git", "-C", str(repo), "diff", "--name-only", "-z", "--no-renames",
               "--diff-filter=AM", f"{base}...HEAD"]
    try:
        result = subprocess.run(command, capture_output=True, text=True, check=False)
    except OSError as error:
        raise GateError(f"could not run git: {error}") from error
    if result.returncode != 0:
        raise GateError(f"git diff against {base!r} failed: {result.stderr.strip()}")
    return select_source_files(name for name in result.stdout.split("\0") if name)


# --- Cobertura reports ---------------------------------------------------------------------------

def find_reports(coverage_dir):
    """Every Cobertura report under `coverage_dir`, recursively, in a stable order."""
    root = Path(coverage_dir)
    if not root.is_dir():
        return []
    return sorted(path for path in root.rglob(REPORT_GLOB) if path.is_file())


def read_report(path):
    """The report's source roots and, per <class>, its file name and {line number: hits}."""
    try:
        root = ET.parse(path).getroot()
    except (ET.ParseError, OSError) as error:
        raise GateError(f"cannot read coverage report {path}: {error}") from error
    if root.tag != "coverage":
        raise GateError(f"{path} is not a Cobertura report (root element <{root.tag}>)")

    sources = [(source.text or "").strip() for source in root.iter("source")]
    classes = []
    for cls in root.iter("class"):
        filename = cls.get("filename")
        if not filename:
            continue
        lines = {}
        # Class-level lines only: coverlet repeats them per method under <methods>.
        for line in cls.findall("./lines/line"):
            try:
                number = int(line.get("number"))
                hits = int(line.get("hits", "0"))
            except (TypeError, ValueError) as error:
                raise GateError(f"{path}: bad <line> in {filename}: {error}") from error
            lines[number] = lines.get(number, 0) + hits
        classes.append((filename, lines))
    return [source for source in sources if source], classes


def _is_absolute(path):
    return path.startswith("/") or bool(_WINDOWS_ABSOLUTE.match(path))


def candidate_paths(filename, sources):
    """Where a report's file name may point: itself if absolute, else joined to each source root."""
    filename = filename.replace("\\", "/")
    if _is_absolute(filename) or not sources:
        return [posixpath.normpath(filename)]
    return [posixpath.normpath(source.replace("\\", "/").rstrip("/") + "/" + filename)
            for source in sources]


def match_changed(candidates, changed):
    """The changed repo path that a report path names, or None.

    Report paths are absolute on the build machine (or mapped, as in /_/src/... for deterministic
    builds), so a changed path matches when it is the whole path or a suffix at a '/' boundary.
    """
    for candidate in candidates:
        for path in changed:
            if candidate == path or candidate.endswith("/" + path):
                return path
    return None


def merge_line_hits(report_paths, changed):
    """
    {changed path: {line number: total hits}}, merged over every report and <class> element. A
    changed file that no report mentions has no key.
    """
    merged = {}
    for report in report_paths:
        sources, classes = read_report(report)
        for filename, lines in classes:
            target = match_changed(candidate_paths(filename, sources), changed)
            if target is None:
                continue
            hits = merged.setdefault(target, {})
            for number, count in lines.items():
                hits[number] = hits.get(number, 0) + count
    return merged


# --- Evaluation and output -----------------------------------------------------------------------

# A method, constructor, accessor or local-function body, or an expression body. Enum-only and
# interface-only files have none, and Coverlet leaves them out of its reports.
_EXECUTABLE = re.compile(r"(\)|\bget|\bset|\binit|\badd|\bremove)\s*\{|=>")
_COMMENT = re.compile(r"//[^\n]*|/\*.*?\*/", re.S)


def has_executable_code(path, repo="."):
    """Whether a C# file holds code a test could cover. An unreadable file counts as holding none."""
    try:
        text = (Path(repo) / path).read_text(encoding="utf-8")
    except OSError:
        return False
    return _EXECUTABLE.search(_COMMENT.sub("", text)) is not None


def evaluate(changed, line_hits, repo="."):
    """
    One FileCoverage per changed file, sorted by path. A file in no report passes when it holds no
    executable code (enums, interfaces) and fails otherwise, since then no test project covers it.
    """
    results = []
    for path in sorted(changed):
        lines = line_hits.get(path)
        if lines is None:
            results.append(FileCoverage(path, 0, 0, unreported=has_executable_code(path, repo)))
            continue
        covered = sum(1 for hits in lines.values() if hits > 0)
        results.append(FileCoverage(path, covered, len(lines)))
    return results


def format_table(results, threshold):
    """A Markdown table of the results, readable in a log and in the job summary."""
    rows = ["| File | Covered lines | Coverable lines | Line coverage | Result |",
            "| --- | ---: | ---: | ---: | --- |"]
    for result in results:
        verdict = "pass" if result.passes(threshold) else "FAIL"
        rows.append(f"| `{result.path}` | {result.covered} | {result.coverable} "
                    f"| {result.percent_text()} | {verdict} |")
    return "\n".join(rows)


def _threshold(text):
    value = float(text)
    if not 0 < value <= 100:
        raise argparse.ArgumentTypeError("must be in (0, 100]")
    return value


def parse_args(argv):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--base", required=True,
                        help="base ref to diff against, for example origin/main")
    parser.add_argument("--coverage-dir", default="coverage",
                        help="directory searched recursively for *.cobertura.xml (default: coverage)")
    parser.add_argument("--threshold", type=_threshold, default=DEFAULT_THRESHOLD,
                        help="minimum line coverage percent per changed file (default: 95)")
    parser.add_argument("--repo", default=".", help="git working tree (default: .)")
    parser.add_argument("--summary", default=None,
                        help="file to append the Markdown result to, such as $GITHUB_STEP_SUMMARY")
    return parser.parse_args(argv)


def _append_summary(path, text):
    if path:
        with open(path, "a", encoding="utf-8") as summary:
            summary.write(text + "\n")


def main(argv=None):
    args = parse_args(argv)
    heading = f"Changed-file line coverage (threshold {args.threshold:g}%, base {args.base})"
    try:
        changed = changed_source_files(args.base, args.repo)
        if not changed:
            message = f"{heading}: no C# files under src/ were added or changed."
            print(message)
            _append_summary(args.summary, f"### {message}")
            return 0
        reports = find_reports(args.coverage_dir)
        if not reports:
            raise GateError(f"no Cobertura reports ({REPORT_GLOB}) under {args.coverage_dir}; "
                            "was the test step run with --collect:\"XPlat Code Coverage\"?")
        line_hits = merge_line_hits(reports, changed)
    except GateError as error:
        print(f"coverage gate: error: {error}", file=sys.stderr)
        return 2

    results = evaluate(changed, line_hits, args.repo)
    table = format_table(results, args.threshold)
    failing = [result for result in results if not result.passes(args.threshold)]
    print(f"{heading}, {len(reports)} report(s):\n\n{table}", flush=True)
    _append_summary(args.summary, f"### {heading}\n\n{table}")
    if failing:
        print(f"coverage gate: {len(failing)} of {len(results)} changed file(s) are under "
              f"{args.threshold:g}% line coverage", file=sys.stderr)
        return 1
    print(f"coverage gate: all {len(results)} changed file(s) pass.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
