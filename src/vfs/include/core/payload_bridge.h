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

void refii_bridge_new_game_result(uint32_t success);
void refii_bridge_new_game_generation(void);

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
void refii_bridge_note_bink_movie_rect(uint32_t player, int32_t left, int32_t top, int32_t right,
                                       int32_t bottom);
void refii_bridge_note_bink_video_requested(uint8_t* base, uint32_t data_guest);
void refii_bridge_note_bink_sound(uint8_t* base, uint32_t player, uint32_t sound);
void refii_bridge_note_bink_volume(uint32_t player, uint32_t track_id, float volume);

// Guest tick pump. Wrappers: refii::vfs::GuestTickPump* in vfs_hooks_private.h.
//
// The host POSTS and the payload DRAINS, so the queue and the counters live in refii_vfs
// (src/core/guest_tick_pump_state.cpp) and the payload reaches them here. Observe returns the
// tick count BEFORE the increment, so the pump's first tick is 0.
/// The front end's subtitles setting as the guest last stated it: 1 on, 0 off, -1 unknown.
void refii_bridge_front_end_note_subtitles(int32_t state);

/// Which pause tabs the guest's own predicate (`sub_826B83D0`) answered yes for, bit N = tab N
/// for N in 2..8. The predicate runs in the payload, on the guest thread; the answer is read by
/// the host bridge, so it has to cross here rather than by C++ symbol.
void refii_bridge_note_pause_tabs(uint32_t mask);

// The effect transforms the guest states, recorded per PSInstance. Wrappers:
// refii::vfs::NoteFx* in vfs_hooks_private.h.
//
// The payload's two setter hooks write and the host bridge reads, so the table lives in
// refii_vfs (src/core/stated_fx_state.cpp) and crosses here. Four lanes each because which
// lane holds x depends on the recompiler's register layout; the host decides.
void refii_bridge_note_fx_position(uint32_t instance, const float* lanes);
void refii_bridge_note_fx_orientation(uint32_t instance, const uint32_t* lanes);

// The targeting highlight colour the guest states. Wrappers: refii::vfs::NoteHighlight* in
// vfs_hooks_private.h. Same direction of travel, same reason — the table is in refii_vfs
// (src/core/target_highlight_state.cpp). RGBA packed big-endian, R in the top byte.
void refii_bridge_note_highlight_colour(uint32_t component, uint32_t rgba);
void refii_bridge_note_highlight_target(void);

// Each renderable's stated appearance. Wrappers: refii::vfs::NoteAppearance / ForgetAppearance
// in vfs_hooks_private.h; the table is in refii_vfs (src/core/entity_appearance_state.cpp).
// Words, as the guest keeps them on CECGraphicAppearance (offsets from the component):
//   0 alpha +112, 1 camera alpha +116, 2 scale +108 (float bits),
//   3..6 tint +80, +84, +88, +92 (float bits, memory order),
//   7 flags: kAppearanceDrawable = +197 non-zero, kAppearanceSaturation = +196 non-zero.
enum {
  kAppearanceWords = 8,
  kAppearanceFlags = 7,
  kAppearanceDrawable = 1,
  kAppearanceSaturation = 2,
};
void refii_bridge_note_appearance(uint32_t component, const uint32_t* words);
void refii_bridge_forget_appearance(uint32_t component);

// Each character part's morph targets as the guest states them to `sub_82A48B90`: `pairs` is
// `count` x {target model hash, weight float bits}; count 0 states the part unmorphed.
// Wrapper: refii::vfs::NotePartMorph; table in src/core/part_morph_state.cpp.
enum {
  kPartMorphParts = 24,
  kPartMorphTargets = 8,
};
void refii_bridge_note_part_morph(uint32_t entity, uint32_t base_model, const uint32_t* pairs,
                                  uint32_t count);

