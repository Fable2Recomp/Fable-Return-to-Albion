#!/usr/bin/env python3
"""Entry point: `python3 tools/refii_mcp/__main__.py`, or `python3 -m refii_mcp`.

The path bootstrap is so the first form works. `.mcp.json` names a script, not a module, and
the server must start the same way whatever directory the client happens to run it from.
"""
import os
import sys

if __package__ in (None, ""):
    sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    __package__ = "refii_mcp"

from refii_mcp import protocol, tools   # noqa: E402,F401  (importing registers the tools)


def main():
    protocol.serve()


if __name__ == "__main__":
    main()
