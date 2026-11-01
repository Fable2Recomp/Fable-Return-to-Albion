// refii — the payload/host C ABI.
//
// THE PAYLOAD MUST IMPORT NO C++ SYMBOLS. librefii_generated.so is compiled on the
// player's machine by whatever compiler they have; librefii_runtime.so is compiled by us. A
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

// Guest tick queue. The host POSTS and the title's tick pump DRAINS, so the queue and the
// counters live in refii_runtime (src/core/guest_tick_queue.cpp) and the payload reaches them
// here. Observe returns the tick count BEFORE the increment, so the pump's first tick is 0.
uint64_t refii_bridge_guest_tick_pump_observe_tick(void);
uint64_t refii_bridge_guest_tick_pump_drained(void);

/// The guest's render frame: incremented on the game thread each time the 30 Hz HF pass
/// (`sub_821A61B0`, which writes every character's pose) returns. 0 before the first.
uint32_t refii_bridge_guest_frame(void);
void refii_bridge_note_guest_frame(void);
void refii_bridge_guest_tick_pump_note_ran(void);

// The drain protocol, and ONLY from the guest tick thread. Begin takes the whole queue aside
// under the lock and answers how many it took — swapped rather than popped one at a time so
// that a post made WHILE the drain runs lands in the next tick instead of extending this one
// indefinitely, and so no host mutex is held across guest code. Read each by index with the
// usual measure-then-fill convention, defer the ones that could not run yet, and End puts the
// deferred ones back at the FRONT so a later post cannot overtake them.
uint32_t refii_bridge_guest_tick_pump_begin_drain(void);
size_t refii_bridge_guest_tick_pump_taken_action(uint32_t index, char* out, size_t capacity);
void refii_bridge_guest_tick_pump_defer_action(const char* name, size_t length);
void refii_bridge_guest_tick_pump_end_drain(void);

// The name the game shows for the player with this XUID, by RexNet's rule (core/coop_names.cpp):
// a friend's display name, anyone else's pseudonym. 1 when the XUID is a known player.
int32_t refii_bridge_safe_name_for_xuid(uint64_t xuid, char* out, uint32_t capacity);
// The name to show for a name another machine's game sent (a known player's pseudonym → their
// name by the same rule); 0 when it names no known player. And this player's own name.
int32_t refii_bridge_safe_name_for_sent(const char* sent, char* out, uint32_t capacity);
int32_t refii_bridge_own_name(char* out, uint32_t capacity);

//
int32_t refii_bridge_read_host_asset(const char* path, size_t length,
                                     uint8_t* out, uint32_t capacity, uint32_t* out_size);
int32_t refii_bridge_read_host_asset_prefix(const char* path, size_t length, uint32_t count,
                                            uint8_t* out, uint32_t capacity,
                                            uint32_t* out_size);
int32_t refii_bridge_host_asset_size(const char* path, size_t length, uint32_t* out_size);
int32_t refii_bridge_host_asset_is_override(const char* path, size_t length);
size_t refii_bridge_host_asset_name_for_hash(uint32_t hash, char* out, size_t capacity);

/// The UI data generation: bumped when something an open menu lists changes (an ability level,
/// experience, an item, gold, a drawn weapon); Godot re-reads its open list when it moves.
/// A change also marks the data dirty: the pump takes the hero sample on its next tick rather
/// than at its cadence and bumps again once it is published (`_published`), so the re-read
/// that follows a change sees the changed values.
void refii_bridge_ui_data_changed(void);
int32_t refii_bridge_ui_data_take_dirty(void);
void refii_bridge_ui_data_published(void);
uint32_t refii_bridge_ui_data_generation(void);

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
// - the host's MASK says which kinds it wants (bit = kind; nothing is queued otherwise):
//   REFII_MOD_HOST_MASK_WORDS words of 64, word w holding kinds 64w..64w+63. Words 0 and 1 arrive
//   through the host's two mask exports (RefiiHostSetModEventMask / ...MaskHigh), the words past
//   them through the tick's command lane (mod_commands.cpp `mask_word`); the verdict mask below
//   has the same shape. A kind at or past 64 * REFII_MOD_HOST_MASK_WORDS is never posted — the
//   catalog's test holds REFII_EVENT_COUNT under it.
// - the host POSTS an input edge (a mod's input.json action, pressed or released, for player 1
//   or 2) and the payload's tick polls them into InputAction events.
#define REFII_MOD_HOST_MASK_WORDS 4
void refii_bridge_mod_event_post(int32_t kind, const char* fields, size_t length);
uint64_t refii_bridge_mod_host_event_mask_word(int32_t word);
void refii_bridge_mod_set_host_event_mask_word(int32_t word, uint64_t mask);
int32_t refii_bridge_mod_input_poll(char* out_name, size_t capacity, int32_t* out_player,
                                    int32_t* out_pressed);
