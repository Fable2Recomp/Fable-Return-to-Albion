// The headless Gameface core — see core/headless_presentation.h for the contract. PAYLOAD side:
// compiled into each title's module (REFII_PAYLOAD_CORE_SOURCES); it calls the guest through the
// title's GamefaceTable and reaches the host only through C entries (core/payload_bridge.h).
//
// THE CREATE. The maker (CGameface vtable +28) makes, names and gives an engine to a
// CPresentation with the guest's own calls, and does none of the rest: no kernel list, no
// deserialise, no renderer. The root (+500, service +8) is made here; every other element is made
// as the guest names it (child-by-name, MakeNamed) or walks it (the child walk, ExpandBeforeWalk),
// from the file's element table.
//
// THE SCENE. +24 is called unchecked as scene vf +16 after a slide change; it is a zeroed block
// with a guest copy of CScene's vtable whose refresh slot is the presentation's own `blr` and whose
// render-list slot answers an empty list.
//
// THE DESTROY. A headless presentation is in neither the renderer nor the kernel's list. The
// fields that point at host-owned guest memory are cleared (the service dtor frees +500, the
// params dtor +596, Mgr:Slides +628, each with the kernel free), then slot 0 with 0 — which frees
// the engine — and the kernel free of the presentation; then the elements, the block and the
// slide records.

#include "core/headless_presentation.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <cctype>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <utility>

#include <rex/logging.h>

#include "core/host_asset_reader.h"
#include "core/payload_bridge.h"
#include "core/told_statements.h"

namespace refii::headless {
namespace {

// ---------------------------------------------------------------------------------------------
// Guest memory, big-endian.

uint32_t LoadU32(const uint8_t* base, uint32_t at) {
    uint32_t v = 0;
    std::memcpy(&v, base + at, sizeof v);
    return __builtin_bswap32(v);
}

uint16_t LoadU16(const uint8_t* base, uint32_t at) {
    uint16_t v = 0;
    std::memcpy(&v, base + at, sizeof v);
    return __builtin_bswap16(v);
}

uint8_t LoadU8(const uint8_t* base, uint32_t at) { return base[at]; }

void StoreU32(uint8_t* base, uint32_t at, uint32_t value) {
    const uint32_t v = __builtin_bswap32(value);
    std::memcpy(base + at, &v, sizeof v);
}

void StoreU16(uint8_t* base, uint32_t at, uint16_t value) {
    const uint16_t v = __builtin_bswap16(value);
    std::memcpy(base + at, &v, sizeof v);
}

void StoreU8(uint8_t* base, uint32_t at, uint8_t value) { base[at] = value; }

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

bool Milestone(uint64_t n) { return n <= 32u || (n & (n - 1u)) == 0u; }

// ---------------------------------------------------------------------------------------------
// The element as the guest's deserialiser lays it out.

constexpr uint32_t kElemName = 0u;
constexpr uint32_t kElemParent = 12u;
constexpr uint32_t kElemSibling = 16u;
constexpr uint32_t kElemChild = 20u;
constexpr uint32_t kElemFlags = 28u;
constexpr uint32_t kElemAttributeCount = 31u;
constexpr uint32_t kElemSlideBase = 40u;
constexpr uint32_t kElemSlideCount = 42u;
constexpr uint32_t kElemTime = 48u;
constexpr uint32_t kTimelineAttributes = 68u;  // flag 0x0001 (the 68-byte timeline element)
constexpr uint32_t kPlainAttributes = 32u;     // the 32-byte element
constexpr uint32_t kElementMinimum = 0x100u;   // > 68: room for the timeline fields +40..+64
constexpr uint16_t kFlagOwnsTimeline = 0x0001u;
constexpr uint16_t kFlagTimeWindow = 0x0002u;
constexpr uint16_t kFlagActive = 0x2000u;
constexpr uint32_t kAttributeRecord = 8u;
constexpr uint32_t kMaxAttributes = 255u;      // the count is a byte
// A fileless element's room for the attributes its setters name (position, rotation, opacity,
// scale, and a spare).
constexpr uint32_t kFilelessRecords = 12u;
constexpr uint32_t kFloatRecord = 3u << 2;     // the record word's type field: 3 = float
constexpr uint32_t kSlideRecord = 32u;         // Mgr:Slides +8: name[16], +16 flags, +20 start, +24 end, +28 actions

// The host block behind one presentation: the stand-in scene, its empty list, the params record.
constexpr uint32_t kBlockScene = 0x000u;       // 0x700 (> the render-list offset)
constexpr uint32_t kBlockRenderList = 0x700u;  // 0x20
constexpr uint32_t kBlockParamRecord = 0x720u; // {Parameters, 0}
constexpr uint32_t kBlockSize = 0x730u;

constexpr uint32_t kCharStringMeasure = 0xFFFFFFFFu;  // the ctor's length: measure the text

constexpr int32_t kNoOrdinal = -1;
constexpr int32_t kOrdinalByName = -2;

// ---------------------------------------------------------------------------------------------
// THE FILE: the .bgf's element table and slide records, as far as a headless tree needs them.

struct FileTable {
    struct Element {
        uint32_t name = 0;
        int32_t parent = -1;
        uint8_t flags = 0;  // bit 0 active, bit 2 a component (its own timeline)
        std::vector<std::pair<uint32_t, uint32_t>> strings;  // attribute id, string index
        std::vector<std::pair<uint32_t, uint32_t>> records;  // numeric: the store's word, value
        uint16_t slide_base = 0;
        uint8_t slide_count = 0;
        bool OwnsTimeline() const { return (flags & 4u) != 0u; }
    };
    /// One of a slide's entries: the element, its active bit, the numeric attribute commands (the
    /// store's word, value) and the track refs (`on << 15 | 1 << 14 | track`).
    struct Entry {
        int32_t element = -1;
        bool active = false;
        std::vector<std::pair<uint32_t, uint32_t>> commands;
        std::vector<uint16_t> refs;
    };
    struct Slide {
        char name[16] = {};
        uint8_t flags = 0;
        uint32_t start = 0;
        uint32_t end = 0;
        std::vector<Entry> entries;
    };
    /// One animation track: the element, the attribute id it drives, its keys.
    struct Track {
        int32_t element = -1;
        uint32_t attribute = 0;
        bool active = false;
        bool dynamic = false;
        uint32_t first_key = 0;
        uint16_t key_count = 0;
    };
    std::vector<Element> elements;
    std::vector<std::vector<int32_t>> children;
    std::vector<std::string> strings;
    std::vector<Slide> slides;
    std::vector<Track> tracks;
    std::vector<std::array<uint32_t, 6>> keys;  // time, value, c1 time, c1 value, c2 time, c2 value: float bits

    int32_t ChildNamed(int32_t ordinal, uint32_t name) const {
        if (ordinal < 0 || static_cast<size_t>(ordinal) >= children.size()) {
            return kNoOrdinal;
        }
        for (const int32_t child : children[static_cast<size_t>(ordinal)]) {
            if (elements[static_cast<size_t>(child)].name == name) {
                return child;
            }
        }
        return kNoOrdinal;
    }

