// refii — a mod's requests for Godot, shared by every title's payload: effects and lights
// (REFII_MOD_HOST_FX_KIND), boxes, notices, menus, marks, meters, fades, the HUD, input and pad
// presses (REFII_MOD_HOST_UI_KIND), menu rows and host screens (REFII_MOD_HOST_MENU_KIND). Pure
// formatting and posting through the bus (core/mod_values.h): no guest read, so a title's natives
// and helpers call these whatever game they serve. The contract is core/refii_mod_requests.h.

#include "core/refii_mod_requests.h"

#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <string_view>

#include <rex/logging.h>

#include "core/mod_values.h"
#include "core/payload_bridge.h"

// ---------------------------------------------------------------------------------------------
// Effects: a request for Godot, posted on the host-only kind REFII_MOD_HOST_FX_KIND.
// ---------------------------------------------------------------------------------------------

namespace {

/// The payload's ids are 1 .. 2^23-1; Godot's own play_fx counts from 2^23 (Engine_FxPlayer), so
/// every id is exact as a Lua number (a 32-bit float) and no surface's id collides with another's.
constexpr uint32_t kFxIdSpan = 0x7FFFFFu;
std::atomic<uint32_t> g_fx_ids{0};

/// The fields text RefiiHost parses: key=value, a 'quoted' text never carrying a quote.
uint32_t PostPlayFx(const char* name, const float* xyz, uint32_t handle, const refii_mod_fx_options* options) {
  if (name == nullptr || name[0] == 0) {
    return 0;
  }
  char clean[128];
  size_t n = 0;
  for (; name[n] != 0 && n + 1 < sizeof(clean); ++n) {
    const char c = name[n];
    clean[n] = (c == '\'' || c == ',' || static_cast<unsigned char>(c) < 0x20) ? '_' : c;
  }
  clean[n] = 0;
  const refii_mod_fx_options o = options != nullptr ? *options : refii_mod_fx_default_options();
  const float origin[3] = {xyz != nullptr ? xyz[0] : 0.0f, xyz != nullptr ? xyz[1] : 0.0f, xyz != nullptr ? xyz[2] : 0.0f};
  const uint32_t id = (g_fx_ids.fetch_add(1u, std::memory_order_relaxed) % kFxIdSpan) + 1u;
  // The helper point (options->at), as a quoted field: a name never carries a quote or a comma.
  char at[96];
  size_t m = 0;
  for (const char* c = o.at; c != nullptr && *c != 0 && m + 1 < sizeof(at); ++c) {
    at[m++] = (*c == '\'' || *c == ',' || static_cast<unsigned char>(*c) < 0x20) ? '_' : *c;
  }
  at[m] = 0;
  char fields[640];
  const int written = std::snprintf(
      fields, sizeof(fields),
      "op='play',id=%u,name='%s',x=%.9g,y=%.9g,z=%.9g,entity=%u,duration=%.9g,scale=%.9g,r=%.9g,g=%.9g,b=%.9g,"
      "intensity=%.9g,loop=%d,ox=%.9g,oy=%.9g,oz=%.9g,at='%s'",
      id, clean, origin[0], origin[1], origin[2], handle, o.duration, o.scale, o.tint[0], o.tint[1], o.tint[2],
      o.intensity, o.loop != 0 ? 1 : 0, o.offset[0], o.offset[1], o.offset[2], at);
  if (written <= 0 || static_cast<size_t>(written) >= sizeof(fields)) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_FX_KIND, std::string_view(fields, static_cast<size_t>(written)));
  return id;
}

}  // namespace

extern "C" uint32_t refii_mod_play_fx(const char* name, const float* xyz, const refii_mod_fx_options* options) {
  return PostPlayFx(name, xyz, 0u, options);
}

extern "C" uint32_t refii_mod_play_fx_on(const char* name, uint32_t entity, const refii_mod_fx_options* options) {
  if (entity == 0) {
    return 0;
  }
  return PostPlayFx(name, nullptr, refii_bridge_mod_handle(entity), options);
}