// Which of the hero's garments are dyed: the guest's own `sub_826CAFA8` per slot, sampled on
// the GUI tick. kDyeStateWords words per item: 0 item record hash, 1 flags (bit 0 slot 1 dyed,
// bit 1 slot 2 dyed, bit 2 the item is worn). Table in src/core/dye_state.cpp.
enum {
  kDyeStateWords = 2,
  kDyeStateItems = 160,
};
void refii_bridge_note_dye_states(const uint32_t* words, uint32_t count);

// Each character's texture work as the guest states it. Wrappers: refii::vfs::BeginTextureLayers
// / NoteTextureLayers; table in src/core/texture_layer_state.cpp. A layer is kTextureLayerWords
// words: 0 texture id, 1 blend mode (0 opaque, 1 alpha, 2 additive, 3 blend-factor, 4 masked),
// 2 weight (float bits), 3 has-tint, 4 tint bytes B G R A (memory order), 5 brightness (float
// bits), 6 bump texture id (overlays only).
// `kind` 0 is a composite stated to `sub_825B38B0`, layer 0 the base. `kind` 1 is the OVERLAY
// list sent as command type 14 through `sub_8222AAC8`: scars and blend-mode-0 morphs, drawn
// over the finished material; texture id is the overlay's diffuse.
// `kind` 2 is a WHOLE-ENTITY SWAP (`sub_825B7100`, the shadow creature's and component 192's):
// original 0 every diffuse map, 1 every specular map, 2 every normal map; one layer, the
// replacement at weight 1.
// `begin` drops what the entity stated before.
enum {
  kTextureLayerWords = 7,
  kTextureLayersPerTexture = 8,
  kTextureLayerTextures = 40,
};
void refii_bridge_begin_texture_layers(uint32_t entity);
// Publishes the statements since the begin as the entity's layers, in one step.
void refii_bridge_end_texture_layers(uint32_t entity);
void refii_bridge_note_texture_layers(uint32_t entity, uint32_t original, uint32_t kind,
                                      const uint32_t* layers, uint32_t count);

// The decals the guest states (src/hooks/decals.cpp, table in src/core/decal_state.cpp): a ring
// of 50, the oldest replaced. kDecalWords words each: 0 serial (the host's), 1 owner render
// twin or 0, 2 texture name hash, 3..6 position, 7..10 direction (float bits, guest world),
// 11 / 12 half sizes, 13 life, 14 flag.
enum {
  kDecalWords = 15,
};
void refii_bridge_note_decal(const uint32_t* words);
void refii_bridge_clear_decals(void);
void refii_bridge_drop_decals_of(uint32_t owner);

// The FX mesh particle descriptors the guest's effects state (src/hooks/fx_meshes.cpp, table in
// src/core/fx_mesh_state.cpp): noted at `sub_82456D58`, dropped when their twin is destroyed.
void refii_bridge_note_fx_mesh(uint32_t descriptor);
void refii_bridge_drop_fx_mesh(uint32_t descriptor);
uint32_t refii_bridge_fx_meshes(uint32_t* out, uint32_t capacity);

// The layers the guest's own dye builder states for ONE inventory item, keyed by the item's
// record hash and written as a character's composites are (kTextureLayerWords per layer); the
// menu previews a dyed garment through them. Sampled on the GUI tick for every dyed item the
// hero wears or carries. Table in src/core/item_dye_layer_state.cpp.
enum {
  kItemDyeLayerItems = 64,
  kItemDyeLayerWords = 4 * (2 + kTextureLayersPerTexture * kTextureLayerWords),
};
void refii_bridge_set_item_dye_layers(const uint32_t* records, const uint32_t* offsets,
                                      const uint32_t* counts, uint32_t items,
                                      const uint32_t* words);

// The pub-game seam. The host claims the guest's pub-game module and posts the fscommands
// Godot's game issues; the pump replacement (src/hooks/pubgame_pump.cpp, on the guest's render
// thread) drains them into the guest's own handlers and reports back what the movie would have
// been told. State in src/core/pubgame_state.cpp. `numbers` is 3 doubles, `integers` 2.
int32_t refii_bridge_pubgame_claimed(void);
int32_t refii_bridge_pubgame_take_command(uint32_t* kind, char* text, size_t text_capacity,
                                          double* numbers, int64_t* integers);
