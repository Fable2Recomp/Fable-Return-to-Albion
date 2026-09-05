"""An MCP server over the RUNNING game's debug channel.

The game opens a loopback JSON socket when `REFII_DEBUG_PORT` is set (see
`Godot/src/game/GameDebugServer.cs`); this exposes it as MCP tools, so a screen can be looked
at and questioned while it is up instead of being inferred from a log written before the run.

Layout — one concern per module, the same rule the engine follows:

  channel.py   the handshake, the socket, and who answered
  registry.py  the `@tool` decorator: a tool is one function with its schema
  protocol.py  MCP over stdio, one JSON-RPC message per line
  tools/       the tools themselves, grouped by what they ask about

NO THIRD-PARTY DEPENDENCIES. `.mcp.json` starts this with a bare `python3`, on whatever
interpreter the machine has; a `pip install` between a question and its answer is exactly the
delay this server exists to remove.
"""