/// The Lua surfaces' form: the entity is already a handle (Refii.play_fx_on).
extern "C" uint32_t refii_mod_play_fx_handle(const char* name, const float* xyz, uint32_t handle,
                                             const refii_mod_fx_options* options) {
  return PostPlayFx(name, xyz, handle, options);
}

extern "C" uint32_t refii_mod_play_fx_at(const char* name, uint32_t entity, const char* helper,
                                         const refii_mod_fx_options* options) {
  refii_mod_fx_options o = options != nullptr ? *options : refii_mod_fx_default_options();
  o.at = helper;
  return refii_mod_play_fx_on(name, entity, &o);
}

extern "C" void refii_mod_stop_fx(uint32_t id) {
  if (id == 0) {
    return;
  }
  char fields[64];
  const int written = std::snprintf(fields, sizeof(fields), "op='stop',id=%u", id);
  if (written > 0 && static_cast<size_t>(written) < sizeof(fields)) {
    refii::mods::PostEvent(REFII_MOD_HOST_FX_KIND, std::string_view(fields, static_cast<size_t>(written)));
  }
}

// ---------------------------------------------------------------------------------------------
// Lights: a request for Godot on the effect kind (Engine_ModLights reads op='light' / 'light_off')
// ---------------------------------------------------------------------------------------------

namespace {

/// The payload's light ids share the effects' span (1 .. 2^23-1); Godot's own count from 2^23.
std::atomic<uint32_t> g_light_ids{0};

/// The light request as the bus carries it: the entity as a handle (0 for the world point).
uint32_t PostLight(uint32_t handle, const float* xyz, const float* rgb, float energy, float range,
                   const refii_mod_light_options* options) {
  if (rgb == nullptr) {
    return 0;
  }
  const uint32_t id = (g_light_ids.fetch_add(1u, std::memory_order_relaxed) % kFxIdSpan) + 1u;
  const float origin[3] = {xyz != nullptr ? xyz[0] : 0.0f, xyz != nullptr ? xyz[1] : 0.0f, xyz != nullptr ? xyz[2] : 0.0f};
  const float offset[3] = {options != nullptr ? options->offset[0] : 0.0f, options != nullptr ? options->offset[1] : 0.0f,
                           options != nullptr ? options->offset[2] : 0.0f};
  char at[96];
  size_t m = 0;
  for (const char* c = options != nullptr ? options->at : nullptr; c != nullptr && *c != 0 && m + 1 < sizeof(at); ++c) {
    at[m++] = (*c == '\'' || *c == ',' || static_cast<unsigned char>(*c) < 0x20) ? '_' : *c;
  }
  at[m] = 0;
  // The spot fields (dx dy dz cone atten) ride every request; cone 0 is the omni light.
  const float direction[3] = {options != nullptr ? options->direction[0] : 0.0f,
                              options != nullptr ? options->direction[1] : 0.0f,
                              options != nullptr ? options->direction[2] : 0.0f};
  char fields[512];
  const int written = std::snprintf(
      fields, sizeof(fields),
      "op='light',id=%u,entity=%u,x=%.9g,y=%.9g,z=%.9g,r=%.9g,g=%.9g,b=%.9g,energy=%.9g,range=%.9g,ox=%.9g,oy=%.9g,oz=%.9g,"
      "at='%s',flicker=%.9g,dx=%.9g,dy=%.9g,dz=%.9g,cone=%.9g,atten=%.9g",
      id, handle, origin[0], origin[1], origin[2], rgb[0], rgb[1], rgb[2], energy, range, offset[0], offset[1], offset[2], at,
      options != nullptr ? options->flicker : 0.0f, direction[0], direction[1], direction[2],
      options != nullptr ? options->cone : 0.0f, options != nullptr ? options->attenuation : 0.0f);
  if (written <= 0 || static_cast<size_t>(written) >= sizeof(fields)) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_FX_KIND, std::string_view(fields, static_cast<size_t>(written)));
  return id;
}

}  // namespace

extern "C" uint32_t refii_mod_light(uint32_t entity, const float* xyz, const float* rgb, float energy, float range,
                                    const refii_mod_light_options* options) {
  return PostLight(entity != 0u ? refii_bridge_mod_handle(entity) : 0u, entity != 0u ? nullptr : xyz, rgb, energy, range, options);
}

