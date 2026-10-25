#!/usr/bin/env python3
"""Check that the repository's documentation points at things that exist.

For every Markdown file it checks
  * relative links     [text](path/to/file.md#anchor)  - the file must exist (anchors are not checked);
  * repo paths in code `tools/zone_report.py`, `docs/re/assets.md` ... - a path that starts with a top-level folder
    of this repository must exist (a trailing `*` or placeholder such as <zone> is skipped).
Exit status 1 when something is missing, so CI can fail on a broken reference.

Usage:  python tools/check_docs.py            (run from anywhere)
"""
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
TOP = ("bootstrap", "docs", "tools", "src", "installer", ".github")
LINK = re.compile(r"\[[^\]]*\]\(([^)\s]+)\)")
CODE = re.compile(r"`([^`\n]+)`")
SKIP_DIRS = {".git", "node_modules", "__pycache__"}


def markdown_files():
    for base, dirs, files in os.walk(ROOT):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in files:
            if name.lower().endswith(".md"):
                yield os.path.join(base, name)


def check_file(path):
    problems = []
    here = os.path.dirname(path)
    in_fence = False
    with open(path, encoding="utf-8") as f:
        for number, line in enumerate(f, 1):
            if line.lstrip().startswith("```"):
                in_fence = not in_fence
                continue
            if in_fence:
                continue
            for target in LINK.findall(line):
                if re.match(r"^[a-z][a-z0-9+.-]*:", target, re.I) or target.startswith("#"):
                    continue          # http(s):, mailto:, in-page anchor
                rel = target.split("#", 1)[0]
                if rel and not os.path.exists(os.path.normpath(os.path.join(here, rel))):
                    problems.append((number, "broken link: " + target))
            for code in CODE.findall(line):
                token = code.strip().replace("\\", "/")
                if token.split("/", 1)[0] not in TOP or "/" not in token:
                    continue
                token = token.split()[0]
                if any(ch in token for ch in "*<>{}$|=") or token.endswith("/..."):
                    continue
                if not os.path.exists(os.path.join(ROOT, token.rstrip("/"))):
                    problems.append((number, "missing path: " + token))
    return problems


def main():
    bad = 0
    count = 0
    for path in sorted(markdown_files()):
        count += 1
        for number, text in check_file(path):
            print("%s:%d: %s" % (os.path.relpath(path, ROOT).replace(os.sep, "/"), number, text))
            bad += 1
    print("checked %d Markdown files, %d problem(s)" % (count, bad))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
