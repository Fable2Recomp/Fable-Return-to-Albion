// refii - Fable 3's UI cut: the `GUI.*` Lua natives the game VM calls, captured with their
// arguments and posted to the host (core/gui_native_calls.h; the shared core drains them for
// Godot's Fable3GuiNatives). See docs/ui_native_cut.md for the design; this file is its Fable 3
// instance. Taking a call does not suppress it: the guest's implementation still runs.
//
// THE VM IS HAVOKSCRIPT ON LUA 5.1 SHAPES, and ONE library serves both the game scripts and
// Gameface (one copy of every ldo/lauxlib string; the Gameface bindings at 0x82C2xxxx call the
// same luaD_call sub_82215290). Every native is a SHARED DISPATCHER closure whose single upvalue
// is a 4-byte full userdata holding the C++ implementation: the registration helpers write
// `*lua_newuserdata(L, 4) = impl` (0x822E069C) then `pushcclosure(L, dispatcher, 1, 0)`
// (0x822E06B4) then the name hook sub_82359150 (0x822E06C4); every dispatcher opens with
// `closure = *(*(L+32) - 8)`, `tag = *(closure+16)`, tag 7 -> `impl = *(*(closure+12) + 16)`
// (0x822E10F0..0x822E1128). So the identity is the IMPLEMENTATION POINTER, as in Fable 2; the
// dispatcher is shared across namespaces and names nothing.
//
// ONE HOOK SEES EVERY C CALL: the C-precall sub_82214E40(&L->base_ci, L, nresults, funcTV*,
// newTop, pc), the only path to the three `bctrl` sites that run `closure->f(L)` (API luaD_call
// 0x822153B8, OP_CALL 0x822A5A28, OP_TAILCALL 0x822A6C18). On entry r4 = L, r6 = the function's
// TValue (tag 10 = cfunction, its closure at +0) and r7 = the new top, so the arguments are the
// TValues from func+8 up to r7 (the precall itself sets L->base = func+8 at 0x82214EB4 and
// L->top = newTop at 0x82214EB8).
//
// LAYOUTS (Fable 3 IDB, disc build): lua_State top +28 / base +32 (0x82215370 `lwz r7,0x1C(r31)`,
// 0x822E10F0 `lwz r4,0x20(r3)`); TValue 8 bytes, value +0, tag +4; tags 0 nil, 1 boolean,
// 2 lightuserdata, 3 number, 4 string, 5 table, 7 userdata, 10 cfunction (typename table
// 0x8329F638; pushcclosure 0x82239964 writes 10); lua_Number is a 32-bit FLOAT (0x82247280 `lfs
// f1,0(r30)` into "%0.14g"); CClosure f +0, env +4, nupvalues u16 +8, upvalue[0] TValue +12
// (0x822398B0..0x82239924); Udata payload at +16 (0x821FBEC4); TString length = `*ts & 0x3FFFFFFF`
// and characters at ts+8 (0x82247268 `clrlwi r9,r10,2`, 0x82247274 `addi r3,r11,8`); Table node
// MASK (count-1) at +4, node array at +8, 24-byte nodes with the key TValue at +0, the value TValue
// at +8 and the chain at +16 (luaH_get 0x82214948..0x8221498C, `idx*3*8`).
//
// THE NAME HOOK sub_82359150(r3 = namespace slot, r4 = L, r5 = name chars) runs right after
// pushcclosure in every registration helper (2,168 callers), with the closure the top TValue
// (`*(L+28) - 8`). The namespace object is `*r3`: +8 its CCharString handle (rep at +8, the
// characters at rep+0 — `sub_82226960(v11, *v3 + 8)` then `*(char**)v12[0]` at 0x823591E4), +12
// the byte gating the function's own body. The `GUI` table is `_G.GlobalTable.GUI`
// (sub_825237F0); `GUI.Emotion` is a namespace created inside it (0x82524E34).

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "core/gui_native_calls.h"
#include "core/payload_bridge.h"

REX_EXTERN(__imp__rex_luaD_precallC);
REX_EXTERN(__imp__rex_lhLuaNamespace_RegisterFunctionHash);

