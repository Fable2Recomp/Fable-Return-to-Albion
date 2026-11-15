// refii - Fable 3's mod Lua, as the tick pump and the debug channel reach it (mod_lua_hooks.cpp).
#pragma once

#include <cstdint>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include <rex/ppc.h>

namespace refii::fable3::modlua {

/// The once-per-HF-step upkeep on the game VM's own thread: the host's mod commands and input,
/// the Lua subscription masks, the heroes stated to Lua, the tick event. Nothing on another thread
/// or with no game VM.
void Tick(PPCContext& ctx, uint8_t* base);

/// Runs `chunk` in the game VM. False, with the Lua error in `error`, when it did not compile or
/// raised; false with a reason when there is no game VM or this is not its thread.
bool Run(PPCContext& ctx, uint8_t* base, std::string_view chunk, const char* name, std::string* error);

/// Whether Lua's handlers or Godot's mask want `kind`: a seam builds its fields only then.
bool Wants(int32_t kind);

/// What the mods decided about an event a seam asked them about: cancelled, and the catalog's
/// writable fields they wrote (by name).
struct Verdict {
  bool cancelled = false;
  std::vector<std::pair<std::string, double>> writes;

  void Write(const std::string& field, double value);
  bool Read(const char* field, double& value) const;
};

/// Asks the mods about `kind` NOW, inside the seam: Lua's handlers (their cancel and their writes
/// through Refii._write), then a Godot verdict, then the host's event with the decided fields. Off
/// the VM's thread, or inside another decision, the event is queued as a report and nothing is
/// decided.
Verdict Decide(PPCContext& ctx, uint8_t* base, int32_t kind, const std::string& fields);

/// `kind` with its table text (`name=value,...`, Lua literals), raised to Lua and Godot from the
/// next tick on the VM's thread. Safe from any seam on any thread.
void QueueEvent(int32_t kind, std::string fields);

/// Player 1's or 2's hero entity (the world's hero holders, keyed by PlayerID), or 0.
uint32_t Hero(const uint8_t* base, int32_t player);

/// 1 or 2 when `entity` is that player's hero, else 0.
int32_t PlayerOf(const uint8_t* base, uint32_t entity);

/// A text as a single-quoted Lua literal's body.
std::string Quoted(std::string_view text);

/// level_loaded (after the level manager's post-load pass) / level_unloading (before its unload),
/// from world_hooks.cpp with the manager (mod_events_hooks.cpp).
void NoteLevel(const uint8_t* base, uint32_t manager, bool loaded);

}  // namespace refii::fable3::modlua
