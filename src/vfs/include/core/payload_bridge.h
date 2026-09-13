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
void refii_bridge_note_bink_movie_closed(uint32_t player);
void refii_bridge_note_bink_video_requested(uint8_t* base, uint32_t data_guest);

// Guest tick pump. Wrappers: refii::vfs::GuestTickPump* in vfs_hooks_private.h.
//
// The host POSTS and the payload DRAINS, so the queue and the counters live in refii_vfs
// (src/core/guest_tick_pump_state.cpp) and the payload reaches them here. Observe returns the
// tick count BEFORE the increment, so the pump's first tick is 0.
/// The front end's subtitles setting as the guest last stated it: 1 on, 0 off, -1 unknown.
void refii_bridge_front_end_note_subtitles(int32_t state);

// Menu spawn capture: a row's identity goes into a Lua table the host cannot read, so it is
// copied from `sub_826CE8D0`'s registers. `begin` runs on `DeleteAllSpawnedMenuItems`, rows
// follow in spawn order; both on the guest tick thread, host reads under the action-queue lock.
void refii_bridge_menu_spawn_begin(void);
void refii_bridge_menu_spawn_row(const char* name, const char* parent, uint32_t id,
                                 int32_t event_type, int32_t rating);

// Keyed form. The capture is ONE list, so without a key a template sourced `capture:Root`
// takes whatever was spawned last, whoever spawned it — the front end's rows once appeared on
// the real-estate screen. `screen_id` is `manager+4` off the in-game GUI, read at the
// boundary; 0xFFFFFFFF means "no in-game screen states this population" (the front end).
void refii_bridge_menu_spawn_begin_for(uint32_t screen_id);

// Labelled form: String2 (display text) decoded from UTF-16BE at capture.
void refii_bridge_menu_spawn_row_labelled(const char* name, const char* parent, uint32_t id,
                                          int32_t event_type, int32_t rating,
                                          const char* label);

uint64_t refii_bridge_guest_tick_pump_observe_tick(void);
uint64_t refii_bridge_guest_tick_pump_drained(void);
void refii_bridge_guest_tick_pump_note_ran(void);
void refii_bridge_guest_tick_pump_note_result(uint32_t addr, uint64_t r3,
                                              const char* note, size_t length);

// The drain protocol, and ONLY from the guest tick thread. Begin takes the whole queue aside
// under the lock and answers how many it took — swapped rather than popped one at a time so
// that a post made WHILE the drain runs lands in the next tick instead of extending this one
// indefinitely, and so no host mutex is held across guest code. Read each by index with the
// usual measure-then-fill convention, defer the ones that could not run yet, and End puts the
// deferred ones back at the FRONT so a later post cannot overtake them.
// Menu capture settle bracket: `spawn_begin` marks the rows partial, `_settle` clears it.
void refii_bridge_menu_settle(void);
int32_t refii_bridge_menu_populating(void);

uint32_t refii_bridge_guest_tick_pump_begin_drain(void);
size_t refii_bridge_guest_tick_pump_taken_action(uint32_t index, char* out, size_t capacity);
void refii_bridge_guest_tick_pump_defer_action(const char* name, size_t length);
void refii_bridge_guest_tick_pump_end_drain(void);

// Saved-game count. The payload samples it by calling the guest's own probe on the tick
// thread; the host reads the answer. Non-zero from _wants_ means "take a fresh sample".
//
// Clear and note are separate, and the pump clears BEFORE it probes: the probe runs guest
// code, and a request the host makes during it has to survive. Clearing afterwards would
// swallow that one and leave the host waiting on an answer that never refreshes.
int32_t refii_bridge_guest_tick_pump_wants_save_count(void);
void refii_bridge_guest_tick_pump_clear_save_count_request(void);
void refii_bridge_guest_tick_pump_note_save_count(int32_t count);
void refii_bridge_guest_tick_pump_request_save_count(void);

// The in-game loading screen the guest stood up, or 0. Shared because the payload's own
// finisher and the host's step finisher must not disagree about which object that is.
uint32_t refii_bridge_resolve_in_game_loading_screen(uint8_t* base);

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

//
int32_t refii_bridge_read_host_asset(const char* path, size_t length,
                                     uint8_t* out, uint32_t capacity, uint32_t* out_size);
int32_t refii_bridge_read_host_asset_prefix(const char* path, size_t length, uint32_t count,
                                            uint8_t* out, uint32_t capacity,
                                            uint32_t* out_size);
int32_t refii_bridge_host_asset_size(const char* path, size_t length, uint32_t* out_size);
size_t refii_bridge_host_asset_name_for_hash(uint32_t hash, char* out, size_t capacity);

//
void refii_bridge_publish_hero_derived(const void* sampled);
void refii_bridge_hero_derived_snapshot(void* out);
void refii_bridge_publish_quests(const void* entries, uint32_t count);
uint32_t refii_bridge_requested_world_map_region(void);
void refii_bridge_publish_world_map_region(const void* region);

// The rest of the pump's sampled pages, on the same terms as the two above: the payload
// samples on the guest thread and publishes, the host asks for the one row it is showing.
// Each POD goes over as const void* because its definition is in hero_derived.h, which is
// C++; the host casts it back. "sell" is int32_t rather than bool for the reason the
// contract gives at the top of this file.
void refii_bridge_publish_family(const void* members, uint32_t count);
void refii_bridge_publish_real_estate(const void* estate);
uint32_t refii_bridge_requested_quest_travel(void);
void refii_bridge_publish_quest_travel(const void* travel);
uint32_t refii_bridge_requested_shop_price(int32_t* sell);
void refii_bridge_publish_shop_price(const void* price);

void refii_bridge_note_gui_native_registration(uint32_t implementation,
                                               const char* name, size_t length);
int32_t refii_bridge_gui_native_name_for(uint32_t implementation,
                                         char* out, size_t capacity, size_t* out_length);
int32_t refii_bridge_gui_natives_registered(void);
uint32_t refii_bridge_gui_native_answer_for(uint32_t implementation, uint32_t* out_bits);
void refii_bridge_note_gui_native_entry(uint32_t implementation);
uint32_t refii_bridge_current_gui_native_entry(void);

void refii_bridge_post_gui_native_call(const uint8_t* blob, size_t length);

void refii_bridge_note_gui_element_change(const void* change);

void refii_bridge_stage_menu_attribute(const char* name, size_t name_length,
                                       const char* text, size_t text_length,
                                       float number, int32_t is_number);

// The runner the two stagers feed. Pointer and length, not std::string: the chunk text is
// carried verbatim and the host copies it into its own queue.
void refii_bridge_run_menu_chunk(const char* chunk, size_t length, uint32_t presentation);

void refii_bridge_ensure_alternate_signal_stack(void);

#ifdef __cplusplus
}  // extern "C"
#endif
