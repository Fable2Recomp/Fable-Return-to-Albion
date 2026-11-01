// refii — the guest step sampler: a title's tick pump states when a step began and on which
// guest context; a host-side thread walks the guest back chains once the step has run past the
// stall threshold, so a blocked step names its site and what every other guest thread was doing.
//
// SHARED STATE, IN refii_runtime, for the same reason as guest_tick_queue.cpp: the payload states
// (its C entries in core/payload_bridge.h) and the sampler is a host thread. The blocked thread
// cannot sample itself, which is why the walk is not in the hook. The time base reads the open
// step too (core/guest_step_sampler.h): a step open longer than one step is a game thread blocked
// on something else, and the clock it would otherwise hold frozen must keep flowing for that
// something else.
//
// THE WALK is RefiiHostGuestBacktrace's (src/hostcore/src/debug_channel.cpp): the live context's
// lr first, then `back = load32(r1); lr = load32(back - 8); r1 = back` — the title's own back
// chain (32-bit `stwu r1`, LR saved at back chain minus 8).
//
// EVERY GUEST READ HERE IS GUARDED. This is a host thread: a fault in guest memory on it is not a
// guest access violation the runtime's handler can answer, it is fatal (build 5: r1 of a thread
// read mid-update, or a back pointer at a stack's guard page, SIGSEGV at guest 0x70030000). So
// no word is read through the arena pointer: each goes through process_vm_readv (ReadProcessMemory
// on Windows), which returns an error on an unmapped or PROT_NONE page and raises nothing; and
// r1 and every back pointer must lie inside the thread's own stack as its PCR states it
// (stack_end_ptr at +0x74 low, stack_base_ptr at +0x70 high), ascending. The first failure ends
// the walk, and a walk that cannot start is "(not sampled)".

#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <thread>

#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <sys/uio.h>
#include <unistd.h>
#endif

#include <rex/chrono/clock.h>
#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xthread.h>

#include "core/guest_step_sampler.h"
#include "core/payload_bridge.h"

