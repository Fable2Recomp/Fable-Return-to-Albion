# Android ARM64 context-switch primitive

These two assembly files are from the official Boost.Context `boost-1.89.0` tag:

- [make_arm64_aapcs_elf_gas.S](https://raw.githubusercontent.com/boostorg/context/boost-1.89.0/src/asm/make_arm64_aapcs_elf_gas.S)
- [jump_arm64_aapcs_elf_gas.S](https://raw.githubusercontent.com/boostorg/context/boost-1.89.0/src/asm/jump_arm64_aapcs_elf_gas.S)

The only changes are symbol renames `make_fcontext` → `refii_make_fcontext` and
`jump_fcontext` → `refii_jump_fcontext`, including their `.global`, `.hidden`,
`.type`, and `.size` declarations. The original copyright/license notices are
retained. See LICENSE_1_0.txt for the Boost Software License 1.0.

SHA-256 before symbol renaming:

```
make_arm64_aapcs_elf_gas.S 6c4a66435182615097418217af504604ac28f233c4c5289fc2751f05f8aaa78e
jump_arm64_aapcs_elf_gas.S c7d0e3687cc8d678097befebc0a5072f3db0e87e1f1894b9287d0dbf0c9f248f
```

The assembly preserves SP, x19–x30, and the low 64 bits of v8–v15, matching the
[Arm AAPCS64 base procedure call standard](https://github.com/ARM-software/abi-aa/blob/2025Q1/aapcs64/aapcs64.rst).
The C++ Fiber wrapper additionally preserves FPCR and FPSR across switches.
It does not alter the Android platform register x18 or thread TLS.

The Android ARM64 runtime builds both files (CMake ASM, Android ARM64 only) behind the
existing `rex::thread::Fiber` API, which stays the public API; no Boost headers or
libraries are needed.

Limits: ordinary ARM64/NEON code only, not SVE/SME or ShadowCallStack. A guest fiber
stays on the OS thread it started on, because ReXGlue keeps guest state thread-local.
Exceptions may unwind within one fiber but not across a context switch, and a fiber's
entry must yield to another fiber and never return.
