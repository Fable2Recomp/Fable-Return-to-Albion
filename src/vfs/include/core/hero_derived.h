// The hero's derived state, sampled on the guest's own thread; shared by the pump, the state
// file and the host bridge.

#ifndef REFII_VFS_CORE_HERO_DERIVED_H_
#define REFII_VFS_CORE_HERO_DERIVED_H_

#include <cstdint>

#include "core/payload_bridge.h"

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

    /// The eleven attractiveness contributions in page order, then the slider's own position.
    ///
    /// `attractiveness_fraction` is 0..1 as `sub_829BA860` states it to the meter, not the sum
    /// of the eleven rows: the guest's sum also carries preference axes 0 and 2, which the page
    /// never displays, and the "BalancingAugmentations" GDB bonus. It clamps to [-100, 100] and
    /// scales by `(total + 100) * 0.0049999999` before the store, so the curve is already applied.
    static constexpr uint32_t kAttractivenessCount = 11;
    float attractiveness[kAttractivenessCount] = {};
    float attractiveness_fraction = 0.0f;

    /// The dog, when one exists: `dog_known` gates the whole page.
    bool dog_known = false;

    /// Levels 1..5, int32 not float: the constructor stores `1` with `stw` (`0x82808130`) and
    /// `sub_829D75F0` uses combat as a one-based index into a five-float table.
    int32_t dog_combat = 0;
    int32_t dog_searching = 0;
    int32_t dog_state = 0;
    char dog_name[64] = {};

    /// The Babel key the guest resolves when no custom name is set: `c20+48`, 0xD1E14CE2 ('Dog')
    /// on an unnamed dog. `dog_name` carries the custom LHString at `c20+52` when it has a length.
    uint32_t dog_name_key = 0;

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
inline void PublishHeroDerived(const HeroDerived& sampled) {
  refii_bridge_publish_hero_derived(&sampled);
}

inline HeroDerived HeroDerivedSnapshot() {
  HeroDerived out;
  refii_bridge_hero_derived_snapshot(&out);
  return out;
}

/// The completed-quest list, same contract; the store is fixed-size.
constexpr uint32_t kMaxCompletedQuests = 256;

inline void PublishQuests(const QuestEntry* entries, uint32_t count) {
  refii_bridge_publish_quests(entries, count);
}

uint32_t QuestSnapshot(QuestEntry* out, uint32_t capacity);

/// ONE FAMILY MEMBER, as the world map's `Family` category lists them.
///
/// The guest's collector `sub_8246F020` builds a vector of 52-byte records carrying a great deal
/// more — a 64-bit entity UID at `record+0`, two fast-travel graph answers at `+20`/`+24`, three
/// mood bands and an event line. The world-map ROW uses exactly one of them: `sub_829A42C8`
/// walks the vector from `begin + 8` in steps of 52 and passes only that field, so the name is
/// all this carries.
/// ONE FAMILY MEMBER, as the family PAGE reads them.
///
/// `sub_8246F020` builds 52-byte records and `sub_829B04E0` — the page filler — takes ten
/// already-formatted `LHString`s straight off fixed offsets and one lookup. Every one of these
/// is the guest's own sentence: the mood bands, the upkeep figures and the two fast-travel
/// answers are computed into the record when it is built, so nothing here is re-derived.
///
///     +8  PageTitle        +12 Group.ChildrenNames    +16 the level (-> region)
///     +20 DistanceAmount   +24 TravelAmount           +28 Note
///     +32 DailyUpkeep      +36 TotalUpkeep            +40 OverallHappiness
///     +44 HomeHappiness    +48 UpkeepHappiness
struct FamilyMember {
    char name[64] = {};
    char children[64] = {};

    /// `sub_82518490(regionMgr, record+16)` — a TAG, like the job page's region line.
    char region[64] = {};
    char distance[64] = {};
    char travel[64] = {};

    /// The member's event line, which is a sentence rather than a figure.
    char note[192] = {};
    char daily_upkeep[64] = {};
    char total_upkeep[64] = {};
    char happiness[64] = {};
    char home_happiness[64] = {};
    char upkeep_happiness[64] = {};

    /// The A-button prompt's Babel TAG, or empty when the guest hides the button.
    /// `sub_82470A78`'s tree — the FAMILY one, which uses `sub_82470FE8` where the jobs page's
    /// `sub_824708D8` uses `sub_82470F80`.
    char prompt[64] = {};
};

