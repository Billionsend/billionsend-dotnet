"""Fail when the tests cover too little of the SDK.

Reads the Cobertura report that `dotnet test --collect:"XPlat Code Coverage"` writes
(see tests/coverage.runsettings) and holds the SDK to two rules:

* the hand-written runtime code (everything under src/BillionSend except Generated/) is
  covered to at least 95% of its lines and 95% of its branches;
* every method of the generated code (src/BillionSend/Generated/) is called by at least one test.

Usage::

    python3 scripts/check_coverage.py "TestResults/*/coverage.cobertura.xml"

The per-file numbers are printed so that a failure says where to look.
"""

from __future__ import annotations

import glob
import sys
import xml.etree.ElementTree as ET

THRESHOLD = 95.0


def load(paths: list[str]):
    """Lines and branches by (file, line), the best of every class that reports them."""
    lines: dict[tuple[str, int], tuple[int, int, int]] = {}
    generated_methods: list[tuple[str, bool]] = []
    for path in paths:
        root = ET.parse(path).getroot()
        for cls in root.iter("class"):
            filename = cls.get("filename", "").replace("\\", "/")
            for line in cls.find("lines").findall("line"):  # type: ignore[union-attr]
                number = int(line.get("number", "0"))
                hits = int(line.get("hits", "0"))
                covered = total = 0
                if line.get("branch") == "True":
                    condition = line.get("condition-coverage", "0% (0/0)")
                    covered, total = (int(n) for n in condition.split("(")[1].rstrip(")").split("/"))
                key = (filename, number)
                old = lines.get(key)
                lines[key] = (hits, covered, total) if old is None else (max(old[0], hits), max(old[1], covered), max(old[2], total))
            if "/Generated/" in f"/{filename}":
                for method in cls.iter("method"):
                    called = any(int(l.get("hits", "0")) > 0 for l in method.iter("line"))
                    generated_methods.append((f"{cls.get('name')}.{method.get('name')}", called))
    return lines, generated_methods


def main() -> int:
    paths = sorted(p for pattern in sys.argv[1:] for p in glob.glob(pattern))
    if not paths:
        print("no coverage report found", file=sys.stderr)
        return 2
    lines, generated_methods = load(paths)

    per_file: dict[str, list[int]] = {}
    for (filename, _), (hits, covered, total) in lines.items():
        if "/Generated/" in f"/{filename}":
            continue
        entry = per_file.setdefault(filename, [0, 0, 0, 0])
        entry[0] += 1
        entry[1] += 1 if hits > 0 else 0
        entry[2] += total
        entry[3] += covered

    failed = False
    totals = [0, 0, 0, 0]
    for filename, (n, hit, branches, covered) in sorted(per_file.items()):
        print(f"{filename:42s} lines {hit}/{n} = {100 * hit / max(n, 1):5.1f}%   branches {covered}/{branches} = {100 * covered / max(branches, 1):5.1f}%")
        for i, value in enumerate((n, hit, branches, covered)):
            totals[i] += value
    line_rate = 100 * totals[1] / max(totals[0], 1)
    branch_rate = 100 * totals[3] / max(totals[2], 1)
    print(f"{'hand-written runtime code':42s} lines {totals[1]}/{totals[0]} = {line_rate:5.1f}%   branches {totals[3]}/{totals[2]} = {branch_rate:5.1f}%")
    if line_rate < THRESHOLD:
        print(f"FAIL: line coverage {line_rate:.1f}% is below {THRESHOLD}%", file=sys.stderr)
        failed = True
    if branch_rate < THRESHOLD:
        print(f"FAIL: branch coverage {branch_rate:.1f}% is below {THRESHOLD}%", file=sys.stderr)
        failed = True

    never = sorted(name for name, called in generated_methods if not called)
    print(f"generated methods: {len(generated_methods) - len(never)}/{len(generated_methods)} called")
    if not generated_methods:
        print("FAIL: no generated method in the report (is the Generated/ file instrumented?)", file=sys.stderr)
        failed = True
    for name in never:
        print(f"FAIL: generated method never called: {name}", file=sys.stderr)
        failed = True
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