void refii_bridge_pubgame_note_event(uint32_t bits);

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

/// The guest's render frame: incremented on the game thread each time the 30 Hz HF pass
/// (`sub_821A61B0`, which writes every character's pose) returns. 0 before the first.
uint32_t refii_bridge_guest_frame(void);
void refii_bridge_note_guest_frame(void);
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
// The open GUI's owner: 1 when PLAYER TWO owns it (the guest's `sub_8236E400`), else 0. Set by the
// payload once per guest tick, read by the host bridge (RefiiHostGetMenuOwner).
void refii_bridge_set_menu_owner_two(int32_t two);
int32_t refii_bridge_menu_owner_two(void);
// A region exit refused `player` (1 or 2) because their partner was out of range (the travel
// rule, coop_hero_hooks.cpp). Once per approach; read-and-cleared by the host
// (RefiiHostTakeExitRefused) as a mask, bit 0 player one, bit 1 player two.
void refii_bridge_note_exit_refused(int32_t player);
int32_t refii_bridge_take_exit_refused(void);
// The things whose use prompt answers player two (coop_hero_hooks.cpp), published by the payload
// once a frame; the host copies them out (RefiiHostGetPlayerTwoPrompts). Returns the count.
// The things whose use prompt shows this frame and which players see it (bit 0 player one, bit 1
// player two: the guest's own in-range test run for each), published by coop_hero_hooks.cpp.
void refii_bridge_set_prompt_masks(const uint32_t* things, const uint8_t* masks, int32_t count);
// Online: this machine's player stands waiting at a region exit for their partner (the travel
// rule). Set by the payload once a frame; the host restates it to the peer (RefiiHostGetExitWaiting).
void refii_bridge_set_exit_waiting(int32_t waiting);
// The online joiner used a region exit at this guest position (coop_hero_hooks.cpp, the exit's
// fire on a joiner). Each use bumps the sequence; the host restates the newest to the peer
// (RefiiHostGetExitRequest), whose exit update fires the exit standing there.
void refii_bridge_set_exit_request(float x, float y, float z);
uint32_t refii_bridge_exit_request(float* out_xyz);
int32_t refii_bridge_exit_waiting(void);
int32_t refii_bridge_prompts_two(uint32_t* out, int32_t capacity);
// The things only player two sees (player one's half hides their icon).
int32_t refii_bridge_prompts_one_hidden(uint32_t* out, int32_t capacity);
// Player two's own hold-to-use meter (coop_hero_hooks.cpp): the fraction 0..1 and whether A is held.
void refii_bridge_player_two_hold_state(float fraction, int32_t pressed);
void refii_bridge_player_two_hold(float* fraction, int32_t* pressed);
// Player two's own hold prompt (coop_hero_hooks.cpp): up, its guest world point and title (UTF-8).
void refii_bridge_player_two_hold_prompt(int32_t up, const float* at, const char* title);
int32_t refii_bridge_player_two_hold_prompt_get(float* at, char* title, uint32_t capacity);
// Local co-op: the use prompt text the OTHER player (not the one the guest's prompt is for) would be
// shown, per thing (coop_hero_hooks.cpp, the dry pass): `thing \t player \t title` lines.
void refii_bridge_set_prompt_titles(const char* text);
// Property notices for the host (hooks/src/property_hooks.cpp): kind 0 a rented home fell into
// disrepair (its tenants stop paying), 1 repaired, 2 not enough gold to repair, 3 nothing to repair.
void refii_bridge_property_notice_post(uint64_t uid);
void refii_bridge_property_notice_post_kind(int32_t kind, uint64_t uid);
int32_t refii_bridge_property_notice_take(int32_t* kind, uint64_t* uid);
uint32_t refii_bridge_prompt_titles(char* out, uint32_t capacity);
// Player two's household actions for the co-op peer to run (mod_events.cpp, the core Lua's
// Refii._household): the joiner's estate actions, gifts, furniture and bed for the host, the host's
// kept gifts for the joiner. `<action>:<uid hex>:<value>`, oldest first; take returns the length,
// 0 when none.
void refii_bridge_estate_intent_post(const char* text);
uint32_t refii_bridge_estate_intent_take(char* out, uint32_t capacity);
// The name the game shows for the player with this XUID, by RexNet's rule (core/coop_names.cpp):
// a friend's display name, anyone else's pseudonym. 1 when the XUID is a known player.
int32_t refii_bridge_safe_name_for_xuid(uint64_t xuid, char* out, uint32_t capacity);
// The name to show for a name another machine's game sent (a known player's pseudonym → their
// name by the same rule); 0 when it names no known player. And this player's own name.
int32_t refii_bridge_safe_name_for_sent(const char* sent, char* out, uint32_t capacity);
int32_t refii_bridge_own_name(char* out, uint32_t capacity);
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

