---
name: ida-pro-mcp
description: >-
  Use this skill when reverse engineering the Xbox 360 binary (default.xex / refii IDB)
  using the IDA Pro MCP tools. Covers decompiling functions to Hex-Rays pseudocode, disassembling,
  inspecting cross-references (xrefs), struct layouts, type signatures, resolving symbols,
  and labeling, commenting, and organizing newly identified functions into IDA directory folders.
---

# IDA Pro MCP Reverse Engineering Guide

This skill guides agents through using the `ida` MCP server to inspect, decompile, and analyze the *Fable 2* Xbox 360 executable (`default.xex`) and database.

---

## 1. Tool Availability & Invocation

Tools on the `ida` MCP server are lazily loaded. Always call them using `call_mcp_tool`:

```json
{
  "ServerName": "ida",
  "ToolName": "<tool_name>",
  "Arguments": { ... }
}
```

Available tools on `ida`:
- `list_databases`: Lists open or cached IDB/I64 instances.
- `open_database`: Opens an IDB/I64 file for inspection.
- `reference`: Looks up available Python API functions and signatures on `ida-domain` and IDAPython. **Always run this before writing execution scripts.**
- `execute_python`: Runs Python code in the active IDA database context (`db` and `ida_domain` are globally injected).
- `save_database`: Persists any added symbols, types, or comments.
- `close_database`: Closes the active database lease.

---

## 2. API Discovery Workflow (`reference`)

Before writing Python code for `execute_python`, query `reference` to inspect method signatures. Do not guess the API shape.

Example queries:
- `reference(query="decompile")`: Find functions for Hex-Rays decompilation.
- `reference(query="xref")`: Find methods for cross-references to/from addresses.
- `reference(query="struct")`: Find methods for struct layout, member offsets, and type inspection.
- `reference(query="function")`: Find methods for locating functions by name or address.

---

## 3. Core Reverse Engineering Workflows

### 3.1. Database Initialization
1. List available databases:
   ```json
   { "ServerName": "ida", "ToolName": "list_databases", "Arguments": {} }
   ```
2. If the desired database is not loaded, open it with `open_database`.

---

### 3.2. Decompiling Functions to Hex-Rays Pseudocode
To decompile a function at a known address (e.g. `0x8245ABCD`) or by symbol name:

```python
# Trailing expression is returned automatically
func = db.get_function("CombatEngine_ApplyDamage")
if not func:
    func = db.get_function(0x8245ABCD)

if func:
    code = func.decompile()
    print(str(code))
else:
    print("Function not found.")
```

---

### 3.3. Finding Cross-References (Xrefs)
Cross-references are essential for determining where game databases, sound cues, or virtual asset paths are loaded:

```python
target_addr = 0x82D01234
xrefs = db.get_xrefs_to(target_addr)

for ref in xrefs:
    caller = db.get_function(ref.from_address)
    caller_name = caller.name if caller else "unknown"
    print(f"Ref from 0x{ref.from_address:08X} ({caller_name}) type={ref.type}")
```

---

### 3.4. Inspecting Structs & Memory Layouts
When translating Xbox 360 data structures to C# or C++:

```python
struct_info = db.get_struct("CItemData")
if struct_info:
    print(f"Struct: {struct_info.name} (Size: {struct_info.size} bytes)")
    for member in struct_info.members:
        print(f"  +0x{member.offset:04X} {member.name} : {member.type} ({member.size} bytes)")
```

---

### 3.5. Labeling, Commenting & Organizing Newly Identified Functions

> [!IMPORTANT]
> **Mandatory Reverse Engineering Hygiene**: Whenever you identify the purpose of an unnamed function (`sub_XXXXXXXX`) or discover new details about an incompletely documented function, you **MUST**:
> 1. **Rename** it to an authoritative, descriptive symbol name.
> 2. **Add repeatable & regular comments** summarizing evidence, parameter roles, and calling subsystems (prefixed with `REFII-DERIVED:`).
> 3. **Move** the function into its proper directory folder in the Functions tree (e.g., `/Game/GameFlow`, `/Game/Render`, `/Havok`, `/Kaim_AI`, `/Lionhead`, `/Formats/GDB`, `/Lua`).
> 4. **Save the database** via the `save_database` tool.

#### Implementation Pattern:
```python
import ida_name, ida_funcs, ida_dirtree

ea = 0x82354D78
new_name = "Havok_LoadScenarioRigidBodies"
target_folder = "/Havok"
comment = "REFII-DERIVED: Loads .havok_scenario root level container and mounts StaticWorldRigidBody, CameraWorldRigidBody, and StairsWorldRigidBody."

f = ida_funcs.get_func(ea)
if f:
    # 1. Rename
    ida_name.set_name(ea, new_name, ida_name.SN_CHECK)
    cur_name = ida_name.get_name(ea)

    # 2. Add repeatable & non-repeatable comments
    ida_funcs.set_func_cmt(f, comment, False)
    ida_funcs.set_func_cmt(f, comment, True)

    # 3. Move in dirtree (Functions window folder)
    dt = ida_dirtree.get_std_dirtree(ida_dirtree.DIRTREE_FUNCS)
    
    # Locate current position
    def locate(name, path=""):
        it = ida_dirtree.dirtree_iterator_t()
        ok = dt.findfirst(it, (path + "/*") if path else "*")
        while ok:
            de = dt.resolve_cursor(it.cursor)
            sub = f"{path}/{dt.get_entry_name(de)}" if path else f"/{dt.get_entry_name(de)}"
            if de.isdir:
                found = locate(name, sub)
                if found: return found
            elif dt.get_entry_name(de) == name:
                return sub
            ok = dt.findnext(it)
        return None

    src = locate(cur_name)
    dst = f"{target_folder}/{cur_name}"
    if src and src != dst:
        dt.rename(src, dst)
```

After updating functions, always invoke:
```json
{
  "ServerName": "ida",
  "ToolName": "save_database",
  "Arguments": {}
}
```

---

## 4. Architectural Considerations for Refii

1. **Big-Endian Architecture**:
   - The Xbox 360 is a big-endian PowerPC system.
   - 16-bit, 32-bit, and 64-bit scalars in raw memory dumps or `.bnk` binary entries are big-endian.
   - When inspecting memory offsets or translating algorithms into C#, use `BinaryPrimitives.ReadUInt32BigEndian` / `WriteUInt32BigEndian`.
2. **Recompiled Correspondence**:
   - Guest addresses (e.g. `0x82123456`) correspond to recompiled functions in `refii_recomp.*.cpp`.
   - Use symbols resolved in IDA to find the matching recompiled block in C++ or the appropriate host service in `Godot/src/Engine/`.
3. **Struct Packing & Alignment**:
   - Xbox 360 PowerPC compilers default to 4-byte or 8-byte alignment for structures.
   - Verify padding bytes when constructing mock buffers or interop carrier structs.
