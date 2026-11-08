// refii - each hero's dog, as Fable 3's game holds it.
//
// Every address is the Fable 3 IDB; every offset names its base.
//
//   EM = *(*(*(Game+0x1C)+4)+4), Game the presented game (presented_world.h) — the object the
//   heroes' slots live on (entity_feed.cpp: EM+0x80 player one's hero, EM+0x88 player two's).
//
// THE SETTER. sub_823BBF38(EM r3, dog r4, hero r5), called from sub_82808080 (0x828082E0), binds a
// dog to its hero: it finds the 16-byte record at EM+0xB8 (else EM+0xC0) whose owner is `hero`
// and re-points its dog, or allocates a record for the empty slot (sub_8221E3C8(16),
// sub_823C6318 at 0x823BC0BC / 0x823BC168) and assigns both weak references — the dog at record+0
// (`sub_82258B38(*slot, dog)` 0x823BC0DC) and the hero at record+8 (0x823BC114). A weak reference
// is {vtable off_820B1CB4, holder}: its entity is *(holder) (sub_82691588: `lwz r11,4(r3)`,
// `lwz r10,0(r11)`). sub_823BC1F8(EM, hero) releases and clears the hero's slot (0x823BC238 /
// 0x823BC268).
//
// THE GETTER. sub_821E7EB8(EM r3, hero r4) is the game's own answer: EM+0xB8's record when its
// owner is `hero` (sub_8238DD58(record+8, hero): *(*(record+0xC)) == hero), else EM+0xC0's, its
// dog *(*(record+4)). GetLocalHeroDog (sub_8221C328 -> sub_821E7F50) asks it for the hero at
// EM+0x7C (holder EM+0x80) once that hero is live, `lbz +0x94 & 0x40`; GetRemoteHeroDog
// (sub_821FC440 -> sub_821E29B0) for EM+0x84 (holder EM+0x88); the Lua GetDog(hero) (sub_82321E58)
// for any live hero. This export reads what that getter reads.

#include <cstdint>

#include "host_abi.h"
#include "host_core.h"
#include "presented_world.h"

namespace {

using refii::fable3::GuestWord;
using refii::fable3::PlausibleGuest;
using refii::hostcore::GuestByteAt;

constexpr uint32_t kHeroOneSlot = 0x80u;
constexpr uint32_t kHeroTwoSlot = 0x88u;
constexpr uint32_t kDogRecords[] = {0xB8u, 0xC0u};

const uint8_t* GuestBase() {
    rex::memory::Memory* memory = refii::hostcore::SafeKernelMemory();
    return memory == nullptr ? nullptr : memory->virtual_membase();
}

bool Live(const uint8_t* base, uint32_t entity) {
    return PlausibleGuest(entity) && (GuestByteAt(base, entity + 0x94u) & 0x40u) != 0u;
}

/// The entity manager of the world standing now, else 0 (entity_feed.cpp's EntityManager).
uint32_t EntityManager(const uint8_t* base) {
    const uint32_t game = refii::fable3::PresentedGame(base);
    if (game == 0u || GuestByteAt(base, game + 0xBAu) == 0u) {
        return 0u;
    }

    const uint32_t em = GuestWord(base, GuestWord(base, GuestWord(base, game + 0x1Cu) + 4u) + 4u);
    return PlausibleGuest(em) ? em : 0u;
}

/// The live dog bound to the live hero in `slot`, as sub_821E7EB8 answers it, else 0.
uint32_t HeroDog(const uint8_t* base, uint32_t em, uint32_t slot) {
    const uint32_t hero = GuestWord(base, GuestWord(base, em + slot));
    if (!Live(base, hero)) {
        return 0u;
    }

    for (const uint32_t at : kDogRecords) {
        const uint32_t record = GuestWord(base, em + at);
        if (!PlausibleGuest(record) || GuestWord(base, GuestWord(base, record + 0xCu)) != hero) {
            continue;
        }

        const uint32_t dog = GuestWord(base, GuestWord(base, record + 4u));
        return Live(base, dog) ? dog : 0u;
    }

    return 0u;
}

}  // namespace

extern "C" {

/// Player one's and player two's dogs (each hero's, as the game binds them in sub_823BBF38), 0
/// for a hero with no live dog. Returns how many were found.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetHeroDogs(uint32_t* out_one, uint32_t* out_two) {
    const uint8_t* base = GuestBase();
    const uint32_t em = base == nullptr ? 0u : EntityManager(base);
    const uint32_t one = em == 0u ? 0u : HeroDog(base, em, kHeroOneSlot);
    const uint32_t two = em == 0u ? 0u : HeroDog(base, em, kHeroTwoSlot);
    if (out_one != nullptr) *out_one = one;
    if (out_two != nullptr) *out_two = two;
    return (one != 0u ? 1u : 0u) + (two != 0u ? 1u : 0u);
}

}  // extern "C"
