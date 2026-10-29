// The told-statement queues: the payload's hooks push through the C ABI, the host bridge takes.
// In refii_vfs, which both link — a copy in each library would have the hooks filling one queue
// while the bridge reads another, empty one (see gui_native_calls.cpp).
#include "gui/gui_told_statements.h"

#include <algorithm>
#include <cstddef>
#include <mutex>
#include <string>
#include <vector>

#include "core/payload_bridge_fable2.h"

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

}  // namespace refii::vfs