    const std::string* StringAttribute(int32_t ordinal, uint32_t id) const {
        if (ordinal < 0 || static_cast<size_t>(ordinal) >= elements.size()) {
            return nullptr;
        }
        for (const auto& [attribute, index] : elements[static_cast<size_t>(ordinal)].strings) {
            if (attribute == id && index < strings.size()) {
                return &strings[index];
            }
        }
        return nullptr;
    }
};

class BgfReader {
public:
    BgfReader(const std::vector<uint8_t>& bytes, bool big_endian) : bytes_(bytes), big_endian_(big_endian) {}
    bool U32(uint32_t* out) {
        if (at_ + 4u > bytes_.size()) {
            return false;
        }
        const uint8_t* b = bytes_.data() + at_;
        *out = big_endian_ ? (uint32_t{b[0]} << 24) | (uint32_t{b[1]} << 16) | (uint32_t{b[2]} << 8) | b[3]
                           : (uint32_t{b[3]} << 24) | (uint32_t{b[2]} << 16) | (uint32_t{b[1]} << 8) | b[0];
        at_ += 4u;
        return true;
    }
    bool U16(uint16_t* out) {
        if (at_ + 2u > bytes_.size()) {
            return false;
        }
        const uint8_t* b = bytes_.data() + at_;
        *out = big_endian_ ? static_cast<uint16_t>((b[0] << 8) | b[1]) : static_cast<uint16_t>((b[1] << 8) | b[0]);
        at_ += 2u;
        return true;
    }
    bool U8(uint8_t* out) {
        if (at_ >= bytes_.size()) {
            return false;
        }
        *out = bytes_[at_++];
        return true;
    }
    bool Skip(size_t n) {
        if (at_ + n > bytes_.size()) {
            return false;
        }
        at_ += n;
        return true;
    }
    size_t At() const { return at_; }
    void Seek(size_t at) { at_ = at; }
    const uint8_t* Data() const { return bytes_.data(); }
    size_t Size() const { return bytes_.size(); }

private:
    const std::vector<uint8_t>& bytes_;
    bool big_endian_;
    size_t at_ = 0;
};

/// The store's word for a file attribute's packed word (id bits 0-26, type 27-29, two high bits):
/// the deserialiser's own conversion (TU1 0x82bc8308, disc 0x82bc1c38) —
/// `(id << 5) | (type << 2) | (bit 30 << 1) | bit 31`.
uint32_t StoreWord(uint32_t p) {
    return ((((((p >> 2) | (p & 0xC0000000u)) >> 4) & 0x7FFFFFFu) | (p & 0xF8000000u)) >> 25 & 0x1Fu) | (p << 5);
}

/// The Animation section's tracks into `table` (nothing when the section does not hold `count`
/// tracks exactly).
void ParseTracks(BgfReader& r, size_t end, uint32_t count, FileTable& table) {
    std::vector<FileTable::Track> tracks;
    std::vector<std::array<uint32_t, 6>> keys;
    if (count > 0x100000u) {
        return;
    }
    tracks.reserve(count);
    for (uint32_t i = 0; i < count; ++i) {
        FileTable::Track track;
        uint32_t element = 0;
        uint8_t active = 0;
        uint8_t dynamic = 0;
        if (!r.U32(&element) || !r.U32(&track.attribute) || !r.U8(&active) || !r.U8(&dynamic) ||
            !r.U16(&track.key_count) || r.At() + size_t{track.key_count} * 24u > end) {
            return;
        }
        track.element = static_cast<int32_t>(element);
        track.active = active == 1u;   // `v32 == 1` 0x82c303d0 -> bit 31
        track.dynamic = dynamic == 1u;  // `v33 == 1` 0x82c303c4 -> bit 30
        track.first_key = static_cast<uint32_t>(keys.size());
        for (uint16_t k = 0; k < track.key_count; ++k) {
            uint32_t w[6] = {};
            for (uint32_t& word : w) {
                if (!r.U32(&word)) {
                    return;
                }
            }
            // The three times are unsigned integers in the file (`lwz`, `std`, `fcfid`) and floats in
            // the store (0x82c3050c..0x82c3056c).
            const float time = static_cast<float>(w[0]);
            const float first = static_cast<float>(w[2]);
            const float second = static_cast<float>(w[4]);
            std::array<uint32_t, 6> key{};
            std::memcpy(&key[0], &time, 4);
            key[1] = w[1];
            std::memcpy(&key[2], &first, 4);
            key[3] = w[3];
            std::memcpy(&key[4], &second, 4);
            key[5] = w[5];
            keys.push_back(key);
        }
        tracks.push_back(track);
    }
    if (r.At() != end) {
        return;
    }
    table.tracks = std::move(tracks);
    table.keys = std::move(keys);
}

/// The .bgf: header 0x20, then size-prefixed sections in order — Memory (19 counts: elements =
/// [0] + [1], strings = [6], slides = [14]), String, Element, Contract, Animation, Logic, Params,
/// Slide. An element record: name hash, parent ordinal, object id, type byte, flags byte (bit 2
/// adds a pad byte and a word), attribute count, 8-byte attribute records. A slide record: name
/// (16 bytes), owner ordinal, flags byte, start, end, entries (ordinal, active byte, commands
/// (8 bytes each), actions (4 bytes each)) — the loader's (TU1 `sub_82BC8F30`) own reads. A
/// component's slides are the run its own owner index names: base = the first one's index,
/// count = how many (`sub_82BB3778`).
std::shared_ptr<const FileTable> ParseBgfTable(const std::vector<uint8_t>& d) {
    constexpr size_t kSectionsStart = 0x20u;
    constexpr uint32_t kMemoryCounts = 19u;
    constexpr uint8_t kComponentFlag = 4u;
    constexpr uint32_t kStringValue = 5u;
    if (d.size() < kSectionsStart || std::memcmp(d.data() + 6, "AnarkBGF", 8) != 0) {
        return nullptr;
    }
    BgfReader r(d, d[0] == 1u);
    uint32_t version = 0;
    r.Seek(14u);
    if (!r.U32(&version) || version != 3u) {
        return nullptr;
    }
    r.Seek(kSectionsStart);
    uint32_t memory_size = 0;
    if (!r.U32(&memory_size)) {
        return nullptr;
    }
    const size_t memory_end = r.At() + memory_size;
    uint32_t counts[kMemoryCounts] = {};
    for (uint32_t i = 0; i < kMemoryCounts; ++i) {
        if (!r.U32(&counts[i])) {
            return nullptr;
        }
    }
    if (memory_end > d.size()) {
        return nullptr;
    }
    r.Seek(memory_end);
    const uint64_t element_count = uint64_t{counts[0]} + counts[1];
    const uint32_t string_count = counts[6];
    const uint32_t slide_count = counts[14];
    if (element_count > 0x100000u || string_count > 0x100000u || slide_count > 0x10000u) {
        return nullptr;
    }

    auto table = std::make_shared<FileTable>();
    uint32_t section = 0;
    if (!r.U32(&section)) {
        return nullptr;
    }
    table->strings.reserve(string_count);
    for (uint32_t i = 0; i < string_count; ++i) {
        uint32_t length = 0;
        if (!r.U32(&length) || r.At() + length > r.Size()) {
            return nullptr;
        }
        table->strings.emplace_back(reinterpret_cast<const char*>(r.Data() + r.At()), length);
        r.Skip(length);
    }

    if (!r.U32(&section)) {
        return nullptr;
    }
    table->elements.resize(static_cast<size_t>(element_count));
    table->children.resize(static_cast<size_t>(element_count));
    for (size_t i = 0; i < table->elements.size(); ++i) {
        FileTable::Element& e = table->elements[i];
        uint32_t parent = 0;
        uint32_t object = 0;
        uint8_t type = 0;
        uint32_t attributes = 0;
        if (!r.U32(&e.name) || !r.U32(&parent) || !r.U32(&object) || !r.U8(&type) || !r.U8(&e.flags)) {
            return nullptr;
        }
        if ((e.flags & kComponentFlag) != 0u && !r.Skip(5u)) {
            return nullptr;
        }
        if (!r.U32(&attributes) || attributes > 0x10000u) {
            return nullptr;
        }
        for (uint32_t a = 0; a < attributes; ++a) {
            uint32_t packed = 0;
            uint32_t value = 0;
            if (!r.U32(&packed) || !r.U32(&value)) {
                return nullptr;
            }
            if (((packed >> 27) & 7u) == kStringValue) {
                e.strings.emplace_back(packed & 0x07FFFFFFu, value);
            } else {
                e.records.emplace_back(StoreWord(packed), value);
            }
        }
        e.parent = static_cast<int32_t>(parent);
        if (e.parent >= 0 && static_cast<size_t>(e.parent) < i) {
            table->children[static_cast<size_t>(e.parent)].push_back(static_cast<int32_t>(i));
        }
    }

    // Contract, Animation, Logic, Params: framed. The Animation section's tracks (the deserialiser's
    // own reads, F3 `sub_82C302A0`: per track the element ordinal, the attribute id, active and
    // dynamic bytes, the key count, then 24-byte keys — u32 time, f32 value, i32 and f32 the first
    // control, u32 and f32 the second; the count is the memory section's [8]); the others skipped.
    for (int s = 0; s < 4; ++s) {
        uint32_t size = 0;
        if (!r.U32(&size) || r.At() + size > r.Size()) {
            return table;  // the elements stand without slides
        }
        const size_t end = r.At() + size;
        if (s == 1) {
            ParseTracks(r, end, counts[8], *table);
        }
        r.Seek(end);
    }
    uint32_t slide_section = 0;
    if (!r.U32(&slide_section)) {
        return table;
    }
    std::vector<FileTable::Slide> slides;
    std::vector<int32_t> owners;
    slides.reserve(slide_count);
    for (uint32_t i = 0; i < slide_count; ++i) {
        FileTable::Slide slide;
        if (r.At() + 16u > r.Size()) {
            return table;
        }
        std::memcpy(slide.name, r.Data() + r.At(), 16u);
        r.Skip(16u);
        uint32_t owner = 0;
        uint32_t entries = 0;
        if (!r.U32(&owner) || !r.U8(&slide.flags) || !r.U32(&slide.start) || !r.U32(&slide.end) ||
            !r.U32(&entries) || entries > 0x10000u) {
            return table;
        }
        // Each entry (F3 `sub_82C30B38`): ordinal, active byte, commands (packed attribute word,
        // value), refs (two bytes — the first 0 for a track, the second the on bit — then the
        // index; 0x82c30f34..0x82c30fe8). String commands and logic refs are not kept.
        slide.entries.reserve(entries);
        for (uint32_t j = 0; j < entries; ++j) {
            FileTable::Entry entry;
            uint32_t ordinal = 0;
            uint8_t active = 0;
            uint32_t commands = 0;
            uint32_t refs = 0;
            if (!r.U32(&ordinal) || !r.U8(&active) || !r.U32(&commands) || commands > 0x10000u) {
                return table;
            }
            for (uint32_t c = 0; c < commands; ++c) {
                uint32_t packed = 0;
                uint32_t value = 0;
                if (!r.U32(&packed) || !r.U32(&value)) {
                    return table;
                }
                if (((packed >> 27) & 7u) != kStringValue) {
                    entry.commands.emplace_back(StoreWord(packed), value);
                }
            }
            if (!r.U32(&refs) || refs > 0x10000u) {
                return table;
            }
            for (uint32_t a = 0; a < refs; ++a) {
                uint8_t kind = 0;
                uint8_t on = 0;
                uint16_t index = 0;
                if (!r.U8(&kind) || !r.U8(&on) || !r.U16(&index)) {
                    return table;
                }
                if (kind == 0u) {
                    entry.refs.push_back(static_cast<uint16_t>((on != 0u ? 0x8000u : 0u) | 0x4000u | (index & 0x3FFFu)));
                }
            }
            entry.element = static_cast<int32_t>(ordinal);
            entry.active = active != 0u;
            slide.entries.push_back(std::move(entry));
        }
        slides.push_back(slide);
        owners.push_back(static_cast<int32_t>(owner));
    }
    // Each owner's run, as the builder numbers it: base at its first slide, count of its slides.
    for (size_t i = 0; i < owners.size(); ++i) {
        const int32_t owner = owners[i];
        if (owner < 0 || static_cast<size_t>(owner) >= table->elements.size()) {
            continue;
        }
        FileTable::Element& e = table->elements[static_cast<size_t>(owner)];
        if (e.slide_count == 0u) {
            e.slide_base = static_cast<uint16_t>(i);
        }
        if (e.slide_count < 0xFFu) {
            ++e.slide_count;
        }
    }
    table->slides = std::move(slides);
    return table;
}

/// The file a headless presentation names, read once per path (`GAME:\data\…` → the mount's
/// `data/…`, then without `data/`).
std::shared_ptr<const FileTable> FileTableFor(const std::string& file, const char* title) {
    static std::mutex m;
    static std::unordered_map<std::string, std::shared_ptr<const FileTable>> tables;
    std::string path = file;
    std::replace(path.begin(), path.end(), '\\', '/');
    const size_t device = path.find(':');
    const size_t slash = path.find('/');
    if (device != std::string::npos && (slash == std::string::npos || device < slash)) {
        path.erase(0, device + 1);
    }
    while (!path.empty() && path.front() == '/') {
        path.erase(0, 1);
    }
    std::string key = path;
    std::transform(key.begin(), key.end(), key.begin(),
                   [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    {
        std::lock_guard<std::mutex> lock(m);
        const auto it = tables.find(key);
        if (it != tables.end()) {
            return it->second;
        }
    }
    std::shared_ptr<const FileTable> table;
    std::vector<uint8_t> bytes;
    const bool data = key.rfind("data/", 0) == 0;
    if (refii::vfs::ReadHostAsset(path, bytes) || (data && refii::vfs::ReadHostAsset(path.substr(5), bytes))) {
        table = ParseBgfTable(bytes);
    }
    size_t records = 0;
    if (table) {
        for (const FileTable::Element& e : table->elements) {
            records += e.records.size();
        }
    }
    REXLOG_INFO("Refii headless presentation ({}): '{}' {} ({} element(s), {} numeric attribute(s), {} "
                "slide(s), {} byte(s))",
                title, file,
                table ? "read: its elements answer the file's children, attributes, flags and slides"
                      : "DID NOT READ — its elements answer no children, attributes or slides",
                table ? table->elements.size() : 0u, records, table ? table->slides.size() : 0u, bytes.size());
    std::lock_guard<std::mutex> lock(m);
    tables[key] = table;
    return table;
}

// ---------------------------------------------------------------------------------------------
// The live presentations and their elements.

struct Headless {
    uint32_t presentation = 0;
    uint32_t root = 0;
    uint32_t block = 0;
    uint32_t slide_records = 0;
    uint32_t gameface = 0;
    uint32_t serial = 0;
    int32_t start_ms = 0;
    bool said_500 = false;
    bool said_1000 = false;
    std::string file;
    std::vector<uint32_t> elements;          // id - 1; the root is id 0
    std::shared_ptr<const FileTable> table;  // null when the file did not read
    std::unordered_set<uint32_t> expanded;   // elements whose file children are made
    uint32_t track_records = 0;              // Mgr:Animations +8 (THE FILE'S SLIDES AND TRACKS)
    uint32_t track_keys = 0;                 // Mgr:Animations +16
    uint32_t slide_entries = 0;              // the slide records' entry streams (+28)
    size_t tracks_laid = 0;
    size_t tracks_dark = 0;                  // tracks whose element could not be made
    size_t entries_laid = 0;
};

struct ElementState {
    uint32_t presentation = 0;
    uint32_t id = 0;
    int32_t ordinal = kNoOrdinal;
    uint32_t capacity = 0;  // attribute records the element has room for
};

std::mutex g_mutex;
std::vector<Headless> g_live;
std::unordered_map<uint32_t, ElementState> g_element_keys;
uint32_t g_scene_vtable = 0;
uint32_t g_next_serial = 0;
std::atomic<uint32_t> g_inert_record{0};
std::atomic<uint64_t> g_made{0};
std::atomic<uint64_t> g_freed{0};
std::atomic<uint64_t> g_named{0};
std::atomic<uint64_t> g_file_strings{0};
std::atomic<uint64_t> g_remembered{0};

Headless* LiveLocked(uint32_t presentation) {
    for (Headless& e : g_live) {
        if (e.presentation == presentation) {
            return &e;
        }
    }
    return nullptr;
}

// ---------------------------------------------------------------------------------------------
// Guest calls.

PPCFunc* Resolve(const GamefaceTable& t, uint32_t address) {
    static std::mutex m;
    static std::unordered_map<uint32_t, PPCFunc*> cache;
    if (address == 0u || t.find == nullptr) {
        return nullptr;
    }
    std::lock_guard<std::mutex> lock(m);
    const auto it = cache.find(address);
    if (it != cache.end()) {
        return it->second;
    }
    PPCFunc* fn = t.find(address);
    cache.emplace(address, fn);
    if (fn == nullptr) {
        REXLOG_ERROR("Refii headless presentation ({}): guest function 0x{:08X} is not a recompiled entry",
                     t.title, address);
    }
    return fn;
}

/// One guest call on a copy of `ctx` (frames below `sp`); answers r3.
uint32_t CallAt(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t sp, uint32_t address,
                uint32_t r3, uint32_t r4 = 0u, uint32_t r5 = 0u, uint32_t r6 = 0u, uint32_t r7 = 0u) {
    PPCFunc* fn = Resolve(t, address);
    if (fn == nullptr) {
        return 0u;
    }
    PPCContext call = ctx;
    call.r1.u32 = sp;
    call.r3.u32 = r3;
    call.r4.u32 = r4;
    call.r5.u32 = r5;
    call.r6.u32 = r6;
    call.r7.u32 = r7;
    call.ctr.u32 = address;
    fn(call, base);
    return call.r3.u32;
}

uint32_t Call(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t address, uint32_t r3,
              uint32_t r4 = 0u, uint32_t r5 = 0u, uint32_t r6 = 0u, uint32_t r7 = 0u) {
    return CallAt(t, ctx, base, ctx.r1.u32, address, r3, r4, r5, r6, r7);
}

uint32_t Slot(const uint8_t* base, uint32_t object, uint32_t offset) {
    return LoadU32(base, LoadU32(base, object) + offset);
}

uint32_t Alloc(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t size) {
    return t.alloc != nullptr ? t.alloc(ctx, base, size) : 0u;
}

void Free(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t address) {
    if (address != 0u && t.free != nullptr) {
        t.free(ctx, base, address);
    }
}

int32_t ClockMs(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t gameface) {
    const uint32_t clock = gameface + t.gameface_clock;
    return static_cast<int32_t>(Call(t, ctx, base, Slot(base, clock, t.clock_slot_ms), clock));
}

std::string GuestCString(const uint8_t* base, uint32_t at) {
    std::string out;
    if (!Plausible(at)) {
        return out;
    }
    for (uint32_t i = 0; i < 260u; ++i) {
        const char c = static_cast<char>(base[at + i]);
        if (c == '\0') {
            break;
        }
        out.push_back(c);
    }
    return out;
}

/// The value a global id word holds, masked to the store's 27 bits; 0 when the title has none.
uint32_t IdAt(const GamefaceTable& t, const uint8_t* base, uint32_t global) {
    return global != 0u ? LoadU32(base, global) & t.attribute_id_mask : 0u;
}

bool EnsureSceneVtable(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t presentation) {
    if (g_scene_vtable != 0u) {
        return true;
    }
    const uint32_t nullsub = Slot(base, presentation, t.pres_slot_nullsub);
    const uint32_t scene = Alloc(t, ctx, base, t.scene_slots * 4u);
    if (scene == 0u) {
        return false;
    }
    for (uint32_t i = 0; i < t.scene_slots; ++i) {
        StoreU32(base, scene + 4u * i, LoadU32(base, t.scene_vtable + 4u * i));
    }
    StoreU32(base, scene + 4u * t.scene_slot_refresh, nullsub);
    g_scene_vtable = scene;
    const bool resolved = Resolve(t, nullsub) != nullptr &&
                          Resolve(t, LoadU32(base, t.scene_vtable + 4u * t.scene_slot_list)) != nullptr;
    REXLOG_INFO("Refii headless presentation ({}): scene vtable 0x{:08X} (copy of 0x{:08X}, refresh = "
                "0x{:08X}); slots resolve: {}",
                t.title, scene, t.scene_vtable, nullsub, resolved ? "yes" : "NO");
    return true;
}

void DeletePresentation(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t presentation) {
    Call(t, ctx, base, Slot(base, presentation, t.pres_slot_dtor), presentation, 0u);
    const uint32_t allocator = Call(t, ctx, base, t.kernel_allocator, 0u);
    Call(t, ctx, base, t.kernel_free, allocator, presentation, t.presentation_size);
}

void NotifyGameface(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t gameface, uint32_t code,
                    uint32_t presentation) {
    const uint32_t callback = LoadU32(base, gameface + t.gameface_callback);
    if (callback != 0u) {
        Call(t, ctx, base, callback, code, presentation, 0u, LoadU32(base, gameface + t.gameface_callback_ctx));
    }
}

void EnsureInertRecord(const GamefaceTable& t, PPCContext& ctx, uint8_t* base) {
    if (g_inert_record.load() != 0u || t.quad_record_ctor == 0u || t.quad_record_size == 0u) {
        return;
    }
    const uint32_t record = Alloc(t, ctx, base, t.quad_record_size);
    if (record == 0u) {
        return;
    }
    std::memset(base + record, 0, t.quad_record_size);
    Call(t, ctx, base, t.quad_record_ctor, record);
    uint32_t none = 0u;
    if (!g_inert_record.compare_exchange_strong(none, record)) {
        Free(t, ctx, base, record);
        return;
    }
    REXLOG_INFO("Refii headless presentation ({}): inert quad record 0x{:08X} ({} bytes, guest ctor 0x{:08X})",
                t.title, record, t.quad_record_size, t.quad_record_ctor);
}

// ---------------------------------------------------------------------------------------------
// Elements.

uint32_t AttributesAt(const uint8_t* base, uint32_t element) {
    return element + ((LoadU16(base, element + kElemFlags) & kFlagOwnsTimeline) != 0u ? kTimelineAttributes
                                                                                     : kPlainAttributes);
}

/// The record of attribute `id` on `element`, or 0 (the lookup TU1 `sub_822848E0`'s walk).
uint32_t RecordOf(const uint8_t* base, uint32_t element, uint32_t id) {
    const uint32_t records = AttributesAt(base, element);
    const uint32_t count = LoadU8(base, element + kElemAttributeCount);
    for (uint32_t i = 0; i < count; ++i) {
        const uint32_t record = records + i * kAttributeRecord;
        if ((LoadU32(base, record) >> 5) == id) {
            return record;
        }
    }
    return 0u;
}

/// What a file element carries, as the deserialiser leaves it: flags, numeric records, and a
/// component's slide base and count. `id_start` / `id_end` are the start and end time ids.
uint16_t FileFlags(const FileTable::Element& e, bool root, uint32_t id_start, uint32_t id_end) {
    uint16_t flags = (e.flags & 1u) != 0u ? kFlagActive : 0u;
    if (root || e.OwnsTimeline()) {
        flags |= kFlagOwnsTimeline;  // the root always: the timeline-owner walk has no null exit
    }
    for (const auto& [word, value] : e.records) {
        (void)value;
        const uint32_t id = word >> 5;
        if ((id_start != 0u && id == id_start) || (id_end != 0u && id == id_end)) {
            flags |= kFlagTimeWindow;
        }
    }
    return flags;
}

/// The bytes an element takes: its fixed part and room for `records` attribute records.
uint32_t ElementBytes(bool owns_timeline, uint32_t records) {
    const uint32_t at = owns_timeline ? kTimelineAttributes : kPlainAttributes;
    return std::max(kElementMinimum, at + records * kAttributeRecord);
}

/// Lays `ordinal`'s file state into a zeroed element of `capacity` records (caller holds g_mutex).
void ApplyFile(uint8_t* base, const Headless& entry, uint32_t element, int32_t ordinal, bool root,
               uint32_t capacity, uint32_t id_start, uint32_t id_end) {
    if (!entry.table || ordinal < 0 || static_cast<size_t>(ordinal) >= entry.table->elements.size()) {
        StoreU16(base, element + kElemFlags, kFlagOwnsTimeline);
        return;
    }
    const FileTable::Element& e = entry.table->elements[static_cast<size_t>(ordinal)];
    const uint16_t flags = FileFlags(e, root, id_start, id_end);
    StoreU16(base, element + kElemFlags, flags);
    const uint32_t records = AttributesAt(base, element);
    const uint32_t count = std::min<uint32_t>({static_cast<uint32_t>(e.records.size()), capacity, kMaxAttributes});
    for (uint32_t i = 0; i < count; ++i) {
        StoreU32(base, records + i * kAttributeRecord, e.records[i].first);
        StoreU32(base, records + i * kAttributeRecord + 4u, e.records[i].second);
    }
    StoreU8(base, element + kElemAttributeCount, static_cast<uint8_t>(count));
    if ((flags & kFlagOwnsTimeline) != 0u && e.slide_count != 0u && entry.slide_records != 0u) {
        StoreU16(base, element + kElemSlideBase, e.slide_base);
        StoreU8(base, element + kElemSlideCount, e.slide_count);
    }
}

/// The file element `ordinal` resolves to under `parent_key` for `hash` (kOrdinalByName: the
/// parent's first file child of that name; a root-level name that is the file root's own answers
/// the root).
int32_t ResolveOrdinal(const Headless& entry, uint32_t parent, int32_t parent_ordinal, uint32_t hash,
                       int32_t ordinal) {
    if (ordinal != kOrdinalByName) {
        return ordinal;
    }
    if (!entry.table || entry.table->elements.empty() || parent_ordinal < 0) {
        return kNoOrdinal;
    }
    return parent == entry.root && entry.table->elements[0].name == hash
               ? 0
               : entry.table->ChildNamed(parent_ordinal, hash);
}

/// A new element `hash` under `parent` (caller holds g_mutex): sized for the file element it
/// stands for (or room for a fileless one's setters), laid out as the file has it, appended to the
/// parent's children, and stated to the host with its parent's id.
uint32_t MakeElementLocked(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, Headless& entry,
                           uint32_t parent, uint32_t hash, int32_t ordinal = kOrdinalByName) {
    const auto parent_key = g_element_keys.find(parent);
    if (parent_key == g_element_keys.end() || parent_key->second.presentation != entry.presentation) {
        return 0u;
    }
    ordinal = ResolveOrdinal(entry, parent, parent_key->second.ordinal, hash, ordinal);
    const FileTable::Element* file =
        entry.table && ordinal >= 0 && static_cast<size_t>(ordinal) < entry.table->elements.size()
            ? &entry.table->elements[static_cast<size_t>(ordinal)]
            : nullptr;
    const bool owns_timeline = file == nullptr || file->OwnsTimeline();
    const uint32_t capacity = std::min<uint32_t>(
        file != nullptr ? static_cast<uint32_t>(file->records.size()) : kFilelessRecords, kMaxAttributes);
    const uint32_t bytes = ElementBytes(owns_timeline, capacity);
    const uint32_t element = Alloc(t, ctx, base, bytes);
    if (element == 0u) {
        return 0u;
    }
    std::memset(base + element, 0, bytes);
    StoreU32(base, element + kElemName, hash);
    StoreU32(base, element + kElemParent, parent);
    ApplyFile(base, entry, element, file != nullptr ? ordinal : kNoOrdinal, false, capacity,
              IdAt(t, base, t.attr_start_time), IdAt(t, base, t.attr_end_time));
    const uint32_t first = LoadU32(base, parent + kElemChild);
    if (first == 0u) {
        StoreU32(base, parent + kElemChild, element);
    } else {
        uint32_t last = first;
        for (uint32_t next = LoadU32(base, last + kElemSibling); next != 0u; next = LoadU32(base, last + kElemSibling)) {
            last = next;
        }
        StoreU32(base, last + kElemSibling, element);
    }
    entry.elements.push_back(element);
    const uint32_t id = static_cast<uint32_t>(entry.elements.size());
    const uint32_t parent_id = parent_key->second.id;
    g_element_keys[element] = ElementState{entry.presentation, id, file != nullptr ? ordinal : kNoOrdinal, capacity};
    refii_bridge_told_headless_element(entry.presentation, entry.serial, id, parent_id, hash);
    return element;
}

/// THE FILE'S CHILDREN, made before the guest walks them (caller holds g_mutex): each file child
/// is the parent's existing element of that name (the k-th of a repeated name taking the k-th
/// made), else made here; the chain is relinked in the file's order, anything the file does not
/// carry after it. Once per element.
void ExpandLocked(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, Headless& entry, uint32_t parent,
                  int32_t ordinal) {
    if (!entry.table || ordinal < 0 || static_cast<size_t>(ordinal) >= entry.table->children.size() ||
        !entry.expanded.insert(parent).second) {
        return;
    }
    const std::vector<int32_t>& wanted = entry.table->children[static_cast<size_t>(ordinal)];
    if (wanted.empty()) {
        return;
    }
    std::vector<uint32_t> existing;
    for (uint32_t c = LoadU32(base, parent + kElemChild); c != 0u && existing.size() < 0x10000u;
         c = LoadU32(base, c + kElemSibling)) {
        existing.push_back(c);
    }
    std::unordered_map<uint32_t, std::vector<uint32_t>> by_name;
    for (const uint32_t c : existing) {
        by_name[LoadU32(base, c + kElemName)].push_back(c);
    }
    std::unordered_map<uint32_t, size_t> taken;
    std::unordered_set<uint32_t> placed;
    std::vector<uint32_t> order;
    order.reserve(wanted.size() + existing.size());
    for (const int32_t child : wanted) {
        const uint32_t name = entry.table->elements[static_cast<size_t>(child)].name;
        std::vector<uint32_t>& made = by_name[name];
        size_t& next = taken[name];
        uint32_t element = 0;
        if (next < made.size()) {
            element = made[next++];
            const auto key = g_element_keys.find(element);
            if (key != g_element_keys.end() && key->second.ordinal != child) {
                // Made fileless (or as another of the name): it takes this file element's state
                // when it has the room and has not been written yet.
                const FileTable::Element& file = entry.table->elements[static_cast<size_t>(child)];
                const bool fits = file.records.size() <= key->second.capacity &&
                                  ((LoadU16(base, element + kElemFlags) & kFlagOwnsTimeline) != 0u ||
                                   !file.OwnsTimeline());
                if (fits && LoadU8(base, element + kElemAttributeCount) == 0u) {
                    ApplyFile(base, entry, element, child, false, key->second.capacity,
                              IdAt(t, base, t.attr_start_time), IdAt(t, base, t.attr_end_time));
                }
                key->second.ordinal = child;
            }
        } else {
            element = MakeElementLocked(t, ctx, base, entry, parent, name, child);
        }
        if (element != 0u && placed.insert(element).second) {
            order.push_back(element);
        }
    }
    for (const uint32_t c : existing) {
        if (placed.insert(c).second) {
            order.push_back(c);
        }
    }
    StoreU32(base, parent + kElemChild, order.empty() ? 0u : order[0]);
    for (size_t i = 0; i < order.size(); ++i) {
        StoreU32(base, order[i] + kElemSibling, i + 1 < order.size() ? order[i + 1] : 0u);
    }
}

/// The presentation's slide records (Mgr:Slides +8), one per file slide: its name, start and end,
/// no actions (+16 = 0: no entries, play once, no end event) — a GoTo lands, sets the element's
/// current slide and restarts its timeline, and nothing else runs in the guest.
uint32_t MakeSlideRecords(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, const FileTable& table) {
    if (table.slides.empty() || t.pres_slide_records == 0u) {
        return 0u;
    }
    const uint32_t bytes = static_cast<uint32_t>(table.slides.size()) * kSlideRecord;
    const uint32_t records = Alloc(t, ctx, base, bytes);
    if (records == 0u) {
        return 0u;
    }
    std::memset(base + records, 0, bytes);
    for (size_t i = 0; i < table.slides.size(); ++i) {
        const uint32_t at = records + static_cast<uint32_t>(i) * kSlideRecord;
        std::memcpy(base + at, table.slides[i].name, 16u);
        StoreU32(base, at + 20u, table.slides[i].start);
        StoreU32(base, at + 24u, table.slides[i].end);
    }
    return records;
}

/// A slide record's +16 from the file's flags byte and its entry count, as the deserialiser's
/// slide maker (F3 `sub_82C385B8`, 0x82c386a4..0x82c386c8) packs it: bits 31..29 `(2 * f) | (f >> 2 &
/// 1)` — the play mode `f & 3` at 31..30, the paused bit `f & 4` at 29 — `f & 8` at 28, `f & 0x10`
/// at 27, the count in 27 bits.
uint32_t SlideWord(uint8_t flags, uint32_t entries) {
    const uint32_t mode = ((2u * flags) & 0x1FEu) | ((flags & 4u) != 0u ? 1u : 0u);
    return ((mode & 7u) << 29) | ((flags & 8u) != 0u ? 1u << 28 : 0u) | ((flags & 0x10u) != 0u ? 1u << 27 : 0u) |
           (entries & 0x7FFFFFFu);
}

/// THE FILE'S SLIDES AND TRACKS (caller holds g_mutex; core/headless_presentation.h): every element
/// a track or a slide entry names is made (its parents first, in the file's order), Mgr:Animations
/// is given the file's tracks over its keys, and each slide record its entries.
void LaySlidesAndTracksLocked(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, Headless& entry) {
    if (t.pres_animations == 0u || !entry.table || entry.slide_records == 0u) {
        return;
    }
    const FileTable& table = *entry.table;
    const int32_t count = static_cast<int32_t>(table.elements.size());
    std::vector<uint32_t> by_ordinal(table.elements.size(), 0u);
    for (const auto& [element, state] : g_element_keys) {
        if (state.presentation == entry.presentation && state.ordinal >= 0 && state.ordinal < count) {
            by_ordinal[static_cast<size_t>(state.ordinal)] = element;
        }
    }
    std::vector<bool> wanted(table.elements.size(), false);
    const auto want = [&](int32_t ordinal) {
        for (int32_t at = ordinal; at > 0 && at < count && !wanted[static_cast<size_t>(at)];
             at = table.elements[static_cast<size_t>(at)].parent) {
            wanted[static_cast<size_t>(at)] = true;
            if (table.elements[static_cast<size_t>(at)].parent >= at) {
                break;  // a parent after its child: the walk would not end
            }
        }
    };
    for (const FileTable::Track& track : table.tracks) {
        want(track.element);
    }
    for (const FileTable::Slide& slide : table.slides) {
        for (const FileTable::Entry& e : slide.entries) {
            want(e.element);
        }
    }
    for (int32_t ordinal = 1; ordinal < count; ++ordinal) {
        const FileTable::Element& file = table.elements[static_cast<size_t>(ordinal)];
        if (!wanted[static_cast<size_t>(ordinal)] || by_ordinal[static_cast<size_t>(ordinal)] != 0u ||
            file.parent < 0 || file.parent >= ordinal) {
            continue;
        }
        const uint32_t parent = by_ordinal[static_cast<size_t>(file.parent)];
        if (parent != 0u) {
            by_ordinal[static_cast<size_t>(ordinal)] = MakeElementLocked(t, ctx, base, entry, parent, file.name, ordinal);
        }
    }
    const auto element_at = [&](int32_t ordinal) {
        return ordinal >= 0 && ordinal < count ? by_ordinal[static_cast<size_t>(ordinal)] : 0u;
    };

    // Mgr:Animations: the records `{element, attribute, first key, count | active << 31 | dynamic
    // << 30}` (F3 `sub_82C302A0` 0x82c303e4..0x82c30408) and the keys. A track whose element could
    // not be made stays off: the track pass reads the element only for an active record (`if (*v8
    // >= 0)` before vf +4, F3 0x821ca0ec).
    const uint32_t manager = entry.presentation + t.pres_animations;
    if (!table.tracks.empty() && !table.keys.empty()) {
        const uint32_t records = Alloc(t, ctx, base, static_cast<uint32_t>(table.tracks.size()) * 16u);
        const uint32_t keys = Alloc(t, ctx, base, static_cast<uint32_t>(table.keys.size()) * 24u);
        if (records != 0u && keys != 0u) {
            for (size_t k = 0; k < table.keys.size(); ++k) {
                for (uint32_t w = 0; w < 6u; ++w) {
                    StoreU32(base, keys + static_cast<uint32_t>(k) * 24u + w * 4u, table.keys[k][w]);
                }
            }
            for (size_t i = 0; i < table.tracks.size(); ++i) {
                const FileTable::Track& track = table.tracks[i];
                const uint32_t element = element_at(track.element);
                const uint32_t at = records + static_cast<uint32_t>(i) * 16u;
                StoreU32(base, at, element);
                StoreU32(base, at + 4u, track.attribute);
                StoreU32(base, at + 8u, track.first_key);
                StoreU32(base, at + 12u, (track.active && element != 0u ? 0x80000000u : 0u) |
                                             (track.dynamic ? 0x40000000u : 0u) | (track.key_count & 0x3FFFFFFFu));
                ++(element != 0u ? entry.tracks_laid : entry.tracks_dark);
            }
            StoreU32(base, manager + 8u, records);
            StoreU32(base, manager + 12u, static_cast<uint32_t>(table.tracks.size()));
            StoreU32(base, manager + 16u, keys);
            StoreU32(base, manager + 20u, static_cast<uint32_t>(table.keys.size()));
            entry.track_records = records;
            entry.track_keys = keys;
        } else {
            Free(t, ctx, base, records);
            Free(t, ctx, base, keys);
        }
    }

    // The slides' entries: `{element, commands << 1, refs << 1 | active}`, the commands, the refs
    // padded to a word — the stream the slide enter (F3 `sub_821CA668`) and the GoTo's track
    // re-key (`sub_8228AD90`) walk, each entry ending `((2 * (refs + 1)) & ~3)` past its commands.
    // A ref to a track that is off for want of its element is not laid: switching it on would make
    // the pass read that element.
    uint32_t bytes = 0;
    for (const FileTable::Slide& slide : table.slides) {
        for (const FileTable::Entry& e : slide.entries) {
            bytes += 8u + static_cast<uint32_t>(e.commands.size()) * 8u +
                     ((2u * (static_cast<uint32_t>(e.refs.size()) + 1u)) & ~3u);
        }
    }
    const uint32_t stream = bytes != 0u ? Alloc(t, ctx, base, bytes) : 0u;
    if (stream == 0u) {
        return;
    }
    std::memset(base + stream, 0, bytes);
    uint32_t at = stream;
    for (size_t i = 0; i < table.slides.size(); ++i) {
        const FileTable::Slide& slide = table.slides[i];
        const uint32_t record = entry.slide_records + static_cast<uint32_t>(i) * kSlideRecord;
        const uint32_t first = at;
        uint32_t laid = 0;
        for (const FileTable::Entry& e : slide.entries) {
            const uint32_t element = element_at(e.element);
            if (element == 0u) {
                continue;
            }
            std::vector<uint16_t> refs;
            for (const uint16_t ref : e.refs) {
                const uint32_t track = ref & 0x3FFFu;
                if (track < table.tracks.size() && element_at(table.tracks[track].element) != 0u &&
                    entry.track_records != 0u) {
                    refs.push_back(ref);
                }
            }
            StoreU32(base, at, element);
            StoreU16(base, at + 4u, static_cast<uint16_t>(e.commands.size() << 1));
            StoreU16(base, at + 6u, static_cast<uint16_t>((refs.size() << 1) | (e.active ? 1u : 0u)));
            at += 8u;
            for (const auto& [word, value] : e.commands) {
                StoreU32(base, at, word);
                StoreU32(base, at + 4u, value);
                at += 8u;
            }
            for (size_t r = 0; r < refs.size(); ++r) {
                StoreU16(base, at + static_cast<uint32_t>(r) * 2u, refs[r]);
            }
            at += (2u * (static_cast<uint32_t>(refs.size()) + 1u)) & ~3u;
            ++laid;
        }
        StoreU32(base, record + 16u, SlideWord(slide.flags, laid));
        StoreU32(base, record + 28u, first);
        entry.entries_laid += laid;
    }
    entry.slide_entries = stream;
}

bool KeyOfLocked(uint32_t element, uint32_t* presentation, uint32_t* serial, uint32_t* id) {
    const auto it = g_element_keys.find(element);
    if (it == g_element_keys.end()) {
        return false;
    }
    const Headless* entry = LiveLocked(it->second.presentation);
    if (entry == nullptr) {
        return false;
    }
    *presentation = entry->presentation;
    *serial = entry->serial;
    *id = it->second.id;
    return true;
}

}  // namespace

// ---------------------------------------------------------------------------------------------

uint32_t Create(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t gameface, uint32_t path) {
    EnsureInertRecord(t, ctx, base);
    const uint32_t allocator = Call(t, ctx, base, t.kernel_allocator, 0u);
    const uint32_t presentation = Call(t, ctx, base, t.kernel_alloc, allocator, t.presentation_size,
                                       t.presentation_class_name, t.kernel_source_name, t.kernel_source_line);
    if (presentation == 0u) {
        REXLOG_WARN("Refii headless presentation ({}): the kernel allocator has no room for '{}'", t.title,
                    GuestCString(base, path));
        return 0u;
    }
    Call(t, ctx, base, t.presentation_ctor, presentation, path, gameface);
    Call(t, ctx, base, Slot(base, presentation, t.pres_slot_set_name), presentation, path);
    if (t.give_engine != nullptr) {
        t.give_engine(ctx, base, gameface, presentation);
    }

    const std::string file = GuestCString(base, path);
    const uint32_t id_start = IdAt(t, base, t.attr_start_time);
    const uint32_t id_end = IdAt(t, base, t.attr_end_time);
    std::shared_ptr<const FileTable> table = FileTableFor(file, t.title);
    const FileTable::Element* file_root = table && !table->elements.empty() ? &table->elements[0] : nullptr;
    const uint32_t root_capacity =
        std::min<uint32_t>(file_root != nullptr ? static_cast<uint32_t>(file_root->records.size()) : kFilelessRecords,
                           kMaxAttributes);
    const uint32_t root_bytes = ElementBytes(true, root_capacity);

    const uint32_t block = Alloc(t, ctx, base, kBlockSize);
    const uint32_t root = Alloc(t, ctx, base, root_bytes);
    const uint32_t slide_records = table ? MakeSlideRecords(t, ctx, base, *table) : 0u;
    bool scene_vtable = false;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        scene_vtable = EnsureSceneVtable(t, ctx, base, presentation);
    }
    if (block == 0u || root == 0u || !scene_vtable) {
        Free(t, ctx, base, block);
        Free(t, ctx, base, root);
        Free(t, ctx, base, slide_records);
        DeletePresentation(t, ctx, base, presentation);
        REXLOG_WARN("Refii headless presentation ({}): no guest heap for '{}'", t.title, file);
        return 0u;
    }
    std::memset(base + block, 0, kBlockSize);
    std::memset(base + root, 0, root_bytes);

    const uint32_t scene = block + kBlockScene;
    StoreU32(base, scene, g_scene_vtable);
    StoreU32(base, scene + t.scene_render_list, block + kBlockRenderList);
    StoreU32(base, presentation + t.pres_root, root);
    StoreU32(base, presentation + t.pres_scene, scene);
    if (slide_records != 0u) {
        StoreU32(base, presentation + t.pres_slide_records, slide_records);
    }

    const uint32_t parameters = t.parameters_name != 0u ? LoadU32(base, t.parameters_name) : 0u;
    const uint32_t parameters_text = Plausible(parameters) ? LoadU32(base, parameters) : 0u;
    const uint32_t parameters_hash = Plausible(parameters_text) ? Call(t, ctx, base, t.name_hash, parameters_text) : 0u;
    const int32_t start_ms = ClockMs(t, ctx, base, gameface);

    uint32_t serial = 0;
    uint32_t parameters_element = 0;
    size_t live = 0;
    uint16_t root_flags = 0;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        serial = ++g_next_serial & 0x7FFFFFFFu;
        Headless entry;
        entry.presentation = presentation;
        entry.root = root;
        entry.block = block;
        entry.slide_records = slide_records;
        entry.gameface = gameface;
        entry.serial = serial;
        entry.start_ms = start_ms;
        entry.file = file;
        entry.table = table;
        g_live.push_back(std::move(entry));
        Headless& made = g_live.back();
        ApplyFile(base, made, root, file_root != nullptr ? 0 : kNoOrdinal, true, root_capacity, id_start, id_end);
        if (file_root == nullptr) {
            StoreU16(base, root + kElemFlags, kFlagOwnsTimeline);
        }
        root_flags = LoadU16(base, root + kElemFlags);
        g_element_keys[root] = ElementState{presentation, 0u, file_root != nullptr ? 0 : kNoOrdinal, root_capacity};
        refii_bridge_told_headless_presentation(presentation, serial, file.c_str(), static_cast<uint32_t>(file.size()));
        if (parameters_hash != 0u) {
            parameters_element = MakeElementLocked(t, ctx, base, made, root, parameters_hash);
        }
        LaySlidesAndTracksLocked(t, ctx, base, made);
        if (t.pres_animations != 0u && made.table) {
            REXLOG_INFO("Refii headless presentation ({}): '{}' slides and tracks laid — {} track(s) on, {} without "
                        "an element, {} slide entr(y/ies), {} element(s) made",
                        t.title, file, made.tracks_laid, made.tracks_dark, made.entries_laid, made.elements.size());
        }
        live = g_live.size();
    }
    if (parameters_element != 0u) {
        StoreU32(base, block + kBlockParamRecord, parameters_element);
        StoreU32(base, presentation + t.pres_param_records, block + kBlockParamRecord);
        StoreU32(base, presentation + t.pres_param_count, 1u);
    }

