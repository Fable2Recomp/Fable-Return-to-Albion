---
name: refii-modding
description: >-
  Use this skill when creating, packaging, modifying, or debugging mods for Refii.
  Covers the native .f2mod package container, GDB Title Updates (.gdb.tu), Babel text overlays
  (*.babel.csv), load order resolution, and editor hot-reloading.
---

# Refii Modding System Guide

This skill documents the native modding pipeline for Refii, including the high-performance `.f2mod` package container, multi-mod load orders, sparse Game Database (GDB) Title Updates, and Babel localized string overrides.

---

## 1. Mod Directory & Container Architecture

### 1.1. Native `.f2mod` Packages
All Refii mods are packaged as native `.f2mod` containers residing directly in:
```
assets/mods/<ModName>.f2mod
```
> [!IMPORTANT]
> **Zero loose folders in `assets/mods/`**: Mod directories should never be loose folders in production or development. The game and editor read directly from `.f2mod` packages without unpacking.

### 1.2. The `.f2mod` Format
- **Magic**: `0x444D3246` (`F2MD` in little-endian).
- **Header**: 32 bytes (`Magic`, `Version`, `Flags`, `MetaOffset`, `MetaLength`, `IndexOffset`).
- **Metadata**: Embedded TOML describing mod `title`, `author`, `version`, `description`.
- **Payloads**: 16-byte aligned binary files.
- **Index Table**: Located at the end of the file; stores relative paths, offsets, uncompressed sizes, and UTC timestamps.
- **Append-Only Incremental Sync**: Handled by `F2ModPackage.IncrementalSync(...)` in `< 5ms`. Modifying or deleting an entry appends new payload bytes and rewrites the trailing index table without rewriting the entire archive.

---

## 2. Game Database (GDB) Title Updates (`.gdb.tu`)

> [!TIP]
> **Preferred Asset Replacement Mechanism**: Direct file overlays (like `.tex.png` or `.mdl.glb`) are deprecated. For models, textures, clothing, weapons, and entities, prefer modifying `.gdb.tu` records and properties to point to new assets or change parameters.

### 2.1. Sparse Title Updates
A `.gdb.tu` file is a sparse Title Update database targeting a game table (e.g. `weapons/sword.gdb.tu` targeting `weapons/sword.gdb`).
- It contains only the **records and properties that have changed**.
- Does not require distributing entire multi-megabyte vanilla `.gdb` databases.

### 2.2. Composite GDB Merging & Load Order
- Handled at mount time by `Refii.Gdb.GdbPatchCompositor.Apply`.
- **Multi-Mod Merging**: Merges occur at the individual record and property level.
- **Precedence**: Higher-priority mods in `loadorder.toml` override conflicting properties from lower-priority mods, while distinct property edits across different mods to the same record merge harmoniously.
- Conflicts are detected and reported in `ModTally` and the Launcher Modding panel.

---

## 3. Localized Text Overlays (Babel)

Localised strings are patched into the game's binary text bank (`language/<culture>/text/book.babel`) using CSV overlays:
```
language/en-uk/text/book.babel.csv
```

### 3.1. CSV Format
Lines contain `key,value` pairs (or `key,kind,value`):
```csv
# Refii Babel Text Overlays
0x1000A001,Blade of the Hero
0x1000A002,A legendary sword pulsing with ancient power.
0x2000B001,Moonstone Ring
```
- **Keys**: Either hex string tags (`0x1000A001`) or text tags (`TEXT_SWORD_NAME`).
- **New Keys**: Can be introduced by mods.
- **Compilation**: `ModRowOverlays.Apply` parses the CSV rows directly from `.f2mod` packages, merges them with the base `book.babel`, installs the composite file in `BnkFileSystem`, and invalidates `Engine_BnkTextBankLoader`.

---

## 4. Load Order Configuration (`loadorder.toml`)

Managed in `assets/mods/loadorder.toml`:
```toml
# First entry has highest priority and wins conflicts
enabled = ["OverhaulMod", "TexturePack"]

[pins]
# Explicit file pinning overrides order
"weapons/sword.gdb" = "OverhaulMod"
```

- **Reading & Writing**: Managed via `ModLoadOrder.Read(modsRoot)` and `ModLoadOrder.Write(modsRoot, out _)`.
- **VFS Mount Order**: The mount indexes overlays in reverse (`ModIndex.ResolveOverlays`), so the highest-priority mod is inserted last and shadows earlier files.

---

## 5. Editor Live Tooling & Hot-Reloading

In the Godot Editor (`addons/bnk_mount/`):
- Selecting an item and editing properties updates the active mod via `EditorModValueService.SaveItemOverrides(...)`.
- The service updates `.gdb.tu` and `book.babel.csv` inside the `.f2mod` via `F2ModPackage.IncrementalSync`.
- Calls `BnkMountPlugin.Instance?.ReloadModOverlays()`, instantly refreshing:
  - `Engine_GameGdb`
  - `Engine_BnkTextBankLoader`
  - `Engine_WeaponIndex`, `Engine_ItemIndex`, `Engine_ClothingIndex`
  - Scene tree previews in the viewport
