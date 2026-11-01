// refii - whether Fable 3's guest poses an entity's rig this tick, as the guest decides it.
//
// The animation manager's step (component 23, sub_8220E630) poses a rig only when
// sub_82205E00 says so (`bl sub_82205E00` 0x8220E75C); otherwise the pose is HELD while the clips
// keep running. Every term of that gate is a field the guest keeps, read here as it reads them
// (all Fable 3 IDB, base named per line):
//
//   byte_83556521, byte_83556520           pose everything (0x82205E18, 0x82205F48)
//   entity+0x94 byte, bits 0x40/0x20/0x10   all three set, or the rig is posed (0x82205E30..E68)
//   entity+0x28 qword, low word bit 3       the entity has component 3, else never (0x82205E6C..E90)
//   comp3 = component 3 (CECGraphicAppearance; its +4 is the entity)
//   comp3+0x24 float                        the draw distance the terms below are measured to
//   comp3+0xEA byte                         SetAsAlwaysInView (setter sub_82575FE0, registered
//                                           under that name at 0x8268ADF0)
//   comp3+0xEB byte                         the pose pass's own force (writer sub_823531E0)
//   the heroes                              entity == **(EM+0x80) or **(EM+0x88), EM =
//                                           *(*(*(entity+8)+4)+4) (sub_82209B80 -> sub_82209B20)
//   sub_82209A28(comp3, distance)           within = comp3+0x6C < distance² (0x82209A54..A64);
//                                           with byte_8329EACB and a render proxy at comp3+0x18:
//                                           frames since the proxy was last drawn (proxy+8 against
//                                           the world frame, below) > 30 -> no; < 4 and within ->
//                                           yes (0x82209AA8..0x82209AD8); then comp3+0xEE and
//                                           within (0x82209AE0..0x82209B00)
//   sub_82209DB8(comp3, distance)           the second view, when one stands (sub_82209E88:
//                                           *(*(0x83556EF8)+0x10) +0x1C && +0x1E, or +0x58):
//                                           comp3+0xEF and comp3+0x70 < distance²
//
// comp3+0x6C/+0xEE (and +0x70/+0xEF) are the guest's own last view test, refreshed once per world
// frame by sub_822098C0 (`stb r3,0xEE(r31)` 0x82209A0C: the entity's bounding sphere against the
// camera's half-space, sub_8252FE28 -> sub_821FA358). The world frame is the one the gate reads:
// `*(*(*(game+0xC)+4)+0x80)` (MainGameComponent vtable slot 4 sub_8221A428 = `*(*(component+0xC)
// +0xC)`; game = the presented game, presented_world.h).
//
// WHY THE SHELL ASKS. A cutscene actor that is not a character (the castle intro's curtains and
// duvet, `Character = 0` in animatedcutscenes.gdb) is moved to the cutscene's origin and its clip
// carries the cloth metres away from there; the shell's own approximation of this gate tested the
// entity's ORIGIN against its frustum and held the curtains' pose for the whole shot.

#include <cstdint>
#include <cstring>

#include "host_abi.h"
#include "host_core.h"
#include "presented_world.h"