    const uint64_t made = ++g_made;
    if (Milestone(made)) {
        REXLOG_INFO("Refii headless presentation ({}) #{}: 0x{:08X} '{}' serial {} on gameface 0x{:08X} — root "
                    "0x{:08X} (flags 0x{:04X}), Parameters 0x{:08X}, engine 0x{:08X}, scene 0x{:08X}, slide "
                    "records 0x{:08X}, clock {} ms; live {}",
                    t.title, made, presentation, file, serial, gameface, root, root_flags, parameters_element,
                    LoadU32(base, presentation + t.pres_engine), scene, slide_records, start_ms, live);
    }
    // The create's own notifications: 2 made, 0 loaded.
    NotifyGameface(t, ctx, base, gameface, 2u, presentation);
    NotifyGameface(t, ctx, base, gameface, 0u, presentation);
    return presentation;
}

bool Destroy(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t gameface, uint32_t presentation) {
    Headless entry;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        const auto it = std::find_if(g_live.begin(), g_live.end(),
                                     [&](const Headless& e) { return e.presentation == presentation; });
        if (it == g_live.end()) {
            return false;
        }
        g_element_keys.erase(it->root);
        for (const uint32_t element : it->elements) {
            g_element_keys.erase(element);
        }
        entry = std::move(*it);
        g_live.erase(it);
    }
    refii_bridge_told_headless_gone(presentation, entry.serial);
    NotifyGameface(t, ctx, base, gameface, 1u, presentation);
    const uint32_t gameplan = LoadU32(base, presentation + t.pres_gameplan);
    if (gameplan != 0u) {
        Call(t, ctx, base, Slot(base, gameplan, t.gameplan_slot_release), gameplan);
    }