//
int32_t refii_bridge_read_host_asset(const char* path, size_t length,
                                     uint8_t* out, uint32_t capacity, uint32_t* out_size);
int32_t refii_bridge_read_host_asset_prefix(const char* path, size_t length, uint32_t count,
                                            uint8_t* out, uint32_t capacity,
                                            uint32_t* out_size);
int32_t refii_bridge_host_asset_size(const char* path, size_t length, uint32_t* out_size);
int32_t refii_bridge_host_asset_is_override(const char* path, size_t length);
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
/// The world map's property row: the UID the host wants sampled, and the page's numbers for it.
uint64_t refii_bridge_requested_property_estate(void);
/// The UI data generation: bumped when something an open menu lists changes (an ability level,
/// experience, an item, gold, a drawn weapon); Godot re-reads its open list when it moves.
/// A change also marks the data dirty: the pump takes the hero sample on its next tick rather
/// than at its cadence and bumps again once it is published (`_published`), so the re-read
/// that follows a change sees the changed values.
void refii_bridge_ui_data_changed(void);
int32_t refii_bridge_ui_data_take_dirty(void);
void refii_bridge_ui_data_published(void);
uint32_t refii_bridge_ui_data_generation(void);
void refii_bridge_publish_property_estate(uint64_t uid, const void* estate);
uint32_t refii_bridge_requested_quest_travel(void);
void refii_bridge_publish_quest_travel(const void* travel);
uint32_t refii_bridge_requested_shop_price(int32_t* sell);
void refii_bridge_publish_shop_price(const void* price);
void refii_bridge_publish_clothing_mannequin(uint32_t entity);

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

// The debug channel's switch for the guest's own scene draw: -1 follows REFII_GUEST_RENDER,
// 0 off, 1 on. Read by the DrawScene hook; the state is in src/core/guest_render_state.cpp.
int32_t refii_bridge_guest_render_override(void);

// Hard mods' named values, keyed "<mod>.<name>". A hook publishes, Godot reads; Godot sets a
// setting, a hook reads it. Wrappers: refii::mods in core/mod_values.h; state in
// src/core/mod_values.cpp.
void refii_bridge_mod_publish_f32(const char* key, size_t key_length, float value);
int32_t refii_bridge_mod_value_f32(const char* key, size_t key_length, float* out);
void refii_bridge_mod_set_setting_f32(const char* key, size_t key_length, float value);
float refii_bridge_mod_setting_f32(const char* key, size_t key_length, float fallback);

// The mod event bus, both ways across the bridge (state in src/core/mod_values.cpp):
// - the payload POSTS an event the host asked for (`fields` is the Lua-table-literal body the
//   bus builds for Lua handlers) and the host drains them in order (Godot scripts' RefiiHost);
// - the host's MASK says which kinds it wants (bit = kind; nothing is queued otherwise);
// - the host POSTS an input edge (a mod's input.json action, pressed or released, for player 1
//   or 2) and the payload's tick polls them into InputAction events.
void refii_bridge_mod_event_post(int32_t kind, const char* fields, size_t length);
uint64_t refii_bridge_mod_host_event_mask(void);
// Kinds 64..127.
uint64_t refii_bridge_mod_host_event_mask_high(void);
int32_t refii_bridge_mod_input_poll(char* out_name, size_t capacity, int32_t* out_player,
                                    int32_t* out_pressed);