namespace {

constexpr uint32_t kStateTop = 28u;

constexpr uint32_t kTValueSize = 8u;
constexpr uint32_t kTValueTag = 4u;

constexpr uint32_t kLuaTNil = 0u;
constexpr uint32_t kLuaTBoolean = 1u;
constexpr uint32_t kLuaTLightUserdata = 2u;
constexpr uint32_t kLuaTNumber = 3u;
constexpr uint32_t kLuaTString = 4u;
constexpr uint32_t kLuaTTable = 5u;
constexpr uint32_t kLuaTUserdata = 7u;
constexpr uint32_t kLuaTCFunction = 10u;

constexpr uint32_t kClosureUpvalueCount = 8u;   // u16
constexpr uint32_t kClosureUpvalue0 = 12u;      // TValue
constexpr uint32_t kUdataPayload = 16u;

constexpr uint32_t kTStringLengthMask = 0x3FFFFFFFu;
constexpr uint32_t kTStringChars = 8u;

constexpr uint32_t kTableNodeMask = 4u;
constexpr uint32_t kTableNodes = 8u;
constexpr uint32_t kNodeSize = 24u;
constexpr uint32_t kNodeValue = 8u;

/// Bounds: eight covers every `GUI` native's arity (DisplayMenuBox is the widest); a misread
/// top or mask cannot walk the heap.
constexpr uint32_t kMaxArguments = 8u;
constexpr uint32_t kMaxTagLength = 128u;
constexpr uint32_t kMaxTableNodes = 256u;

/// The name hook's namespace census reports after the registration storm, not at the first call.
constexpr uint64_t kCensusAfterCalls = 2000u;

uint32_t LoadGuestU32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

uint16_t LoadGuestU16(const uint8_t* base, uint32_t address) {
  uint16_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap16(value);
}

float FloatFromBits(uint32_t bits) {
  float value = 0.0f;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

/// A guest pointer, loosely: enough to refuse obvious rubbish while walking structures another
/// thread may be mutating.
bool Plausible(uint32_t value) { return value >= 0x10000u && value < 0xC0000000u; }

/// The characters of a NUL-terminated guest string, bounded.
std::string ReadGuestAscii(const uint8_t* base, uint32_t address, uint32_t max_chars) {
  std::string text;
  if (!Plausible(address)) {
    return text;
  }
  for (uint32_t i = 0; i < max_chars; ++i) {
    const char ch = static_cast<char>(base[address + i]);
    if (ch == '\0') {
      break;
    }
    text.push_back(ch);
  }
  return text;
}

/// A TString by its stated length; a tag is a Babel key, not display text, but these strings are
/// localised literals too, so unprintable bytes do not stop the read.
std::string ReadLuaString(const uint8_t* base, uint32_t tstring, uint32_t max_chars) {
  if (!Plausible(tstring)) {
    return std::string();
  }
  const uint32_t length = LoadGuestU32(base, tstring) & kTStringLengthMask;
  if (length == 0u || length > max_chars) {
    return std::string();
  }
  std::string text;
  text.reserve(length);
  for (uint32_t i = 0; i < length; ++i) {
    const char ch = static_cast<char>(base[tstring + kTStringChars + i]);
    if (ch == '\0') {
      break;
    }
    text.push_back(ch);
  }
  return text;
}

/// The C++ implementation behind a function TValue, or 0 when it is not a dispatcher closure
/// registered the way the natives are.
uint32_t ImplementationFor(const uint8_t* base, uint32_t function_tvalue) {
  if (base == nullptr || !Plausible(function_tvalue)) {
    return 0u;
  }
  if (LoadGuestU32(base, function_tvalue + kTValueTag) != kLuaTCFunction) {
    return 0u;
  }
  const uint32_t closure = LoadGuestU32(base, function_tvalue);
  if (!Plausible(closure) || LoadGuestU16(base, closure + kClosureUpvalueCount) == 0u) {
    return 0u;
  }

  const uint32_t upvalue = LoadGuestU32(base, closure + kClosureUpvalue0);
  const uint32_t tag = LoadGuestU32(base, closure + kClosureUpvalue0 + kTValueTag);
  if (!Plausible(upvalue)) {
    return 0u;
  }
  // The dispatcher's own two cases (0x822E10FC / 0x822E1110): a full userdata's payload, or a
  // light userdata's pointer.
  if (tag == kLuaTUserdata) {
    return LoadGuestU32(base, upvalue + kUdataPayload);
  }
  if (tag == kLuaTLightUserdata) {
    return LoadGuestU32(base, upvalue);
  }
  return 0u;
}

/// The named entries of a table argument, by node scan: the hash is a pointer hash
/// (0x82214958..0x8221496C), so no bucket can be computed host-side. The array part is skipped;
/// the natives read their tables by key.
void ReadTableFields(const uint8_t* base, uint32_t table, refii::vfs::GuiNativeArgument& argument) {
  if (!Plausible(table)) {
    return;
  }
  const uint32_t count = LoadGuestU32(base, table + kTableNodeMask) + 1u;
  const uint32_t nodes = LoadGuestU32(base, table + kTableNodes);
  if (count == 0u || count > kMaxTableNodes || !Plausible(nodes)) {
    return;
  }

  for (uint32_t i = 0; i < count; ++i) {
    const uint32_t node = nodes + (i * kNodeSize);
    if (LoadGuestU32(base, node + kTValueTag) != kLuaTString) {
      continue;
    }
    refii::vfs::GuiNativeArgument::Field field;
    field.name = ReadLuaString(base, LoadGuestU32(base, node), kMaxTagLength);
    if (field.name.empty()) {
      continue;
    }

    const uint32_t tag = LoadGuestU32(base, node + kNodeValue + kTValueTag);
    const uint32_t value = LoadGuestU32(base, node + kNodeValue);
    switch (tag) {
      case kLuaTBoolean:
        field.kind = refii::vfs::GuiNativeArgument::Kind::kBoolean;
        field.boolean = value != 0u;
        break;
      case kLuaTNumber:
        field.kind = refii::vfs::GuiNativeArgument::Kind::kNumber;
        field.number = FloatFromBits(value);
        break;
      case kLuaTString:
        field.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        field.text = ReadLuaString(base, value, kMaxTagLength);
        break;
      default:
        field.kind = refii::vfs::GuiNativeArgument::Kind::kOther;
        break;
    }
    argument.fields.push_back(std::move(field));
  }
}

refii::vfs::GuiNativeArgument ReadArgument(const uint8_t* base, uint32_t slot) {
  refii::vfs::GuiNativeArgument argument;
  const uint32_t tag = LoadGuestU32(base, slot + kTValueTag);
  const uint32_t value = LoadGuestU32(base, slot);
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
      argument.number = FloatFromBits(value);
      break;
    case kLuaTString:
      argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
      argument.text = ReadLuaString(base, value, kMaxTagLength);
      break;
    case kLuaTTable:
      argument.kind = refii::vfs::GuiNativeArgument::Kind::kTable;
      ReadTableFields(base, value, argument);
      break;
    default:
      // Entities (userdata handles) and functions: the shape is recorded, the value means
      // nothing to the host.
      argument.kind = refii::vfs::GuiNativeArgument::Kind::kOther;
      break;
  }
  return argument;
}

/// Records the call: the arguments are the TValues from func+8 to the new top the precall was
/// handed, which is where L->top will be once it has run.
void TakeCall(const uint8_t* base, uint32_t function_tvalue, uint32_t new_top, const std::string& name) {
  refii::vfs::GuiNativeCall call;
  call.name = name;
  if (Plausible(new_top) && new_top > function_tvalue + kTValueSize) {
    const uint32_t count =
        std::min<uint32_t>((new_top - function_tvalue) / kTValueSize - 1u, kMaxArguments);
    for (uint32_t i = 0; i < count; ++i) {
      call.arguments.push_back(ReadArgument(base, function_tvalue + (kTValueSize * (i + 1u))));
    }
  }
  refii::vfs::PostGuiNativeCall(std::move(call));
}

}  // namespace

