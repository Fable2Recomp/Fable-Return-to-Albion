// refii — the payload/host C ABI.
//
// THE PAYLOAD MUST IMPORT NO C++ SYMBOLS. librefii_generated.so is compiled on the
// player's machine by whatever compiler they have; librefii_vfs.so is compiled by us. A
// C++ symbol crossing that line binds both to one C++ ABI, and the two disagree in ways
// no flag fixes: Itanium vs MSVC mangling, libstdc++ vs libc++ std:: layout.
//
// CONTRACT
//   - C linkage, explicit-width scalars only. No bool, no enum, no class type, no
//     std:: type in any signature.
//   - NO OWNERSHIP CROSSES. Caller allocates, caller frees, and a returned string is
//     written into a caller buffer. A MinGW payload and an MSVC host hold separate CRT
//     heaps, so a pointer allocated on one side and released on the other corrupts.
//   - Call sites never name these. Each group is fronted by inline C++ wrappers in the
//     header that already declared it, so the ~729 existing call sites are unchanged.
//
// The wrappers are inline and the entries are cross-module calls either way, so this
// costs nothing: the payload already reached these through the PLT and still does.

#pragma once

// stddef.h too: size_t is not in stdint.h, and this header is included from C++ TUs where
// the only size_t in scope may be std::size_t.
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// Stage 2 voice capture. Wrappers: refii::vfs::NoteVoice* in core/audio_state.h.
void refii_bridge_note_voice_created(uint32_t handle, uint32_t sample_rate,
                                     uint32_t channels);
void refii_bridge_note_voice_destroyed(uint32_t handle);
void refii_bridge_note_voice_started(uint32_t handle);
void refii_bridge_note_voice_stopped(uint32_t handle, int32_t immediate);
void refii_bridge_note_voice_volume(uint32_t handle, float linear_volume);
void refii_bridge_note_voice_pitch_octaves(uint32_t handle, float octaves);
void refii_bridge_note_voice_channel_volumes(uint32_t handle, const float* gains,
                                             uint32_t count);
void refii_bridge_note_voice_pcm(uint32_t handle, const void* pcm, uint32_t byte_count);

// Bink movie state. Wrappers: refii::vfs::NoteBink* in vfs_hooks_private.h.
void refii_bridge_note_bink_movie_open(uint32_t player, uint8_t* base_for_open);
void refii_bridge_note_bink_video_requested(uint8_t* base, uint32_t data_guest);

// Loose-asset and VFS gates. Wrappers: refii::vfs::* in loose_asset_hooks.h.
//
// int32_t rather than bool: a one-byte C++ bool and a C _Bool agree on the ABIs we build
// for, but this header is the one place that must not depend on them continuing to.
int32_t refii_bridge_vfs_loose_asset_override_tracing_enabled(void);
int32_t refii_bridge_vfs_tracing_enabled(void);
int32_t refii_bridge_vfs_verbose_io_tracing_enabled(void);
int32_t refii_bridge_vfs_loose_gameface_binary_overrides_enabled(void);
int32_t refii_bridge_vfs_loose_bank_entry_overrides_enabled(void);
int32_t refii_bridge_vfs_native_bank_parse_probe_enabled(void);
int32_t refii_bridge_vfs_force_retail_update_mode_enabled(void);
int32_t refii_bridge_vfs_frontend_synthetic_handoff_enabled(void);
int32_t refii_bridge_vfs_auto_press_start_enabled(void);
int32_t refii_bridge_vfs_skip_startup_videos_enabled(void);
int32_t refii_bridge_vfs_refii_loose_asset_backend_enabled(void);
int32_t refii_bridge_vfs_startup_video_minimum_playback_ms(void);

// STRINGS ARE WRITTEN INTO A CALLER BUFFER, never returned.
//
// Every one of these returns the length the value NEEDS, whether or not it fitted, and
// writes at most `capacity` bytes. A caller that gets back more than it offered retries
// with a bigger buffer — see the wrappers. Returning a std::string instead would allocate
// on one side of the boundary and free on the other, which is the one thing this ABI
// cannot do.
size_t refii_bridge_vfs_loose_asset_root(char* out, size_t capacity);
size_t refii_bridge_vfs_normalize_loose_asset_key(const char* key, size_t length,
                                                  char* out, size_t capacity);

// Search roots are enumerated rather than returned as a container, for the same reason.
uint32_t refii_bridge_vfs_loose_asset_search_root_count(void);
size_t refii_bridge_vfs_loose_asset_search_root(uint32_t index, char* out, size_t capacity);

int32_t refii_bridge_vfs_should_defer_loose_bank_container(const char* path, size_t length);

// LooseAssetResolution flattened into its three strings. Each out_*_length receives the
// needed length; pass capacity 0 to measure first.
int32_t refii_bridge_vfs_resolve_loose_asset_override(
    const char* path, size_t length,
    char* host_path, size_t host_path_capacity, size_t* out_host_path_length,
    char* normalized_key, size_t normalized_key_capacity, size_t* out_normalized_key_length,
    char* provider_relative, size_t provider_relative_capacity,
    size_t* out_provider_relative_length);

// RefiiReplacementPathResolution: the same three strings, plus root_name and two flags.
int32_t refii_bridge_vfs_resolve_refii_replacement_path(
    const char* path, size_t length,
    char* host_path, size_t host_path_capacity, size_t* out_host_path_length,
    char* normalized_key, size_t normalized_key_capacity, size_t* out_normalized_key_length,
    char* provider_relative, size_t provider_relative_capacity,
    size_t* out_provider_relative_length,
    char* root_name, size_t root_name_capacity, size_t* out_root_name_length,
    int32_t* out_exists, int32_t* out_is_directory);

#ifdef __cplusplus
}  // extern "C"
#endif
