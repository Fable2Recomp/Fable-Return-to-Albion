---
name: refii-live-mcp
description: >-
  Use this skill when interacting with, inspecting, or debugging an active running Refii
  game session using the refii-live MCP server. Covers reading/writing CVars, memory peeking,
  guest logging, GPU states, and driving input sequences.
---

# Refii Live Runtime Debugging Guide (`refii-live`)

This skill documents how to interact with an active, running Refii game session using the `refii-live` MCP server tools.

---

## 1. Overview & Setup

The `refii-live` server connects directly to the Refii host bridge / runtime via a local socket protocol (`tools/refii_mcp/`).

When the game is running, `refii-live` exposes eager tools prefixed with `mcp_refii-live_` (or lazy tools accessible via `call_mcp_tool` under `ServerName: "refii-live"`).

---

## 2. Common Debugging Workflows

### 2.1. Verifying Connection (`refii_ping`)
Always verify connectivity to the running process before attempting state inspection:
- Tool: `refii_ping`
- Returns response latency and process readiness status.

### 2.2. Inspecting and Tuning CVars
Refii exposes runtime configuration variables (CVars) controlling graphics, gameplay flags, audio, and debugging overlays:
- **List CVars**: `refii_cvars` (optionally filtered by prefix or category).
- **Update a CVar**: `refii_cvar_set`
  ```json
  { "cvar": "r_draw_hud", "value": "0" }
  ```

### 2.3. Memory Inspection & Peeking (`refii_peek`, `refii_poke`)
When debugging guest game structures or verifying memory patches:
- **`refii_peek`**: Reads memory at an effective address (`ea`) with a specified length.
  ```json
  { "address": "0x82012340", "length": 64 }
  ```
- **`refii_poke`**: Writes raw bytes to a writable memory address.
- **`refii_sym`**: Resolves symbol names to effective addresses or vice versa.

### 2.4. Guest Log & Diagnostics (`refii_guestlog`, `refii_threads`)
- **`refii_guestlog`**: Fetches the recent log buffer emitted by the recompiled guest engine.
- **`refii_threads`**: Lists active guest worker threads, their program counters, and execution states.

### 2.5. Automated Input Injection (`refii_press`, `refii_action`)
To verify menus, frontend transitions, or test regressions without manual gamepad input:
- **`refii_press`**: Injects a button press or axis change (e.g. `A`, `B`, `Start`, `DpadUp`).
- **`refii_wait`**: Pauses for a specified number of frames to let UI transitions finish.
- **`refii_shot`**: Captures a framebuffer screenshot to verify visual state.
