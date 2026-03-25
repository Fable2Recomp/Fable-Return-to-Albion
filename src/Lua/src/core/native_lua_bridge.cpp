#include "core/native_lua_bridge.h"

#include <algorithm>
#include <cstring>

#include <rex/logging.h>
#include <rex/ppc.h>

PPC_EXTERN_FUNC(rex_lua_getfield);
PPC_EXTERN_FUNC(rex_lua_index2adr);
PPC_EXTERN_FUNC(rex_lua_pushinteger);
PPC_EXTERN_FUNC(rex_lua_pushlstring);
PPC_EXTERN_FUNC(sub_8219A908);
PPC_EXTERN_FUNC(sub_82227A50);
PPC_EXTERN_FUNC(sub_822D7888);
PPC_EXTERN_FUNC(sub_82BCBD08);

namespace refii::lua {
namespace {

constexpr uint32_t kGuestFrameSize = 0x1000;
constexpr uint32_t kScratchStartOffset = 0x100;
constexpr uint32_t kScratchEndPadding = 0x40;

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
  uint8_t* base = base_;
  const uint32_t top = PPC_LOAD_U32(lua_state_guest_ + 8);
  const uint32_t stack_base = PPC_LOAD_U32(lua_state_guest_ + 32);
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
    const char byte = static_cast<char>(PPC_LOAD_U8(guest_ptr + static_cast<uint32_t>(i)));
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
  const uint32_t type = PPC_LOAD_U32(value_address + 4);
  if (type == 0) {
    out = false;
    return true;
  }

  if (type == 1) {
    out = PPC_LOAD_U32(value_address) != 0;
    return true;
  }

  out = true;
  return true;
}

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

  std::memcpy(base_ + guest_address + PPC_PHYS_HOST_OFFSET(guest_address), value.data(),
              value.size());
  *(base_ + guest_address + PPC_PHYS_HOST_OFFSET(guest_address) + value.size()) = '\0';
  return guest_address;
}

uint32_t GuestLuaBridge::IndexToAddress(int index) const {
  ppc_.r3.u32 = lua_state_guest_;
  ppc_.r4.s64 = index;
  rex_lua_index2adr(ppc_, base_);
  return ppc_.r3.u32;
}

}  // namespace refii::lua
