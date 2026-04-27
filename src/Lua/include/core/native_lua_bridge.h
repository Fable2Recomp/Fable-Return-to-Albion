#pragma once

#include <cstdint>
#include <string>
#include <string_view>

struct PPCContext;

namespace refii::lua {

class GuestLuaBridge {
 public:
  GuestLuaBridge(PPCContext& ppc, uint8_t* base, uint32_t lua_state_guest);
  ~GuestLuaBridge();

  GuestLuaBridge(const GuestLuaBridge&) = delete;
  GuestLuaBridge& operator=(const GuestLuaBridge&) = delete;

  int GetTop() const;
  void RestoreTop(int top) const;

  bool PushGlobal(std::string_view name);
  bool PushString(std::string_view value);
  void PushInteger(int32_t value) const;
  void PushBoolean(bool value) const;

  bool PCall(int nargs, int nresults, int errfunc = 0) const;

  bool ReadString(int index, std::string& out) const;
  bool ReadBoolean(int index, bool& out) const;
  bool ReadValueType(int index, int& out) const;
  bool DescribeValue(int index, std::string& out) const;

 private:
  uint32_t AllocateScratch(size_t size, size_t alignment = 4);
  uint32_t WriteScratchString(std::string_view value);
  uint32_t IndexToAddress(int index) const;

  PPCContext& ppc_;
  uint8_t* base_ = nullptr;
  uint32_t lua_state_guest_ = 0;
  uint32_t original_stack_pointer_ = 0;
  uint32_t scratch_begin_guest_ = 0;
  uint32_t scratch_end_guest_ = 0;
  uint32_t scratch_cursor_guest_ = 0;
};

}  // namespace refii::lua
