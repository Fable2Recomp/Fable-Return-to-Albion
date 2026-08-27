// The hero's derived state, sampled on the guest's own thread; shared by the pump, the state
// file and the host bridge.

#ifndef REFII_VFS_CORE_HERO_DERIVED_H_
#define REFII_VFS_CORE_HERO_DERIVED_H_

#include <cstdint>

namespace refii::vfs {


/// The world map's region info page for one region. Fields are strings: the guest's providers
/// (`sub_823C6C18` and siblings, `(LHString* out, LHString* regionName)`) format them.
struct WorldMapRegion {
    bool known = false;

    /// Which region this sample is of; the pump answers a tick late.
    uint32_t index = 0xFFFFFFFFu;
    char name[64] = {};
    char distance[64] = {};
    char travel[64] = {};
    char fine[32] = {};
    char owned[32] = {};
    char chests[32] = {};
    char digspots[32] = {};
    char divespots[32] = {};
    char gargoyles[32] = {};
    char silver_keys[32] = {};
    char merchants[512] = {};

    /// How many regions the list holds, so a host can bound its cursor.
    uint32_t count = 0;

    /// Economy as filled stars: `sub_8246FE20`'s return, before the guest's `+1` slide index.
    /// -1 when not sampled.
    int32_t stars = -1;
};

/// One completed quest for the logbook page. Row key is `index + 1000` into the list
/// `sub_82470220` rebuilds: sorted descending by GDB `DisplayPriority`, then filtered
/// order-preserving.
struct QuestEntry {
    /// GDB record hash from `quest+4`; name, description and DisplayPriority hang off it.
    uint32_t record = 0;

    /// `quest+36` and `quest+32`, signed ints, gated on `> 0` by the page.
    int32_t money = 0;
    int32_t renown = 0;

    /// Babel tag of the conclusion, LHString at `quest+0x50`; empty when none.
    char conclusion[64] = {};
};

/// The hero's derived state: accessor returns, not fields. Morality is `sub_8223D320`, renown
/// `sub_821C7D30`. Personality bands test at 0.375 and 0.625. `known` is false until sampled.
struct HeroDerived {
    bool known = false;

    /// Displayed morality and purity, and the range each is normalised against.
    float morality = 0.0f;
    float morality_min = 0.0f;
    float morality_max = 0.0f;
    float purity = 0.0f;
    float purity_min = 0.0f;
    float purity_max = 0.0f;

    /// Displayed renown: the field plus modifier 10, clamped at zero.
    int32_t renown = 0;

    /// The hero's title as the Babel tag the guest resolves; empty when unresolved.
    char title_tag[64] = {};

    /// The eleven attractiveness contributions in page order, then the slider's total.
    static constexpr uint32_t kAttractivenessCount = 11;
    float attractiveness[kAttractivenessCount] = {};
    float attractiveness_total = 0.0f;

    /// The dog, when one exists: `dog_known` gates the whole page.
    bool dog_known = false;

    /// Levels 1..5, int32 not float: the constructor stores `1` with `stw` (`0x82808130`) and
    /// `sub_829D75F0` uses combat as a one-based index into a five-float table.
    int32_t dog_combat = 0;
    int32_t dog_searching = 0;
    int32_t dog_state = 0;
    char dog_name[64] = {};

    /// GDB record ids of learned tricks, not name hashes: `sub_8219C5E8` maps `FNV1(name)`
    /// through a hash -> record-id table before storing.
    uint32_t dog_trick_ids[32] = {};
    uint32_t dog_trick_count = 0;

    /// Logbook stat counters: flat int32 array at `c57 + 348 + 4*id`; ids up to 123.
    static constexpr uint32_t kStatCount = 124;
    int32_t stats[kStatCount] = {};
    bool stats_known = false;

    /// Hero-side spell damage multiplier indexed by level 1..5; slot 0 unused.
    float spell_damage[6] = {};
};

/// Published by the pump on the guest thread; read by the host bridge on Godot's.
void PublishHeroDerived(const HeroDerived& sampled);
HeroDerived HeroDerivedSnapshot();

/// The completed-quest list, same contract; the store is fixed-size.
constexpr uint32_t kMaxCompletedQuests = 256;
void PublishQuests(const QuestEntry* entries, uint32_t count);
uint32_t QuestSnapshot(QuestEntry* out, uint32_t capacity);

/// Which region the host wants sampled; the pump answers for that one only.
void RequestWorldMapRegion(uint32_t index);
uint32_t RequestedWorldMapRegion();
void PublishWorldMapRegion(const WorldMapRegion& region);
WorldMapRegion WorldMapRegionSnapshot();

}  // namespace refii::vfs

#endif  // REFII_VFS_CORE_HERO_DERIVED_H_
