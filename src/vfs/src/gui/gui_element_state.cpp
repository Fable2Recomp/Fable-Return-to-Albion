#include "gui/gui_element_state.h"

#include <atomic>
#include <mutex>
#include <unordered_map>
#include <utility>

namespace refii::vfs {
namespace {

std::mutex& Mutex() {
    static std::mutex mutex;
    return mutex;
}

std::vector<GuiElementChange>& Pending() {
    static std::vector<GuiElementChange> pending;
    return pending;
}

/// Where each (element, kind, id) already sits in `Pending`, so a repeat overwrites in place.
std::unordered_map<uint64_t, std::size_t>& Index() {
    static std::unordered_map<uint64_t, std::size_t> index;
    return index;
}

/// One channel of one element. `id` is 27 bits for an attribute and 16 for a flags mask, and
/// zero for a slide or a text notification — 28 bits holds all three.
uint64_t KeyOf(const GuiElementChange& change) {
    return (static_cast<uint64_t>(change.element) << 32) |
           (static_cast<uint64_t>(change.kind) << 28) |
           static_cast<uint64_t>(change.id & 0x0FFFFFFFu);
}

std::atomic<uint64_t> g_recorded{0};
std::atomic<uint64_t> g_dropped{0};

/// Ceiling, generous: a screen mid-transition writes thousands per frame and the host drains
/// once a frame. This is a change log, not a statement feed, so drops are counted rather than
/// evicted — dropping from either end would leave the host permanently disagreeing about that
/// element, and its answer is to re-walk the presentation once.
constexpr std::size_t kMaxPending = 65536u;

}  // namespace

void NoteGuiElementChange(GuiElementChange change) {
    std::lock_guard<std::mutex> lock(Mutex());
    g_recorded.fetch_add(1, std::memory_order_relaxed);

    // Coalesced per (element, kind, id): the host draws once per drain, so only the value the
    // channel ended on matters. Every key that was written is still present with its final
    // value, unlike an eviction, which would lose the key outright.
    const uint64_t key = KeyOf(change);
    auto existing = Index().find(key);
    if (existing != Index().end() && existing->second < Pending().size()) {
        Pending()[existing->second] = std::move(change);
        return;
    }

    if (Pending().size() >= kMaxPending) {
        g_dropped.fetch_add(1, std::memory_order_relaxed);
        return;
    }

    Index()[key] = Pending().size();
    Pending().push_back(std::move(change));
}

std::vector<GuiElementChange> TakeGuiElementChanges() {
    std::vector<GuiElementChange> taken;
    std::lock_guard<std::mutex> lock(Mutex());
    taken.swap(Pending());
    Index().clear();
    return taken;
}

void GuiElementCounters(uint64_t* out_recorded, uint32_t* out_pending, uint64_t* out_dropped) {
    if (out_recorded != nullptr) {
        *out_recorded = g_recorded.load(std::memory_order_relaxed);
    }
    if (out_dropped != nullptr) {
        *out_dropped = g_dropped.load(std::memory_order_relaxed);
    }
    if (out_pending != nullptr) {
        std::lock_guard<std::mutex> lock(Mutex());
        *out_pending = static_cast<uint32_t>(Pending().size());
    }
}

}  // namespace refii::vfs
