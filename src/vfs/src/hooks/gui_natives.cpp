// The UI cut: intercepting the ~120 `GUI.*` Lua natives the game VM calls. See
// docs/ui_native_cut.md.
//
// One hook, not 120 — every lua_CFunction in the game VM is invoked through a single indirect
// call inside luaD_precall_2 (0x822289F0):
//
//     v26 = (*(int (**)(int))(**(u32**)(*(u32*)(L + 20) + 4) + 16))(L);
//
// Note which copy: the title links two Lua libraries, and luaD_precall (0x8219EF68) is the
// Gameface UI VM's — it never sees a GUI native, because RegisterGUI registers into the game VM.
//
// The identity is the implementation pointer, held in the thunk's upvalue 1, never the
// registered function: that is a thunk encoding argument shape only and shared across
// namespaces (sub_823CE2E0 alone serves 81 registrations including GDB and EntityManager).

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstdlib>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>

#include <rex/logging.h>

#include "vfs_hooks_private.h"
#include "core/guest_state.h"
#include "gui/gui_native_calls.h"
#include "hooks/ppc_externs.h"

namespace {

/// Lua 5.1, 32-bit, as this title builds it: luaD_precall reads CClosure.f at +16
/// (0x8219F1C0), and lua_touserdata (0x822280F8) returns `*value + 24` for a full userdata,
/// fixing the Udata header at 24 bytes.
constexpr uint32_t kClosureIsC = 6u;        // lu_byte isC
constexpr uint32_t kClosureUpvalues = 7u;   // lu_byte nupvalues
constexpr uint32_t kCClosureUpvalue0 = 20u; // TValue upvalue[0]
constexpr uint32_t kUdataHeader = 24u;
constexpr uint32_t kTValueType = 4u;
constexpr uint32_t kTValueSize = 8u;
constexpr uint32_t kLuaTUserdata = 7u;

/// `lua_State` field offsets, stock Lua 5.1 32-bit as the plan records them.
constexpr uint32_t kStateTop = 8u;

/// Lua type tags.
constexpr uint32_t kLuaTNil = 0u;
constexpr uint32_t kLuaTBoolean = 1u;
constexpr uint32_t kLuaTNumber = 3u;
constexpr uint32_t kLuaTString = 4u;
constexpr uint32_t kLuaTTable = 5u;

/// `Table` as the GAME VM lays it out — not the Gameface VM's layout. The two Lua 5.1 builds
/// differ field for field: Gameface's `luaH_getnum` (0x821E18D8) reads `sizearray` at +0x1C,
/// `array` at +0x0C and an `lsizenode` byte at +7; the game VM's (`luaH_getnum_v2`, 0x82229160)
/// reads +0x20, +0x10 and a full 32-bit node COUNT at +8. These are the game VM's — the VM this
/// hook's `luaD_precall` (0x82228730) belongs to, whose nil object is 0x82099700.
constexpr uint32_t kTableSizeNode = 0x08u;   // `82228470 lwz r11, 8(r3)` — a COUNT, not a log2
constexpr uint32_t kTableNode = 0x14u;       // `8222847c lwz r10, 0x14(r3)`
constexpr uint32_t kNodeSize = 20u;          // `82229054 addi r10, r10, 0x14`
constexpr uint32_t kNodeValueTag = 4u;       // `822290fc lwz r6, 4(r11)`
constexpr uint32_t kNodeKeyValue = 8u;       // `822290dc lwz r9, 8(r11)`
constexpr uint32_t kNodeKeyTag = 12u;        // `822290e4 lwz r8, 0xC(r11)`

/// Nodes walked per table — a bound so a misread `sizenode` cannot walk the heap.
constexpr uint32_t kMaxTableNodes = 256u;

/// `TString`: stock Lua 5.1 16-byte header, then the characters — next +0, tt +4, marked +5,
/// reserved +6, hash +8, len +12, characters +16 (`luaH_getstr` reads hash at +8,
/// `82228474 lwz r9, 8(r4)`). The length is in the header, so nothing here guesses.
constexpr uint32_t kTStringLength = 12u;
constexpr uint32_t kTStringPayload = 16u;

/// Reads a Lua string by its stated length — never through the best-effort searching reader,
/// which guesses between wide strings, string objects and runs of printable bytes. Does not
/// stop at unprintable bytes either: these strings are localised.
std::string ReadLuaString(uint8_t* base, uint32_t tstring, uint32_t max_chars) {
    const uint32_t length = refii::vfs::LoadGuestU32(base, tstring + kTStringLength);
    if (length == 0 || length > max_chars) {
        return std::string();
    }

    std::string text;
    text.reserve(length);
    for (uint32_t i = 0; i < length; ++i) {
        const char ch = static_cast<char>(
            refii::vfs::LoadGuestU8(base, tstring + kTStringPayload + i));
        if (ch == '\0') {
            break;
        }
        text.push_back(ch);
    }

    return text;
}

/// Arguments recorded per call, and the longest tag kept. Six covers every `GUI` native's
/// arity (`DisplayMenuBox` is the widest at 2..6) and bounds a misread `top`.
constexpr uint32_t kMaxArguments = 6u;
constexpr uint32_t kMaxTagLength = 128u;

/// A guest pointer, loosely. Only used to refuse to chase obvious rubbish while walking
/// structures another thread is mutating.
bool PlausibleGuestPointer(uint32_t value) {
    return value >= 0x10000u && value < 0xC0000000u;
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

/// Walks a table's NAMED entries into the argument.
///
/// Walks the nodes rather than hashing a key: this build's `luaS_newlstr` (0x82227DA0) hashes
/// with MurmurHash2 over big-endian words (`m = 0x5BD1E995`, seed = length), not stock Lua's
/// string hash, so the bucket cannot be computed host-side. The array part is skipped —
/// neither native reads an integer key.
void ReadTableFields(uint8_t* base, uint32_t table, refii::vfs::GuiNativeArgument& argument) {
    if (!PlausibleGuestPointer(table)) {
        return;
    }

    const uint32_t nodes = refii::vfs::LoadGuestU32(base, table + kTableSizeNode);
    const uint32_t node = refii::vfs::LoadGuestU32(base, table + kTableNode);
    if (nodes == 0 || nodes > kMaxTableNodes || !PlausibleGuestPointer(node)) {
        return;
    }

    for (uint32_t i = 0; i < nodes; ++i) {
        const uint32_t entry = node + i * kNodeSize;
        if (refii::vfs::LoadGuestU32(base, entry + kNodeKeyTag) != kLuaTString) {
            continue;
        }

        const uint32_t key = refii::vfs::LoadGuestU32(base, entry + kNodeKeyValue);
        if (!PlausibleGuestPointer(key)) {
            continue;
        }

        refii::vfs::GuiNativeArgument::Field field;
        field.name = ReadLuaString(base, key, kMaxTagLength);
        if (field.name.empty()) {
            continue;
        }

        const uint32_t tag = refii::vfs::LoadGuestU32(base, entry + kNodeValueTag);
        const uint32_t value = refii::vfs::LoadGuestU32(base, entry);
        switch (tag) {
            case kLuaTBoolean:
                field.kind = refii::vfs::GuiNativeArgument::Kind::kBoolean;
                field.boolean = value != 0u;
                break;
            case kLuaTNumber:
                field.kind = refii::vfs::GuiNativeArgument::Kind::kNumber;
                field.number = refii::vfs::RefiiGuestF32FromWord(value);
                break;
            case kLuaTString:
                field.kind = refii::vfs::GuiNativeArgument::Kind::kString;
                if (PlausibleGuestPointer(value)) {
                    field.text = ReadLuaString(base, value, kMaxTagLength);
                }
                break;
            default:
                field.kind = refii::vfs::GuiNativeArgument::Kind::kOther;
                break;
        }

        argument.fields.push_back(std::move(field));
    }
}

/// Reads one argument off the stack. `lua_Number` is `float` here, not double — every reader
/// in the title does `*(float*)tv`.
refii::vfs::GuiNativeArgument ReadArgument(uint8_t* base, uint32_t slot) {
    refii::vfs::GuiNativeArgument argument;
    const uint32_t tag = refii::vfs::LoadGuestU32(base, slot + kTValueType);
    const uint32_t value = refii::vfs::LoadGuestU32(base, slot);

    switch (tag) {
        case kLuaTNil:
            argument.kind = refii::vfs::GuiNativeArgument::Kind::kNone;
            break;
        case kLuaTBoolean:
            argument.kind = refii::vfs::GuiNativeArgument::Kind::kBoolean;
            argument.boolean = value != 0u;
            break;
        case kLuaTNumber:
            argument.kind = refii::vfs::GuiNativeArgument::Kind::kNumber;
            argument.number = refii::vfs::RefiiGuestF32FromWord(value);
            break;
        case kLuaTString:
            argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
            if (PlausibleGuestPointer(value)) {
                argument.text = ReadLuaString(base, value, kMaxTagLength);
            }
            break;
        case kLuaTTable:
            // `DisplayInfoBoxParams` takes its whole configuration this way, so the table is
            // walked rather than recorded as a shape.
            argument.kind = refii::vfs::GuiNativeArgument::Kind::kTable;
            ReadTableFields(base, value, argument);
            break;
        default:
            // Functions and userdata: the shape is recorded, the contents mean nothing here.
            argument.kind = refii::vfs::GuiNativeArgument::Kind::kOther;
            break;
    }

    return argument;
}

/// Records the call and its arguments. Read from `func`, not `L->base`: the hook runs before
/// `luaD_precall` rebases the state, so `L->base` still belongs to the caller. `func[1]` up to
/// `L->top` is the argument list at both moments.
void TakeCall(uint8_t* base, uint32_t lua_state, uint32_t func, const std::string& name) {
    refii::vfs::GuiNativeCall call;
    call.name = name;

    const uint32_t top = refii::vfs::LoadGuestU32(base, lua_state + kStateTop);
    if (PlausibleGuestPointer(top) && top > func) {
        const uint32_t count =
            std::min<uint32_t>((top - func) / kTValueSize - 1u, kMaxArguments);
        for (uint32_t i = 0; i < count; ++i) {
            call.arguments.push_back(ReadArgument(base, func + (kTValueSize * (i + 1u))));
        }
    }

    refii::vfs::PostGuiNativeCall(std::move(call));
}

}  // namespace

REX_EXTERN(rex_luaD_precall) {
    REX_FUNC_PROLOGUE();

    // The cheap gate first: this runs for every native the game VM calls, thousands a second at
    // boot, and until `RegisterGUI` has run every lookup below is guaranteed to miss.
    if (refii::vfs::GuiNativesRegistered()) {
        const uint32_t implementation = ImplementationFor(base, ctx.r4.u32);
        if (implementation != 0) {
            if (const std::string* name = refii::vfs::GuiNativeNameFor(implementation)) {
                TakeCall(base, ctx.r3.u32, ctx.r4.u32, *name);

                // Names the thunk about to run: it would reach its implementation through
                // `lua_touserdata(L, -10003)`, the same pointer chase already done here.
                refii::vfs::NoteGuiNativeEntry(implementation);

#if !defined(__ANDROID__)
                // Diagnostics only: a mutex and a map write per GUI native call. Not on Android,
                // where the guest is CPU-bound.
                static std::mutex mutex;
                static std::unordered_map<uint32_t, uint32_t> seen;
                std::lock_guard<std::mutex> lock(mutex);
                if (++seen[implementation] == 1u) {
                    REXLOG_INFO("Refii UI cut: first GUI.{} impl=0x{:08X} "
                                "({} distinct native(s) seen)",
                                *name, implementation,
                                static_cast<uint32_t>(seen.size()));
                }
#endif
            }
        }
    }

    // Taking the call is not suppressing it: the guest's implementation still runs, so every
    // predicate the scripts read back keeps answering out of the guest's own state.
    // Suppression is a per-native decision made elsewhere.
    __imp__rex_luaD_precall(ctx, base);
}

/// Registration: name the implementation that is about to be bound.
///
/// `sub_823CE420(namespace, L, name)` runs between `lua_pushcclosure` and `lua_setfield`, so the
/// closure is the top Lua value and its implementation is one dereference away — the same
/// upvalue-1 userdata the call path reads.
///
/// Gated on the namespace being `GUI`: this is called once per binding across ~3,000 natives,
/// and the namespace is the first argument, so the rest cost one string compare each.
REX_EXTERN(rex_lhLuaNamespace_RegisterFunctionHash) {
    REX_FUNC_PROLOGUE();

    const uint32_t namespace_object = ctx.r3.u32;
    const uint32_t lua_state = ctx.r4.u32;
    const uint32_t name_object = ctx.r5.u32;

    if (base != nullptr && lua_state != 0 && PlausibleGuestPointer(namespace_object)) {
        // Both strings are read in the shape the binary states, never by probing the depth: a
        // probe that dereferenced past the characters loaded text as a pointer, which faults on
        // the Windows reserved arena.
        //
        // The namespace name is the `CCharString` handle at `*r3 + 8`: `r3` points at the
        // namespace SLOT, not the object (`sub_82307518` builds it with
        // `sub_823CE200(&slot, ..., "GUI")` and passes `&slot` down). This function copies it
        // with `sub_821F0240(v12, *v3 + 8)` and reaches the characters as `*v13[0]`
        // (0x823CE4B4) — handle, block, characters. Reading from `r3` instead lands on the
        // `lua_State` pointer at `nsObj+0`.
        //
        // The name (`r5`) is the characters themselves: `sub_821DA9C0(v13, a3)` takes it as a
        // `char*` and measures it a byte at a time.
        //
        // All three registration shapes reach here with the closure on top of the stack — the
        // shared `sub_823CE2E0`, the per-arity registrars (`sub_823091B0` and family) and
        // `sub_82307518`'s inline bindings (0x823075a4..0x823075e8) — which is why this
        // function, not any one registrar, is the observation point. Its own body is gated on
        // `*(u8*)(*r3 + 12)`, but this hook runs before handing the call on.
        const uint32_t namespace_slot_object = refii::vfs::LoadGuestU32(base, namespace_object);
        const std::string namespace_name =
            PlausibleGuestPointer(namespace_slot_object)
                ? refii::vfs::ReadGuestLhString(base, namespace_slot_object + 8u)
                : std::string();

        // A namespace census, so "no `GUI` natives" distinguishes never reaching this hook from
        // reaching it with a namespace that never reads as `GUI`.
        {
            static std::mutex mutex;
            static std::unordered_map<std::string, uint32_t> namespaces;
            static uint64_t calls = 0;
            static bool reported = false;

            std::lock_guard<std::mutex> lock(mutex);
            ++calls;
            if (namespaces.size() < 32u || namespaces.count(namespace_name) != 0u) {
                ++namespaces[namespace_name];
            }

            // After the registration loop has had time to run, not at the first call.
            if (!reported && calls >= 2000u) {
                reported = true;
                std::string census;
                for (const auto& entry : namespaces) {
                    if (!census.empty()) {
                        census += " ";
                    }
                    census += (entry.first.empty() ? std::string("<unreadable>") : entry.first) +
                              "=" + std::to_string(entry.second);
                }
                REXLOG_INFO("Refii UI cut: registration hook ran {} time(s); namespaces seen: {}",
                            calls, census);
            }
        }

        if (namespace_name == "GUI") {
            // L->top is at +8; the pushed closure is the TValue below it.
            const uint32_t top = refii::vfs::LoadGuestU32(base, lua_state + kStateTop);
            if (PlausibleGuestPointer(top)) {
                const uint32_t implementation = ImplementationFor(base, top - kTValueSize);
                std::string name = refii::vfs::FormatGuestAsciiBytes(base, name_object, 128u);

                // A native whose implementation is a bare `blr` is a stub, and all six stubs in
                // the namespace share one function. Interception keys on the implementation
                // pointer, so a call through it cannot be attributed to any one name — leave it
                // unnamed. Detected by the instruction (`blr` = 0x4E800020, big-endian) rather
                // than by an address list.
                if (implementation != 0 && !name.empty() &&
                    refii::vfs::LoadGuestU32(base, implementation) == 0x4E800020u) {
                    REXLOG_INFO("Refii UI cut: GUI.{} is a STUB — its implementation 0x{:08X} is "
                                "a bare blr, and every stub in the namespace shares it",
                                name, implementation);
                    name.clear();
                }

                if (implementation != 0 && !name.empty()) {
                    refii::vfs::NoteGuiNativeRegistration(implementation, std::move(name));
                }
            }
        }
    }

    __imp__rex_lhLuaNamespace_RegisterFunctionHash(ctx, base);
}


namespace {

/// `L->top`, and the write that pushes one result onto it, exactly as the thunks do: TValue at
/// `top`, type tag at `top+4`, `top` advanced by the 8-byte TValue size.
constexpr uint32_t kStateTopOffset = 8u;

bool PushAnswer(uint8_t* base, uint32_t lua_state, uint32_t tag, uint32_t value) {
    const uint32_t top = refii::vfs::LoadGuestU32(base, lua_state + kStateTopOffset);
    if (!PlausibleGuestPointer(top)) {
        return false;
    }

    refii::vfs::StoreGuestU32(base, top, value);
    refii::vfs::StoreGuestU32(base, top + 4u, tag);
    refii::vfs::StoreGuestU32(base, lua_state + kStateTopOffset, top + kTValueSize);
    return true;
}

/// Answers this thunk from the host, or false to let the guest's implementation run.
bool AnswerFromHost(PPCContext& ctx, uint8_t* base, bool boolean) {
    uint32_t bits = 0;
    const refii::vfs::GuiNativeAnswerKind kind =
        refii::vfs::GuiNativeAnswerFor(refii::vfs::CurrentGuiNativeEntry(), &bits);

    // The answer kind must match the thunk; the host cannot see which thunk it arrives at.
    const bool wanted = boolean ? kind == refii::vfs::GuiNativeAnswerKind::kBoolean
                                : kind == refii::vfs::GuiNativeAnswerKind::kFloat;
    if (!wanted) {
        return false;
    }

    if (!PushAnswer(base, ctx.r3.u32, boolean ? kLuaTBoolean : kLuaTNumber,
                    boolean ? (bits != 0u ? 1u : 0u) : bits)) {
        return false;
    }

    ctx.r3.u32 = 1u;  // one result, exactly as the thunk returns
    return true;
}

}  // namespace

/// True when the host owns this native outright and the guest's copy should not run. A void
/// native suppresses by returning zero results, as the shipped thunk does.
static bool SuppressForHost(PPCContext& ctx) {
    if (refii::vfs::GuiNativeAnswerFor(refii::vfs::CurrentGuiNativeEntry(), nullptr) !=
        refii::vfs::GuiNativeAnswerKind::kSuppress) {
        return false;
    }

    ctx.r3.u32 = 0u;  // no results
    return true;
}

#define REFII_SUPPRESSIBLE_THUNK(name)          \
    REX_EXTERN(name) {                          \
        REX_FUNC_PROLOGUE();                    \
        if (SuppressForHost(ctx)) {             \
            return;                             \
        }                                       \
        __imp__##name(ctx, base);               \
    }

REFII_SUPPRESSIBLE_THUNK(rex_lhLuaBind_ThunkVoidNoArgs)
REFII_SUPPRESSIBLE_THUNK(rex_lhLuaBind_ThunkVoidOneNumber)
REFII_SUPPRESSIBLE_THUNK(rex_LuaAPI_SetHeroSpellTableItem)
REFII_SUPPRESSIBLE_THUNK(rex_lhLuaBind_ThunkVoidTwoNumbers)
REFII_SUPPRESSIBLE_THUNK(rex_lhLuaBind_ThunkVoidFiveArgs)
REFII_SUPPRESSIBLE_THUNK(rex_lhLuaBind_ThunkForwardToDispatcher)

#undef REFII_SUPPRESSIBLE_THUNK

/// The no-argument BOOLEAN predicate thunk.
REX_EXTERN(rex_lhLuaBind_ThunkReturnBool) {
    REX_FUNC_PROLOGUE();

    if (AnswerFromHost(ctx, base, /*boolean=*/true)) {
        return;
    }

    __imp__rex_lhLuaBind_ThunkReturnBool(ctx, base);
}

/// The no-argument FLOAT predicate thunk.
REX_EXTERN(rex_lhLuaBind_ThunkReturnFloat) {
    REX_FUNC_PROLOGUE();

    if (AnswerFromHost(ctx, base, /*boolean=*/false)) {
        return;
    }

    __imp__rex_lhLuaBind_ThunkReturnFloat(ctx, base);
}
