# Extractor Third-Party Notes

## ZLIB

The extractor module requires zlib and will use a bundled copy if available at:

- `src/extractor/thirdparty/zlib`

Vendored mode is preferred so builds do not depend on system package discovery.
The subtree should contain a standard zlib source tree with a top-level `CMakeLists.txt`.

To add the dependency as a submodule (recommended):

```bash
cd /path/to/refii

git submodule add https://github.com/madler/zlib.git src/extractor/thirdparty/zlib
```

If you prefer not to use a vendored copy, keep that folder absent and ensure a
system zlib is discoverable by `find_package(ZLIB)`.