    StoreU32(base, presentation + t.pres_root, 0u);
    StoreU32(base, presentation + t.pres_scene, 0u);
    StoreU32(base, presentation + t.pres_param_records, 0u);
    StoreU32(base, presentation + t.pres_param_count, 0u);
    if (t.pres_param_size_a != 0u) {
        StoreU32(base, presentation + t.pres_param_size_a, 0u);
    }
    if (t.pres_param_size_b != 0u) {
        StoreU32(base, presentation + t.pres_param_size_b, 0u);
    }
    if (t.pres_slide_records != 0u) {
        StoreU32(base, presentation + t.pres_slide_records, 0u);
    }
    if (t.pres_animations != 0u) {
        // Mgr:Animations' dtor kernel-frees +8 (sized from its +28 / +32, F3 `sub_82C37650`).
        for (uint32_t offset = 8u; offset <= 20u; offset += 4u) {
            StoreU32(base, presentation + t.pres_animations + offset, 0u);
        }
    }
    DeletePresentation(t, ctx, base, presentation);
    for (const uint32_t element : entry.elements) {
        Free(t, ctx, base, element);
    }
    Free(t, ctx, base, entry.root);
    Free(t, ctx, base, entry.block);
    Free(t, ctx, base, entry.slide_records);
    Free(t, ctx, base, entry.track_records);
    Free(t, ctx, base, entry.track_keys);
    Free(t, ctx, base, entry.slide_entries);

