// The told-statement queues and the headless presentations' registry: a title's hooks push
// through the C ABI (core/payload_bridge.h), its host bridge takes (core/told_statements.h). In
// librefii_runtime, which the payload and every title's bridge reach — a copy in each library
// would have the hooks filling one queue while the bridge reads another, empty one (see
// gui_native_calls.cpp).
#include "core/told_statements.h"

#include <algorithm>
#include <cstddef>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

namespace {

/// The statements wait here for the host: their own queue, not the GUI call feed's — a hidden
/// list's Lua re-placing its rows every frame would push the boxes' calls out of that one.
constexpr std::size_t kMaxTold = 16384u;
constexpr std::size_t kMaxTexts = 2048u;

struct ToldText {
    uint32_t presentation = 0;
    uint32_t root_hash = 0;
    uint32_t ordinal = 0;
    std::u16string text;
};

std::mutex g_lock;
std::vector<refii::vfs::ToldStatement> g_told;
std::vector<ToldText> g_texts;

}  // namespace

extern "C" void refii_bridge_told_push(uint32_t kind, uint32_t presentation, uint32_t root_hash,
                                       uint32_t ordinal, uint32_t a, uint32_t b) {
    refii::vfs::ToldStatement told;
    told.kind = static_cast<refii::vfs::ToldStatement::Kind>(kind);
    told.presentation = presentation;
    told.root_hash = root_hash;
    told.ordinal = ordinal;
    told.a = a;
    told.b = b;
    std::lock_guard<std::mutex> lock(g_lock);
    if (g_told.size() >= kMaxTold) {
        g_told.erase(g_told.begin(), g_told.begin() + static_cast<std::ptrdiff_t>(kMaxTold / 4u));
    }
    g_told.push_back(told);
}

extern "C" void refii_bridge_told_text_push(uint32_t presentation, uint32_t root_hash, uint32_t ordinal,
                                            const uint16_t* units, uint32_t length) {
    ToldText told;
    told.presentation = presentation;
    told.root_hash = root_hash;
    told.ordinal = ordinal;
    if (units != nullptr) {
        told.text.assign(reinterpret_cast<const char16_t*>(units), length);
    }
    std::lock_guard<std::mutex> lock(g_lock);
    if (g_texts.size() >= kMaxTexts) {
        g_texts.erase(g_texts.begin(), g_texts.begin() + static_cast<std::ptrdiff_t>(kMaxTexts / 4u));
    }
    g_texts.push_back(std::move(told));
}

namespace {

/// One headless presentation's file and named elements (id - 1), as its creation stated them;
/// kept until another presentation is made at the same address.
struct HeadlessFile {
    uint32_t serial = 0;
    std::string file;
    std::vector<std::pair<uint32_t, uint32_t>> elements;  // (parent id, name hash)
    bool live = true;
};

std::mutex g_headless_lock;
std::unordered_map<uint32_t, HeadlessFile> g_headless;

}  // namespace

extern "C" void refii_bridge_told_headless_presentation(uint32_t presentation, uint32_t serial,
                                                        const char* file, uint32_t length) {
    HeadlessFile entry;
    entry.serial = serial;
    if (file != nullptr) {
        entry.file.assign(file, length);
    }
    std::lock_guard<std::mutex> lock(g_headless_lock);
    g_headless[presentation] = std::move(entry);
}

extern "C" void refii_bridge_told_headless_gone(uint32_t presentation, uint32_t serial) {
    std::lock_guard<std::mutex> lock(g_headless_lock);
    const auto it = g_headless.find(presentation);
    if (it != g_headless.end() && it->second.serial == serial) {
        it->second.live = false;
    }
}