namespace {

using Clock = std::chrono::steady_clock;

constexpr uint32_t kMaxFrames = 12u;
constexpr uint32_t kOtherThreadFrames = 6u;
constexpr auto kStallThreshold = std::chrono::milliseconds(250);
constexpr auto kPollPeriod = std::chrono::milliseconds(25);
/// A step still blocked this long is logged from here as well: the pump's own line only comes
/// when the step ends.
constexpr auto kHungReport = std::chrono::seconds(5);

/// The PCR's stack words (rex/system/xthread.h X_KPCR): +0x70 base (high), +0x74 end (low).
constexpr uint32_t kPcrStackBase = 0x70u;
constexpr uint32_t kPcrStackEnd = 0x74u;

std::mutex g_mutex;
const PPCContext* g_context = nullptr;
const uint8_t* g_base = nullptr;
uint64_t g_step = 0;
Clock::time_point g_started{};
bool g_open = false;

/// The open step for the time base's lock-free read: its start in guest ticks, 0 when closed.
std::atomic<uint64_t> g_step_start_real{0};

/// The sample of the step in progress, or of the last one taken.
uint64_t g_sampled_step = ~0ull;
std::array<uint32_t, kMaxFrames> g_frames{};
uint32_t g_frame_count = 0;
bool g_hung_reported = false;

std::atomic<bool> g_thread_started{false};

/// Copies `size` bytes from `src` without faulting: false when any byte is unreadable.
bool GuardedRead(const void* src, void* dst, size_t size) {
#ifdef _WIN32
  SIZE_T copied = 0;
  return ::ReadProcessMemory(::GetCurrentProcess(), src, dst, size, &copied) != 0 &&
         copied == size;
#else
  struct iovec local{dst, size};
  struct iovec remote{const_cast<void*>(src), size};
  return ::process_vm_readv(::getpid(), &local, 1, &remote, 1, 0) == static_cast<ssize_t>(size);
#endif
}

bool ReadGuestU32(const uint8_t* base, uint32_t address, uint32_t* out) {
  if (address < 0x10000u || address >= 0xC0000000u - 4u) {
    return false;
  }
  uint32_t value = 0;
  if (!GuardedRead(base + address, &value, sizeof(value))) {
    return false;
  }
  *out = __builtin_bswap32(value);
  return true;
}

struct StackRange {
  uint32_t low = 0;   // stack_end_ptr
  uint32_t high = 0;  // stack_base_ptr
  bool Holds(uint32_t sp) const { return sp >= low && sp + 8u <= high; }
};

/// The thread's stack as its PCR states it; false when the PCR is unreadable or absurd.
bool ReadStackRange(const uint8_t* base, const ::rex::system::XThread& thread, StackRange* out) {
  const uint32_t pcr = thread.pcr_ptr();
  uint32_t low = 0, high = 0;
  if (pcr == 0u || !ReadGuestU32(base, pcr + kPcrStackEnd, &low) ||
      !ReadGuestU32(base, pcr + kPcrStackBase, &high)) {
    return false;
  }
  if (low < 0x10000u || high <= low || high >= 0xC0000000u || high - low > 0x1000000u) {
    return false;
  }
  out->low = low;
  out->high = high;
  return true;
}

/// The frames of one context, innermost first; 0 when its r1 is not inside its stack (a torn
/// read, a thread between stacks) — never a fault.
uint32_t Walk(const PPCContext* context, const uint8_t* base, const StackRange& stack,
              uint32_t* out, uint32_t capacity) {
  uint32_t sp = 0, lr = 0;
  {
    // Host memory, but the thread may be writing it: read whole words once.
    const uint32_t r1 = context->r1.u32;
    const uint64_t link = context->lr;
    sp = r1;
    lr = static_cast<uint32_t>(link);
  }
  if (!stack.Holds(sp) || lr == 0u) {
    return 0;
  }
  uint32_t written = 0;
  out[written++] = lr;
  while (written < capacity) {
    uint32_t back = 0;
    if (!ReadGuestU32(base, sp, &back) ||
        !refii::pace::AcceptBackChain(sp, back, stack.low, stack.high)) {
      break;
    }
    uint32_t saved = 0;
    if (!ReadGuestU32(base, back - 8u, &saved) || saved == 0u) {
      break;
    }
    out[written++] = saved;
    sp = back;
  }
  return written;
}

void FormatFrames(const uint32_t* frames, uint32_t count, char* out, size_t capacity) {
  size_t used = 0;
  for (uint32_t i = 0; i < count && used + 14 < capacity; ++i) {
    used += static_cast<size_t>(
        std::snprintf(out + used, capacity - used, "%s0x%08X", i == 0 ? "" : " < ", frames[i]));
  }
  out[used < capacity ? used : capacity - 1] = '\0';
}

/// Walks the blocked thread (the one whose live context the pump stated) and logs every other
/// guest thread's top frames: the one it is waiting on is among them. One line per thread, from
/// the kernel's thread table (the hang report's walk, src/hostcore/src/crash_handlers.cpp). The
/// stated context is matched to its XThread so its stack range comes from its own PCR; a context
/// no live thread owns is not read at all.
uint32_t SampleThreads(uint64_t step, const PPCContext* blocked, const uint8_t* base,
                       uint32_t* blocked_frames, uint32_t capacity) {
  uint32_t blocked_count = 0;
  ::rex::system::KernelState* state = ::rex::system::kernel_state();
  if (state == nullptr) {
    return 0;
  }
  auto threads = state->object_table()->GetObjectsByType<::rex::system::XThread>();
  for (const auto& thread : threads) {
    PPCContext* context =
        thread->thread_state() != nullptr ? thread->thread_state()->context() : nullptr;
    if (context == nullptr) {
      continue;
    }
    StackRange stack;
    if (!ReadStackRange(base, *thread, &stack)) {
      continue;
    }
    if (context == blocked) {
      blocked_count = Walk(context, base, stack, blocked_frames, capacity);
      continue;
    }
    uint32_t frames[kOtherThreadFrames];
    const uint32_t count = Walk(context, base, stack, frames, kOtherThreadFrames);
    char text[kOtherThreadFrames * 14 + 1];
    FormatFrames(frames, count, text, sizeof(text));
    REXLOG_INFO("Refii pace: HF step {} blocked; thread {} '{}' at {}", step,
                thread->thread_id(),
                thread->thread() != nullptr ? thread->thread()->name().c_str() : "",
                count != 0u ? text : "(not sampled)");
  }
  return blocked_count;
}

void SamplerThread() {
  for (;;) {
    std::this_thread::sleep_for(kPollPeriod);

    std::lock_guard lock(g_mutex);
    if (!g_open || g_context == nullptr || g_base == nullptr) {
      continue;
    }
    const auto elapsed = Clock::now() - g_started;
    if (elapsed < kStallThreshold) {
      continue;
    }

    if (g_sampled_step != g_step) {
      g_frame_count = SampleThreads(g_step, g_context, g_base, g_frames.data(), kMaxFrames);
      g_sampled_step = g_step;
      g_hung_reported = false;
    }

    if (!g_hung_reported && elapsed >= kHungReport) {
      char text[kMaxFrames * 14 + 1];
      FormatFrames(g_frames.data(), g_frame_count, text, sizeof(text));
      REXLOG_WARN("Refii pace: HF step {} still blocked after {} ms at {}", g_step,
                  std::chrono::duration_cast<std::chrono::milliseconds>(elapsed).count(),
                  g_frame_count != 0u ? text : "(not sampled)");
      g_hung_reported = true;
    }
  }
}

void EnsureThread() {
  if (g_thread_started.exchange(true)) {
    return;
  }
  std::thread(SamplerThread).detach();
}

}  // namespace