// - the host POSTS a command line (a helper call or a query from a Godot script, the grammar in
//   src/hooks/mod_commands.cpp) and the payload's tick runs it with the real guest context; a
//   query's reply is posted back as an event of kind REFII_MOD_HOST_REPLY_KIND, mask or not.
int32_t refii_bridge_mod_command_poll(char* out_line, size_t capacity);
// - a mod's Lua ERROR (init or handler) is posted as text; the host keeps them for the Studio and
//   the debug channel (RefiiHostDrainModErrors), independent of the event mask.
void refii_bridge_mod_error_post(const char* text, size_t length);
// - the HANDLE table (refii_mod_handle): one per session, shared by the payload, which assigns
//   handles, and the host, which turns a script's handle back into its entity.
uint32_t refii_bridge_mod_handle(uint32_t address);
uint32_t refii_bridge_mod_handle_address(uint32_t handle);
/// The entity at `address` is gone: its handle now resolves to 0, and an object that later takes
/// the address gets a new handle.
void refii_bridge_mod_handle_forget(uint32_t address);
// - a Godot script's VERDICT: the host registers a handler the payload calls synchronously, on the
//   guest thread inside the seam, for the kinds in the verdict mask. `fields` is the event's table
//   text; the handler writes "cancel=1,<field>=<number>,…" into `out` and returns its length.
typedef int32_t (*refii_mod_verdict_fn)(int32_t kind, const char* fields, size_t length, char* out, size_t capacity);
void refii_bridge_mod_set_verdict_handler(refii_mod_verdict_fn handler);
refii_mod_verdict_fn refii_bridge_mod_verdict_handler(void);
void refii_bridge_mod_set_verdict_mask(uint64_t mask);
uint64_t refii_bridge_mod_verdict_mask(void);
// Kinds 64..127.
void refii_bridge_mod_set_verdict_mask_high(uint64_t mask);
uint64_t refii_bridge_mod_verdict_mask_high(void);

// PLAYER STATE (state in src/core/player_state.cpp, the contract in core/player_state.h): a mod's
// per-player values, owned by the machine that plays the hero and the same on both machines.
// `player` is 1 or 2; a key is "<mod>.<name>".
// - read: 1 and the value when the key is set for that player, else 0;
int32_t refii_bridge_player_get(int32_t player, const char* key, size_t key_length, float* out);
// - write: `add` adds `value` instead of setting it; `saved` keeps the key with the player's save;
void refii_bridge_player_write(int32_t player, const char* key, size_t key_length, float value, int32_t add,
                               int32_t saved);
// - the payload states which players this machine plays (bit 0 player one, bit 1 player two),
//   every tick; a player gaining an owner here keeps what it holds, one losing it waits for the peer;
void refii_bridge_player_set_owned(uint32_t mask);
uint32_t refii_bridge_player_owned(void);
// - a new hero for `player`: its session values go; its saved values stay until a save load
//   replaces them;
void refii_bridge_player_new_hero(int32_t player);
// - everything the player held goes: `fresh` for a new hero not loaded from a save (a new game —
//   its values may be written into the save it makes), 0 for a player who left;
void refii_bridge_player_clear(int32_t player, int32_t fresh);
// - the mod event bus is running handlers for the game's COPY of something the other co-op
//   machine decides (on 1, off 0, nested): writes are dropped meanwhile, since the deciding machine
//   makes the same ones;
void refii_bridge_player_replica(int32_t on);
int32_t refii_bridge_player_in_replica(void);
// - the game loaded `player`'s hero from the save named `save_name` (its content file name):
//   the saved values become that folder's, or none when it keeps none;
void refii_bridge_player_load_saved(int32_t player, const char* save_name);
// - whether `player`'s saved values were last loaded from the save named `save_name`;
int32_t refii_bridge_player_loaded_from(int32_t player, const char* save_name);
// - the game has just written `player`'s hero into the save named `save_name`: its saved values
//   go into that folder. Returns 1 when written.
int32_t refii_bridge_player_write_saved(int32_t player, const char* save_name);
// - saved BYTES under a key: written beside the save with the saved values and read back with them,
//   and the owner's go to the peer (in parts, when they change and every few seconds). get: the
//   size (the bytes into `out` when `capacity` holds them), -1 when none; set: null `data` drops
//   the key.
int32_t refii_bridge_player_blob_get(int32_t player, const char* key, uint8_t* out, uint32_t capacity);
void refii_bridge_player_blob_set(int32_t player, const char* key, const uint8_t* data, uint32_t size);

