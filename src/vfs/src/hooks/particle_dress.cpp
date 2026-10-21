#include "vfs_hooks_private.h"

#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <vector>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/host_asset_reader.h"
#include "core/refii_mod.h"
#include "hooks/ppc_externs.h"

// THE GUEST'S PARTICLE INSTANCES DRESSED BY THE MODS' EFFECTS, AND ITS BANK RELOADED LIVE.
//
// A mod effect (fx/effects/*.json) may state an effect-level `scale` and `tint`. The guest's
// effect record carries neither, so they are applied to every live instance of that effect where
// the guest itself scales and colours one (TU1; the disc build's functions are the same bodies,
// guest_addresses.inc):
//
// SCALE. `sub_83232290(instance, f1)` is the instance's scale setter behind
// ParticleAttacher.SetParticleScale (0x8276724c) and fourteen other callers: it walks the
// instance's elements and calls each element's vtable slot 11 (0x83232358 `lwz r10,0x2C(r11)`),
// which for an emitter is `sub_82A03C30`: `stfs f1,0xCC(r3)` / `stfs f1,0x19C(r3)`. The getter
// `sub_83232108` averages the emitters' +0x19C (0x832321e4). Every element starts at 1.0
// (`sub_8322F790` hands 1.0, 1.0 to `sub_83228588` at 0x8322f8d0). So an effect's authored
// scale is the instance's scale at construction, and a later SetParticleScale(s) from the game
// means s times it: the setter is hooked to multiply, the getter to divide, so a script reading
// the scale back and setting it again does not compound.
//
// TINT. There is no colour on an instance: the constructor `sub_83233300` writes only +48 = -1.0,
// +52 = 1.0 and +68 = 1.0 as floats. A particle's colour is its MATERIAL's keyframe table — the
// draw `sub_821CA108` loads it from `*(renderData+48)` (`lwz r11,0x30(r3)` region, the c337
// keyframe colours at 0x821ca1e0..) — and the material is the SYSTEM TYPE's, shared by every
// instance of every effect that uses that system:
//   emitter vtable slot 23 (+92) `sub_8322FA38` -> `sub_83228AB0(element+0x150, system)` keeps the
//   system at element+0x170 and builds the element's render data at element+0x168
//   (`sub_83228638`) from `*(system+56)`, the material, by the system's mode `*(system+32)`:
//   mode 1 quads `sub_83223A58` stores it at +48 (`a1[12] = a2`), mode 2 trails `sub_83222328`
//   at +64, mode 3 trails `sub_83221C70` at +44.
//   The material (`sub_83227BF0`: 192 bytes for modes 1 and 2, 148 for mode 3) holds its
//   keyframes as a vector at +4 (first +8, last +12, end +16): 40-byte keyframes for mode 1
//   (`sub_832467C0`, colour rgba at +0), 28-byte for modes 2 and 3 (`sub_83245998`,
//   `sub_83245768`, colour rgba at +0).
// So the per-instance colour path IS the render data's material pointer: an instance is tinted by
// pointing each of its emitters' render data at a copy of its material whose keyframe colours are
// multiplied. Copies are made once per (material, tint) and shared; the render side copies the
// pointer from the render data on every update (`sub_821C6F80`: `*(a3+48) = result[12]`), so the
// classic renderer and the guest-drawn groups under the modern look both draw it.
//
// THE BANK RELOAD. The guest reads particle_bank.bnk once, on the "Permanent Bank Mounter" thread
// (`sub_822F3898`, 0x822f3954..0x822f39cc):
//   sub_821F0240(path, &dword_834978C4)          the particle root, copied
//   sub_821DA9C0(path, "particle_bank.bnk")      appended
//   sub_82C6F870(file, path, 1, 2)                opened
//   sub_82214DD8(path)                            the string released
//   sub_82A22840(stream, file, 0x4000)            a buffered stream over it
//   sub_832361B8(stream)                          THE LOAD
//   stream->vtable = off_820F2B20; sub_82A22958(stream); stream->vtable = off_820F2B90;
//   sub_82C6EDD8(file)                            torn down
// `sub_832361B8` checks the "ParticleBankFile" magic, then each section loader CLEARS its
// registry and refills it: `sub_8322C848` materials (0x8349E640, cleared again at the end,
// 0x832362fc — load-time only), `sub_8322C438` systems (0x8349E65C), `sub_8322BFD8` entity types
// (0x8349E68C), `sub_8322FF90` effects (0x8349E56C, read by `sub_8322FDC8` at every spawn). The
// clear `sub_82313998` releases each node's smart pointer (node+16 {object, count block}); every
// live element holds its own counted reference to its type and system (`sub_8322A9B0`,
// `sub_83228AB0` ++count), so live instances keep the generation they were built from and every
// spawn after the reload builds from the new one. What a counted reference does not cover is the
// RAW material pointer the render data and its render-side twin copy: a material freed while its
// last instance's twin still draws would be read after free. So before the reload every
// registry entry is retained here (one more count), and released only once nothing else has
// held it for kIdleTicksBeforeRelease ticks — by then no twin can still name its material.
// Spawning and the manager update both run on the game thread this runs on (the fifteen
// `sub_83233300` callers are game components; `sub_83234C38` is reached from the world update).

