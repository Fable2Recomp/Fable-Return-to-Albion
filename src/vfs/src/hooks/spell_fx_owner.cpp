// refii — WHICH SPELL A SPELL'S EFFECTS BELONG TO, as the guest states it in the spell's own update,
// and the classic plays riding a spell's entity placed in that same update.
//
// The live-effect feed (RefiiHostGetActiveFx) names each effect node's OWNER — the entity whose
// particle component (85) plays it, `comp85+4` — and what that entity is ATTACHED to
// (`comp85+0x20`). For a spell that is the spell only when the spell's own entity plays the effect
// or the effect entity was attached to it:
//
//   FIREBALL: the shot's effect is an FX entity ("TargetedShotFX0<level>", sub_828E5720 0x828e5874)
//     created by sub_825F81E0 with r6 = the shot entity (*(*(spell+0x14)), the SpellShotEntity
//     sub_828E5180 makes), which attaches it there (sub_825F8DD8 0x825f8284 -> sub_8256A0F0). The
//     feed's ATTACHED is the shot: stated, nothing to add here.
//   VORTEX: the ground FX ("VortexFXGround<surface>", sub_8275A3E8) is an FX entity created with
//     `li r6,0` (0x8275a4c8) — attached to nothing — kept in the vortex component's four weak refs
//     at +0x170.. and placed each tick by the component's update (sub_827590E0) through
//     sub_825F86F0 at the twister's point (+0x140, `li r28,0x140` 0x82759210 / `lvx128 v1,r31,r28`
//     0x82759238). Its feed owner is that FX entity, which no cast names.
//   BLADES: each sword record (640 bytes, sub_825A5EF8) keeps an FX entity at +0x10 (created with
//     `li r6,0`, sub_825A71E0 0x825a72c8, stored 0x825a7304) and a spell-owned instance at +0x48 —
//     the feed reports no owner for that one — and the record's update (sub_825A63A8, called per
//     record by the controller sub_825A3550 0x825a36cc) places both: the instance through
//     sub_832325A8 (0x825a660c) and the FX entity through sub_825F86F0 (0x825a6658). The sword
//     entity itself is the record's weak ref at +0 (*(*(record+4)), sub_82173E38).
//
// So the relation is taken where the guest states it: sub_825F86F0 (rex_FxEntity_PlaceAt: r3 the
// FX entity, v1 the point) and the instance setter (particle_transform.cpp) called from INSIDE the
// vortex's update (r3 the component; its entity at +4, 0x82759bd8) or a sword record's update (r3
// the record) name an effect of THAT spell's entity. Each such (effect, spell entity) pair goes to
// Godot on the host-only fx kind as `op='ride',fx=<address>,on=<address>` (Engine_FxPlayer), where
// the `with` rule binds an effect whose owner, or whose instance, rides a play's entity. A pair is
// restated at most once a second while the guest keeps stating it; Godot lets a pair go once the
// guest has stopped (the FX entity or the spell gone).
//
// THE PLAYS RIDE THE SPELL'S OWN MOVE. The vortex's update moves its entity to the twister at its
// end (sub_82759928 0x8275925c: `stvx128 v127,r29,r25` +0x140, then the entity's placement
// interface `lwz r3,0x7C(r11)` / vtable +0x64 at 0x82759bdc..0x82759bf4) — the entity travels with
// the twister. A classic play riding that entity (mod_commands.cpp guest_fx) was placed once per
// tick from the command pump, outside the update; it is now also placed at the end of the update
// that moved the entity, as the guest's own ground effects are placed inside it. A sword's update
// places its effects the same way, so a play riding a sword is placed after it.
//
// TU1 / disc (versions/*/refii_config.toml; matched by size and an identical instruction sequence,
// and by their callers: sub_825F86F0's three callers are these two updates and sub_8294D990 in both):
//   rex_VortexComponent_Update  0x827590E0 / 0x82730AC0 (the component's vtable, 0x820D41A8)
//   rex_DamoclesSword_Update    0x825A63A8 / 0x82557190
//   rex_FxEntity_PlaceAt        0x825F86F0 / 0x824B8938

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <string_view>
#include <unordered_map>

#include "core/mod_values.h"
#include "core/refii_mod.h"
#include "core/refii_mod_events.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

namespace refii::vfs {
void PlaceFxRiding(PPCContext& ctx, uint8_t* base, uint32_t entity);
}  // namespace refii::vfs

