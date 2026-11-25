#!/usr/bin/env python3
"""Builds README.zh-CN.md, README.ja.md and README.ko.md from docs/readme/*.template.md.

The code blocks and the migration table come from README.md, so every translation has exactly the
same code as the English README. Run after editing README.md or a template."""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


def main() -> None:
    english = (ROOT / "README.md").read_text(encoding="utf-8")
    blocks = re.findall(r"```[a-z]*\n.*?```", english, flags=re.S)
    migration = english.split("## Migrating from 1.x", 1)[1]
    table = "\n".join(line for line in migration.split("\n\n")[2].splitlines() if line.startswith("|"))
    for lang in ["zh-CN", "ja", "ko"]:
        text = (ROOT / "docs" / "readme" / f"README.{lang}.template.md").read_text(encoding="utf-8")
        for i, block in enumerate(blocks, start=1):
            text = text.replace("{{CODE%d}}" % i, block)
        text = text.replace("{{MIGRATION_TABLE}}", table)
        if "{{" in text:
            raise SystemExit(f"README.{lang}: unfilled placeholder")
        (ROOT / f"README.{lang}.md").write_text(text, encoding="utf-8")
        print(f"README.{lang}.md written")


if __name__ == "__main__":
    main()
