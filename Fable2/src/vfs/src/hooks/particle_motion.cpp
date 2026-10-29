#include "vfs_hooks_private.h"

#include <cmath>
#include <cstdint>
#include <cstring>

#include "core/guest_build.h"
#include "core/refii_mod.h"
#include "hooks/ppc_externs.h"

// WHAT THE PARTICLE BANK CANNOT STATE, ADDED WHERE THE GUEST MOVES AND TURNS ITS PARTICLES.
//
// A mod effect (fx/effects/*.json) may give an emitter a SPIN (FxEmitter.Spin) and a mesh visual a
// REST ORIENTATION (FxVisual.MeshAlign). The guest's records have no field for either; the host
// writes them into the dress table beside the guest's bank (particle_dress.cpp reads it:
// `spin 0xENTITY radiansPerSecond`, `align 0xSYSTEM x y z w`). TU1 addresses; the disc build's
// bodies are the same (size and instruction count: 0x8322CF40 / 0x83229378, 0x178 bytes, 94;
// 0x83220AC8 / 0x8321A250, 0x41C bytes, 263), its data rows in guest_addresses.inc.
//
// SPIN. `sub_8322CF40(element r3, position r4, velocity r5, frame r6, attract r7, damp r8)` is the
// integrator every particle of every emitter steps through, at its birth (the emitter's spawn slot
// 27, e.g. `sub_82A04208`) and each update (`sub_82A0CD68`, `sub_82269FE0`, `sub_8226A1B8` …):
// velocity += acceleration (`unk_8349E5D0`), the attractors' pull (`sub_83229A98`), × damping
// (`unk_8349E5E0`), then position += velocity (0x8322d08c..0x8322d09c). All of it runs inside the
// emitter's step: `sub_8322D988` copies the emitter's spawn matrix into `unk_8349E5F0`
// (0x8322d9a8..0x8322d9d4) and only then calls `sub_8322F1E0` (0x8322dc88), which calls the
// emitter's slot 26 (0x8322f28c), `sub_8322D720`, the simulation. So while a particle steps, that
// global is ITS emitter's matrix in the frame its particles are stored in: row 2 the emitter's Z
// axis, row 3 its origin. After the guest's own step, a spinning emitter's particle is turned
// about that axis through that origin, position and velocity alike, by spin ÷ 30 × the step's
// frames (`flt_8335F1D4[dword_83496DD4]`, the factor the guest's own rates take, 0x832214c0).
// The element's type is `*(element+0xD0)` and its key, the emitter's entity id, `*(type+4)`.
//
// ALIGN. `sub_83220AC8` births a mesh particle's visual: it stores the orientation its caller
// hands it in r6:r7 at particle+0x30 (0x83220dc0, 0x83220dc4) — the emitter's spawn rotation
// (`sub_8223B9A0(q, unk_8349E5F0)` at 0x82a0b148, passed at 0x82a0b390) — and the initial angle
// about the visual's axis at +0x40. Each step `sub_832213E8` turns +0x30 by the rate on the left
// (`sub_822ADF78`: dst = arg ⊗ dst, 0x832215d0), and the draw `sub_821CEA80` sends
// (+0x30) ⊗ (+0x40). r8 is the particle's system at every call site (`lwz r8,0x170(r31)` at
// 0x82a0b378 and 0x82a06d0c; `lwz r8,0x30(r31)` in `sub_832249D8`), its id at system+4
// (`sub_83228210`). A system with an align has r6:r7 replaced by (r6:r7) ⊗ align before the
// birth: the mesh is turned by the align in the emitter's frame, then by the emitter.

PPC_EXTERN_FUNC(__imp__rex_CParticleEmitter_StepParticle);
PPC_EXTERN_FUNC(__imp__rex_CMeshParticle_InitVisual);

namespace refii::vfs {
bool FxMotionStated();                                   // particle_dress.cpp
bool FxEmitterSpin(uint32_t entity, float& radians_per_second);
bool FxSystemMeshAlign(uint32_t system, float* xyzw);
}  // namespace refii::vfs