// - the host POSTS a command line (a helper call or a query from a Godot script, the grammar in
//   Fable2/src/vfs/src/hooks/mod_commands.cpp) and the payload's tick runs it with the real guest context; a
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
//   The mask is REFII_MOD_HOST_MASK_WORDS words, as the event mask.
void refii_bridge_mod_set_verdict_mask_word(int32_t word, uint64_t mask);
uint64_t refii_bridge_mod_verdict_mask_word(int32_t word);

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
// The route's MOD LANE: a line posted with the prefix `pm:` (a mod's partner message,
// refii_mod_send_to_partner) is kept apart on the receiver and taken here — the mod bus drains it
// on the guest tick (mod_events.cpp DrainPartnerMessages) and the Lua never sees it; the same
// order and once-only as the rest of the route.
int32_t refii_bridge_route_take_mod(char* out, uint32_t capacity);

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

// THE MOUSE'S CAMERA TURN (src/core/mouse_look_state.cpp), taken by Fable2/src/vfs/src/hooks/mouse_look.cpp
// once per camera update of the local player's camera manager, guest tick thread only.
// - take everything the host posted (degrees at the middle sensitivity; right and up positive);
//   the state's sequence goes odd until the matching state call;
void refii_bridge_mouse_look_take(float* out_yaw, float* out_pitch);
// - state what became of it: the yaw and pitch taken (posted degrees), vertical held back below
//   the camera's dead zone (returned to the pending turn), the turn the camera made for it in this
//   update (its own degrees, rightward and upward), its degrees per posted degree (0 keeps the
//   last), and how the host draws a turn not yet taken: `mode` 0 not at all, 1 about the vertical
//   through the focus `pivot_forward` metres along the level line of sight, 2 about the camera's
//   own position, 3 rigidly about the point (`pivot_forward`, `pivot_right`, `pivot_up`) metres
//   along the camera's level forward, level right and world up — the yaw about its vertical, the
//   pitch about its level right axis, elevation bounded at `pitch_limit` degrees — with the ranged
//   aim `aim` (its reticule anchor) turning with the view.
void refii_bridge_mouse_look_state(uint32_t tick, float taken_yaw, float taken_pitch,
                                   float held_pitch, float absorbed_yaw, float absorbed_pitch,
                                   float factor, float pivot_forward, float pivot_right,
                                   float pivot_up, float pitch_limit, int32_t mode,
                                   uint32_t aim);
// - whether the mouse is the player's look device (the host's device switch): nonzero while the
//   keyboard and mouse were touched last;
int32_t refii_bridge_mouse_look_device(void);
// - take every mouse-wheel notch the host posted since the last take, rightward positive (a
//   notch down +1, up -1);
int32_t refii_bridge_mouse_look_take_wheel(void);
// - count one target switch handed to a target lock (a mouse flick or a wheel notch), for the
//   debug channel.
void refii_bridge_mouse_look_flicked(void);

// THE MOVIE THE GUEST STATES, for the host to play (core/movie_state.h; Godot's GameVideo). A
// title's Bink hooks state each call as the guest makes it; the host bridge's movie exports read
// it. Every title's player has the same contract: a request (the .bik's bytes, or its path), the
// sound it is opened with, the rect it is drawn in, and two bytes of the player's own — the one
// its decode sets when the movie ends, which the host writes when its own copy ends, and the one
// a skip sets, which the host reads. The title names those bytes; nothing here knows a layout.
// - the source the guest handed its Bink open: a .bik in guest memory (forward or word-swapped,
//   as a loader that byte-swaps on the way in leaves it) or a path naming one;
void refii_bridge_movie_requested(uint8_t* base, uint32_t data_guest);
// - the sound `player` was opened with: 0 silent, else the Bink track ids it plays, at 1.0;
void refii_bridge_movie_sound(uint32_t player, uint32_t sound, const uint32_t* track_ids,
                              uint32_t count);
