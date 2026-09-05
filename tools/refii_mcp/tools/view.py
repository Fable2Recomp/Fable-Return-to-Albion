"""Looking at the game."""
import base64
import io
import os
import time

from ..channel import SHOTS, ask
from ..registry import tool

# The inline copy is a reading aid, not the artefact: wide enough that a menu row's text and a
# page's layout are legible, small enough that a screenshot does not cost megabytes of context
# every time one is taken. The full-resolution PNG is always written and always named.
INLINE_WIDTH = 1280
INLINE_QUALITY = 82


def _inline(path):
    """The shot as an MCP image block, downscaled. Returns (blocks, note)."""
    try:
        from PIL import Image
    except ImportError:
        return [], "Pillow is not installed — read the returned path to view the shot"

    try:
        with Image.open(path) as image:
            image = image.convert("RGB")
            if image.width > INLINE_WIDTH:
                height = round(image.height * INLINE_WIDTH / image.width)
                image = image.resize((INLINE_WIDTH, height), Image.LANCZOS)
            buffer = io.BytesIO()
            image.save(buffer, "JPEG", quality=INLINE_QUALITY)
    except OSError as error:
        return [], f"the shot could not be re-encoded for viewing ({error}) — read the path"

    return [{
        "type": "image",
        "data": base64.b64encode(buffer.getvalue()).decode(),
        "mimeType": "image/jpeg",
    }], None


@tool(
    "refii_shot",
    "Write a PNG of the game window and RETURN IT, so the screen can be looked at in the same "
    "call. The full-resolution file is kept and its path returned as well — read that when a "
    "detail is too small to judge in the inline copy.",
    {
        "name": {"type": "string", "description": "Optional label for the file name."},
        "inline": {
            "type": "boolean",
            "description": "Return the image itself as well as the path (default true).",
        },
    },
)
def shot(args):
    os.makedirs(SHOTS, exist_ok=True)
    label = "".join(c if c.isalnum() else "-" for c in str(args.get("name", "shot"))) or "shot"
    path = os.path.join(SHOTS, f"{time.strftime('%H%M%S')}-{label}.png")

    reply = ask({"op": "shot", "path": path}, timeout=15.0)
    if not reply.get("ok") or not reply.get("saved"):
        return reply

    if args.get("inline", True):
        blocks, note = _inline(path)
        if blocks:
            reply["_content"] = blocks
        if note:
            reply["note"] = note

    return reply
