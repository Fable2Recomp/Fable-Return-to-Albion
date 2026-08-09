// The UI cut — intercepting Fable 2's semantic GUI natives.
//
// The plan cuts the UI at the ~120 `GUI.*` Lua natives the GAME VM calls
// (DisplayMessageBox, FadeScreenIn, SetGUIText). Everything below them — the Gameface
// DOM, the .bgf presentations, the UI Lua VM — is discarded rather than reimplemented.
// See docs/ui_native_cut.md.
//
// THIS IS ONE HOOK, NOT 120. Every lua_CFunction in the game VM is invoked through a
// single indirect call inside luaD_precall_2 (0x822289F0):
//
//     v26 = (*(int (**)(int))(**(u32**)(*(u32*)(L + 20) + 4) + 16))(L);
//
// so hooking it sees them all. Note WHICH copy: the title links two Lua libraries, and
// luaD_precall (0x8219EF68) is the GAMEFACE UI VM's — it fires constantly and never sees a
// GUI native, because RegisterGUI registers into the game VM. What it must NOT do is discriminate by the
// registered function: that is a THUNK which encodes argument SHAPE only, is shared
// between natives with different implementations, and is shared across namespaces —
// sub_823CE2E0 alone serves 81 registrations including GDB and EntityManager. Matching a
// thunk would intercept unrelated calls across the whole title.
//
// The identity is the IMPLEMENTATION POINTER, held in the thunk's upvalue 1.

#include <atomic>
#include <cstdint>
#include <cstdlib>
#include <mutex>
#include <string_view>
#include <unordered_map>

#include <rex/logging.h>

#include <string>

#include "vfs_hooks_private.h"
#include "core/guest_state.h"
#include "hooks/ppc_externs.h"

namespace {

/// Lua 5.1, 32-bit, as this title builds it. Verified against the binary rather than
/// assumed: luaD_precall reads CClosure.f at +16 (0x8219F1C0), and lua_touserdata
/// (0x822280F8) returns `*value + 24` for a full userdata, which fixes the Udata header
/// size at 24 bytes.
constexpr uint32_t kClosureIsC = 6u;        // lu_byte isC
constexpr uint32_t kClosureUpvalues = 7u;   // lu_byte nupvalues
constexpr uint32_t kCClosureUpvalue0 = 20u; // TValue upvalue[0]
constexpr uint32_t kUdataHeader = 24u;
constexpr uint32_t kTValueType = 4u;
constexpr uint32_t kLuaTUserdata = 7u;

/// A guest pointer, loosely. Only used to refuse to chase obvious rubbish while walking
/// structures another thread is mutating.
bool PlausibleGuestPointer(uint32_t value) {
    return value >= 0x10000u && value < 0xC0000000u;
}

/// Reports each distinct implementation the game calls, once.
///
/// Once rather than always because a predicate like IsScreenFading is called every frame;
/// what we want is the SET of natives this title actually uses, so the interception
/// surface is built against what runs rather than against all 120 registered names.
/// impl -> registered name, built at registration time.
std::unordered_map<uint32_t, std::string>& NameByImplementation() {
    static std::unordered_map<uint32_t, std::string> names;
    return names;
}

std::mutex& NameMutex() {
    static std::mutex mutex;
    return mutex;
}

void NoteImplementation(uint32_t implementation) {
    static std::mutex mutex;
    static std::unordered_map<uint32_t, uint32_t> seen;

    std::string name;
    {
        std::lock_guard<std::mutex> names(NameMutex());
        auto found = NameByImplementation().find(implementation);
        if (found != NameByImplementation().end()) {
            name = found->second;
        }
    }

    std::lock_guard<std::mutex> lock(mutex);
    const uint32_t count = ++seen[implementation];
    if (count == 1u) {
        REXLOG_INFO("Refii Lua native first call: {} impl=0x{:08X} (distinct so far: {})",
                    name.empty() ? "<unnamed>" : name.c_str(), implementation,
                    static_cast<uint32_t>(seen.size()));
    }
}

/// The implementation behind the closure being called, or 0 when this is not a C function
/// registered the way the GUI natives are.
///
/// `func` is luaD_precall's second argument: a TValue* whose value is the Closure.
uint32_t ImplementationFor(uint8_t* base, uint32_t func) {
    if (base == nullptr || func == 0) {
        return 0;
    }

    const uint32_t closure = refii::vfs::LoadGuestU32(base, func);
    if (!PlausibleGuestPointer(closure)) {
        return 0;
    }

    // A Lua closure has no implementation pointer to find.
    if (base[closure + kClosureIsC] == 0 || base[closure + kClosureUpvalues] == 0) {
        return 0;
    }

    if (refii::vfs::LoadGuestU32(base, closure + kCClosureUpvalue0 + kTValueType) !=
        kLuaTUserdata) {
        return 0;
    }

    const uint32_t userdata =
        refii::vfs::LoadGuestU32(base, closure + kCClosureUpvalue0);
    if (!PlausibleGuestPointer(userdata)) {
        return 0;
    }

    return refii::vfs::LoadGuestU32(base, userdata + kUdataHeader);
}

}  // namespace

