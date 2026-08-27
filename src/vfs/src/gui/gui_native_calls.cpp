#include "gui/gui_native_calls.h"

#include <atomic>
#include <mutex>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#include <rex/logging.h>

namespace refii::vfs {
namespace {

/// The registration map: implementation pointer -> every name bound to it. Written once by
/// `RegisterGUI` at startup, read thereafter; the mutex only guards that write.
/// A vector because names alias: 120 registrations resolve to 114 implementations (e.g.
/// `RemoveCounter` and `RemoveTimer` are both `sub_82305918`). A call reports the first name;
/// registration order is source order in `sub_82307518`, so it is stable across runs.
std::unordered_map<uint32_t, std::vector<std::string>>& Names() {
    static std::unordered_map<uint32_t, std::vector<std::string>> names;
    return names;
}

std::mutex& NamesMutex() {
    static std::mutex mutex;
    return mutex;
}

/// Set once the first `GUI` registration lands, so the call hook bails during boot; a
/// per-call structure walk there hangs `Game_InitDatabases`.
std::atomic<bool> g_registered{false};

std::vector<GuiNativeCall>& Pending() {
    static std::vector<GuiNativeCall> pending;
    return pending;
}

std::mutex& PendingMutex() {
    static std::mutex mutex;
    return mutex;
}

std::atomic<uint64_t> g_intercepted{0};

/// Ceiling: the host drains once a frame and a script storm outruns that. The oldest is
/// dropped — the newest call is the screen's current state.
constexpr std::size_t kMaxPending = 512u;

}  // namespace

void NoteGuiNativeRegistration(uint32_t implementation, std::string name) {
    if (implementation == 0 || name.empty()) {
        return;
    }

    std::lock_guard<std::mutex> lock(NamesMutex());
    std::vector<std::string>& names = Names()[implementation];
    for (const std::string& existing : names) {
        if (existing == name) {
            return;
        }
    }

    names.push_back(std::move(name));
    if (names.size() > 1u) {
        std::string all;
        for (const std::string& bound : names) {
            if (!all.empty()) {
                all += " = ";
            }
            all += bound;
        }

        REXLOG_INFO("Refii UI cut: GUI.{} share one implementation 0x{:08X} — a call to any of "
                    "them reports as '{}'",
                    all, implementation, names.front());
    }

    g_registered.store(true, std::memory_order_release);
}

const std::string* GuiNativeNameFor(uint32_t implementation) {
    if (implementation == 0 || !g_registered.load(std::memory_order_acquire)) {
        return nullptr;
    }

    std::lock_guard<std::mutex> lock(NamesMutex());
    auto found = Names().find(implementation);
    return found == Names().end() || found->second.empty() ? nullptr : &found->second.front();
}

bool GuiNativesRegistered() { return g_registered.load(std::memory_order_acquire); }

namespace {

struct GuiNativeAnswer {
    GuiNativeAnswerKind kind = GuiNativeAnswerKind::kNone;
    uint32_t bits = 0;
};

/// Answers keyed by name, resolved to an implementation at call time: the host only knows
/// names, and six natives share one implementation, so answering "RemoveTimer" must not
/// thereby answer "RemoveCounter".
std::unordered_map<std::string, GuiNativeAnswer>& Answers() {
    static std::unordered_map<std::string, GuiNativeAnswer> answers;
    return answers;
}

std::mutex& AnswersMutex() {
    static std::mutex mutex;
    return mutex;
}

thread_local uint32_t t_entry = 0;

}  // namespace

void SetGuiNativeAnswer(const std::string& name, GuiNativeAnswerKind kind, uint32_t bits) {
    if (name.empty()) {
        return;
    }

    std::lock_guard<std::mutex> lock(AnswersMutex());
    if (kind == GuiNativeAnswerKind::kNone) {
        Answers().erase(name);
        return;
    }

    GuiNativeAnswer& answer = Answers()[name];
    answer.kind = kind;
    answer.bits = bits;
}

GuiNativeAnswerKind GuiNativeAnswerFor(uint32_t implementation, uint32_t* out_bits) {
    if (implementation == 0 || !g_registered.load(std::memory_order_acquire)) {
        return GuiNativeAnswerKind::kNone;
    }

    std::lock_guard<std::mutex> names(NamesMutex());
    auto found = Names().find(implementation);
    if (found == Names().end()) {
        return GuiNativeAnswerKind::kNone;
    }

    std::lock_guard<std::mutex> lock(AnswersMutex());
    if (Answers().empty()) {
        return GuiNativeAnswerKind::kNone;
    }

    // Every name bound to this implementation: aliases are one function and the call cannot
    // say which name it came in under.
    for (const std::string& name : found->second) {
        auto answer = Answers().find(name);
        if (answer != Answers().end()) {
            if (out_bits != nullptr) {
                *out_bits = answer->second.bits;
            }
            return answer->second.kind;
        }
    }

    return GuiNativeAnswerKind::kNone;
}

void NoteGuiNativeEntry(uint32_t implementation) { t_entry = implementation; }

uint32_t CurrentGuiNativeEntry() { return t_entry; }

void PostGuiNativeCall(GuiNativeCall call) {
    call.sequence = g_intercepted.fetch_add(1, std::memory_order_relaxed) + 1u;

    std::lock_guard<std::mutex> lock(PendingMutex());
    if (Pending().size() >= kMaxPending) {
        Pending().erase(Pending().begin());
    }
    Pending().push_back(std::move(call));
}

std::vector<GuiNativeCall> TakeGuiNativeCalls() {
    std::vector<GuiNativeCall> taken;
    std::lock_guard<std::mutex> lock(PendingMutex());
    taken.swap(Pending());
    return taken;
}

void GuiNativeCounters(uint64_t* out_intercepted, uint32_t* out_pending, uint32_t* out_named) {
    if (out_intercepted != nullptr) {
        *out_intercepted = g_intercepted.load(std::memory_order_relaxed);
    }
    if (out_pending != nullptr) {
        std::lock_guard<std::mutex> lock(PendingMutex());
        *out_pending = static_cast<uint32_t>(Pending().size());
    }
    if (out_named != nullptr) {
        std::lock_guard<std::mutex> lock(NamesMutex());
        *out_named = static_cast<uint32_t>(Names().size());
    }
}

}  // namespace refii::vfs