/// The Lua surfaces' form: the entity is already a handle (Refii.light).
extern "C" uint32_t refii_mod_light_handle(uint32_t handle, const float* xyz, const float* rgb, float energy, float range,
                                           const refii_mod_light_options* options) {
  return PostLight(handle, handle != 0u ? nullptr : xyz, rgb, energy, range, options);
}

extern "C" void refii_mod_light_remove(uint32_t id) {
  if (id == 0) {
    return;
  }
  char fields[64];
  const int written = std::snprintf(fields, sizeof(fields), "op='light_off',id=%u", id);
  if (written > 0 && static_cast<size_t>(written) < sizeof(fields)) {
    refii::mods::PostEvent(REFII_MOD_HOST_FX_KIND, std::string_view(fields, static_cast<size_t>(written)));
  }
}

// ---------------------------------------------------------------------------------------------
// The UI: a request for Godot, posted on the host-only kind REFII_MOD_HOST_UI_KIND
// ---------------------------------------------------------------------------------------------

namespace {

/// A text for the bus's quoted field: no quote (the field's end) and no tab or line feed (the
/// record's); a line break crosses as the two characters \n, which Godot reads back.
std::string QuotedField(const char* text, size_t limit) {
  std::string out;
  for (const char* c = text; c != nullptr && *c != 0 && out.size() < limit; ++c) {
    if (*c == '\'') {
      out += '`';
    } else if (*c == '\n') {
      out += "\\n";
    } else if (static_cast<unsigned char>(*c) < 0x20u) {
      out += ' ';
    } else {
      out += *c;
    }
  }
  return out;
}

/// The payload's box ids are 1 .. 2^23-1; Godot's own message_box counts from 2^23 (Engine_UiModRequest).
std::atomic<uint32_t> g_box_ids{0};

int32_t ClampPlayer(int32_t player) { return player == 1 || player == 2 ? player : 0; }

}  // namespace

extern "C" int32_t refii_mod_open_menu(int32_t player, const char* name) {
  const std::string menu = QuotedField(name, 96);
  if (menu.empty()) {
    return 0;
  }
  const std::string fields = "op='open_menu',player=" + std::to_string(ClampPlayer(player)) + ",name='" + menu + "'";
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return 1;
}

extern "C" uint32_t refii_mod_message_box(int32_t player, const char* title, const char* text, const char* accept,
                                          const char* cancel) {
  const std::string head = QuotedField(title, 128);
  const std::string body = QuotedField(text, 480);
  if (head.empty() && body.empty()) {
    return 0;
  }
  const uint32_t id = (g_box_ids.fetch_add(1u, std::memory_order_relaxed) % kFxIdSpan) + 1u;
  const std::string fields = "op='message_box',id=" + std::to_string(id) + ",player=" +
                             std::to_string(ClampPlayer(player)) + ",title='" + head + "',text='" + body +
                             "',accept='" + QuotedField(accept, 64) + "',cancel='" + QuotedField(cancel, 64) +
                             "',buttons=" + (cancel != nullptr ? "2" : "1");
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return id;
}

extern "C" uint32_t refii_mod_notice(int32_t player, const char* title, const char* text, float seconds) {
  const std::string head = QuotedField(title, 128);
  const std::string body = QuotedField(text, 480);
  if (head.empty() && body.empty()) {
    return 0;
  }
  const uint32_t id = (g_box_ids.fetch_add(1u, std::memory_order_relaxed) % kFxIdSpan) + 1u;
  const float shown = std::isfinite(seconds) && seconds > 0.0f ? seconds : 0.0f;
  char length[32];
  std::snprintf(length, sizeof(length), "%.3f", static_cast<double>(shown));
  const std::string fields = "op='notice',id=" + std::to_string(id) + ",player=" + std::to_string(ClampPlayer(player)) +
                             ",title='" + head + "',text='" + body + "',seconds=" + length;
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return id;
}

