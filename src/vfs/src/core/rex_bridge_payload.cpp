// refii — the payload side of the ReXGlue runtime bridge.
//
// DEFINES THE rex:: SYMBOLS THE PAYLOAD CALLS, INSIDE THE PAYLOAD. The generated code and
// the hooks call rex:: functions by their C++ names, and those names cannot be changed —
// ReXGlue's codegen emits them and is not ours to alter. Shadowing the eleven SDK headers
// that declare them would work, but this is the same result for one file: define each one
// here as a forwarder to the C entry in core/rex_payload_bridge.h.
//
// The mangled name is then RESOLVED WITHIN THE MODULE rather than imported, so nothing
// crosses to the host but C. That is what lets a payload built by the player's compiler —
// on Windows, one whose C++ ABI is not ours — link against libraries we built.
//
// This file compiles ONLY into the payload (REFII_VFS_HOOK_INTERNAL_SOURCES). Building it
// into refii_vfs as well would give the host two definitions of every one of these.
//
// COMPILED -fvisibility=hidden, and it does not work without that. These definitions collide
// with librexruntime's own, and the payload is dlopen'd RTLD_GLOBAL — the global scope is
// searched ahead of refii_vfs's link group, so an EXPORTED copy captures refii_vfs's lazily
// bound call and rex_bridge_host.cpp's forwarder recurses into itself until the stack ends.
// Set in src/hooks/CMakeLists.txt and in GameCodeBuild.CompileAllAsync, one per build path.

#include "core/rex_payload_bridge.h"

#include <memory>
#include <mutex>

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

namespace rex::chrono {
uint64_t Clock::QueryGuestTickCount() { return refii_bridge_rex_query_guest_tick_count(); }
}  // namespace rex::chrono

namespace rex::runtime {

PPCFunc* ResolveIndirectFunction(uint32_t guest_address) {
  return reinterpret_cast<PPCFunc*>(refii_bridge_rex_resolve_indirect_function(guest_address));
}

MMIOHandler* MMIOHandler::global_handler() {
  return static_cast<MMIOHandler*>(refii_bridge_rex_mmio_global_handler());
}

bool MMIOHandler::CheckLoad(uint32_t virtual_address, uint32_t* out_value) {
  return refii_bridge_rex_mmio_check_load(this, virtual_address, out_value) != 0;
}

bool MMIOHandler::CheckStore(uint32_t virtual_address, uint32_t value) {
  return refii_bridge_rex_mmio_check_store(this, virtual_address, value) != 0;
}

}  // namespace rex::runtime

namespace rex::perf {
void IncrementCounter(CounterId id, int64_t delta) {
  refii_bridge_rex_increment_counter(static_cast<uint32_t>(id), delta);
}
}  // namespace rex::perf

namespace rex::memory {

uint32_t Memory::SystemHeapAlloc(uint32_t size, uint32_t alignment, uint32_t flags) {
  return refii_bridge_rex_system_heap_alloc(this, size, alignment, flags);
}

void Memory::SystemHeapFree(uint32_t address, uint32_t* out_region_size) {
  refii_bridge_rex_system_heap_free(this, address, out_region_size);
}

const BaseHeap* Memory::LookupHeap(uint32_t address) const {
  return static_cast<const BaseHeap*>(refii_bridge_rex_lookup_heap(this, address));
}

bool BaseHeap::QueryRegionInfo(uint32_t base_address, HeapAllocationInfo* out_info) {
  return refii_bridge_rex_query_region_info(this, base_address, out_info) != 0;
}

}  // namespace rex::memory

namespace rex {
Runtime* Runtime::instance() {
  return static_cast<Runtime*>(refii_bridge_rex_runtime_instance());
}
}  // namespace rex

namespace rex::system {

KernelState* kernel_state() {
  return static_cast<KernelState*>(refii_bridge_rex_kernel_state());
}

XThread* XThread::GetCurrentThread() {
  return static_cast<XThread*>(refii_bridge_rex_xthread_current());
}

bool XThread::IsInThread() { return refii_bridge_rex_xthread_is_in_thread() != 0; }

void XThread::SetLastError(uint32_t error_code) {
  refii_bridge_rex_xthread_set_last_error(error_code);
}

void XThread::EnqueueApc(uint32_t normal_routine, uint32_t normal_context, uint32_t arg1,
                         uint32_t arg2) {
  refii_bridge_rex_xthread_enqueue_apc(this, normal_routine, normal_context, arg1, arg2);
}

}  // namespace rex::system

namespace rex::kernel::xboxkrnl {

void VdQueryVideoMode(system::X_VIDEO_MODE* video_mode) {
  refii_bridge_rex_vd_query_video_mode(video_mode);
}

uint32_t xeNtSetEvent(uint32_t handle, rex::be<uint32_t>* previous_state_ptr) {
  return refii_bridge_rex_nt_set_event(handle, previous_state_ptr);
}

}  // namespace rex::kernel::xboxkrnl

namespace rex {

// NON-OWNING. The host owns this logger; the payload's shared_ptr aliases it with a no-op
// deleter so no control block or object is ever freed across the boundary.
std::shared_ptr<spdlog::logger> GetLogger() {
  return std::shared_ptr<spdlog::logger>(
      static_cast<spdlog::logger*>(refii_bridge_rex_default_logger()),
      [](spdlog::logger*) {});
}

}  // namespace rex

namespace rex::thread {
std::recursive_mutex& global_critical_region::mutex() {
  return *static_cast<std::recursive_mutex*>(refii_bridge_rex_global_critical_region_mutex());
}
}  // namespace rex::thread

// NOT ON WINDOWS, where spdlog is IN this module and defining it here is a hard link error.
//
// Every other forwarder above replaces a symbol the payload would otherwise import from
// librexruntime, and the archive member that also defines it is never pulled, so there is
// nothing to collide with. spdlog is the exception on Windows: rexruntime is a DLL there and
// its import library does not re-export the spdlog it statically absorbed, so the payload
// links spdlogd.lib for itself. That library is built consolidated -- spdlog.cpp.obj carries
// logger::log_it_, logger::err_handler_, backtracer::enabled AND logger::flush in one member
// -- so the payload's real uses of the first three drag the member in, and lld-link then sees
// two definitions of flush. On Linux librexruntime.so exports its spdlog with default
// visibility, the payload's undefined references bind to it at load time, libspdlog.a is
// never consulted, and this definition stands alone.
//
// So on Windows flush() resolves to spdlog's own, running against the host-owned logger this
// points at. That is safe in the way the rest of this file is not: flush_() walks sinks_ and
// dispatches virtually into the host's sink objects, and both sides are our compiler and our
// copy of these headers -- refii_generated is not the player's build. Losing the C hop costs
// only the hop.
#if !defined(_WIN32)
namespace spdlog {
void logger::flush() { refii_bridge_rex_logger_flush(this); }
}  // namespace spdlog
#endif