/// The C-precall: every C closure the game VM calls passes here once, before the call.
REX_EXTERN(rex_luaD_precallC) {
  // The cheap gate first: this runs for every native the VM calls, thousands a second, and
  // until the `GUI` namespace has registered every lookup below is guaranteed to miss.
  if (refii::vfs::GuiNativesRegistered()) {
    const uint32_t function_tvalue = ctx.r6.u32;
    const uint32_t implementation = ImplementationFor(base, function_tvalue);
    if (implementation != 0u) {
      if (const std::string* name = refii::vfs::GuiNativeNameFor(implementation)) {
        TakeCall(base, function_tvalue, ctx.r7.u32, *name);

        // Names the dispatcher about to run, for a thunk hook that answers from the host.
        refii::vfs::NoteGuiNativeEntry(implementation);

        static std::mutex mutex;
        static std::unordered_map<uint32_t, uint32_t> seen;
        std::lock_guard<std::mutex> lock(mutex);
        if (++seen[implementation] == 1u) {
          REXLOG_INFO("Refii UI cut: first GUI.{} impl=0x{:08X} ({} distinct native(s) seen)", *name,
                      implementation, static_cast<uint32_t>(seen.size()));
        }
      }
    }
  }

  __imp__rex_luaD_precallC(ctx, base);
}