extern "C" int32_t refii_mod_text(const char* text, char* out, uint32_t capacity) {
  if (text == nullptr || out == nullptr || capacity == 0u) {
    return -1;
  }
  return refii_bridge_mod_text(text, std::strlen(text), out, capacity);
}

extern "C" uint32_t refii_mod_menu_box(int32_t player, const char* title, const char* text, const char* const* options,
                                       int32_t count, int32_t cancelable) {
  constexpr int32_t kMaxOptions = 16;  // Engine_UiModRequest.MaxMenuOptions
  const int32_t n = options == nullptr || count < 0 ? 0 : (count > kMaxOptions ? kMaxOptions : count);
  if (n == 0) {
    return 0;
  }
  const uint32_t id = (g_box_ids.fetch_add(1u, std::memory_order_relaxed) % kFxIdSpan) + 1u;
  std::string fields = "op='menu_box',id=" + std::to_string(id) + ",player=" + std::to_string(ClampPlayer(player)) +
                       ",title='" + QuotedField(title, 128) + "',text='" + QuotedField(text, 480) +
                       "',cancel=" + (cancelable != 0 ? "1" : "0") + ",count=" + std::to_string(n);
  for (int32_t i = 0; i < n; ++i) {
    fields += ",o" + std::to_string(i) + "='" + QuotedField(options[i], 96) + "'";
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return id;
}

// ---------------------------------------------------------------------------------------------
// HUD: markers, fades and meters — requests for Godot on the UI kind (Engine_UiModBoxes hands
// them to Engine_HudModMarkers, Engine_UiModFade and Engine_HudModMeters). Never an event.
// ---------------------------------------------------------------------------------------------

namespace {

/// Marker ids: the payload's 1 .. 2^23-1, Godot's from 2^23 (Engine_HudModMarkers.FirstHostId).
std::atomic<uint32_t> g_mark_ids{0};

std::string Float(float value) {
  char text[32];
  std::snprintf(text, sizeof(text), "%.9g", static_cast<double>(std::isfinite(value) ? value : 0.0f));
  return text;
}

}  // namespace

extern "C" uint32_t refii_mod_mark(int32_t player, uint32_t entity, const float* xyz, const char* icon,
                                   const char* text) {
  const std::string picture = QuotedField(icon, 160);
  const std::string line = QuotedField(text, 160);
  if (picture.empty() && line.empty()) {
    return 0;
  }
  const uint32_t id = (g_mark_ids.fetch_add(1u, std::memory_order_relaxed) % kFxIdSpan) + 1u;
  const uint32_t handle = entity != 0u ? refii_bridge_mod_handle(entity) : 0u;
  const std::string fields = "op='mark',id=" + std::to_string(id) + ",player=" + std::to_string(ClampPlayer(player)) +
                             ",entity=" + std::to_string(handle) + ",x=" + Float(xyz != nullptr ? xyz[0] : 0.0f) +
                             ",y=" + Float(xyz != nullptr ? xyz[1] : 0.0f) + ",z=" + Float(xyz != nullptr ? xyz[2] : 0.0f) +
                             ",icon='" + picture + "',text='" + line + "'";
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return id;
}

extern "C" void refii_mod_unmark(uint32_t id) {
  if (id == 0u) {
    return;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='unmark',id=" + std::to_string(id));
}

extern "C" uint32_t refii_mod_world_meter(int32_t player, uint32_t entity, const float* xyz, float value, float max,
                                          const char* style) {
  if (!(max > 0.0f) || !std::isfinite(max)) {
    return 0;
  }
  const uint32_t id = (g_mark_ids.fetch_add(1u, std::memory_order_relaxed) % kFxIdSpan) + 1u;
  const uint32_t handle = entity != 0u ? refii_bridge_mod_handle(entity) : 0u;
  const std::string fields = "op='mark',id=" + std::to_string(id) + ",player=" + std::to_string(ClampPlayer(player)) +
                             ",entity=" + std::to_string(handle) + ",x=" + Float(xyz != nullptr ? xyz[0] : 0.0f) +
                             ",y=" + Float(xyz != nullptr ? xyz[1] : 0.0f) + ",z=" + Float(xyz != nullptr ? xyz[2] : 0.0f) +
                             ",icon='',text='',meter=1,value=" + Float(value) + ",max=" + Float(max) + ",style='" +
                             QuotedField(style, 32) + "'";
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return id;
}

extern "C" int32_t refii_mod_mark_meter(uint32_t id, float value, float max, const char* style) {
  if (id == 0u) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='mark_meter',id=" + std::to_string(id) + ",value=" + Float(value) +
                                                     ",max=" + Float(max) + ",style='" + QuotedField(style, 32) + "'");
  return 1;
}

extern "C" int32_t refii_mod_fade(int32_t player, int32_t to_black, float seconds) {
  const std::string fields = "op='fade',player=" + std::to_string(ClampPlayer(player)) + ",to=" +
                             (to_black != 0 ? "1" : "0") + ",seconds=" + Float(seconds > 0.0f ? seconds : 0.0f);
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return 1;
}

extern "C" int32_t refii_mod_meter(int32_t player, const char* id, const char* label, float value, float max,
                                   const char* style) {
  const std::string key = QuotedField(id, 96);
  if (key.empty()) {
    return 0;
  }
  const std::string fields = "op='meter',player=" + std::to_string(ClampPlayer(player)) + ",id='" + key + "',label='" +
                             QuotedField(label, 128) + "',value=" + Float(value) + ",max=" + Float(max) + ",style='" +
                             QuotedField(style, 32) + "'";
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return 1;
}

extern "C" int32_t refii_mod_meter_remove(const char* id) {
  const std::string key = QuotedField(id, 96);
  if (key.empty()) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='meter_remove',id='" + key + "'");
  return 1;
}

// Input claims and HUD overrides: state Godot's pad publish and HUD feed read (ModInput,
// Engine_UiHudOverrides), posted on the host-only UI kind like the meters.

extern "C" int32_t refii_mod_input_claim(int32_t player, const char* action, int32_t on) {
  const std::string name = QuotedField(action, 96);
  if (name.empty()) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='input_claim',player=" + std::to_string(ClampPlayer(player)) +
                                                     ",action='" + name + "',on=" + (on != 0 ? "1" : "0"));
  return 1;
}