constexpr uint32_t kMaxFamilyMembers = 32;
inline void PublishFamily(const FamilyMember* members, uint32_t count) {
  refii_bridge_publish_family(members, count);
}

uint32_t FamilySnapshot(FamilyMember* out, uint32_t capacity);

/// THE REAL ESTATE SCREEN'S ONE BUILDING — screen 28.
///
/// Its left column is a list of COMMANDS (Buy, Sell, Rent Out, Evict...), not of properties:
/// there is exactly one building, resolved when the screen opens, and no selection-change event
/// to hook. So this is a single record rather than a list, and it is sampled while the screen
/// stands.
///
/// WHAT CROSSES IS DATA, NOT PRESENTATION. Every number here is the guest's own answer —
/// `sub_8282AEB0` (value), `sub_8282C1C0` (economy), `sub_8282C110` (income), `sub_8282BCA0`
/// (sale floor), `sub_8282BA48` (purchase) and the three not-owned deltas — because each of
/// them averages or walks something the host has no business reproducing: prosperity off a
/// world component, a GDB adjustment array matched against the building-type mask, an
/// occupant-opinion term weighted over five personality axes. The FORMATTING is Godot's: the
/// star bin, the `<gold_img>` prefix, the negative recolour, which of the four Variable rows
/// stands, and the HOUSE-versus-SHOP legend are all decided from these fields.
struct RealEstate {
    bool known = false;

    /// The GDB record the page's text hangs off — `BuildingName`, `BuildingAddress`,
    /// `BuildingAnecdotes`, `BasicSalePrice`, `CanSellOncePurchased`. It is NOT the building
    /// entity's own record: `sub_821DA3B0` takes the entity's def handle at `BE+28`, then a
    /// property named after the component's class, and the entry-ref THAT yields is this hash.
    uint32_t record = 0;

    /// `*(u32*)(CECBuilding+28)` — the BuildingType bitmask. Bit 0 HOME, then DOCK, FARM,
    /// TOWNHALL, WAREHOUSE, SHOP, BAR, BORDELLO, UNIQUE, STALL, HOUSE, TEMPLE.
    /// `mask & 0xEFE` is "is a business", the guest's own test at 0x82999CF8.
    uint32_t type_mask = 0;

    bool owned = false;
    bool has_tenant = false;

    int32_t value = 0;
    int32_t economy = 0;
    int32_t decoration = 0;
    int32_t income = 0;
    int32_t sale_price = 0;

    /// Owned only: `sub_8282BCA0 - sub_8282AEB0`, the negative selling fee.
    int32_t selling_fees = 0;

    /// Not owned only: the three price deltas the page lists as Variable1..3.
    int32_t not_vacant = 0;
    int32_t owner_killed = 0;
    int32_t hero_opinion = 0;

    /// `sub_823C8510` on the TENANT when there is one, else on the OWNER when the hero is not
    /// the owner. Empty when the entity is gone (the page's "OTHER" case) and empty when the
    /// hero owns it — the guest never names the hero this way; see `title_record`.
    char occupant[128] = {};

    /// A HERO-OWNED PROPERTY IS NAMED AFTER THE HERO'S TITLE, not through the creature-name
    /// path. `sub_82998FB0` branches on ownership first (0x8299959c) and, when the hero owns
    /// the building, calls `sub_824D8A10(&text, heroComponent57)`: the lazy GDB ref at
    /// `comp57+92` (`{hash, cache}`, resolved by `sub_821B24F8`), its `HeroTitleComponent`
    /// reference, that entry's `TextTag`, through Babel. This is that hash; 0 when the hero
    /// holds no title, which the guest draws as an empty name. `sub_823C8510` cannot name the
    /// hero: it carries no component 20.
    uint32_t title_record = 0;

    /// The GDB record carrying the `Bonuses` array. NOT `record`: `sub_82917F60` runs the same
    /// `sub_821DA3B0` indirection over component **184**, so the array hangs off that
    /// component's own sub-entry. The host reads the array and each element's
    /// `ShortNameTextTag` from its own GDB; no string crosses.
    uint32_t bonuses_record = 0;
};

inline void PublishRealEstate(const RealEstate& estate) {
  refii_bridge_publish_real_estate(&estate);
}

RealEstate RealEstateSnapshot();

