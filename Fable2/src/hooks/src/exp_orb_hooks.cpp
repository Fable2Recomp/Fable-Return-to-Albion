#include "exp_orb_hooks.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <unordered_map>

#include "core/guest_build.h"
#include "core/guest_state.h"

namespace {

struct Vec3 {
  float x;
  float y;
  float z;
};

struct OrbTrack {
  float last_dist = 0.0f;
  int stall_ticks = 0;
  int close_ticks = 0;
  bool has_prev = false;
};

std::mutex g_orb_track_mutex;
std::unordered_map<uint32_t, OrbTrack> g_orb_tracks;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

bool IsFinite(const Vec3& v) {
  return std::isfinite(v.x) && std::isfinite(v.y) && std::isfinite(v.z);
}

float Dist(const Vec3& a, const Vec3& b) {
  const float dx = a.x - b.x;
  const float dy = a.y - b.y;
  const float dz = a.z - b.z;
  return std::sqrt((dx * dx) + (dy * dy) + (dz * dz));
}

float LoadGuestF32(uint8_t* base, uint32_t addr) {
  const uint32_t bits = refii::vfs::LoadGuestU32(base, addr);
  float value = 0.0f;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

void StoreGuestF32(uint8_t* base, uint32_t addr, float value) {
  uint32_t bits = 0u;
  std::memcpy(&bits, &value, sizeof(bits));
  refii::vfs::StoreGuestU32(base, addr, bits);
}

Vec3 ReadVec3(uint8_t* base, uint32_t addr) {
  return {
      LoadGuestF32(base, addr + 0u),
      LoadGuestF32(base, addr + 4u),
      LoadGuestF32(base, addr + 8u),
  };
}

void WriteVec3(uint8_t* base, uint32_t addr, const Vec3& v) {
  StoreGuestF32(base, addr + 0u, v.x);
  StoreGuestF32(base, addr + 4u, v.y);
  StoreGuestF32(base, addr + 8u, v.z);
}

bool TryLocateOrbHkBody(uint8_t* base, uint32_t orb_entity, uint32_t* out_hk_body) {
  const uint32_t phys = refii::vfs::LoadGuestU32(base, orb_entity + 0x7Cu);
  if (!Plausible(phys)) {
    return false;
  }

  const uint32_t proxy = refii::vfs::LoadGuestU32(base, phys + 0x20u);
  if (!Plausible(proxy)) {
    return false;
  }

  const uint32_t hk_delegate_vt = refii::guest::Address(0x82005B80u);
  uint32_t body = 0u;
  if (refii::vfs::LoadGuestU32(base, proxy) == hk_delegate_vt) {
    body = proxy;
  } else {
    const uint32_t hop = refii::vfs::LoadGuestU32(base, proxy + 0x38u);
    if (!Plausible(hop) || refii::vfs::LoadGuestU32(base, hop) != hk_delegate_vt) {
      return false;
    }
    body = hop;
  }

  const uint32_t hk_body = refii::vfs::LoadGuestU32(base, body + 0x2Cu);
  if (!Plausible(hk_body)) {
    return false;
  }

  *out_hk_body = hk_body;
  return true;
}

bool TryReadDelegatePos(uint8_t* base, uint32_t delegate_obj, int depth, Vec3* out_pos) {
  if (!Plausible(delegate_obj) || depth > 3) {
    return false;
  }

  const uint32_t vt = refii::vfs::LoadGuestU32(base, delegate_obj);
  if (vt == refii::guest::Address(0x82006A40u)) {
    return TryReadDelegatePos(base, refii::vfs::LoadGuestU32(base, delegate_obj + 0x38u),
                              depth + 1, out_pos);
  }
  if (vt == refii::guest::Address(0x82005460u)) {
    const Vec3 pos = ReadVec3(base, delegate_obj + 0xF0u);
    if (!IsFinite(pos)) {
      return false;
    }
    *out_pos = pos;
    return true;
  }
  if (vt == refii::guest::Address(0x82005B80u)) {
    const uint32_t hk_body = refii::vfs::LoadGuestU32(base, delegate_obj + 0x2Cu);
    if (!Plausible(hk_body)) {
      return false;
    }
    const Vec3 pos = ReadVec3(base, hk_body + 0x110u);
    if (!IsFinite(pos)) {
      return false;
    }
    *out_pos = pos;
    return true;
  }
  return false;
}

bool TryReadHeroPos(uint8_t* base, uint32_t hero_entity, Vec3* out_pos) {
  const uint32_t phys = refii::vfs::LoadGuestU32(base, hero_entity + 0x7Cu);
  if (!Plausible(phys)) {
    return false;
  }

  const uint32_t vt = refii::vfs::LoadGuestU32(base, phys);
  if (vt == refii::guest::Address(0x82007038u) ||
      vt == refii::guest::Address(0x820071A0u) ||
      vt == refii::guest::Address(0x820C4B90u)) {
    const Vec3 cached = ReadVec3(base, phys + 0x70u);
    const float mag_sq = (cached.x * cached.x) + (cached.y * cached.y) + (cached.z * cached.z);
    if (IsFinite(cached) && mag_sq > 1e-4f) {
      *out_pos = cached;
      return true;
    }
  }

  const uint32_t proxy = refii::vfs::LoadGuestU32(base, phys + 0x20u);
  return TryReadDelegatePos(base, proxy, 0, out_pos);
}

void WriteOrbPosition(uint8_t* base, uint32_t hk_body, const Vec3& new_pos) {
  const Vec3 old_pos = ReadVec3(base, hk_body + 0x110u);
  const Vec3 com0 = ReadVec3(base, hk_body + 0x120u);
  const Vec3 com1 = ReadVec3(base, hk_body + 0x130u);

  WriteVec3(base, hk_body + 0x110u, new_pos);
  if (IsFinite(old_pos)) {
    if (IsFinite(com0) && Dist(com0, old_pos) < 1.0f) {
      WriteVec3(base, hk_body + 0x120u, new_pos);
    }
    if (IsFinite(com1) && Dist(com1, old_pos) < 1.0f) {
      WriteVec3(base, hk_body + 0x130u, new_pos);
    }
  }
}

}  // namespace

namespace refii::hooks {

void FixupExperienceOrb(uint8_t* base, uint32_t target_hero, uint32_t orb) {
  if (base == nullptr || !Plausible(orb) || !Plausible(target_hero)) {
    return;
  }

  const uint32_t orb_entity = refii::vfs::LoadGuestU32(base, orb + 4u);
  if (!Plausible(orb_entity)) {
    return;
  }

  uint32_t hk_body = 0u;
  Vec3 hero_pos{};
  if (!TryLocateOrbHkBody(base, orb_entity, &hk_body) ||
      !TryReadHeroPos(base, target_hero, &hero_pos)) {
    return;
  }

  Vec3 orb_pos = ReadVec3(base, hk_body + 0x110u);
  if (!IsFinite(orb_pos) || !IsFinite(hero_pos)) {
    return;
  }

  // Case A: rescue orbs spawned/dropped into the -1000 terrain sector miss void.
  const float dx = orb_pos.x - hero_pos.x;
  const float dy = orb_pos.y - hero_pos.y;
  if (orb_pos.z < -500.0f && ((dx * dx) + (dy * dy)) < (60.0f * 60.0f)) {
    orb_pos.z = hero_pos.z + 0.60f;
    WriteOrbPosition(base, hk_body, orb_pos);
  }

  // Case B: preserve normal bouncy Havok physics before RT is held; only unblock & lift while
  // suckActive (`orb + 0x28`) is engaged.
  const bool sucking = refii::vfs::LoadGuestU8(base, orb + 0x28u) != 0u;
  std::lock_guard<std::mutex> lock(g_orb_track_mutex);
  if (g_orb_tracks.size() > 2048u) {
    g_orb_tracks.clear();
  }

  OrbTrack& track = g_orb_tracks[orb];
  if (!sucking) {
    track.stall_ticks = 0;
    track.close_ticks = 0;
    track.has_prev = false;
    return;
  }

  const Vec3 aim = {hero_pos.x, hero_pos.y, hero_pos.z + 0.85f};
  float dist = Dist(aim, orb_pos);
  if (dist > 150.0f) {
    track.has_prev = false;
    return;
  }

  if (dist < 1.15f) {
    ++track.close_ticks;
  } else {
    track.close_ticks = 0;
  }

  if (track.has_prev && (track.last_dist - dist) < 0.08f) {
    ++track.stall_ticks;
    if (track.stall_ticks >= 15 || track.close_ticks >= 4) {
      StoreGuestF32(base, orb + 0x3Cu, 10.0f);
      WriteOrbPosition(base, hk_body, aim);
      track.stall_ticks = 0;
      track.close_ticks = 0;
      track.has_prev = false;
      return;
    }

    if (track.stall_ticks >= 2 && dist > 1e-4f) {
      const float step = std::min(dist, 0.65f);
      const float inv = step / dist;
      Vec3 stepped = {
          orb_pos.x + ((aim.x - orb_pos.x) * inv),
          orb_pos.y + ((aim.y - orb_pos.y) * inv),
          orb_pos.z + ((aim.z - orb_pos.z) * inv),
      };
      if (orb_pos.z < aim.z) {
        stepped.z = std::min(aim.z, stepped.z + 0.20f);
      }
      WriteOrbPosition(base, hk_body, stepped);
      dist = Dist(aim, stepped);
    }
  } else if (track.has_prev) {
    if (track.stall_ticks > 0) {
      --track.stall_ticks;
    }
  }

  track.last_dist = dist;
  track.has_prev = true;
}

}  // namespace refii::hooks