    const uint64_t freed = ++g_freed;
    if (Milestone(freed)) {
        size_t live = 0;
        {
            std::lock_guard<std::mutex> lock(g_mutex);
            live = g_live.size();
        }
        REXLOG_INFO("Refii headless presentation ({}): 0x{:08X} '{}' serial {} freed with {} named element(s) "
                    "(#{}); live {}",
                    t.title, presentation, entry.file, entry.serial, entry.elements.size(), freed, live);
    }
    return true;
}

uint32_t MakeNamed(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t service, uint32_t hash,
                   uint32_t parent) {
    if (hash == 0u) {
        return 0u;
    }
    uint32_t element = 0;
    uint32_t presentation = 0;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        Headless* entry = LiveLocked(service - t.pres_service);
        if (entry == nullptr) {
            return 0u;
        }
        presentation = entry->presentation;
        element = MakeElementLocked(t, ctx, base, *entry, parent != 0u ? parent : entry->root, hash);
    }
    const uint64_t n = ++g_named;
    if (element != 0u && Milestone(n)) {
        REXLOG_INFO("Refii headless presentation ({}): element 0x{:08X} named 0x{:08X} under 0x{:08X} of 0x{:08X} "
                    "(#{})",
                    t.title, element, hash, parent, presentation, n);
    }
    return element;
}