/// ONE JOB'S JOURNEY — the world map's job page.
///
/// `sub_829AFE58` fills `Lines.DistanceAmount` and `Lines.TravelAmount` by handing the quest's
/// `ObjectiveLevel` to the SAME two providers the region page uses — `sub_823C9000` and
/// `sub_823C8BE0` — and that LHString is inline on the quest at `+132`, so `quest+132` is
/// already the handle address they take.
///
/// It passes `0` as the travel provider's third argument where the region page passes `1`, and
/// `sub_823C8CE0` appends nothing for a whole-hour count of zero — so a job under an hour away
/// legitimately shows an empty travel time. That is the guest's own behaviour, kept.
struct QuestTravel {
    bool known = false;

    /// The quest this answers for; the pump replies a tick late, so a page must check.
    uint32_t record = 0;
    char distance[64] = {};
    char travel[64] = {};

    /// The objective level's REGION, as the narrow tag the page sets with `sub_82425F40`.
    /// `sub_82518490` finds it by walking the region container for the one that owns the
    /// level; the tag is the region object's own word 0.
    char region[64] = {};

    /// The A-button prompt's Babel TAG, or empty when the guest hides the button outright.
    /// `sub_824708D8` picks between three tags and a hidden button off four guest predicates,
    /// so the pump runs them rather than the host guessing at travel state.
    char prompt[64] = {};

    /// "Time Remaining", already worded. `GUI_SHARED_X_DAYS` is a PRINTF format string, not a
    /// `%1` template — `sub_823C9228` formats the number with `sub_823B0900` and hands both to
    /// `sub_82B3F250`. Substituting `%1` into it host-side left the format string on screen.
    char days[64] = {};
};

/// Which quest the host wants a journey for; the pump answers for that one only.
void RequestQuestTravel(uint32_t record);
inline uint32_t RequestedQuestTravel() {
  return refii_bridge_requested_quest_travel();
}

inline void PublishQuestTravel(const QuestTravel& travel) {
  refii_bridge_publish_quest_travel(&travel);
}

QuestTravel QuestTravelSnapshot();

/// ONE SHOP ROW'S ASKING PRICE.
///
/// What a shop CHARGES is not the item's worth: `sub_82992F90` — the guest's own price display
/// — resolves the shopkeeper off screen 22, takes its `CECShopkeeper` (component 27) and calls
/// `sub_82872910` to buy or `sub_828727B8` to sell. Both weight a base price by the
/// shopkeeper's opinion axes and the shop's wealth, so there is no markup ratio to reproduce.
struct ShopPrice {
    bool known = false;

    /// The row this answers for — a per-population TICKET, never a record hash.
    uint32_t ticket = 0;
    int32_t price = 0;

    /// The item's GDB record: `*(*holder + 4)`, the lazy ref `sub_8296B310` resolves with
    /// `sub_821B24F8` to read `BasePrice`. Zero when the ticket did not resolve.
    uint32_t record = 0;
};

void RequestShopPrice(uint32_t ticket, bool sell);

/// The clothing screen's (16) preview mannequin entity, resolved on the guest thread by the
/// ticket at `screen+44` through the preview manager `*(GUI+8)` and published every tick — 0 when
/// the screen is not up. Crosses through the bridge state like every other pump answer: the
/// bridge does not link the pump's hooks.
inline void PublishClothingMannequin(uint32_t entity) {
  refii_bridge_publish_clothing_mannequin(entity);
}

uint32_t ClothingMannequinSnapshot();
inline uint32_t RequestedShopPrice(bool* sell) {
  int32_t selling = 0;
  const uint32_t ticket = refii_bridge_requested_shop_price(&selling);
  if (sell != nullptr) {
    *sell = selling != 0;
  }
  return ticket;
}

inline void PublishShopPrice(const ShopPrice& price) {
  refii_bridge_publish_shop_price(&price);
}

ShopPrice ShopPriceSnapshot();

/// Which region the host wants sampled; the pump answers for that one only.
void RequestWorldMapRegion(uint32_t index);

inline uint32_t RequestedWorldMapRegion() {
  return refii_bridge_requested_world_map_region();
}

inline void PublishWorldMapRegion(const WorldMapRegion& region) {
  refii_bridge_publish_world_map_region(&region);
}

WorldMapRegion WorldMapRegionSnapshot();

}  // namespace refii::vfs

#endif  // REFII_VFS_CORE_HERO_DERIVED_H_
