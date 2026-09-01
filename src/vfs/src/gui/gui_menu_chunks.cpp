#include "gui/gui_menu_chunks.h"

#include <atomic>
#include <mutex>
#include <utility>

namespace refii::vfs {
namespace {

/// One mutex covers both the staged bag and the completed-call queue: a stage and a run are
/// two halves of one transaction, and a host drain must not land between them.
std::mutex& Mutex() {
    static std::mutex mutex;
    return mutex;
}

std::vector<MenuChunkAttribute>& Staged() {
    static std::vector<MenuChunkAttribute> staged;
    return staged;
}

std::vector<MenuChunkCall>& Pending() {
    static std::vector<MenuChunkCall> pending;
    return pending;
}

std::atomic<uint64_t> g_recorded{0};
std::atomic<uint64_t> g_dropped{0};

/// Ceiling: a full repopulation is a few hundred chunks and nothing guarantees the host is
/// draining. The oldest is dropped — the newest statements are the list's current state — and
/// drops are counted, since a truncated population is a menu silently missing rows.
constexpr std::size_t kMaxPending = 2048u;

/// A bag this large means a chunk ran without consuming; bounded so a leak cannot grow.
constexpr std::size_t kMaxStaged = 64u;

}  // namespace

extern "C" void refii_bridge_stage_menu_attribute(const char* name_data, size_t name_length,
                                                  const char* text_data, size_t text_length,
                                                  float number, int32_t is_number_flag) {
    std::string name = name_data != nullptr && name_length != 0u
                           ? std::string(name_data, name_length)
                           : std::string();
    std::string text = text_data != nullptr && text_length != 0u
                           ? std::string(text_data, text_length)
                           : std::string();
    const bool is_number = is_number_flag != 0;

    if (name.empty()) {
        return;
    }

    std::lock_guard<std::mutex> lock(Mutex());
    if (Staged().size() >= kMaxStaged) {
        return;
    }

    // Last write wins per name: the guest re-stages rather than clearing, and two `String1`s
    // in one bag would leave the host picking.
    for (MenuChunkAttribute& existing : Staged()) {
        if (existing.name == name) {
            existing.text = std::move(text);
            existing.number = number;
            existing.is_number = is_number;
            return;
        }
    }

    MenuChunkAttribute attribute;
    attribute.name = std::move(name);
    attribute.text = std::move(text);
    attribute.number = number;
    attribute.is_number = is_number;
    Staged().push_back(std::move(attribute));
}

void RunMenuChunk(std::string chunk, uint32_t presentation) {
    if (chunk.empty()) {
        return;
    }

    std::lock_guard<std::mutex> lock(Mutex());

    MenuChunkCall call;
    call.chunk = std::move(chunk);
    call.presentation = presentation;
    call.staged.swap(Staged());
    call.sequence = g_recorded.fetch_add(1, std::memory_order_relaxed) + 1u;

    if (Pending().size() >= kMaxPending) {
        Pending().erase(Pending().begin());
        g_dropped.fetch_add(1, std::memory_order_relaxed);
    }

    Pending().push_back(std::move(call));
}

std::vector<MenuChunkCall> TakeMenuChunkCalls() {
    std::vector<MenuChunkCall> taken;
    std::lock_guard<std::mutex> lock(Mutex());
    taken.swap(Pending());
    return taken;
}

void MenuChunkCounters(uint64_t* out_recorded, uint32_t* out_pending, uint64_t* out_dropped) {
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
