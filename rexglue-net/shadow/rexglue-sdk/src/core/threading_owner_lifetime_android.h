// Guest-mutant owner lifetime for threading_posix.cpp's PosixCondition<Mutant>.
//
// Android has no robust pthread mutexes, so a thread that exits holding a guest
// mutant would leave its waiters asleep for good. There each thread holds a
// lifetime token instead: a mutant keeps a weak reference to its owner's, reads
// an expired token as abandoned, and its waits poll that lifetime rather than
// wait for a notification that never comes. Elsewhere the tracker is empty --
// never expired, never polled -- and the mutant is exactly the plain one.
#pragma once

#include <rex/platform.h>

#if REX_PLATFORM_ANDROID
#include <memory>
#endif

namespace rex::thread {

#if REX_PLATFORM_ANDROID
// A weak token is immune to thread-id reuse and expires for ordinary C++
// threads as well as runtime workers. Runtime exit paths clear it before
// publishing their thread as signaled, so zero-time waits observe abandonment.
struct AndroidOwnerLifetime {};
inline thread_local std::shared_ptr<AndroidOwnerLifetime> android_owner_lifetime;

class MutantOwnerLifetime {
 public:
  // Waits on the mutant poll the owner's lifetime (PosixConditionBase::Wait).
  static constexpr bool kPolled = true;

  // The calling thread now owns the mutant.
  void Claim() {
    if (!android_owner_lifetime) {
      android_owner_lifetime = std::make_shared<AndroidOwnerLifetime>();
    }
    owner_ = android_owner_lifetime;
  }

  // Whether the owning thread has exited without releasing the mutant.
  bool expired() const { return owner_.expired(); }

  // Called on a thread's exit paths, before the thread is published as finished.
  static void ReleaseCurrentThread() { android_owner_lifetime.reset(); }

 private:
  std::weak_ptr<AndroidOwnerLifetime> owner_;
};
#else
class MutantOwnerLifetime {
 public:
  static constexpr bool kPolled = false;
  void Claim() {}
  bool expired() const { return false; }
  static void ReleaseCurrentThread() {}
};
#endif

}  // namespace rex::thread
