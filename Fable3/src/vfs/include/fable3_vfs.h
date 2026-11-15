// refii - Fable 3's VFS, the title library: what the host mount overrides, asked by the guest's
// file-system hooks (Fable3/src/hooks/src/vfs_hooks.cpp) and answered from the host's own
// statements (core/host_asset_reader.h — Godot's hash index, override query and reader, one
// mount for both sides).
//
// A C ABI on purpose: the hooks are compiled into the payload, which may import no C++ symbol
// from a host library (cmake/refii_check_payload_abi.cmake). Listed among the hook target's
// sources, so the player's build compiles the hooks against this same header.

#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/// The mount path's capacity a caller hands refiii_vfs_find_override; the host's index refuses
/// a longer name (core/host_asset_reader.cpp, kMaxAssetNameBytes).
#define REFIII_VFS_PATH_CAPACITY 512u

/// The host's override for the resource the guest names by `hash` (the FNV-1 the root file
/// system keys its table by), with its size in `out_size` and the path, NUL-terminated, in
/// `out_path`: REFIII_VFS_OVERRIDE when the host's hash index names a path for it and states that
/// path an override, REFIII_VFS_MOD_OWNED when the host states a mod composed or contributed it
/// (core/host_asset_reader.h HostAssetOverrideKind); 0 otherwise, and for an empty file (the
/// guest's memory file cannot hold zero bytes).
#define REFIII_VFS_OVERRIDE 1
#define REFIII_VFS_MOD_OWNED 2
int32_t refiii_vfs_find_override(uint32_t hash, uint32_t* out_size, char* out_path,
                                 size_t path_capacity);

/// Reads the override `path` names into `out`. 1 when exactly `size` bytes came back.
int32_t refiii_vfs_read_override(const char* path, uint8_t* out, uint32_t size);

/// The hook's verdict on one override, logged per hit: `served` 1 when the guest now reads the
/// host's bytes, 0 when its own disk file stood (vfs_hooks.cpp states when).
void refiii_vfs_note_override(uint32_t hash, const char* path, uint32_t size, int32_t served);

/// A file the guest's own table lacks, reported present because the host states it.
void refiii_vfs_note_added(uint32_t hash, const char* path);

/// Run totals since load: overrides served, overrides left to the guest's disk file, files
/// reported present. Any pointer may be null.
void refiii_vfs_override_counts(uint64_t* out_served, uint64_t* out_left_to_disk,
                                uint64_t* out_added);

/// The debug channel's probe outcome (Fable3/src/vfs/src/debug_result.cpp): stated by the payload's
/// tick after it ran one, read by the shell's RefiiHostGetDebugCallResult. 0 before any.
void refiii_vfs_note_debug_result(uint32_t addr, uint64_t r3, const char* note, size_t length);
uint32_t refiii_vfs_debug_result(uint64_t* out_seq, uint32_t* out_addr, uint64_t* out_r3, char* out_note,
                                 uint32_t note_capacity);

#ifdef __cplusplus
}
#endif