extern "C" void refii_bridge_told_headless_element(uint32_t presentation, uint32_t serial, uint32_t id,
                                                   uint32_t parent_id, uint32_t name_hash) {
    std::lock_guard<std::mutex> lock(g_headless_lock);
    const auto it = g_headless.find(presentation);
    if (it == g_headless.end() || it->second.serial != serial || id == 0u) {
        return;
    }
    std::vector<std::pair<uint32_t, uint32_t>>& elements = it->second.elements;
    if (elements.size() < id) {
        elements.resize(id);
    }
    elements[id - 1u] = {parent_id, name_hash};
}

namespace refii::vfs {

uint32_t TakeToldStatements(uint32_t* words, uint32_t capacity) {
    std::lock_guard<std::mutex> lock(g_lock);
    const uint32_t fit = words != nullptr ? capacity / 6u : 0u;
    const uint32_t count = std::min<uint32_t>(fit, static_cast<uint32_t>(g_told.size()));
    for (uint32_t i = 0; i < count; ++i) {
        const ToldStatement& told = g_told[i];
        uint32_t* out = words + (i * 6u);
        out[0] = static_cast<uint32_t>(told.kind);
        out[1] = told.presentation;
        out[2] = told.root_hash;
        out[3] = told.ordinal;
        out[4] = told.a;
        out[5] = told.b;
    }
    g_told.erase(g_told.begin(), g_told.begin() + static_cast<std::ptrdiff_t>(count));
    return count;
}

uint32_t TakeToldTexts(uint32_t* words, uint32_t word_capacity, uint16_t* units, uint32_t unit_capacity) {
    std::lock_guard<std::mutex> lock(g_lock);
    if (words == nullptr || units == nullptr) return 0;
    uint32_t count = 0;
    uint32_t used = 0;
    while (count < g_texts.size() && (count + 1u) * 4u <= word_capacity &&
           used + g_texts[count].text.size() <= unit_capacity) {
        const ToldText& told = g_texts[count];
        uint32_t* out = words + count * 4u;
        out[0] = told.presentation;
        out[1] = told.root_hash;
        out[2] = told.ordinal;
        out[3] = static_cast<uint32_t>(told.text.size());
        for (char16_t unit : told.text) {
            units[used++] = static_cast<uint16_t>(unit);
        }
        ++count;
    }
    g_texts.erase(g_texts.begin(), g_texts.begin() + static_cast<std::ptrdiff_t>(count));
    return count;
}

uint32_t ToldHeadlessFile(uint32_t presentation, uint32_t serial, char* out, uint32_t capacity) {
    std::lock_guard<std::mutex> lock(g_headless_lock);
    const auto it = g_headless.find(presentation);
    if (out == nullptr || capacity == 0u || it == g_headless.end() || it->second.serial != serial) {
        return 0u;
    }
    const uint32_t length = std::min<uint32_t>(static_cast<uint32_t>(it->second.file.size()), capacity - 1u);
    std::memcpy(out, it->second.file.data(), length);
    out[length] = '\0';
    return length;
}

uint32_t ToldHeadlessLive(uint32_t* out, uint32_t capacity) {
    std::lock_guard<std::mutex> lock(g_headless_lock);
    uint32_t count = 0;
    for (const auto& [presentation, entry] : g_headless) {
        if (!entry.live) {
            continue;
        }
        if (out != nullptr && count < capacity) {
            out[count] = presentation;
        }
        ++count;
    }
    return count;
}

bool ToldHeadlessElement(uint32_t presentation, uint32_t serial, uint32_t id, uint32_t* parent_id,
                         uint32_t* name_hash) {
    std::lock_guard<std::mutex> lock(g_headless_lock);
    const auto it = g_headless.find(presentation);
    if (parent_id == nullptr || name_hash == nullptr || it == g_headless.end() ||
        it->second.serial != serial || id == 0u || id > it->second.elements.size()) {
        return false;
    }
    const std::pair<uint32_t, uint32_t>& element = it->second.elements[id - 1u];
    if (element.second == 0u) {
        return false;
    }
    *parent_id = element.first;
    *name_hash = element.second;
    return true;
}

}  // namespace refii::vfs