extern "C" int32_t refii_mod_hud_icon(int32_t player, const char* element, const char* texture) {
  const std::string name = QuotedField(element, 96);
  if (name.empty()) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='hud_icon',player=" + std::to_string(ClampPlayer(player)) +
                                                     ",element='" + name + "',texture='" + QuotedField(texture, 160) + "'");
  return 1;
}

extern "C" int32_t refii_mod_hud_slide(int32_t player, const char* element, int32_t slide) {
  const std::string name = QuotedField(element, 96);
  if (name.empty() || slide < 0) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='hud_slide',player=" + std::to_string(ClampPlayer(player)) +
                                                     ",element='" + name + "',slide=" + std::to_string(slide));
  return 1;
}

extern "C" int32_t refii_mod_hud_hold(int32_t player, const char* piece, int32_t on) {
  const std::string name = QuotedField(piece, 96);
  if (name.empty()) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='hud_hold',player=" + std::to_string(ClampPlayer(player)) +
                                                     ",piece='" + name + "',on=" + (on != 0 ? "1" : "0"));
  return 1;
}

// A part of an entity hidden in Godot's drawing (Refii.Entities.Engine_HiddenParts), by the entity's
// handle and the player whose view loses it (0 every view): Godot owns the skeleton the part is a
// bone of, and the views.
extern "C" int32_t refii_mod_hide_part_for(uint32_t entity, const char* part, int32_t on, int32_t player) {
  const std::string name = QuotedField(part, 96);
  const uint32_t handle = entity != 0u ? refii_bridge_mod_handle(entity) : 0u;
  if (name.empty() || handle == 0u || player < 0 || player > 2) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='hide_part',entity=" + std::to_string(handle) + ",part='" + name +
                                                     "',on=" + (on != 0 ? "1" : "0") +
                                                     ",player=" + std::to_string(player));
  return 1;
}

