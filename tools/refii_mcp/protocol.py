"""MCP over stdio: one JSON-RPC message per line."""
import json
import sys

from . import registry

VERSION = "0.2.0"


def send(message):
    sys.stdout.write(json.dumps(message) + "\n")
    sys.stdout.flush()


def content_for(result):
    """The content blocks one tool call answers with: its JSON, plus anything it attached."""
    extra = result.pop("_content", None) if isinstance(result, dict) else None
    blocks = [{"type": "text", "text": json.dumps(result, indent=2)}]
    if extra:
        blocks.extend(extra)
    return blocks


def serve(stream=sys.stdin):
    for line in stream:
        line = line.strip()
        if not line:
            continue
        try:
            request = json.loads(line)
        except ValueError:
            continue

        method = request.get("method")
        request_id = request.get("id")

        if method == "initialize":
            send({
                "jsonrpc": "2.0", "id": request_id,
                "result": {
                    "protocolVersion": "2024-11-05",
                    "capabilities": {"tools": {}},
                    "serverInfo": {"name": "refii-live", "version": VERSION},
                },
            })
        elif method == "tools/list":
            send({"jsonrpc": "2.0", "id": request_id, "result": {"tools": registry.TOOLS}})
        elif method == "tools/call":
            params = request.get("params", {})
            result = registry.call(params.get("name", ""), params.get("arguments", {}))
            send({
                "jsonrpc": "2.0", "id": request_id,
                "result": {
                    "content": content_for(result),
                    "isError": not result.get("ok", False),
                },
            })
        elif request_id is not None:
            send({
                "jsonrpc": "2.0", "id": request_id,
                "error": {"code": -32601, "message": f"unknown method {method}"},
            })