PPC_EXTERN_FUNC(__imp__rex_CParticleSystemEntityInstance_Ctor);
PPC_EXTERN_FUNC(__imp__rex_CParticleSystemEntityInstance_SetScale);
PPC_EXTERN_FUNC(__imp__rex_CParticleSystemEntityInstance_GetScale);

namespace {

using refii::mods::LoadF32;
using refii::mods::LoadU32;
using refii::mods::StoreF32;
using refii::mods::StoreU32;

constexpr uint32_t kOperatorNew = 0x8221F258u;
constexpr uint32_t kOperatorDelete = 0x8221BD38u;
constexpr uint32_t kRefRelease = 0x821B7B18u;
constexpr uint32_t kSetScale = 0x83232290u;

constexpr uint32_t kParticleRoot = 0x834978C4u;
constexpr uint32_t kBankName = 0x820A1A88u;
constexpr uint32_t kStringCopy = 0x821F0240u;
constexpr uint32_t kStringAppend = 0x821DA9C0u;
constexpr uint32_t kStringRelease = 0x82214DD8u;
constexpr uint32_t kFileOpen = 0x82C6F870u;
constexpr uint32_t kStreamCtor = 0x82A22840u;
constexpr uint32_t kBankLoad = 0x832361B8u;
constexpr uint32_t kStreamVtable = 0x820F2B20u;
constexpr uint32_t kStreamDtor = 0x82A22958u;
constexpr uint32_t kStreamBaseVtable = 0x820F2B90u;
constexpr uint32_t kFileClose = 0x82C6EDD8u;

/// The registries the load clears and refills: effects, systems, entity types (the std::map
/// objects; +4 is the head node).
constexpr uint32_t kRegistries[] = {0x8349E56Cu, 0x8349E65Cu, 0x8349E68Cu};
constexpr uint32_t kSystemRegistry = 0x8349E65Cu;

constexpr uint32_t kElementsBegin = 12u;
constexpr uint32_t kElementsEnd = 16u;
constexpr uint32_t kElementNext = 0xF8u;
constexpr uint32_t kElementType = 0xD0u;
constexpr uint32_t kElementRenderData = 0x168u;
constexpr uint32_t kElementSystem = 0x170u;
constexpr uint32_t kSystemMode = 32u;
constexpr uint32_t kSystemMaterial = 56u;

constexpr uint32_t kIdleTicksBeforeRelease = 300u;

/// The table the host composes beside the guest's bank (Engine_FxModEffects.DressKey): one line
/// per mod effect with a scale or tint, `0xHASH scale r g b`; `spin 0xENTITY radiansPerSecond` per
/// mod emitter that spins and `align 0xSYSTEM x y z w` per mod system whose mesh particles are
/// born turned (both read by particle_motion.cpp).
constexpr const char* kDressKey = "refii/fx_effect_dress.txt";

/// The spin and align lines, published whole: the integrator's hook reads them once per particle
/// step, so it takes the current generation without a lock. A generation is never freed while the
/// guest runs (a reload makes one).
struct Motion {
  std::unordered_map<uint32_t, float> spin;
  std::unordered_map<uint32_t, std::array<float, 4>> align;
};

struct Dress {
  float scale = 1.0f;
  float tint[3] = {1.0f, 1.0f, 1.0f};
};

struct Copy {
  uint32_t root = 0;  // the bank's own material this copy descends from
  uint32_t address = 0;
};

struct Retained {
  uint32_t object = 0;
  uint32_t block = 0;
  uint32_t idle = 0;
  bool system = false;
};

std::mutex g_mutex;
std::unordered_map<uint32_t, Dress> g_dress;
bool g_dress_read = false;
/// Instances built with a mod scale (the address; re-stated at every construction, so a reused
/// address never keeps a dead instance's entry).
std::unordered_map<uint32_t, float> g_instance_scale;
/// Tinted materials by (material, tint bits).
std::unordered_map<std::string, Copy> g_copies;
std::vector<Retained> g_retained;
std::atomic<const Motion*> g_motion{nullptr};
std::vector<std::unique_ptr<Motion>> g_motion_generations;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

uint32_t Bits(float value) {
  uint32_t bits;
  std::memcpy(&bits, &value, sizeof(bits));
  return bits;
}

bool Call(PPCContext& work, uint8_t* base, uint32_t tu1) {
  return refii::mods::CallGuest(work, base, refii::guest::Address(tu1));
}

void ReadDressLocked() {
  g_dress.clear();
  g_dress_read = true;
  g_motion.store(nullptr, std::memory_order_release);
  std::vector<uint8_t> text;
  if (!refii::vfs::ReadHostAsset(kDressKey, text) || text.empty()) {
    return;
  }
  text.push_back(0);
  auto motion = std::make_unique<Motion>();
  const char* line = reinterpret_cast<const char*>(text.data());
  while (*line != 0) {
    unsigned int hash = 0;
    Dress d;
    float v[4] = {0.0f, 0.0f, 0.0f, 0.0f};
    if (std::strncmp(line, "spin ", 5) == 0) {
      if (std::sscanf(line + 5, "%x %f", &hash, &v[0]) == 2 && hash != 0 && v[0] == v[0] && v[0] != 0.0f) {
        motion->spin[hash] = v[0];
      }
    } else if (std::strncmp(line, "align ", 6) == 0) {
      if (std::sscanf(line + 6, "%x %f %f %f %f", &hash, &v[0], &v[1], &v[2], &v[3]) == 5 && hash != 0) {
        const float length = std::sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2] + v[3] * v[3]);
        if (length > 1e-6f) {
          motion->align[hash] = {v[0] / length, v[1] / length, v[2] / length, v[3] / length};
        }
      }
    } else if (std::sscanf(line, "%x %f %f %f %f", &hash, &d.scale, &d.tint[0], &d.tint[1], &d.tint[2]) == 5 &&
               hash != 0 && d.scale > 0.0001f) {
      g_dress[hash] = d;
    }
    const char* next = std::strchr(line, '\n');
    if (next == nullptr) {
      break;
    }
    line = next + 1;
  }
  REXLOG_INFO("Refii fx: {} mod effect(s) carry a scale or tint, {} emitter(s) a spin, {} system(s) a mesh alignment",
              g_dress.size(), motion->spin.size(), motion->align.size());
  if (!motion->spin.empty() || !motion->align.empty()) {
    g_motion.store(motion.get(), std::memory_order_release);
    g_motion_generations.push_back(std::move(motion));
  }
}

