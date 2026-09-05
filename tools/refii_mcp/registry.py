"""A tool is one function with its schema, declared where it is implemented.

The single table this replaces held every description at the top of the file and every
implementation in one `if` ladder at the bottom, so adding a tool meant editing two places that
could not see each other — and a tool the game already answered (`elements`, `peek`, `ping`)
sat unexposed because the ladder never grew a branch for it.
"""
TOOLS = []
HANDLERS = {}


def tool(name, description, properties=None, required=None):
    """Register one MCP tool. The decorated function takes the argument dict, returns a dict.

    A returned dict may carry `_content`: extra MCP content blocks (an image, say) sent
    alongside the JSON. `ok` decides whether the call is reported as an error.
    """
    def register(function):
        TOOLS.append({
            "name": name,
            "description": description,
            "inputSchema": {
                "type": "object",
                "properties": properties or {},
                **({"required": required} if required else {}),
            },
        })
        HANDLERS[name] = function
        return function

    return register


def call(name, arguments):
    handler = HANDLERS.get(name)
    if handler is None:
        return {"ok": False, "error": f"unknown tool '{name}'"}
    return handler(arguments or {})