/// Registration: names the implementation the closure on top of the stack is about to be bound
/// under. Gated on the namespace: this runs once per binding across 5,259 natives.
REX_EXTERN(rex_lhLuaNamespace_RegisterFunctionHash) {
  const uint32_t namespace_slot = ctx.r3.u32;
  const uint32_t lua_state = ctx.r4.u32;
  const uint32_t name_chars = ctx.r5.u32;

  if (base != nullptr && Plausible(namespace_slot) && Plausible(lua_state)) {
    const uint32_t namespace_object = LoadGuestU32(base, namespace_slot);
    std::string namespace_name;
    if (Plausible(namespace_object)) {
      const uint32_t rep = LoadGuestU32(base, namespace_object + 8u);
      if (Plausible(rep)) {
        namespace_name = ReadGuestAscii(base, LoadGuestU32(base, rep), 64u);
      }
    }

    // The census: "no GUI natives" must distinguish never reaching this hook from reaching it
    // with a namespace that never reads as GUI.
    {
      static std::mutex mutex;
      static std::unordered_map<std::string, uint32_t> namespaces;
      static uint64_t calls = 0u;
      static bool reported = false;
      std::lock_guard<std::mutex> lock(mutex);
      ++calls;
      if (namespaces.size() < 48u || namespaces.count(namespace_name) != 0u) {
        ++namespaces[namespace_name];
      }
      if (!reported && calls >= kCensusAfterCalls) {
        reported = true;
        std::string census;
        for (const auto& entry : namespaces) {
          if (!census.empty()) {
            census += " ";
          }
          census += (entry.first.empty() ? std::string("<unreadable>") : entry.first) + "=" +
                    std::to_string(entry.second);
        }
        REXLOG_INFO("Refii UI cut: registration hook ran {} time(s); namespaces seen: {}", calls, census);
      }
    }

    // `GUI` and the `Emotion` namespace nested inside it (sub_825237F0): the latter's natives
    // are named `Emotion.<Name>` so the host's table reads as the scripts do.
    const bool gui = namespace_name == "GUI";
    const bool emotion = namespace_name == "Emotion" || namespace_name == "GUI.Emotion";
    if (gui || emotion) {
      const uint32_t top = LoadGuestU32(base, lua_state + kStateTop);
      if (Plausible(top)) {
        const uint32_t implementation = ImplementationFor(base, top - kTValueSize);
        std::string name = ReadGuestAscii(base, name_chars, 128u);
        if (emotion && !name.empty()) {
          name = "Emotion." + name;
        }

        // A native whose implementation is a bare `blr` is a stub, and the stubs share one
        // function (AddHealthBar / RemoveHealthBar / DisplayFastTravelScreen = 0x82C1BA40).
        // Interception keys on the implementation, so a call through it cannot be attributed
        // to one name — leave it unnamed. Detected by the instruction, not an address list.
        if (implementation != 0u && !name.empty() && Plausible(implementation) &&
            LoadGuestU32(base, implementation) == 0x4E800020u) {
          REXLOG_INFO("Refii UI cut: GUI.{} is a STUB (implementation 0x{:08X} is a bare blr)", name,
                      implementation);
          name.clear();
        }

        if (implementation != 0u && !name.empty()) {
          refii::vfs::NoteGuiNativeRegistration(implementation, std::move(name));
        }
      }
    }
  }

  __imp__rex_lhLuaNamespace_RegisterFunctionHash(ctx, base);
}