bool DressFor(uint32_t hash, Dress& out) {
  std::lock_guard lock(g_mutex);
  // Ready: the prefix read with a zero count answers whether Godot's reader is installed.
  if (!g_dress_read && refii_bridge_read_host_asset_prefix(kDressKey, std::strlen(kDressKey), 0u, nullptr, 0u, nullptr) != 0) {
    ReadDressLocked();
  }
  auto found = g_dress.find(hash);
  if (found == g_dress.end()) {
    return false;
  }
  out = found->second;
  return true;
}

float InstanceScale(uint32_t instance) {
  std::lock_guard lock(g_mutex);
  auto found = g_instance_scale.find(instance);
  return found == g_instance_scale.end() ? 1.0f : found->second;
}

uint32_t NextElement(uint8_t* base, uint32_t element) {
  const uint32_t block = LoadU32(base, element + kElementNext);
  return Plausible(block) ? LoadU32(base, block) : 0u;
}

/// An emitter element (kinds 0 and 9: the test `sub_83232290` makes at 0x83232304..0x8323233c
/// through the type's vtable slot 5).
bool IsEmitter(const PPCContext& ctx, uint8_t* base, uint32_t element) {
  const uint32_t type = LoadU32(base, element + kElementType);
  if (!Plausible(type)) {
    return false;
  }
  PPCContext work = refii::mods::Frame(ctx, 0x200u);
  if (!refii::mods::CallVirtual(work, base, type, 5u)) {
    return false;
  }
  return work.r3.u32 == 0u || work.r3.u32 == 9u;
}

uint32_t Allocate(const PPCContext& ctx, uint8_t* base, uint32_t bytes) {
  PPCContext work = refii::mods::Frame(ctx, 0x200u);
  work.r3.u64 = bytes;
  return Call(work, base, kOperatorNew) && Plausible(work.r3.u32) ? work.r3.u32 : 0u;
}

