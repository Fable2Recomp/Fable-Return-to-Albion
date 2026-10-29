#pragma once

// A std::mutex the in-game Tracy capture can see: every wait, acquisition and release, per lock,
// by name. Use it where std::mutex was (lock_guard, unique_lock, scoped_lock; with a condition
// variable, std::condition_variable_any): `refii::TracedMutex g_lock{"guest clock grant"};`.
// For locks that live as long as the process — Tracy keeps the lock's name and location for the
// whole capture.
//
// REGISTERED ON FIRST USE, NOT AT CONSTRUCTION. Tracy's own Lockable announces itself when it is
// built, and these locks are globals built while the library loads — before the game starts the
// profiler, which under TRACY_MANUAL_LIFETIME is a null dereference. So the lock reaches Tracy the
// first time it is taken after the profiler is up.
//
// Tracy counts every acquisition against every release (its on-demand bookkeeping), so a release
// reports to Tracy exactly when its acquisition did; which one that was lives in a member only the
// owner touches. Without TRACY_ENABLE (the payload, or a build without the profiler) this is a
// plain std::mutex.

#include <cstdint>
#include <mutex>

#if defined(TRACY_ENABLE)

#include <atomic>

#include <tracy/TracyC.h>

namespace refii {

class TracedMutex {
public:
    explicit TracedMutex(const char* name) : where_{name, name, "", 0, 0} {}

    TracedMutex(const TracedMutex&) = delete;
    TracedMutex& operator=(const TracedMutex&) = delete;

    void lock() {
        TracyCLockCtx context = Context();
        const bool recorded = context != nullptr && ___tracy_before_lock_lockable_ctx(context) != 0;
        mutex_.lock();
        if (recorded) ___tracy_after_lock_lockable_ctx(context);
        held_ = context;
    }

    bool try_lock() {
        TracyCLockCtx context = Context();
        const bool acquired = mutex_.try_lock();
        if (context != nullptr) ___tracy_after_try_lock_lockable_ctx(context, acquired ? 1 : 0);
        if (acquired) held_ = context;
        return acquired;
    }

    void unlock() {
        TracyCLockCtx context = held_;
        held_ = nullptr;
        mutex_.unlock();
        if (context != nullptr) ___tracy_after_unlock_lockable_ctx(context);
    }

private:
    TracyCLockCtx Context() {
        TracyCLockCtx context = context_.load(std::memory_order_acquire);
        if (context != nullptr || !TracyCIsStarted) return context;
        TracyCLockCtx announced = ___tracy_announce_lockable_ctx(&where_);
        TracyCLockCtx expected = nullptr;
        if (!context_.compare_exchange_strong(expected, announced, std::memory_order_acq_rel)) {
            ___tracy_terminate_lockable_ctx(announced);
            return expected;
        }
        return announced;
    }

    std::mutex mutex_;
    const ___tracy_source_location_data where_;
    std::atomic<TracyCLockCtx> context_{nullptr};
    TracyCLockCtx held_ = nullptr;  // the owner's acquisition, as Tracy saw it
};

}  // namespace refii

#else

namespace refii {

class TracedMutex : public std::mutex {
public:
    explicit TracedMutex(const char*) {}
};

}  // namespace refii

#endif
