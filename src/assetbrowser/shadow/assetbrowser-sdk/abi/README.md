# mdlconv — `.mdl` → glTF, via the asset browser's converter

Exposes the Fable2AssetBrowser model converter to Godot/C# over a C ABI, the same
way `refii_bnk_extractor` exposes the bank reader. **Nothing here reimplements the
format** — the browser's parser is the working implementation and this only marshals.

Status: **not yet buildable in-tree.** The browser source is not vendored, and one
unresolved symbol remained when this was last linked out-of-tree. What is captured
here is the part that was expensive to work out: the core/UI boundary.

## Why this exists

The browser's entry point is ideal for us:

```cpp
bool mdl_to_glb_full(const std::vector<unsigned char>& mdl_data,
                     const std::string& glb_path,
                     const std::string& mdl_source_path,
                     std::string& err_msg);
```

Bytes in, GLB out — and **Godot imports GLB natively**, so there is no mesh
assembly on our side. Feed it from `BankFileSystem` (the user's own install, read in
place) and write the GLB to `user://`; nothing derived lands in the repo.

## The core/UI boundary

`core-sources.txt` lists the **18 translation units** the converter actually needs.
Found empirically by linking, so treat it as a starting point, not gospel:

- `MDL/` — mdl_converter, ModelParser, MdlFbxExport, MdlTexExport
- `textures/` — TexParser, LhTexCodec, DdsExport, TiffExport
- `animations/` — AnimBank, AnimDataFile, AnimDecoder, AnimPlayer, Locomotion
- `Utilities/` — DebugLog, Files, Utils
- `ISO/IsoMount`, `BNKCore`

**No ImGui, no quest system, no level authoring.** Compiling all non-UI sources
instead pulls in 40 spurious symbols (ImGui widgets, `Quest::Bp::Register*Nodes`,
`Level::Creation::*`) — the converter needs none of them.

Excluded deliberately: `textures/export/TextureExport.cpp` is a UI menu (4 ImGui
references) and is not part of the converter.

## The one real seam

`coreshim.cpp` stubs exactly three things, and they are all the **texture-embedding**
path:

- `decode_tex_to_rgba`
- `tex_export_png` / `tex_export_jpg`
- `g_level_vfs_texture_body_bnks`

This is the right boundary rather than a workaround: refii already decodes `.tex` to
Godot textures in C#, and the GLB carries texture **names**, which is what we bind
on. Under the Godot migration these become library inputs (a callback), not stubs.

Everything else the converter touches is real code that compiles — including
`IsoMount`, which is worth compiling rather than stubbing.

## Next step

The asset browser's ImGui UI is being migrated to Godot. That migration needs a
**core library / UI split** regardless, and the split is roughly the list above with
the texture seam as a callback. Doing it in the browser repo unblocks both the
migration and this converter in one move, and gives a build target that survives
their refactors — better than an object list reconstructed from link failures.

## Verifying whatever it emits

The format is independently decoded from the binary; see the `refii-mdl-format` and
`refii-staticsubmesh-vertex-decl` notes. Use them to CHECK the converter's output
rather than as a source to reimplement from. Load-bearing facts:

- header is 104 bytes (100 for v33/34); geometry is a separate bank entry under the
  **same `.mdl` path**; `.mdl.gmd` is a small `GameMesh` skeleton, NOT geometry
- LOD *i* at `sum(sizes[0..i-1])`; size slots are indexed, not dense
- rigid vertex stride 20: FLOAT16_4 position, 11/11/10 normal, FLOAT16_2 UV,
  2/10/10/10 tangent. Skinned 28, tree 36
- indices are always 16-bit with `0xFFFF` restart