void Free(const PPCContext& ctx, uint8_t* base, uint32_t at) {
  if (!Plausible(at)) {
    return;
  }
  PPCContext work = refii::mods::Frame(ctx, 0x200u);
  work.r3.u32 = at;
  Call(work, base, kOperatorDelete);
}

std::string CopyKey(uint32_t material, const float* rgb) {
  char key[48];
  std::snprintf(key, sizeof(key), "%08X:%08X%08X%08X", material, Bits(rgb[0]), Bits(rgb[1]), Bits(rgb[2]));
  return key;
}

/// `material` with its keyframe colours multiplied by `rgb`: a shared copy, made on first use.
uint32_t Tinted(const PPCContext& ctx, uint8_t* base, uint32_t material, uint32_t mode, const float* rgb) {
  const uint32_t bytes = mode == 3u ? 148u : 192u;
  const uint32_t stride = mode == 1u ? 40u : 28u;
  const std::string key = CopyKey(material, rgb);
  uint32_t root = material;
  {
    std::lock_guard lock(g_mutex);
    auto found = g_copies.find(key);
    if (found != g_copies.end()) {
      return found->second.address;
    }
    for (const auto& [k, copy] : g_copies) {
      if (copy.address == material) {
        root = copy.root;
        break;
      }
    }
  }

  const uint32_t first = LoadU32(base, material + 8u);
  const uint32_t last = LoadU32(base, material + 12u);
  if (!Plausible(first) || last < first || (last - first) % stride != 0u || last - first > 64u * stride) {
    return 0u;
  }
  const uint32_t length = last - first;
  const uint32_t copy = Allocate(ctx, base, bytes);
  const uint32_t frames = copy != 0u ? Allocate(ctx, base, length != 0u ? length : stride) : 0u;
  if (copy == 0u || frames == 0u) {
    Free(ctx, base, copy);
    return 0u;
  }
  std::memcpy(base + copy, base + material, bytes);
  std::memcpy(base + frames, base + first, length);
  for (uint32_t at = frames; at < frames + length; at += stride) {
    for (uint32_t c = 0; c < 3u; ++c) {
      StoreF32(base, at + 4u * c, LoadF32(base, at + 4u * c) * rgb[c]);
    }
  }
  StoreU32(base, copy + 8u, frames);
  StoreU32(base, copy + 12u, frames + length);
  StoreU32(base, copy + 16u, frames + length);

  std::lock_guard lock(g_mutex);
  g_copies[key] = Copy{root, copy};
  return copy;
}

/// Frees every tinted copy descending from `material` (its system is gone; no render data can name
/// it any more).
void ForgetCopies(const PPCContext& ctx, uint8_t* base, uint32_t material) {
  std::vector<uint32_t> freed;
  {
    std::lock_guard lock(g_mutex);
    for (auto it = g_copies.begin(); it != g_copies.end();) {
      if (it->second.root == material) {
        freed.push_back(it->second.address);
        it = g_copies.erase(it);
      } else {
        ++it;
      }
    }
  }
  for (uint32_t copy : freed) {
    Free(ctx, base, LoadU32(base, copy + 8u));
    Free(ctx, base, copy);
  }
}

void ApplyScale(const PPCContext& ctx, uint8_t* base, uint32_t instance, float scale) {
  PPCContext work = refii::mods::Frame(ctx, 0x200u);
  work.r3.u32 = instance;
  work.f1.f64 = scale;
  __imp__rex_CParticleSystemEntityInstance_SetScale(work, base);
}

void RetainNode(uint8_t* base, uint32_t node, uint32_t head, bool system, int depth) {
  if (!Plausible(node) || node == head || depth > 64 || refii::mods::LoadU8(base, node + 25u) != 0u) {
    return;
  }
  RetainNode(base, LoadU32(base, node), head, system, depth + 1);
  const uint32_t object = LoadU32(base, node + 16u);
  const uint32_t block = LoadU32(base, node + 20u);
  if (Plausible(object) && Plausible(block)) {
    auto* word = reinterpret_cast<std::atomic<uint32_t>*>(base + block);
    uint32_t seen = word->load(std::memory_order_relaxed);
    while (!word->compare_exchange_weak(seen, __builtin_bswap32(__builtin_bswap32(seen) + 1u),
                                        std::memory_order_acq_rel)) {
    }
    g_retained.push_back(Retained{object, block, 0u, system});
  }
  RetainNode(base, LoadU32(base, node + 8u), head, system, depth + 1);
}

}  // namespace