void ExpandBeforeWalk(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t service, uint32_t parent) {
    if (!Plausible(parent)) {
        return;
    }
    std::lock_guard<std::mutex> lock(g_mutex);
    if (Headless* entry = LiveLocked(service - t.pres_service)) {
        const auto key = g_element_keys.find(parent);
        if (key != g_element_keys.end() && key->second.presentation == entry->presentation) {
            ExpandLocked(t, ctx, base, *entry, parent, key->second.ordinal);
        }
    }
}

bool AnswerAttributeString(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t out, uint32_t element,
                           uint32_t presentation, uint32_t name) {
    if (presentation == 0u || !Plausible(element) || !Plausible(name)) {
        return false;
    }
    std::shared_ptr<const FileTable> table;
    int32_t ordinal = kNoOrdinal;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        const auto key = g_element_keys.find(element);
        if (key == g_element_keys.end()) {
            return false;
        }
        const Headless* entry = LiveLocked(key->second.presentation);
        if (entry == nullptr) {
            return false;
        }
        table = entry->table;
        ordinal = key->second.ordinal;
    }
    if (!table || ordinal < 0) {
        return false;
    }
    const uint32_t block = LoadU32(base, name);
    const uint32_t text = Plausible(block) ? LoadU32(base, block) : 0u;
    if (!Plausible(text)) {
        return false;
    }
    const uint32_t id = Call(t, ctx, base, t.name_hash, text) & t.attribute_id_mask;
    const std::string* value = table->StringAttribute(ordinal, id);
    if (value == nullptr || value->empty()) {
        return false;
    }
    const uint32_t copy = Alloc(t, ctx, base, static_cast<uint32_t>(value->size()) + 1u);
    if (copy == 0u) {
        return false;
    }
    std::memcpy(base + copy, value->data(), value->size());
    base[copy + value->size()] = 0u;
    Call(t, ctx, base, t.char_string_dtor, out);
    Call(t, ctx, base, t.char_string_from_cstr, out, copy, kCharStringMeasure);
    Free(t, ctx, base, copy);
    const uint64_t n = ++g_file_strings;
    if (Milestone(n)) {
        REXLOG_INFO("Refii headless presentation ({}): element 0x{:08X} (file #{}) answered its file's attribute "
                    "0x{:07X} = '{}' (#{})",
                    t.title, element, ordinal, id, *value, n);
    }
    return true;
}

