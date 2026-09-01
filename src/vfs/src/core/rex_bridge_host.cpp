// refii — the host side of the payload's ReXGlue runtime bridge.
//
// Each entry is the real rex:: call, reached through C so the payload never imports a C++
// symbol from us. Lives in refii_vfs, which the payload already links, so no shadow SDK
// source is needed; the pointers are the host's own objects handed back opaquely.

#include "core/rex_payload_bridge.h"

// For the cvar bridge below: TakeString turns the caller-buffer getter back into a
// std::string on THIS side's heap, which is where rex::cvar's getter has to return one.
#include "core/payload_bridge_string.h"

#include <cstring>
#include <filesystem>
#include <string>
#include <string_view>
#include <utility>

#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/thread.h>

#include <rex/chrono/clock.h>
#include <rex/perf/counter.h>
#include <rex/ppc/context.h>
#include <rex/runtime.h>
#include <rex/system/kernel_state.h>
#include <rex/system/mmio_handler.h>
#include <rex/system/xmemory.h>
#include <rex/system/xthread.h>
#include <rex/kernel/xboxkrnl/video.h>
#include <rex/kernel/xboxkrnl/threading.h>

extern "C" uint64_t refii_bridge_rex_query_guest_tick_count(void) {
  return ::rex::chrono::Clock::QueryGuestTickCount();
}

extern "C" void* refii_bridge_rex_resolve_indirect_function(uint32_t guest_address) {
  return reinterpret_cast<void*>(::rex::runtime::ResolveIndirectFunction(guest_address));
}

extern "C" void refii_bridge_rex_increment_counter(uint32_t counter_id, int64_t delta) {
  ::rex::perf::IncrementCounter(static_cast<::rex::perf::CounterId>(counter_id), delta);
}

extern "C" uint32_t refii_bridge_rex_system_heap_alloc(void* memory, uint32_t size,
                                                       uint32_t alignment, uint32_t flags) {
  return static_cast<::rex::memory::Memory*>(memory)->SystemHeapAlloc(size, alignment, flags);
}

extern "C" void refii_bridge_rex_system_heap_free(void* memory, uint32_t address,
                                                  uint32_t* out_region_size) {
  static_cast<::rex::memory::Memory*>(memory)->SystemHeapFree(address, out_region_size);
}

extern "C" const void* refii_bridge_rex_lookup_heap(const void* memory, uint32_t address) {
  return static_cast<const ::rex::memory::Memory*>(memory)->LookupHeap(address);
}

extern "C" int32_t refii_bridge_rex_query_region_info(void* heap, uint32_t base_address,
                                                      void* out_info) {
  return static_cast<::rex::memory::BaseHeap*>(heap)->QueryRegionInfo(
             base_address, static_cast<::rex::memory::HeapAllocationInfo*>(out_info))
             ? 1
             : 0;
}

extern "C" void* refii_bridge_rex_mmio_global_handler(void) {
  return ::rex::runtime::MMIOHandler::global_handler();
}

extern "C" int32_t refii_bridge_rex_mmio_check_load(void* handler, uint32_t address,
                                                    uint32_t* out_value) {
  return static_cast<::rex::runtime::MMIOHandler*>(handler)->CheckLoad(address, out_value) ? 1
                                                                                           : 0;
}

extern "C" int32_t refii_bridge_rex_mmio_check_store(void* handler, uint32_t address,
                                                     uint32_t value) {
  return static_cast<::rex::runtime::MMIOHandler*>(handler)->CheckStore(address, value) ? 1 : 0;
}

extern "C" void* refii_bridge_rex_runtime_instance(void) { return ::rex::Runtime::instance(); }

extern "C" size_t refii_bridge_rex_runtime_root(uint32_t kind, char* out, size_t capacity) {
  const ::rex::Runtime* runtime = ::rex::Runtime::instance();
  if (runtime == nullptr) {
    return 0;
  }

  const std::filesystem::path* root = nullptr;
  switch (kind) {
    case 0: root = &runtime->game_data_root(); break;
    case 1: root = &runtime->user_data_root(); break;
    case 2: root = &runtime->update_data_root(); break;
    case 3: root = &runtime->cache_root(); break;
    default: return 0;
  }

  const std::u8string utf8 = root->u8string();
  if (out != nullptr && capacity != 0u) {
    const size_t copy = capacity < utf8.size() ? capacity : utf8.size();
    std::memcpy(out, utf8.data(), copy);
  }
  return utf8.size();
}