namespace {

using refii::fable3::GuestWord;
using refii::fable3::PlausibleGuest;
using refii::hostcore::GuestByteAt;
using refii::hostcore::SafeKernelMemory;

/// The shell's answers: hold, pose, or no statement (no world, no entity).
constexpr uint32_t kHeld = 0u;
constexpr uint32_t kPosed = 1u;
constexpr uint32_t kNotStated = 2u;

constexpr uint32_t kPoseAll = 0x83556521u;
constexpr uint32_t kPoseAllVisible = 0x83556520u;
constexpr uint32_t kProxyFramesGate = 0x8329EACBu;
constexpr uint32_t kSecondView = 0x83556EF8u;

uint8_t Byte(const uint8_t* base, uint32_t address) {
    return PlausibleGuest(address) ? GuestByteAt(base, address) : 0u;
}

float Float(const uint8_t* base, uint32_t address) {
    const uint32_t bits = GuestWord(base, address);
    float value;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

/// The entity's component `id` from its {id, component} vector at +0x54/+0x58, or 0.
uint32_t Component(const uint8_t* base, uint32_t entity, uint32_t id) {
    const uint32_t first = GuestWord(base, entity + 0x54u);
    const uint32_t last = GuestWord(base, entity + 0x58u);
    if (!PlausibleGuest(first) || last < first || last - first > 8u * 256u) {
        return 0u;
    }

    for (uint32_t at = first; at < last; at += 8u) {
        if (GuestWord(base, at) == id) {
            const uint32_t component = GuestWord(base, at + 4u);
            return PlausibleGuest(component) ? component : 0u;
        }
    }

    return 0u;
}

/// sub_82209B20: the handle at `slot` (+4 its holder, holder+0 the entity) names `entity`.
bool HandleNames(const uint8_t* base, uint32_t slot, uint32_t entity) {
    const uint32_t holder = GuestWord(base, slot + 4u);
    return PlausibleGuest(holder) && GuestWord(base, holder) == entity;
}

/// sub_82209A28 without the refresh: the first view's term.
bool FirstView(const uint8_t* base, uint32_t game, uint32_t comp3, float distanceSq) {
    const bool within = Float(base, comp3 + 0x6Cu) < distanceSq;
    const uint32_t proxy = GuestWord(base, comp3 + 0x18u);
    if (Byte(base, kProxyFramesGate) != 0u && PlausibleGuest(proxy)) {
        const uint32_t frame = GuestWord(base, GuestWord(base, GuestWord(base, game + 0xCu) + 4u) + 0x80u);
        const int32_t since = static_cast<int32_t>(frame - GuestWord(base, proxy + 8u));
        if (since > 30) {
            return false;
        }

        if (since < 4 && within) {
            return true;
        }
    }

    return Byte(base, comp3 + 0xEEu) != 0u && within;
}

/// sub_82209DB8 without the refresh: the second view's term, when a second view stands.
bool SecondView(const uint8_t* base, uint32_t comp3, float distanceSq) {
    const uint32_t view = GuestWord(base, GuestWord(base, kSecondView) + 0x10u);
    if (!PlausibleGuest(view)) {
        return false;
    }

    const bool stands = (Byte(base, view + 0x1Cu) != 0u && Byte(base, view + 0x1Eu) != 0u) ||
                        GuestWord(base, view + 0x58u) != 0u;
    return stands && Byte(base, comp3 + 0xEFu) != 0u && Float(base, comp3 + 0x70u) < distanceSq;
}

}  // namespace

extern "C" {

/// sub_82205E00 for `entity`: 1 when the guest poses its rig this tick, 0 when it holds the pose,
/// 2 when there is no world or no entity to ask about.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityPoseGate(uint32_t entity) {
    rex::memory::Memory* memory = SafeKernelMemory();
    const uint8_t* base = memory == nullptr ? nullptr : memory->virtual_membase();
    if (base == nullptr || !PlausibleGuest(entity)) {
        return kNotStated;
    }

    const uint32_t game = refii::fable3::PresentedGame(base);
    if (game == 0u) {
        return kNotStated;
    }

    if (Byte(base, kPoseAll) != 0u) {
        return kPosed;
    }

    const uint8_t flags = Byte(base, entity + 0x94u);
    if ((flags & 0x70u) != 0x70u) {
        return kPosed;
    }

    if ((GuestWord(base, entity + 0x2Cu) & 0x8u) == 0u) {
        return kHeld;
    }

    const uint32_t comp3 = Component(base, entity, 3u);
    if (comp3 == 0u) {
        return kHeld;
    }

    if (Byte(base, kPoseAllVisible) != 0u || Byte(base, comp3 + 0xEAu) != 0u || Byte(base, comp3 + 0xEBu) != 0u) {
        return kPosed;
    }

    const uint32_t em = GuestWord(base, GuestWord(base, GuestWord(base, entity + 8u) + 4u) + 4u);
    if ((flags & 0x40u) != 0u && PlausibleGuest(em) &&
        (HandleNames(base, em + 0x7Cu, entity) || HandleNames(base, em + 0x84u, entity))) {
        return kPosed;
    }

    const float distance = Float(base, comp3 + 0x24u);
    const float distanceSq = distance * distance;
    return FirstView(base, game, comp3, distanceSq) || SecondView(base, comp3, distanceSq) ? kPosed : kHeld;
}

/// The sphere the shell's own view test uses when a title states no pose gate (Fable 2:
/// RefiiHostGetEntityViewSphere there). Fable 3 always states its gate when it has an entity to
/// ask about, so the shell never reaches that test here: 0, none. The guest's own sphere is not
/// a substitute: sub_822098C0's view term (sub_821FA358, 0x82209A00/0x82209A08) tests the
/// placement's physics body (entity+0x80 slot 0x74, shape copied by sub_82231010) or the
/// placement position as a point (sub_82206250) — the placement, not where a clip carries the bones.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityViewSphere(uint32_t, float*) {
    return 0u;
}

}  // extern "C"