size_t UpdateAll(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t gameface, int32_t* clock_out) {
    std::vector<uint32_t> mine;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        for (const Headless& e : g_live) {
            if (e.gameface == gameface) {
                mine.push_back(e.presentation);
            }
        }
    }
    if (mine.empty()) {
        return 0u;
    }
    const int32_t clock = ClockMs(t, ctx, base, gameface);
    if (clock_out != nullptr) {
        *clock_out = clock;
    }
    for (const uint32_t presentation : mine) {
        if (IsHeadless(presentation)) {  // an earlier Update's callback may have destroyed it
            Call(t, ctx, base, Slot(base, presentation, t.pres_slot_update), presentation, static_cast<uint32_t>(clock));
        }
    }
    return mine.size();
}

void NoteRootRead(const GamefaceTable& t, PPCContext& ctx, uint8_t* base, uint32_t root) {
    if (root == 0u) {
        return;
    }
    uint32_t gameface = 0;
    uint32_t presentation = 0;
    int32_t start_ms = 0;
    bool said_500 = false;
    bool said_1000 = false;
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        const auto it = std::find_if(g_live.begin(), g_live.end(), [&](const Headless& e) { return e.root == root; });
        if (it == g_live.end()) {
            return;
        }
        gameface = it->gameface;
        presentation = it->presentation;
        start_ms = it->start_ms;
        said_500 = it->said_500;
        said_1000 = it->said_1000;
    }
    const int32_t time = std::max(0, ClockMs(t, ctx, base, gameface) - start_ms);
    StoreU32(base, root + kElemTime, static_cast<uint32_t>(time));
    const bool past_500 = time >= 500;
    const bool past_1000 = time >= 1000;
    if ((past_500 && !said_500) || (past_1000 && !said_1000)) {
        {
            std::lock_guard<std::mutex> lock(g_mutex);
            if (Headless* e = LiveLocked(presentation)) {
                e->said_500 = e->said_500 || past_500;
                e->said_1000 = e->said_1000 || past_1000;
            }
        }
        REXLOG_INFO("Refii headless presentation ({}): root 0x{:08X} of 0x{:08X} read at {} ms (past {} ms)", t.title,
                    root, presentation, time, past_1000 ? 1000 : 500);
    }
}

