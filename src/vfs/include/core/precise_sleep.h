// refii — a sleep that lasts what was asked for, on every host.
//
// WHY THIS EXISTS. Every pacing constant in this tree (the guest yield back-off's 50-500 us
// ramp, the 1 ms completion poll, the 30 Hz present pacer) was measured against Linux, where
// std::this_thread::sleep_for is nanosleep and lands within a few percent. On Windows the same
// call is Sleep(): libc++ rounds the request UP to a whole millisecond
// (`__libcpp_thread_sleep_for`: `chrono::ceil<milliseconds>` then `Sleep`), MSVC's STL does the
// equivalent, and Sleep() itself waits for the next timer tick — 15.6 ms unless some module in
// the process has raised the resolution. A 50 us request therefore cost between 1 ms and
// 15.6 ms, 20x to 300x the figure the surrounding code was designed around.
//
// WHAT WINDOWS CAN HONOUR, and what this does about the rest:
//   - A high-resolution waitable timer (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, Windows 10 1803
//     and later) fires on a ~0.5 ms grid whatever the process timer period is. Requests of
//     kTimerFloor and longer wait on one, and wake up to one grid step late, never early.
//   - Nothing on Windows sleeps for less than that grid. A shorter request is spent YIELDING
//     until its deadline: bounded by the request itself (under half a millisecond), which keeps
//     a short handoff short without parking the thread for ten times what it asked.
//   - Before 1803 there is no such timer. The process timer period is raised to 1 ms and
//     Sleep() is used, which then lands within 1-2 ms.
//
// HEADER-ONLY AND C-API-ONLY ON PURPOSE. This compiles into the payload (the player's zig,
// MinGW driver, libc++) and into the host (MSVC ABI); a C++ symbol does not resolve across
// that boundary, and winmm is reached through GetProcAddress so neither link line changes.

#pragma once

#include <chrono>
#include <thread>

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

namespace refii {

#ifdef _WIN32

namespace precise_sleep_detail {

// Older SDK and MinGW headers predate the flag.
constexpr DWORD kHighResolutionTimerFlag = 0x00000002;  // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION

// The high-resolution timer's grid. A request under this cannot be slept, only yielded.
constexpr std::chrono::microseconds kTimerFloor{500};

// Raises the process timer period to 1 ms, once. Also what makes the runtime's own Sleep(1)
// and every timed wait with a millisecond timeout land within a tick of 1 ms rather than
// 15.6 ms. Never lowered again: the period is per-process and dies with it.
inline void RaiseTimerResolutionOnce() {
  static const bool raised = [] {
    HMODULE winmm = ::LoadLibraryW(L"winmm.dll");
    if (winmm == nullptr) {
      return false;
    }
    using TimeBeginPeriod = UINT(WINAPI*)(UINT);
    auto begin = reinterpret_cast<TimeBeginPeriod>(
        reinterpret_cast<void*>(::GetProcAddress(winmm, "timeBeginPeriod")));
    return begin != nullptr && begin(1) == 0;  // TIMERR_NOERROR
  }();
  (void)raised;
}

// One timer per thread, made on first use and closed with the thread.
struct ThreadTimer {
  HANDLE handle = nullptr;
  bool tried = false;

  ~ThreadTimer() {
    if (handle != nullptr) {
      ::CloseHandle(handle);
    }
  }

  HANDLE Get() {
    if (!tried) {
      tried = true;
      handle = ::CreateWaitableTimerExW(nullptr, nullptr, kHighResolutionTimerFlag,
                                        TIMER_ALL_ACCESS);
    }
    return handle;
  }
};

inline ThreadTimer& Timer() {
  thread_local ThreadTimer timer;
  return timer;
}

}  // namespace precise_sleep_detail

// Makes Sleep() and millisecond timed waits land within a tick of 1 ms process-wide. Safe to
// call from anywhere, any number of times.
inline void RaiseTimerResolution() { precise_sleep_detail::RaiseTimerResolutionOnce(); }

inline void PreciseSleepUntil(std::chrono::steady_clock::time_point deadline) {
  using namespace precise_sleep_detail;
  RaiseTimerResolutionOnce();

  for (;;) {
    const auto remaining = deadline - std::chrono::steady_clock::now();
    if (remaining <= std::chrono::steady_clock::duration::zero()) {
      return;
    }

    // Under the grid: nothing can sleep this briefly, so the rest is yielded. Bounded by
    // kTimerFloor, and the only part of a wait that costs CPU.
    if (remaining < kTimerFloor) {
      ::SwitchToThread();
      continue;
    }

    // The timer fires on the first grid point at or after the due time, so the wake is late
    // by up to kTimerFloor and never early. A caller pacing against absolute deadlines (the
    // present pacer) does not accumulate that; a caller backing off does not care.
    if (HANDLE timer = Timer().Get()) {
      LARGE_INTEGER due;
      due.QuadPart = -static_cast<LONGLONG>(
          std::chrono::duration_cast<std::chrono::nanoseconds>(remaining).count() / 100);
      if (::SetWaitableTimer(timer, &due, 0, nullptr, nullptr, FALSE) != 0 &&
          ::WaitForSingleObject(timer, INFINITE) == WAIT_OBJECT_0) {
        return;
      }
    }

    // No high-resolution timer on this Windows (before 10 1803), or it failed: Sleep() at the
    // raised 1 ms period, which lands within a tick or two of the request.
    const auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(remaining).count();
    ::Sleep(ms > 1 ? static_cast<DWORD>(ms) : 1);
    return;
  }
}

#else  // !_WIN32

inline void RaiseTimerResolution() {}

inline void PreciseSleepUntil(std::chrono::steady_clock::time_point deadline) {
  std::this_thread::sleep_until(deadline);
}

#endif

template <class Rep, class Period>
inline void PreciseSleepFor(std::chrono::duration<Rep, Period> duration) {
#ifdef _WIN32
  PreciseSleepUntil(std::chrono::steady_clock::now() +
                    std::chrono::duration_cast<std::chrono::steady_clock::duration>(duration));
#else
  std::this_thread::sleep_for(duration);
#endif
}

}  // namespace refii