// - a track's linear volume, 0..1, as the guest restates it;
void refii_bridge_movie_volume(uint32_t player, uint32_t track_id, float volume);
// - `player` is open and live: its done byte (guest address), and its skip byte (0 when its
//   class has none). Drops whatever rect was stated for the player's last movie;
void refii_bridge_movie_open(uint32_t player, uint32_t done_byte, uint32_t skip_byte);
// - the skip byte of the live `player`, stated by an owner that learns it after the open;
void refii_bridge_movie_skip_byte(uint32_t player, uint32_t skip_byte);
// - the rect the guest draws `player` in, in its own 2D screen space;
void refii_bridge_movie_rect(uint32_t player, int32_t left, int32_t top, int32_t right,
                             int32_t bottom);
// - `player` is closing; the host stops reading through it before the guest frees it.
void refii_bridge_movie_closed(uint32_t player);

// THE UI CUT'S SEAM (src/runtime/src/core/gui_native_calls.cpp; C++ API core/gui_native_calls.h).
// A title's hook on its game Lua VM's `luaD_precall` recognises a `GUI.*` native by the
// IMPLEMENTATION pointer behind the thunk and posts the call with its arguments; its hook on the
// VM's registration names each implementation first. The shared host exports drain the calls and
// hold the host's answers to predicates the host owns.
// - a `GUI` registration binds `implementation` under `name`;
void refii_bridge_note_gui_native_registration(uint32_t implementation,
                                               const char* name, size_t length);
// - the first name bound to `implementation` (0 when it is not a GUI native);
int32_t refii_bridge_gui_native_name_for(uint32_t implementation,
                                         char* out, size_t capacity, size_t* out_length);
// - whether any GUI registration has landed, so the call hook skips the boot storm;
int32_t refii_bridge_gui_natives_registered(void);
// - the host's answer for `implementation`: 0 none, 1 boolean, 2 float, 3 suppress;
uint32_t refii_bridge_gui_native_answer_for(uint32_t implementation, uint32_t* out_bits);
// - the implementation whose thunk is about to run on this thread, and its read-back;
void refii_bridge_note_gui_native_entry(uint32_t implementation);
uint32_t refii_bridge_current_gui_native_entry(void);
// - one intercepted call, flattened by core/gui_native_calls.h's blob codec.
void refii_bridge_post_gui_native_call(const uint8_t* blob, size_t length);

// THE VIEW A TITLE STATES (src/runtime/src/core/stated_view.cpp). A title whose hook sees the
// camera the guest renders the world with states it here, every step; its host bridge reads it
// back for the shell.
// - position, up and forward (the guest's world axes) and the horizontal field of view in
//   radians, `snap` non-zero when the guest cut rather than blended;
void refii_bridge_camera_stated(const float* position, const float* up, const float* forward,
                                float horizontal_fov, int32_t snap);
// - the last stated camera, 11 floats (position, up, forward, horizontal fov, snap); 0 before
//   any was stated.
int32_t refii_bridge_camera_read(float* out);
// - the two cameras the guest blends each step, both in the stated 11-float layout: `live` the
//   camera this step and `previous` the one latched last step; read back as 22 floats, live first.
void refii_bridge_camera_pair_stated(const float* live, const float* previous);
int32_t refii_bridge_camera_pair_read(float* out);
// THE HOST'S WORLD BUILD: set while Godot builds the level the guest is loading; a title's hook
// on the guest's own preload gate reads it, so the guest waits in its loading screen.
void refii_bridge_set_world_build_pending(int32_t pending);
int32_t refii_bridge_world_build_pending(void);

#ifdef __cplusplus
}  // extern "C"
#endif