namespace refii::vfs {

/// Whether the dress table states any spin or mesh alignment: the integrator's hook does nothing
/// else when it does not.
bool FxMotionStated() { return g_motion.load(std::memory_order_acquire) != nullptr; }

/// The spin, radians per second, the dress table states for the emitter whose type key is `entity`.
bool FxEmitterSpin(uint32_t entity, float& radians_per_second) {
  const Motion* motion = g_motion.load(std::memory_order_acquire);
  if (motion == nullptr) {
    return false;
  }
  auto found = motion->spin.find(entity);
  if (found == motion->spin.end()) {
    return false;
  }
  radians_per_second = found->second;
  return true;
}

/// The rest orientation (x, y, z, w, unit) the dress table states for system `system`'s mesh particles.
bool FxSystemMeshAlign(uint32_t system, float* xyzw) {
  const Motion* motion = g_motion.load(std::memory_order_acquire);
  if (motion == nullptr) {
    return false;
  }
  auto found = motion->align.find(system);
  if (found == motion->align.end()) {
    return false;
  }
  for (int k = 0; k < 4; ++k) {
    xyzw[k] = found->second[k];
  }
  return true;
}

/// Points every emitter of `instance` at a copy of its material with the keyframe colours times
/// `rgb` (see above). White does nothing.
void TintFxInstance(const PPCContext& ctx, uint8_t* base, uint32_t instance, const float* rgb) {
  if (!Plausible(instance) || (rgb[0] == 1.0f && rgb[1] == 1.0f && rgb[2] == 1.0f)) {
    return;
  }
  const uint32_t begin = LoadU32(base, instance + kElementsBegin);
  const uint32_t end = LoadU32(base, instance + kElementsEnd);
  if (!Plausible(begin) || end < begin || end - begin > 8u * 256u) {
    return;
  }
  for (uint32_t at = begin; at + 8u <= end; at += 8u) {
    const uint32_t root = LoadU32(base, at);
    uint32_t element = Plausible(root) ? NextElement(base, root) : 0u;
    for (int n = 0; Plausible(element) && n < 4096; ++n, element = NextElement(base, element)) {
      if (!IsEmitter(ctx, base, element)) {
        continue;
      }
      const uint32_t data = LoadU32(base, element + kElementRenderData);
      const uint32_t system = LoadU32(base, element + kElementSystem);
      if (!Plausible(data) || !Plausible(system)) {
        continue;
      }
      const uint32_t mode = LoadU32(base, system + kSystemMode);
      const uint32_t slot = mode == 1u ? 48u : mode == 2u ? 64u : mode == 3u ? 44u : 0u;
      const uint32_t material = slot != 0u ? LoadU32(base, data + slot) : 0u;
      if (!Plausible(material)) {
        continue;
      }
      const uint32_t copy = Tinted(ctx, base, material, mode, rgb);
      if (copy != 0u) {
        StoreU32(base, data + slot, copy);
      }
    }
  }
}

/// The guest's own particle_bank.bnk read again through its own loader (see above), the previous
/// generation retained until nothing holds it. False when the guest's loader is not reachable.
bool ReloadParticleBank(PPCContext& ctx, uint8_t* base) {
  {
    std::lock_guard lock(g_mutex);
    for (uint32_t tu1 : kRegistries) {
      const uint32_t map = refii::guest::Address(tu1);
      const uint32_t head = LoadU32(base, map + 4u);
      if (Plausible(head)) {
        RetainNode(base, LoadU32(base, head + 4u), head, tu1 == kSystemRegistry, 0);
      }
    }
  }

  const PPCContext frame = refii::mods::Frame(ctx, 0x400u);
  const uint32_t path = (frame.r1.u32 + 0x100u) & ~0xFu;
  const uint32_t file = path + 0x20u;
  const uint32_t stream = path + 0x40u;
  std::memset(base + path, 0, 0x100u);

  PPCContext work = frame;
  work.r3.u32 = path;
  work.r4.u32 = refii::guest::Address(kParticleRoot);
  if (!Call(work, base, kStringCopy)) {
    return false;
  }
  work = frame;
  work.r3.u32 = path;
  work.r4.u32 = refii::guest::Address(kBankName);
  Call(work, base, kStringAppend);
  work = frame;
  work.r3.u32 = file;
  work.r4.u32 = path;
  work.r5.u64 = 1u;
  work.r6.u64 = 2u;
  Call(work, base, kFileOpen);
  work = frame;
  work.r3.u32 = path;
  Call(work, base, kStringRelease);
  work = frame;
  work.r3.u32 = stream;
  work.r4.u32 = file;
  work.r5.u64 = 0x4000u;
  Call(work, base, kStreamCtor);
  work = frame;
  work.r3.u32 = stream;
  Call(work, base, kBankLoad);
  StoreU32(base, stream, refii::guest::Address(kStreamVtable));
  work = frame;
  work.r3.u32 = stream;
  Call(work, base, kStreamDtor);
  StoreU32(base, stream, refii::guest::Address(kStreamBaseVtable));
  work = frame;
  work.r3.u32 = file;
  Call(work, base, kFileClose);

  {
    std::lock_guard lock(g_mutex);
    ReadDressLocked();
  }
  REXLOG_INFO("Refii fx: the guest's particle bank was read again; {} entries of the last generation retained",
              g_retained.size());
  return true;
}

/// Once a tick: a retained entry nothing else has held for kIdleTicksBeforeRelease ticks is
/// released (a system's tinted copies with it).
void PumpParticleBank(PPCContext& ctx, uint8_t* base) {
  std::vector<Retained> release;
  {
    std::lock_guard lock(g_mutex);
    for (auto it = g_retained.begin(); it != g_retained.end();) {
      if (LoadU32(base, it->block) <= 1u) {
        if (++it->idle >= kIdleTicksBeforeRelease) {
          release.push_back(*it);
          it = g_retained.erase(it);
          continue;
        }
      } else {
        it->idle = 0u;
      }
      ++it;
    }
  }
  for (const Retained& r : release) {
    if (r.system) {
      ForgetCopies(ctx, base, LoadU32(base, r.object + kSystemMaterial));
    }
    const PPCContext frame = refii::mods::Frame(ctx, 0x200u);
    const uint32_t pointer = (frame.r1.u32 + 0x100u) & ~0xFu;
    StoreU32(base, pointer, r.object);
    StoreU32(base, pointer + 4u, r.block);
    PPCContext work = frame;
    work.r3.u32 = pointer;
    Call(work, base, kRefRelease);
  }
}

}  // namespace refii::vfs