REX_EXTERN(sub_82228730) {
    REX_FUNC_PROLOGUE();

    // The observation is skippable, the delegation is not.
    //
    // This sits on the game VM's C-function call path, so it runs for EVERY native those
    // shipped scripts invoke — and `sub_82459280`, the gameplay Lua bring-up at 0x82311318,
    // is the first thing to exercise it in bulk. That call is currently entered and never
    // returned from, so this hook has to be removable in one run to be cleared or convicted:
    // REFII_LUA_NATIVE_TRACE=0 keeps the guest's behaviour and drops ours.
    static const bool observe = [] {
        const char* value = std::getenv("REFII_LUA_NATIVE_TRACE");
        return value != nullptr && std::string_view(value) == "1";
    }();
    if (!observe) {
        __imp__sub_82228730(ctx, base);
        return;
    }

    // Observation only for now: report which implementations the game actually calls, so
    // the interception surface is built against what runs rather than against all 120
    // registered names. Rate-limited per implementation, since a native like
    // IsScreenFading is polled every frame.
    // Total entries as well as natives, so "the hook never runs" is distinguishable from
    // "it runs but this VM calls nothing registered that way". There are TWO Lua VMs in
    // this title — the game VM and the Gameface UI VM — with their own copies of the
    // library, so a silent hook may simply be on the other one.
    static std::atomic<uint32_t> entries{0};
    const uint32_t entry = entries.fetch_add(1, std::memory_order_relaxed);
    // Where the filter gives up, counted separately — a silent zero could be any of the
    // four steps and guessing which has already cost this project time.
    static std::atomic<uint32_t> lua_closures{0};
    static std::atomic<uint32_t> c_no_upvalues{0};
    static std::atomic<uint32_t> upvalue_not_userdata{0};
    static std::atomic<bool> reported_upvalue{false};

    {
        const uint32_t closure = refii::vfs::LoadGuestU32(base, ctx.r4.u32);
        if (PlausibleGuestPointer(closure)) {
            if (base[closure + kClosureIsC] == 0) {
                lua_closures.fetch_add(1, std::memory_order_relaxed);
            } else if (base[closure + kClosureUpvalues] == 0) {
                c_no_upvalues.fetch_add(1, std::memory_order_relaxed);
            } else if (!reported_upvalue.exchange(true, std::memory_order_relaxed)) {
                // The first C closure that carries upvalues at all — dump the raw header
                // so the layout can be checked against the binary rather than assumed.
                REXLOG_INFO("Refii first C closure WITH upvalues: closure=0x{:08X} "
                            "isC={} nup={} uv0.value=0x{:08X} uv0.tt={} f=0x{:08X}",
                            closure, base[closure + kClosureIsC],
                            base[closure + kClosureUpvalues],
                            refii::vfs::LoadGuestU32(base, closure + kCClosureUpvalue0),
                            refii::vfs::LoadGuestU32(
                                base, closure + kCClosureUpvalue0 + kTValueType),
                            refii::vfs::LoadGuestU32(base, closure + 16u));
            }

            if (refii::vfs::LoadGuestU32(
                           base, closure + kCClosureUpvalue0 + kTValueType) !=
                       kLuaTUserdata) {
                upvalue_not_userdata.fetch_add(1, std::memory_order_relaxed);
            }
        }
    }

    if (entry == 0u || entry == 2000u || entry == 20000u) {
        REXLOG_INFO("Refii luaD_precall_2 entries={} lua={} c_no_upvalues={} "
                    "upvalue_not_userdata={} (func=0x{:08X})",
                    entry + 1u,
                    lua_closures.load(std::memory_order_relaxed),
                    c_no_upvalues.load(std::memory_order_relaxed),
                    upvalue_not_userdata.load(std::memory_order_relaxed),
                    ctx.r4.u32);
    }

    const uint32_t implementation = ImplementationFor(base, ctx.r4.u32);
    if (implementation != 0) {
        NoteImplementation(implementation);
    }

    __imp__sub_82228730(ctx, base);
}