namespace refii::pace {

bool AcceptBackChain(uint32_t sp, uint32_t back, uint32_t stack_low, uint32_t stack_high) {
  // Ascending, 8-aligned (the 360 ABI's stack alignment), inside the stack with room for the
  // saved LR at back - 8 and the back-chain word at back.
  return back > sp && (back & 7u) == 0u && back >= stack_low + 8u && back + 4u <= stack_high;
}

bool StepBlocked(uint64_t real_now, uint64_t step_ticks) {
  const uint64_t start = g_step_start_real.load(std::memory_order_relaxed);
  return start != 0u && real_now > start && real_now - start > step_ticks;
}

}  // namespace refii::pace

extern "C" void refii_bridge_guest_step_begin(const void* ppc_context, const void* guest_base,
                                              uint64_t step) {
  EnsureThread();
  const uint64_t real = ::rex::chrono::Clock::QueryGuestTickCount();
  std::lock_guard lock(g_mutex);
  g_context = static_cast<const PPCContext*>(ppc_context);
  g_base = static_cast<const uint8_t*>(guest_base);
  g_step = step;
  g_started = Clock::now();
  g_open = true;
  g_step_start_real.store(real == 0u ? 1u : real, std::memory_order_relaxed);
}

extern "C" uint32_t refii_bridge_guest_step_end(uint64_t step, uint32_t* out_frames,
                                                uint32_t capacity) {
  g_step_start_real.store(0u, std::memory_order_relaxed);
  std::lock_guard lock(g_mutex);
  g_open = false;
  if (g_sampled_step != step || out_frames == nullptr) {
    return 0;
  }
  const uint32_t count = g_frame_count < capacity ? g_frame_count : capacity;
  std::memcpy(out_frames, g_frames.data(), count * sizeof(uint32_t));
  return count;
}
