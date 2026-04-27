# Shader Third-Party Notes

## XenosRecomp

- Source: https://github.com/hedge-dev/XenosRecomp
- Vendoring mode: snapshot copy (not a git submodule)
- Snapshot commit: `990d03b28a27b50277ee5d8d942e1c5f873869d1`
- Snapshot path: `src/Shader/thirdparty/XenosRecomp`
- Included bundled dependencies from upstream submodules:
  - `thirdparty/dxc-bin`
  - `thirdparty/fmt`
  - `thirdparty/smol-v`
  - `thirdparty/xxHash`
  - `thirdparty/zstd`

All project-specific behavior lives in refii wrapper code under `src/Shader/src`.