// THE EVENT RELAY (state in src/core/event_relay.cpp, the wire in core/event_relay.h): a mod event
// decided on this co-op machine, posted for the peer as the payload's own text (`<kind>:<fields>`,
// no ';', '|' or newline), and the peer's events taken back one at a time (1 and the text in `out`
// while one waits, 0 when none does). The payload decides what is relayed (mod_events.cpp).
void refii_bridge_event_relay_post(const char* text);
int32_t refii_bridge_event_relay_take(char* out, uint32_t capacity);

// THE ROUTE (src/core/event_relay.cpp, the wire in core/event_relay.h): the core co-op Lua's calls
// for the player the peer plays (a box, a question, a camera or HUD change, a Player.* command) and
// the answers coming back, as the Lua's own text (no ';', '|' or newline). Unlike the relay it is
// ordered and acknowledged: every line arrives once, in the order posted, or (the peer gone for
// kRouteLife) not at all. take: 1 and the text in `out` while one waits, 0 when none does.
void refii_bridge_route_post(const char* text);
int32_t refii_bridge_route_take(char* out, uint32_t capacity);

// HERO IDENTITY AND PARTNERSHIPS (src/core/hero_partners.cpp), Fable 3's player-to-player marriage
// and business partnership: a hero's id (48 bits, kept with its save; 0 not yet known), made once by
// the machine that plays the hero; a partnership in both partners' stores (1 business, 2 married);
// a building's co-owner (the world's, player one's store) and the gold owed to an absent partner.
uint64_t refii_bridge_hero_id(int32_t player);
void refii_bridge_hero_id_ensure(int32_t player);
int32_t refii_bridge_partner_kind(int32_t player, uint64_t other);
// The partnership between the two heroes in the game (both stores agree), 0 none.
int32_t refii_bridge_partnership(void);
int32_t refii_bridge_partnership_set(int32_t kind);
uint64_t refii_bridge_coowner(uint64_t uid);
void refii_bridge_coowner_set(uint64_t uid, uint64_t id);
int32_t refii_bridge_owed(uint64_t id);
void refii_bridge_owed_add(uint64_t id, int32_t gold);

// PARTNER CONSENT (src/core/coop_consent.cpp): a co-op script asks the other player before doing
// something that changes both players' game. `kind` 1 sleep (detail: the sleep type), 2 sex,
// 0 a mod's own question (its text); `asker` the asking player, 1 or 2.
// - the payload asks (a ticket) and polls the answer: -1 pending, 0 no, 1 yes;
uint32_t refii_bridge_consent_ask(int32_t kind, int32_t asker, int32_t detail);
uint32_t refii_bridge_consent_ask_text(int32_t asker, const char* text);
int32_t refii_bridge_consent_answer(uint32_t ticket);
// - the host takes each new question once (with its text, empty for kinds 1 and 2) and gives its
//   answer.
int32_t refii_bridge_consent_take(uint32_t* ticket, int32_t* kind, int32_t* asker, int32_t* detail,
                                  char* text, uint32_t capacity);
void refii_bridge_consent_give(uint32_t ticket, int32_t yes);

