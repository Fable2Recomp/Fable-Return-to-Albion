#include "core/native_lua_bridge.h"
#include "core/native_lua_rexglue_compat.h"

#include <algorithm>
#include <cstdio>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc.h>

PPC_EXTERN_FUNC(rex_lua_getfield);
PPC_EXTERN_FUNC(rex_lua_index2adr);
PPC_EXTERN_FUNC(rex_lua_pushinteger);
PPC_EXTERN_FUNC(rex_lua_pushlstring);
PPC_EXTERN_FUNC(sub_8219A908);
PPC_EXTERN_FUNC(sub_82227A50); // Lumen: lua_pushboolean
PPC_EXTERN_FUNC(sub_822D7888); // Lumen: lua_objlen
PPC_EXTERN_FUNC(sub_82BCBD08); // Lumen: lua_settop
PPC_EXTERN_FUNC(sub_8220A4B0); // Lumen: lua_pushnumber (float32 lua_Number)
PPC_EXTERN_FUNC(sub_82229218); // Lumen: lua_pushvalue

namespace refii::lua {
namespace {

constexpr uint32_t kGuestFrameSize = 0x1000;
constexpr uint32_t kScratchStartOffset = 0x100;
constexpr uint32_t kScratchEndPadding = 0x40;

constexpr int kLuaTNone = -1;
constexpr int kLuaTNil = 0;
constexpr int kLuaTBoolean = 1;
constexpr int kLuaTLightUserdata = 2;
constexpr int kLuaTNumber = 3;
constexpr int kLuaTString = 4;
constexpr int kLuaTTable = 5;
constexpr int kLuaTFunction = 6;
constexpr int kLuaTUserdata = 7;
constexpr int kLuaTThread = 8;
constexpr int kLuaRegistryIndex = -10000;

std::string_view LuaTypeName(int type) {
  switch (type) {
    case kLuaTNone:
      return "no value";
    case kLuaTNil:
      return "nil";
    case kLuaTBoolean:
      return "boolean";
    case kLuaTLightUserdata:
      return "lightuserdata";
    case kLuaTNumber:
      return "number";
    case kLuaTString:
      return "string";
    case kLuaTTable:
      return "table";
    case kLuaTFunction:
      return "function";
    case kLuaTUserdata:
      return "userdata";
    case kLuaTThread:
      return "thread";
    default:
      return "unknown";
  }
}

bool IsAddressableStackIndex(int top, int index) {
  if (index == 0) {
    return false;
  }
  if (index > 0) {
    return index <= top;
  }
  return -index <= top;
}

void SanitizeLuaLogText(std::string& value) {
  for (char& ch : value) {
    if (ch == '\r' || ch == '\n' || ch == '\t') {
      ch = ' ';
    }
  }
}

}  // namespace

GuestLuaBridge::GuestLuaBridge(PPCContext& ppc, uint8_t* base, uint32_t lua_state_guest)
    : ppc_(ppc),
      base_(base),
      lua_state_guest_(lua_state_guest),
      original_stack_pointer_(ppc.r1.u32) {
  const uint32_t reserved_stack =
      (original_stack_pointer_ - kGuestFrameSize) & ~static_cast<uint32_t>(0x1F);
  ppc_.r1.u32 = reserved_stack;
  scratch_begin_guest_ = reserved_stack + kScratchStartOffset;
  scratch_end_guest_ = original_stack_pointer_ - kScratchEndPadding;
  scratch_cursor_guest_ = scratch_begin_guest_;
}

GuestLuaBridge::~GuestLuaBridge() { ppc_.r1.u32 = original_stack_pointer_; }

int GuestLuaBridge::GetTop() const {
  const uint32_t top = PpcLoadU32(base_, lua_state_guest_ + 8);
  const uint32_t stack_base = PpcLoadU32(base_, lua_state_guest_ + 32);
  return static_cast<int>((top - stack_base) / 8);
}

void GuestLuaBridge::RestoreTop(int top) const {
  ppc_.r3.u32 = lua_state_guest_;
  ppc_.r4.s64 = top;
  sub_82BCBD08(ppc_, base_);
}

bool GuestLuaBridge::PushGlobal(std::string_view name) {
  const uint32_t guest_name = WriteScratchString(name);
  if (guest_name == 0) {
    REXLOG_WARN("Native Lua bridge ran out of scratch space while pushing {}", name);
    return false;
  }

  ppc_.r3.u32 = lua_state_guest_;
  ppc_.r4.u32 = guest_name;
  rex_lua_getfield(ppc_, base_);
  return ppc_.r3.s32 != 0;
}

bool GuestLuaBridge::PushString(std::string_view value) {
  const uint32_t guest_value = WriteScratchString(value);
  if (guest_value == 0) {
    REXLOG_WARN("Native Lua bridge ran out of scratch space while pushing a string value");
    return false;
  }

  ppc_.r3.u32 = lua_state_guest_;
  ppc_.r4.u32 = guest_value;
  ppc_.r5.u32 = static_cast<uint32_t>(value.size());
  rex_lua_pushlstring(ppc_, base_);
  return true;
}

void GuestLuaBridge::PushInteger(int32_t value) const {
  ppc_.r3.u32 = lua_state_guest_;
  ppc_.r4.s64 = value;
  rex_lua_pushinteger(ppc_, base_);
}

void GuestLuaBridge::PushBoolean(bool value) const {
  ppc_.r3.u32 = lua_state_guest_;
  ppc_.r4.s64 = value ? 1 : 0;
  sub_82227A50(ppc_, base_);
}

void GuestLuaBridge::PushNumber(double value) const {
  ppc_.r3.u32 = lua_state_guest_;
  // The guest build stores lua_Number as float32 (stfs f1); passing the
  // double in f1 lets the guest instruction do the narrowing.
  ppc_.f1.f64 = value;
  sub_8220A4B0(ppc_, base_);
}

void GuestLuaBridge::PushNil() const {
  // 5.1 semantics: settop above the current top fills the gap with nil.
  ppc_.r3.u32 = lua_state_guest_;
  ppc_.r4.s64 = GetTop() + 1;
  sub_82BCBD08(ppc_, base_);
}

void GuestLuaBridge::PushValueAt(int index) const {
  ppc_.r3.u32 = lua_state_guest_;
  ppc_.r4.s64 = index;
  sub_82229218(ppc_, base_);
}

bool GuestLuaBridge::PCall(int nargs, int nresults, int errfunc) const {
  ppc_.r3.u32 = lua_state_guest_;
  ppc_.r4.s64 = nargs;
  ppc_.r5.s64 = nresults;
  ppc_.r6.s64 = errfunc;
  sub_8219A908(ppc_, base_);
  return ppc_.r3.s32 == 0;
}

bool GuestLuaBridge::ReadString(int index, std::string& out) const {
  ppc_.r3.u32 = lua_state_guest_;
  ppc_.r4.s64 = index;
  sub_822D7888(ppc_, base_);
  const uint32_t guest_ptr = ppc_.r3.u32;
  if (guest_ptr == 0) {
    return false;
  }

  uint8_t* base = base_;
  out.clear();
  for (size_t i = 0; i < 4096; ++i) {
    const char byte =
        static_cast<char>(PpcLoadU8(base, guest_ptr + static_cast<uint32_t>(i)));
    if (byte == '\0') {
      return true;
    }
    out.push_back(byte);
  }

  return true;
}

bool GuestLuaBridge::ReadBoolean(int index, bool& out) const {
  const uint32_t value_address = IndexToAddress(index);
  if (value_address == 0) {
    return false;
  }

  uint8_t* base = base_;
  const uint32_t type = PpcLoadU32(base, value_address + 4);
  if (type == 0) {
    out = false;
    return true;
  }

  if (type == 1) {
    out = PpcLoadU32(base, value_address) != 0;
    return true;
  }

  out = true;
  return true;
}

bool GuestLuaBridge::ReadNumber(int index, double& out) const {
  const uint32_t value_address = IndexToAddress(index);
  if (value_address == 0) {
    return false;
  }

  uint8_t* base = base_;
  if (PpcLoadU32(base, value_address + 4) != kLuaTNumber) {
    return false;
  }

  const uint32_t bits = PpcLoadU32(base, value_address);
  float value;
  std::memcpy(&value, &bits, sizeof(value));
  out = value;
  return true;
}

bool GuestLuaBridge::ReadValueType(int index, int& out) const {
  if (index > kLuaRegistryIndex && !IsAddressableStackIndex(GetTop(), index)) {
    out = kLuaTNone;
    return true;
  }

  const uint32_t value_address = IndexToAddress(index);
  if (value_address == 0) {
    return false;
  }

  uint8_t* base = base_;
  out = static_cast<int>(PpcLoadU32(base, value_address + 4));
  return true;
}

bool GuestLuaBridge::DescribeValue(int index, std::string& out) const {
  int type = kLuaTNone;
  if (!ReadValueType(index, type)) {
    return false;
  }

  const uint32_t value_address = IndexToAddress(index);
  if (value_address == 0) {
    return false;
  }

  uint8_t* base = base_;
  const uint32_t raw_value = PpcLoadU32(base, value_address);

  out.clear();

  switch (type) {
    case kLuaTNone:
      out = "no value";
      return true;

    case kLuaTNil:
      out = "nil";
      return true;

    case kLuaTBoolean:
      out = raw_value != 0 ? "boolean true" : "boolean false";
      return true;

    case kLuaTString:
    case kLuaTNumber: {
      std::string text;
      if (ReadString(index, text) && !text.empty()) {
        SanitizeLuaLogText(text);
        out = std::string(LuaTypeName(type));
        out += ": ";
        out += text;
        return true;
      }
      out = std::string(LuaTypeName(type));
      return true;
    }

    case kLuaTLightUserdata:
    case kLuaTTable:
    case kLuaTFunction:
    case kLuaTUserdata:
    case kLuaTThread: {
      char buffer[64];
      std::snprintf(buffer, sizeof(buffer), "%.*s@0x%08X",
                    static_cast<int>(LuaTypeName(type).size()), LuaTypeName(type).data(),
                    raw_value);
      out = buffer;
      return true;
    }

    default: {
      char buffer[64];
      std::snprintf(buffer, sizeof(buffer), "unknown(type=%d,value=0x%08X)", type, raw_value);
      out = buffer;
      return true;
    }
  }
}

void GuestLuaBridge::ResetScratch() { scratch_cursor_guest_ = scratch_begin_guest_; }

uint32_t GuestLuaBridge::AllocateScratch(size_t size, size_t alignment) {
  const uint32_t mask = static_cast<uint32_t>(std::max<size_t>(alignment, 1) - 1);
  const uint32_t cursor = (scratch_cursor_guest_ + mask) & ~mask;
  const uint32_t end = cursor + static_cast<uint32_t>(size);
  if (end > scratch_end_guest_) {
    return 0;
  }

  scratch_cursor_guest_ = end;
  return cursor;
}

uint32_t GuestLuaBridge::WriteScratchString(std::string_view value) {
  const uint32_t guest_address = AllocateScratch(value.size() + 1, 4);
  if (guest_address == 0) {
    return 0;
  }

  std::memcpy(PpcGuestPtr(base_, guest_address), value.data(), value.size());
  *PpcGuestPtr(base_, guest_address + static_cast<uint32_t>(value.size())) = '\0';
  return guest_address;
}

uint32_t GuestLuaBridge::IndexToAddress(int index) const {
  ppc_.r3.u32 = lua_state_guest_;
  ppc_.r4.s64 = index;
  rex_lua_index2adr(ppc_, base_);
  return ppc_.r3.u32;
}

}  // namespace refii::lua