// The instance constructor `sub_83233300(instance, typeRef)`: r4 is the effect type's smart
// pointer, whose object's +4 is the effect's bank key (`sub_8323E818`: `*(v5 + 4) = a2`). The
// instance is dressed once built: its elements exist and each emitter's render data has been
// made (`sub_83228AB0` at attach, in the constructor's loop).
REX_EXTERN(rex_CParticleSystemEntityInstance_Ctor) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();

  const uint32_t ref = ctx.r4.u32;
  const uint32_t type = Plausible(ref) ? LoadU32(base, ref) : 0u;
  const uint32_t hash = Plausible(type) ? LoadU32(base, type + 4u) : 0u;
  __imp__rex_CParticleSystemEntityInstance_Ctor(ctx, base);
  const uint32_t instance = ctx.r3.u32;
  if (!Plausible(instance)) {
    return;
  }

  Dress dress;
  const bool dressed = hash != 0u && DressFor(hash, dress);
  {
    std::lock_guard lock(g_mutex);
    if (dressed && dress.scale != 1.0f) {
      g_instance_scale[instance] = dress.scale;
    } else {
      g_instance_scale.erase(instance);
    }
  }
  if (!dressed) {
    return;
  }
  if (dress.scale != 1.0f) {
    ApplyScale(ctx, base, instance, dress.scale);
  }
  refii::vfs::TintFxInstance(ctx, base, instance, dress.tint);
  ctx.r3.u32 = instance;
}

// SetParticleScale and its fourteen other callers: the stated scale times the effect's own.
REX_EXTERN(rex_CParticleSystemEntityInstance_SetScale) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  const float scale = InstanceScale(ctx.r3.u32);
  if (scale != 1.0f) {
    ctx.f1.f64 *= scale;
  }
  REFII_VFS_NATIVE_PASSTHROUGH(rex_CParticleSystemEntityInstance_SetScale);
}

// GetParticleScale: what the game stated, without the effect's own factor.
REX_EXTERN(rex_CParticleSystemEntityInstance_GetScale) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  const float scale = InstanceScale(ctx.r3.u32);
  __imp__rex_CParticleSystemEntityInstance_GetScale(ctx, base);
  if (scale != 1.0f) {
    ctx.f1.f64 /= scale;
  }
}