bool ElementKey(uint32_t element, uint32_t* presentation, uint32_t* serial, uint32_t* id) {
    std::lock_guard<std::mutex> lock(g_mutex);
    return KeyOfLocked(element, presentation, serial, id);
}

bool IsHeadless(uint32_t presentation) {
    std::lock_guard<std::mutex> lock(g_mutex);
    return LiveLocked(presentation) != nullptr;
}

int32_t FileOrdinal(uint32_t element) {
    std::lock_guard<std::mutex> lock(g_mutex);
    const auto it = g_element_keys.find(element);
    if (it == g_element_keys.end() || LiveLocked(it->second.presentation) == nullptr) {
        return kNoOrdinal;
    }
    return it->second.ordinal;
}

uint32_t InertQuadRecord() { return g_inert_record.load(); }

Memory RememberWrite(uint8_t* base, uint32_t element, const uint32_t* ids, const float* values, size_t count) {
    if (base == nullptr || !Plausible(element)) {
        return Memory::kNotHeadless;
    }
    std::lock_guard<std::mutex> lock(g_mutex);
    const auto key = g_element_keys.find(element);
    if (key == g_element_keys.end() || LiveLocked(key->second.presentation) == nullptr) {
        return Memory::kNotHeadless;
    }
    if (key->second.ordinal >= 0) {
        return Memory::kFileAnswers;
    }
    uint32_t held = LoadU8(base, element + kElemAttributeCount);
    size_t missing = 0;
    for (size_t i = 0; i < count; ++i) {
        if (ids[i] != 0u && RecordOf(base, element, ids[i]) == 0u) {
            ++missing;
        }
    }
    if (held + missing > key->second.capacity) {
        return Memory::kNoRoom;
    }
    const uint32_t records = AttributesAt(base, element);
    for (size_t i = 0; i < count; ++i) {
        if (ids[i] == 0u || RecordOf(base, element, ids[i]) != 0u) {
            continue;
        }
        uint32_t bits = 0;
        std::memcpy(&bits, &values[i], sizeof bits);
        StoreU32(base, records + held * kAttributeRecord, (ids[i] << 5) | kFloatRecord);
        StoreU32(base, records + held * kAttributeRecord + 4u, bits);
        ++held;
    }
    StoreU8(base, element + kElemAttributeCount, static_cast<uint8_t>(held));
    const uint64_t n = ++g_remembered;
    if (missing != 0u && Milestone(n)) {
        REXLOG_INFO("Refii headless presentation: fileless element 0x{:08X} now holds {} attribute(s) a setter "
                    "named (#{})",
                    element, held, n);
    }
    return Memory::kRemembered;
}

bool TellAttribute(uint32_t element, uint32_t attribute, uint32_t value_bits) {
    uint32_t presentation = 0;
    uint32_t serial = 0;
    uint32_t id = 0;
    if (!ElementKey(element, &presentation, &serial, &id)) {
        return false;
    }
    refii_bridge_told_push(static_cast<uint32_t>(refii::vfs::ToldStatement::Kind::kAttribute), presentation, serial,
                           refii::vfs::kHeadlessOrdinal | id, attribute, value_bits);
    return true;
}

bool TellActive(uint32_t element, bool on) {
    uint32_t presentation = 0;
    uint32_t serial = 0;
    uint32_t id = 0;
    if (!ElementKey(element, &presentation, &serial, &id)) {
        return false;
    }
    refii_bridge_told_push(static_cast<uint32_t>(refii::vfs::ToldStatement::Kind::kActive), presentation, serial,
                           refii::vfs::kHeadlessOrdinal | id, on ? 1u : 0u, 0u);
    return true;
}

bool TellTexture(uint32_t element, uint32_t hash) {
    uint32_t presentation = 0;
    uint32_t serial = 0;
    uint32_t id = 0;
    if (!ElementKey(element, &presentation, &serial, &id)) {
        return false;
    }
    refii_bridge_told_push(static_cast<uint32_t>(refii::vfs::ToldStatement::Kind::kTexture), presentation, serial,
                           refii::vfs::kHeadlessOrdinal | id, hash, 0u);
    return true;
}

bool TellText(uint32_t element, const std::vector<uint16_t>& units) {
    uint32_t presentation = 0;
    uint32_t serial = 0;
    uint32_t id = 0;
    if (!ElementKey(element, &presentation, &serial, &id)) {
        return false;
    }
    refii_bridge_told_text_push(presentation, serial, refii::vfs::kHeadlessOrdinal | id, units.data(),
                                static_cast<uint32_t>(units.size()));
    return true;
}

}  // namespace refii::headless