extern "C" void* refii_bridge_rex_kernel_state(void) { return ::rex::system::kernel_state(); }

extern "C" void* refii_bridge_rex_xthread_current(void) {
  return ::rex::system::XThread::GetCurrentThread();
}

extern "C" int32_t refii_bridge_rex_xthread_is_in_thread(void) {
  return ::rex::system::XThread::IsInThread() ? 1 : 0;
}

extern "C" void refii_bridge_rex_xthread_set_last_error(uint32_t error_code) {
  ::rex::system::XThread::SetLastError(error_code);
}

extern "C" void refii_bridge_rex_xthread_enqueue_apc(void* thread, uint32_t normal_routine,
                                                     uint32_t normal_context, uint32_t arg1,
                                                     uint32_t arg2) {
  static_cast<::rex::system::XThread*>(thread)->EnqueueApc(normal_routine, normal_context, arg1,
                                                           arg2);
}

extern "C" void refii_bridge_rex_vd_query_video_mode(void* video_mode) {
  ::rex::kernel::xboxkrnl::VdQueryVideoMode(
      static_cast<::rex::system::X_VIDEO_MODE*>(video_mode));
}

extern "C" uint32_t refii_bridge_rex_nt_set_event(uint32_t handle, void* previous_state) {
  return ::rex::kernel::xboxkrnl::xeNtSetEvent(
      handle, static_cast<::rex::be<uint32_t>*>(previous_state));
}

extern "C" void* refii_bridge_rex_default_logger(void) { return ::rex::GetLogger().get(); }

extern "C" void refii_bridge_rex_logger_flush(void* logger) {
  if (logger != nullptr) {
    static_cast<spdlog::logger*>(logger)->flush();
  }
}

extern "C" void* refii_bridge_rex_global_critical_region_mutex(void) {
  return &::rex::thread::global_critical_region::mutex();
}

extern "C" void refii_bridge_rex_global_critical_region_lock(void) {
  ::rex::thread::global_critical_region::mutex().lock();
}

extern "C" int32_t refii_bridge_rex_global_critical_region_try_lock(void) {
  return ::rex::thread::global_critical_region::mutex().try_lock() ? 1 : 0;
}

extern "C" void refii_bridge_rex_global_critical_region_unlock(void) {
  ::rex::thread::global_critical_region::mutex().unlock();
}

// CVAR REGISTRATION FOR THE PAYLOAD. Its cvars have to reach the same registry the launcher
// and the config file drive, and rex::cvar::RegisterFlag takes a FlagEntry holding
// std::function and std::string -- neither of which survives a compiler boundary. The entry
// is therefore rebuilt HERE, on this side, from scalars and text, and its setter and getter
// close over the payload's own function pointers.
//
// The name is copied into the closures because the payload's `name` argument is only valid
// for the duration of this call.
extern "C" int32_t refii_bridge_rex_cvar_register(
    const char* name, uint32_t type, const char* category, const char* description,
    const char* default_value, uint32_t lifecycle, int32_t is_debug_only,
    int32_t (*setter)(const char* name, const char* value),
    size_t (*getter)(const char* name, char* out, size_t capacity)) {
  if (name == nullptr || setter == nullptr || getter == nullptr) {
    return 0;
  }

  std::string owned_name(name);

  ::rex::cvar::FlagEntry entry{};
  entry.name = owned_name;
  entry.type = static_cast<::rex::cvar::FlagType>(type);
  entry.category = category != nullptr ? category : "";
  entry.description = description != nullptr ? description : "";
  entry.setter = [setter, owned_name](std::string_view value) {
    // std::string_view is not NUL-terminated; the C side takes a C string.
    return setter(owned_name.c_str(), std::string(value).c_str()) != 0;
  };
  entry.getter = [getter, owned_name]() {
    return ::refii::bridge::TakeString([&](char* out, size_t capacity) {
      return getter(owned_name.c_str(), out, capacity);
    });
  };
  entry.command_callback = [](auto&&...) {};
  entry.lifecycle = static_cast<::rex::cvar::Lifecycle>(lifecycle);
  entry.default_value = default_value != nullptr ? default_value : "";
  entry.is_debug_only = is_debug_only != 0;

  return ::rex::cvar::RegisterFlag(std::move(entry)).has_value() ? 1 : 0;
}

extern "C" void refii_bridge_rex_cvar_unregister(const char* name) {
  if (name != nullptr) {
    ::rex::cvar::UnregisterFlag(name);
  }
}
