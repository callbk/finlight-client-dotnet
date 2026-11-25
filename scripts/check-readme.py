#!/usr/bin/env python3
"""Checks the README skeleton (R-DOCS-1): exact headings of README.md in order, the version table,
and that every translation has the same heading levels and the same code blocks."""
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SKELETON = [
    "# finlight client for .NET",
    "## Versions",
    "## Requirements",
    "## Installation",
    "## Quick start",
    "## Search articles",
    "## Look up articles, companies and sources",
    "## Absent, null and empty",
    "## Real-time streams",
    "## Webhooks",
    "## Errors and retries",
    "## Configuration",
    "## Logging",
    "## Data notes",
    "## Migrating from 1.x",
    "## Development",
    "## License",
]


def headings(text: str) -> list[str]:
    out, fenced = [], False
    for line in text.splitlines():
        if line.startswith("```"):
            fenced = not fenced
        elif not fenced and re.match(r"^#{1,6} ", line):
            out.append(line.strip())
    return out


def code_blocks(text: str) -> list[str]:
    return re.findall(r"```[a-z]*\n.*?```", text, flags=re.S)


def main() -> int:
    problems = []
    english = (ROOT / "README.md").read_text(encoding="utf-8")
    if headings(english) != SKELETON:
        problems.append(f"README.md headings differ from the skeleton: {headings(english)}")
    if "| SDK version | API version | Status |" not in english:
        problems.append("README.md has no version table")
    if not english.split("## Versions")[0].count("**Breaking release.**"):
        problems.append("README.md does not start with the breaking-release note")
    levels = [h.split(" ")[0] for h in headings(english)]
    for name in ["README.zh-CN.md", "README.ja.md", "README.ko.md"]:
        text = (ROOT / name).read_text(encoding="utf-8")
        if [h.split(" ")[0] for h in headings(text)] != levels:
            problems.append(f"{name}: heading levels differ from README.md")
        if headings(text)[0] != SKELETON[0]:
            problems.append(f"{name}: the title differs")
        if code_blocks(text) != code_blocks(english):
            problems.append(f"{name}: code blocks differ from README.md")
        if "| SDK version | API version | Status |" not in text:
            problems.append(f"{name}: no version table")
    for p in problems:
        print("readme:", p, file=sys.stderr)
    if not problems:
        print("readme: ok")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
