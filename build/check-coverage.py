"""Coverage gate: fails when a merged Cobertura report falls below the minimum line or branch rate.

    python3 build/check-coverage.py <cobertura.xml> --line 80 --branch 65

CI merges the per-project reports with ReportGenerator first (docs/TESTING.md, "Coverage gates").
The minimums sit a little below the measured figures, so coverage can only drift down by a point or
two before the build fails; raise them as coverage improves.
"""

import argparse
import sys
import xml.etree.ElementTree as ElementTree


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("report")
    parser.add_argument("--line", type=float, required=True, help="minimum line coverage, percent")
    parser.add_argument("--branch", type=float, required=True, help="minimum branch coverage, percent")
    args = parser.parse_args()

    root = ElementTree.parse(args.report).getroot()
    line = float(root.get("line-rate", "0")) * 100
    branch = float(root.get("branch-rate", "0")) * 100

    failures = []
    for name, actual, minimum in (("line", line, args.line), ("branch", branch, args.branch)):
        status = "ok" if actual >= minimum else "BELOW MINIMUM"
        print(f"{name:>6} coverage {actual:6.2f} %  (minimum {minimum:.2f} %)  {status}")
        if actual < minimum:
            failures.append(name)

    if failures:
        print(f"Coverage gate failed: {', '.join(failures)} coverage below the minimum.", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
