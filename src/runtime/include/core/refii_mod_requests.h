// refii — the contract of a mod's requests for Godot, shared by every title (implemented in
// src/payload/src/core/mod_requests.cpp, compiled into every title's payload): the options an effect
// and a light take, and the C entries that post effects, lights, boxes, notices, menus, marks,
// meters, fades, HUD and input requests and menu rows on the bus's host-only kinds. Godot acts on
// them (Engine_FxPlayer, Engine_ModLights, Engine_UiModBoxes, Engine_UiModRows); none reads the
// guest. Fable 2's core/refii_mod.h includes this; a Fable 3 native calls the same entries.

#pragma once

#include <cstdint>

/// The host-only kinds these requests cross under (never bus events; posted whatever the mask says).
#ifndef REFII_MOD_HOST_FX_KIND
#define REFII_MOD_HOST_FX_KIND 1001
#endif
#ifndef REFII_MOD_HOST_UI_KIND
#define REFII_MOD_HOST_UI_KIND 1002
#endif
#ifndef REFII_MOD_HOST_MENU_KIND
#define REFII_MOD_HOST_MENU_KIND 1003
#endif

/// The most bytes of rows refii_mod_set_menu_rows carries; past it the rows are cut at the last whole row.
#define REFII_MOD_MENU_ROWS_MAX 32768

/// How a played effect is shown. Null options mean refii_mod_fx_default_options().
typedef struct refii_mod_fx_options {
  /// Seconds before it is stopped; 0 plays until refii_mod_stop_fx (or, with loop 0, until the
  /// effect has run out).
  float duration;
  /// A size multiplier on every position, motion and size of the effect, as a node's scale is.
  float scale;
  /// A colour multiplier on every particle (1, 1, 1 untinted).
  float tint[3];
  /// The instance intensity, an alpha term (1 as authored).
  float intensity;
  /// 1 repeats the effect, 0 plays it once.
  int32_t loop;
  /// From the entity (refii_mod_play_fx_on), along the ENTITY'S OWN axes: an effect on an entity
  /// stands in its frame — the effect's axes are the entity's — and turns with it. An entity with
  /// no drawn body of its own (a vortex, a fireball's shot) is followed by position only, the
  /// effect unrotated and the offset in the guest's world axes.
  float offset[3];
  /// A HELPER POINT of the entity's model (refii_mod_play_fx_on): the name of a `.mdl.gmd`
  /// attachment or node — a sword's "Weapon.Weapon.Top" / "Weapon.Weapon.Hold", a creature's
  /// "Character.Carry.Hand.Right" — and the effect stands there, in the helper's own frame (its
  /// position AND orientation when the gmd states one), `offset` along the helper's axes; on a
  /// rigged body it hangs on the bone the helper hangs from, so it follows the pose. Null or ""
  /// is the entity's own frame. An unknown name is warned once and the entity's frame used.
  /// Under the classic look the helper's REST-pose point is folded into the offset.
  const char* at;
} refii_mod_fx_options;

inline refii_mod_fx_options refii_mod_fx_default_options(void) {
  refii_mod_fx_options options{};
  options.scale = 1.0f;
  options.tint[0] = options.tint[1] = options.tint[2] = 1.0f;
  options.intensity = 1.0f;
  options.loop = 1;
  return options;
}

/// The options of refii_mod_light. Zero-initialise and set what you need.
typedef struct refii_mod_light_options {
  /// From the entity, along its own axes (or the helper's when `at` names one), as
  /// refii_mod_fx_options.offset; ignored for a world point.
  float offset[3];
  /// A helper point of the entity's model, as refii_mod_fx_options.at ("Weapon.Weapon.Top"): the
  /// light stands there, on a rigged body on the helper's bone. Null or "" is the entity's frame.
  const char* at;
  /// 0..1: a waver of the energy (a torch, a brazier); 0 is steady.
  float flicker;
  /// A SPOT LIGHT when `cone` is above 0: the light shines along `direction` — in the entity's own
  /// axes (or the helper's when `at` names one), the world's for a world point; any length — over a
  /// cone `cone` degrees from its axis to its edge (the half-angle; at most 89). Zero: an omni light.
  float direction[3];
  float cone;
  /// The cone's edge falloff, a spot's only: 1 (and 0) is linear from the edge to the axis — the
  /// title's own spot form, 1/(1-cos) — higher tightens the beam. Modern only; the classic list
  /// has the one form.
  float attenuation;
} refii_mod_light_options;

extern "C" uint32_t refii_mod_play_fx(const char* name, const float* xyz, const refii_mod_fx_options* options);
extern "C" uint32_t refii_mod_play_fx_on(const char* name, uint32_t entity, const refii_mod_fx_options* options);
extern "C" uint32_t refii_mod_play_fx_handle(const char* name, const float* xyz, uint32_t handle, const refii_mod_fx_options* options);
extern "C" uint32_t refii_mod_play_fx_at(const char* name, uint32_t entity, const char* helper, const refii_mod_fx_options* options);
extern "C" void refii_mod_stop_fx(uint32_t id);
extern "C" uint32_t refii_mod_light(uint32_t entity, const float* xyz, const float* rgb, float energy, float range, const refii_mod_light_options* options);
extern "C" uint32_t refii_mod_light_handle(uint32_t handle, const float* xyz, const float* rgb, float energy, float range, const refii_mod_light_options* options);
extern "C" void refii_mod_light_remove(uint32_t id);
extern "C" int32_t refii_mod_open_menu(int32_t player, const char* name);
extern "C" uint32_t refii_mod_message_box(int32_t player, const char* title, const char* text, const char* accept, const char* cancel);
extern "C" uint32_t refii_mod_notice(int32_t player, const char* title, const char* text, float seconds);
extern "C" int32_t refii_mod_text(const char* text, char* out, uint32_t capacity);
extern "C" uint32_t refii_mod_menu_box(int32_t player, const char* title, const char* text, const char* const* options, int32_t count, int32_t cancelable);
extern "C" uint32_t refii_mod_mark(int32_t player, uint32_t entity, const float* xyz, const char* icon, const char* text);
extern "C" void refii_mod_unmark(uint32_t id);
extern "C" uint32_t refii_mod_world_meter(int32_t player, uint32_t entity, const float* xyz, float value, float max, const char* style);
extern "C" int32_t refii_mod_mark_meter(uint32_t id, float value, float max, const char* style);
extern "C" int32_t refii_mod_fade(int32_t player, int32_t to_black, float seconds);
extern "C" int32_t refii_mod_meter(int32_t player, const char* id, const char* label, float value, float max, const char* style);
extern "C" int32_t refii_mod_meter_remove(const char* id);
extern "C" int32_t refii_mod_input_claim(int32_t player, const char* action, int32_t on);
extern "C" int32_t refii_mod_hud_icon(int32_t player, const char* element, const char* texture);
extern "C" int32_t refii_mod_hud_slide(int32_t player, const char* element, int32_t slide);
extern "C" int32_t refii_mod_hud_hold(int32_t player, const char* piece, int32_t on);
extern "C" int32_t refii_mod_hide_part_for(uint32_t entity, const char* part, int32_t on, int32_t player);
extern "C" int32_t refii_mod_hide_part(uint32_t entity, const char* part, int32_t on);
extern "C" int32_t refii_mod_press(int32_t player, const char* button, int32_t held);
extern "C" int32_t refii_mod_tap(int32_t player, const char* button, int32_t ticks);
extern "C" int32_t refii_mod_set_menu_rows(int32_t player, const char* menu, const char* rows);
extern "C" int32_t refii_mod_open_screen(int32_t player, uint32_t screen);