namespace {

using refii::mods::LoadF32;
using refii::mods::LoadU32;
using refii::mods::StoreF32;

constexpr uint32_t kSpawnMatrix = 0x8349E5F0u;
constexpr uint32_t kStepFrames = 0x8335F1D4u;
constexpr uint32_t kStepClass = 0x83496DD4u;
constexpr uint32_t kElementType = 0xD0u;
constexpr uint32_t kRegistryKey = 4u;
constexpr float kFps = 30.0f;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

float FloatOf(uint32_t bits) {
  float value;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

uint32_t BitsOf(float value) {
  uint32_t bits;
  std::memcpy(&bits, &value, sizeof(bits));
  return bits;
}

/// `v` (a guest vector at `at`) turned by the rotation matrix `m` (rows), about `origin`.
void Turn(uint8_t* base, uint32_t at, const float m[9], const float origin[3]) {
  const float d[3] = {LoadF32(base, at) - origin[0], LoadF32(base, at + 4u) - origin[1],
                      LoadF32(base, at + 8u) - origin[2]};
  for (uint32_t r = 0; r < 3u; ++r) {
    StoreF32(base, at + 4u * r, origin[r] + m[3 * r] * d[0] + m[3 * r + 1] * d[1] + m[3 * r + 2] * d[2]);
  }
}

}  // namespace

REX_EXTERN(rex_CParticleEmitter_StepParticle) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  const uint32_t element = ctx.r3.u32;
  const uint32_t position = ctx.r4.u32;
  const uint32_t velocity = ctx.r5.u32;
  __imp__rex_CParticleEmitter_StepParticle(ctx, base);
  if (!refii::vfs::FxMotionStated() || !Plausible(element) || !Plausible(position) || !Plausible(velocity)) {
    return;
  }
  const uint32_t type = LoadU32(base, element + kElementType);
  float spin = 0.0f;
  if (!Plausible(type) || !refii::vfs::FxEmitterSpin(LoadU32(base, type + kRegistryKey), spin)) {
    return;
  }

  const uint32_t matrix = refii::guest::Address(kSpawnMatrix);
  float axis[3] = {LoadF32(base, matrix + 32u), LoadF32(base, matrix + 36u), LoadF32(base, matrix + 40u)};
  const float length = std::sqrt(axis[0] * axis[0] + axis[1] * axis[1] + axis[2] * axis[2]);
  if (!(length > 1e-6f)) {
    return;
  }
  for (float& a : axis) {
    a /= length;
  }
  const float origin[3] = {LoadF32(base, matrix + 48u), LoadF32(base, matrix + 52u), LoadF32(base, matrix + 56u)};
  const uint32_t step_class = LoadU32(base, refii::guest::Address(kStepClass));
  const float frames = step_class < 8u ? LoadF32(base, refii::guest::Address(kStepFrames) + 4u * step_class) : 1.0f;
  const float angle = spin / kFps * frames;

  // The rotation by `angle` about `axis` (the same matrix Engine_FxSystem.AxisAngleMat builds).
  const float c = std::cos(angle), s = std::sin(angle), ic = 1.0f - c;
  const float x = axis[0], y = axis[1], z = axis[2];
  const float m[9] = {c + x * x * ic,     x * y * ic - z * s, x * z * ic + y * s,
                      y * x * ic + z * s, c + y * y * ic,     y * z * ic - x * s,
                      z * x * ic - y * s, z * y * ic + x * s, c + z * z * ic};
  Turn(base, position, m, origin);
  const float none[3] = {0.0f, 0.0f, 0.0f};
  Turn(base, velocity, m, none);
}

REX_EXTERN(rex_CMeshParticle_InitVisual) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  const uint32_t system = ctx.r8.u32;
  float a[4];
  if (refii::vfs::FxMotionStated() && Plausible(system) &&
      refii::vfs::FxSystemMeshAlign(LoadU32(base, system + kRegistryKey), a)) {
    // r6:r7 is the quaternion as the caller spilled it: x y in r6, z w in r7 (0x83220ae4/0x83220aec).
    const float q[4] = {FloatOf(static_cast<uint32_t>(ctx.r6.u64 >> 32)), FloatOf(static_cast<uint32_t>(ctx.r6.u64)),
                        FloatOf(static_cast<uint32_t>(ctx.r7.u64 >> 32)), FloatOf(static_cast<uint32_t>(ctx.r7.u64))};
    // q ⊗ a: the align first, in the emitter's frame, then the emitter's rotation.
    float r[4] = {q[3] * a[0] + q[0] * a[3] + q[1] * a[2] - q[2] * a[1],
                  q[3] * a[1] + q[1] * a[3] + q[2] * a[0] - q[0] * a[2],
                  q[3] * a[2] + q[2] * a[3] + q[0] * a[1] - q[1] * a[0],
                  q[3] * a[3] - q[0] * a[0] - q[1] * a[1] - q[2] * a[2]};
    const float length = std::sqrt(r[0] * r[0] + r[1] * r[1] + r[2] * r[2] + r[3] * r[3]);
    if (length > 1e-6f) {
      for (float& v : r) {
        v /= length;
      }
      ctx.r6.u64 = (static_cast<uint64_t>(BitsOf(r[0])) << 32) | BitsOf(r[1]);
      ctx.r7.u64 = (static_cast<uint64_t>(BitsOf(r[2])) << 32) | BitsOf(r[3]);
    }
  }
  REFII_VFS_NATIVE_PASSTHROUGH(rex_CMeshParticle_InitVisual);
}