namespace {

constexpr uint32_t kComponentOwner = 4u;
constexpr uint32_t kRecordRefBody = 4u;
constexpr double kRestateSeconds = 1.0;
constexpr double kForgetSeconds = 5.0;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

/// The spell entity whose update is running (game thread only), 0 outside one.
uint32_t g_scope = 0u;

/// Inside sub_825F86F0: its own instance placement is the FX entity's, already related by owner.
bool g_in_place = false;

struct Stated {
  uint32_t on = 0u;
  double posted = -1.0;
  double seen = 0.0;
};

std::unordered_map<uint32_t, Stated> g_stated;

double Now() {
  using clock = std::chrono::steady_clock;
  static const clock::time_point start = clock::now();
  return std::chrono::duration<double>(clock::now() - start).count();
}

void Post(uint32_t fx, uint32_t on) {
  char fields[96];
  const int written = std::snprintf(fields, sizeof(fields), "op='ride',fx=%u,on=%u", fx, on);
  if (written > 0 && static_cast<size_t>(written) < sizeof(fields)) {
    refii::mods::PostEvent(REFII_MOD_HOST_FX_KIND, std::string_view(fields, static_cast<size_t>(written)));
  }
}

/// `fx` (an FX entity, or an effect instance) is one of `on`'s effects: told to Godot when new or
/// changed, and again each kRestateSeconds while the guest keeps stating it.
void Note(uint32_t fx, uint32_t on) {
  if (fx == 0u || on == 0u || fx == on) {
    return;
  }
  const double now = Now();
  Stated& s = g_stated[fx];
  s.seen = now;
  if (s.on != on || now - s.posted >= kRestateSeconds) {
    s.on = on;
    s.posted = now;
    Post(fx, on);
  }
  if (g_stated.size() > 256u) {
    for (auto it = g_stated.begin(); it != g_stated.end();) {
      it = now - it->second.seen > kForgetSeconds ? g_stated.erase(it) : std::next(it);
    }
  }
}

/// Runs `original` with `entity` as the spell in scope, then places the plays riding it.
template <typename Original>
void InScope(PPCContext& ctx, uint8_t* base, uint32_t entity, Original original) {
  const uint32_t outer = g_scope;
  g_scope = Plausible(entity) ? entity : 0u;
  original(ctx, base);
  g_scope = outer;
  if (Plausible(entity) && refii_mod_entity_alive(base, entity) != 0) {
    refii::vfs::PlaceFxRiding(ctx, base, entity);
  }
}

}  // namespace

namespace refii::vfs {

/// From the instance setter (particle_transform.cpp): an instance placed by a spell's own update,
/// outside sub_825F86F0, is that spell's (the Blades' spell-owned instance at record+0x48).
void NoteSpellFxInstancePlaced(uint32_t instance) {
  if (g_scope != 0u && !g_in_place) {
    Note(instance, g_scope);
  }
}

}  // namespace refii::vfs

// The vortex component's update: r3 the component, its entity at +4.
REX_EXTERN(rex_VortexComponent_Update) {
  REX_FUNC_PROLOGUE();
  const uint32_t component = ctx.r3.u32;
  const uint32_t entity = Plausible(component) ? refii::mods::LoadU32(base, component + kComponentOwner) : 0u;
  InScope(ctx, base, entity, [](PPCContext& c, uint8_t* b) { __imp__rex_VortexComponent_Update(c, b); });
}

// One Blades sword record's update: r3 the record, the sword entity behind its weak ref at +0.
REX_EXTERN(rex_DamoclesSword_Update) {
  REX_FUNC_PROLOGUE();
  const uint32_t record = ctx.r3.u32;
  const uint32_t body = Plausible(record) ? refii::mods::LoadU32(base, record + kRecordRefBody) : 0u;
  const uint32_t entity = Plausible(body) ? refii::mods::LoadU32(base, body) : 0u;
  InScope(ctx, base, entity, [](PPCContext& c, uint8_t* b) { __imp__rex_DamoclesSword_Update(c, b); });
}

// An FX entity and its particle instance placed at v1: r3 the FX entity.
REX_EXTERN(rex_FxEntity_PlaceAt) {
  REX_FUNC_PROLOGUE();
  if (g_scope != 0u) {
    Note(ctx.r3.u32, g_scope);
  }
  const bool outer = g_in_place;
  g_in_place = true;
  __imp__rex_FxEntity_PlaceAt(ctx, base);
  g_in_place = outer;
}