/// Registration: name the implementation that is about to be bound.
///
/// sub_823CE420(namespace, L, name) runs between lua_pushcclosure and lua_setfield, so the
/// closure is the top Lua value and its implementation is one dereference away — the same
/// upvalue-1 userdata the call path reads. Recording it here means the interception surface
/// is keyed by the identity the runtime actually uses, rather than by a metadata column
/// that holds the shared THUNK for some entries.
REX_EXTERN(sub_823CE420) {
    REX_FUNC_PROLOGUE();

    // Same gate as the call-path hook, and it should have covered this from the start.
    //
    // The measured stall is inside `sub_82458C88` — the native registration fan-out, which
    // calls THIS function once per binding across ~3,000 natives. Our work here is a
    // three-hop pointer chase plus a best-effort string read, under a mutex, inline on that
    // thread. An earlier run appeared to exonerate the UI cut, but REFII_LUA_NATIVE_TRACE=0
    // only silenced the luaD_precall_2 hook; this one kept running. That is the wrong kind
    // of "cleared", so the flag now covers both halves and the hook can be judged honestly.
    static const bool observe = [] {
        const char* value = std::getenv("REFII_LUA_NATIVE_TRACE");
        return value != nullptr && std::string_view(value) == "1";
    }();
    if (!observe) {
        __imp__sub_823CE420(ctx, base);
        return;
    }

    const uint32_t lua_state = ctx.r4.u32;
    const uint32_t name_object = ctx.r5.u32;

    if (base != nullptr && lua_state != 0) {
        // L->top is at +8; the pushed closure is the TValue below it.
        const uint32_t top = refii::vfs::LoadGuestU32(base, lua_state + 8u);
        if (PlausibleGuestPointer(top)) {
            const uint32_t implementation = ImplementationFor(base, top - 8u);
            if (implementation != 0) {
                // The name may be the characters, a string object holding them, or an
                // object holding a record that holds them. Try each rather than guess:
                // getting this wrong is silent, and a wrong dereference reads rubbish that
                // looks like a name.
                std::string name;
                uint32_t candidate = name_object;
                for (int hop = 0; hop < 3 && name.empty(); ++hop) {
                    if (!PlausibleGuestPointer(candidate)) {
                        break;
                    }

                    std::string text =
                        refii::vfs::ReadBestEffortGuestString(base, candidate, 128);

                    // A registered name is ASCII and short. Anything else means we are one
                    // dereference short.
                    bool sane = !text.empty() && text.size() < 64u;
                    for (char c : text) {
                        if (c < 0x20 || c > 0x7E) {
                            sane = false;
                            break;
                        }
                    }

                    if (sane) {
                        name = std::move(text);
                        break;
                    }

                    candidate = refii::vfs::LoadGuestU32(base, candidate);
                }

                if (!name.empty()) {
                    std::lock_guard<std::mutex> names(NameMutex());
                    NameByImplementation()[implementation] = name;
                }
            }
        }
    }

    __imp__sub_823CE420(ctx, base);
}