// ONE-TIME COLLECTABLES PER PLAYER (src/core/coop_collectables.cpp): silver keys by entity address and
// UniqueID; a player's taking is a saved player value. The mask is bit 0 player one, bit 1 player two.
void refii_bridge_collectable_track(uint32_t address, uint64_t uid);
void refii_bridge_collectable_forget(uint32_t address);
uint32_t refii_bridge_collectable_mask(uint64_t uid);
void refii_bridge_collectable_take(int32_t player, uint64_t uid);
// - the host reads (address, mask) pairs of the tracked keys someone has taken; returns the pairs.
uint32_t refii_bridge_collectable_masks(uint32_t* out, uint32_t capacity);

// A VILLAGER'S OPINION OF PLAYER TWO (src/core/coop_opinion.cpp), by the opinion component's
// address: the component's per-hero regions in guest byte order (mod_events.cpp kOpinionRegions),
// starting with +24..+64 (base and current axes) then +176..+236 (the five modifier lists).
#define REFII_OPINION_BLOCK_BYTES 208
int32_t refii_bridge_opinion_two_load(uint32_t component, uint8_t* out);
void refii_bridge_opinion_two_store(uint32_t component, const uint8_t* in);
void refii_bridge_opinion_two_forget(uint32_t component);
// Hero one's slot (the player record `players`'s +108 holder) as our readers must see it on any
// thread: an opinion scope for hero two lends it to hero two for one guest call
// (refii_bridge_hero_slot_lend / _return); a read overlapping a lend answers the real holder.
void refii_bridge_hero_slot_lend(uint32_t real_holder);
void refii_bridge_hero_slot_return(void);
uint32_t refii_bridge_hero_one_holder(const uint8_t* base, uint32_t players);

// PLAYER TWO'S SPLIT-SCREEN HUD (src/core/coop_hud_two.cpp): their targeted title and d-pad
// icons, from the guest's own builders called for hero two (hooks/guest_tick_pump.cpp).
// - the pump states them each tick (known 0 off split screen; icons: up, left, down, right);
void refii_bridge_player_two_hud_state(int32_t known, uint32_t target, const char* title,
                                       const char* const* icons);
// - the host reads the title then the four icon paths, newline-separated, and the targeted
//   entity; returns 1 when stated.
uint32_t refii_bridge_player_two_hud(uint32_t* target, char* out, uint32_t capacity);
// - nonzero while player two's d-pad fill runs (the shared tutorial calls are skipped); the
//   payload's own (the pump sets it, coop_hero_hooks.cpp reads it).
int32_t refii_bridge_dpad_query_two(void);

// THE MOUSE'S CAMERA TURN (src/core/mouse_look_state.cpp), taken by src/hooks/mouse_look.cpp
// once per camera update of the local player's camera manager, guest tick thread only.
// - take everything the host posted (degrees at the middle sensitivity; right and up positive);
//   the state's sequence goes odd until the matching state call;
void refii_bridge_mouse_look_take(float* out_yaw, float* out_pitch);
// - state what became of it: the yaw taken (posted degrees), vertical held back below the
//   camera's dead zone (returned to the pending turn), the yaw the camera turned for it in this
//   update (its own degrees, rightward), its degrees per posted degree (0 keeps the last), and how
//   the host draws a turn not yet taken: `mode` 0 not at all, 1 about the vertical through the
//   focus `pivot` metres along the level line of sight, 2 about the camera's own position.
void refii_bridge_mouse_look_state(uint32_t tick, float taken_yaw, float held_pitch,
                                   float absorbed_yaw, float factor, float pivot, int32_t mode);
// - whether the mouse is the player's look device (the host's device switch): nonzero while the
//   keyboard and mouse were touched last;
int32_t refii_bridge_mouse_look_device(void);
// - count one target switch handed to a target lock (a mouse flick), for the debug channel.
void refii_bridge_mouse_look_flicked(void);

#ifdef __cplusplus
}  // extern "C"
#endif