extern "C" int32_t refii_mod_hide_part(uint32_t entity, const char* part, int32_t on) {
  return refii_mod_hide_part_for(entity, part, on, 0);
}

// Presses: state Godot's pad publish ORs into the player's pad (ModInputPresses), on the UI kind
// like the claims. A press is one player's pad: 1 or 2, never both.

extern "C" int32_t refii_mod_press(int32_t player, const char* button, int32_t held) {
  const std::string name = QuotedField(button, 32);
  if (name.empty() || (player != 1 && player != 2)) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='press',player=" + std::to_string(player) + ",button='" + name +
                                                     "',held=" + std::to_string(held < 0 ? 0 : (held > 255 ? 255 : held)));
  return 1;
}

extern "C" int32_t refii_mod_tap(int32_t player, const char* button, int32_t ticks) {
  const std::string name = QuotedField(button, 32);
  if (name.empty() || (player != 1 && player != 2) || ticks < 1) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='tap',player=" + std::to_string(player) + ",button='" + name +
                                                     "',ticks=" + std::to_string(ticks > 600 ? 600 : ticks));
  return 1;
}

// ---------------------------------------------------------------------------------------------
// Menu rows and host screens: a request for Godot, posted on the host-only kind
// REFII_MOD_HOST_MENU_KIND (Engine_UiModRows reads it)
// ---------------------------------------------------------------------------------------------

namespace {

/// One bus record's share of a rows text: the record is at most 2048 bytes (the runtime's
/// kMaxFieldsLength, src/runtime/src/core/mod_values.cpp) with the head and the menu name around it.
constexpr size_t kRowsChunk = 1800;
std::atomic<uint32_t> g_rows_seq{0};

}  // namespace

extern "C" int32_t refii_mod_set_menu_rows(int32_t player, const char* menu, const char* rows) {
  const std::string list = QuotedField(menu, 96);
  if (list.empty()) {
    return 0;
  }
  // The whole text as the bus carries it; past REFII_MOD_MENU_ROWS_MAX it is cut at the last whole
  // row (an unescaped ';') and the mod errors channel says so — never a silent truncation.
  std::string text = QuotedField(rows, REFII_MOD_MENU_ROWS_MAX + 1);
  if (text.size() > REFII_MOD_MENU_ROWS_MAX) {
    size_t cut = REFII_MOD_MENU_ROWS_MAX;
    while (cut > 0 && !(text[cut] == ';' && (cut == 0 || text[cut - 1] != '\\'))) {
      --cut;
    }
    text.resize(cut);
    char error[256];
    std::snprintf(error, sizeof(error), "set_menu_rows '%s': the rows are over %d bytes; cut to %zu at the last whole row",
                  list.c_str(), REFII_MOD_MENU_ROWS_MAX, cut);
    refii_bridge_mod_error_post(error, std::strlen(error));
    REXLOG_WARN("[mods] {}", error);
  }
  // In parts: Godot keeps them by seq and applies the rows once every part has arrived.
  const size_t parts = text.empty() ? 1u : (text.size() + kRowsChunk - 1) / kRowsChunk;
  const uint32_t seq = g_rows_seq.fetch_add(1u, std::memory_order_relaxed) + 1u;
  for (size_t part = 0; part < parts; ++part) {
    const std::string fields = "op='menu_rows',player=" + std::to_string(ClampPlayer(player)) + ",menu='" + list +
                               "',seq=" + std::to_string(seq) + ",part=" + std::to_string(part) +
                               ",parts=" + std::to_string(parts) + ",rows='" +
                               text.substr(part * kRowsChunk, kRowsChunk) + "'";
    refii::mods::PostEvent(REFII_MOD_HOST_MENU_KIND, fields);
  }
  return 1;
}

extern "C" int32_t refii_mod_open_screen(int32_t player, uint32_t screen) {
  if (screen == 0u) {
    return 0;
  }
  const std::string fields =
      "op='open_screen',player=" + std::to_string(ClampPlayer(player)) + ",screen=" + std::to_string(screen);
  refii::mods::PostEvent(REFII_MOD_HOST_MENU_KIND, fields);
  return 1;
}
