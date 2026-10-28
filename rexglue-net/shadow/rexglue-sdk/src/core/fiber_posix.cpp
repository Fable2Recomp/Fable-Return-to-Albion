/**
 * @file        rex/core/fiber_posix.cpp
 * @brief       POSIX backend for rex::thread::Fiber (makecontext/swapcontext)
 *
 * @copyright   Copyright (c) 2026 Tom Clay <tomc@tctechstuff.com>
 *              All rights reserved.
 *
 * @license     BSD 3-Clause License
 *              See LICENSE file in the project root for full license text.
 */

#if defined(__APPLE__) && !defined(_XOPEN_SOURCE)
// Darwin hides the deprecated ucontext API unless a POSIX/XSI feature level
// is requested before <ucontext.h> is included. fiber.h includes ucontext.h,
// so the feature macro must be set in this translation unit first.
#define _XOPEN_SOURCE 700
#endif

#include <rex/platform.h>
#if REX_PLATFORM_LINUX || REX_PLATFORM_MAC

#include <rex/thread/fiber.h>

#include <cassert>

#if REX_PLATFORM_ANDROID

#if !REX_ARCH_ARM64
#error The Android fiber backend currently supports ARM64 only.
#endif

#include <algorithm>
#include <cerrno>
#include <cstdlib>
#include <limits>
#include <new>
#include <sys/mman.h>
#include <unistd.h>

extern "C" void* refii_make_fcontext(
    void* stack_top, size_t stack_size,
    void (*entry)(rex::thread::detail::FiberTransfer));
extern "C" rex::thread::detail::FiberTransfer refii_jump_fcontext(
    void* target_context, void* data);

namespace rex::thread {

thread_local Fiber* Fiber::tls_current_ = nullptr;

namespace {
inline uint64_t ReadFpcr() {
  uint64_t value;
  asm volatile("mrs %0, fpcr" : "=r"(value) : : "memory");
  return value;
}
inline uint64_t ReadFpsr() {
  uint64_t value;
  asm volatile("mrs %0, fpsr" : "=r"(value) : : "memory");
  return value;
}
inline void RestoreFloatingPoint(uint64_t fpcr, uint64_t fpsr) {
  asm volatile("msr fpcr, %0\n\tmsr fpsr, %1" : : "r"(fpcr), "r"(fpsr) : "memory");
}
}  // namespace

Fiber* Fiber::ConvertCurrentThread() {
  // Repeated conversion must not leak or replace a live thread context.
  if (tls_current_) {
    return tls_current_;
  }
  auto* f = new (std::nothrow) Fiber();
  if (!f) {
    return nullptr;
  }
  f->is_thread_fiber_ = true;
  f->owner_tid_ = gettid();
  f->fpcr_ = ReadFpcr();
  f->fpsr_ = ReadFpsr();
  tls_current_ = f;
  // Its context is captured by the first jump away from the calling thread.
  return f;
}

Fiber* Fiber::Create(size_t stack_size, void (*entry)(void*), void* arg) {
  if (!entry) {
    errno = EINVAL;
    return nullptr;
  }
  const long page = sysconf(_SC_PAGESIZE);
  if (page <= 0) {
    return nullptr;
  }
  const size_t page_size = static_cast<size_t>(page);
  stack_size = std::max(stack_size, size_t{16 * 1024});
  if (stack_size > std::numeric_limits<size_t>::max() - 3 * page_size) {
    errno = ENOMEM;
    return nullptr;
  }
  const size_t usable = (stack_size + page_size - 1) / page_size * page_size;
  const size_t mapping_size = usable + 2 * page_size;
  void* mapping = mmap(nullptr, mapping_size, PROT_NONE,
                       MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
  if (mapping == MAP_FAILED) {
    return nullptr;
  }
  auto* bottom = static_cast<uint8_t*>(mapping) + page_size;
  // Guard both ends; the host stack is writable and never executable.
  if (mprotect(bottom, usable, PROT_READ | PROT_WRITE) != 0) {
    munmap(mapping, mapping_size);
    return nullptr;
  }
  auto* f = new (std::nothrow) Fiber();
  if (!f) {
    munmap(mapping, mapping_size);
    return nullptr;
  }
  f->stack_mapping_ = mapping;
  f->stack_mapping_size_ = mapping_size;
  f->entry_ = entry;
  f->arg_ = arg;
  f->fpcr_ = ReadFpcr();
  f->fpsr_ = ReadFpsr();
  f->context_ = refii_make_fcontext(bottom + usable, usable, &Fiber::Trampoline);
  // First activation assigns ownership, so creation need not happen on the
  // eventual executing thread. An activated fiber cannot migrate OS threads:
  // guest ThreadState and Fiber::Current() depend on that thread's TLS.
  return f;
}

[[noreturn]] void Fiber::Trampoline(detail::FiberTransfer transfer) {
  auto* from = static_cast<Fiber*>(transfer.data);
  from->context_ = transfer.context;
  Fiber* f = tls_current_;
  f->entry_(f->arg_);
  // The API provides no successor for a returned entry. Guest FiberEntryPoint
  // explicitly switches to its main fiber before terminating if resumed.
  std::abort();
}

void Fiber::SwitchTo(Fiber* target) {
  Fiber* from = tls_current_;
  const int tid = gettid();
  if (!from || !target || from->owner_tid_ != tid ||
      (target->owner_tid_ != 0 && target->owner_tid_ != tid)) {
    std::abort();
  }
  if (from == target) {
    return;
  }
  target->owner_tid_ = tid;
  from->fpcr_ = ReadFpcr();
  from->fpsr_ = ReadFpsr();
  tls_current_ = target;
  RestoreFloatingPoint(target->fpcr_, target->fpsr_);
  auto transfer = refii_jump_fcontext(target->context_, from);
  // The fiber that resumed us hands back its newly saved stack pointer.
  static_cast<Fiber*>(transfer.data)->context_ = transfer.context;
  assert(tls_current_ == from);
}

void Fiber::Destroy() {
  if (!is_thread_fiber_ && this == tls_current_) {
    std::abort();  // Never unmap the stack currently executing Destroy().
  }
  // XThread destruction may run after its owning OS thread has exited, from
  // another thread. Only clear this thread's TLS if it actually references us.
  if (this == tls_current_) {
    tls_current_ = nullptr;
  }
  if (stack_mapping_) {
    munmap(stack_mapping_, stack_mapping_size_);
  }
  delete this;
}

}  // namespace rex::thread

#else  // Desktop POSIX ucontext backend.

namespace rex::thread {

thread_local Fiber* Fiber::tls_current_ = nullptr;

Fiber* Fiber::ConvertCurrentThread() {
  auto* f = new Fiber();
  if (getcontext(&f->context_) == -1) {
    delete f;
    return nullptr;
  }
  f->is_thread_fiber_ = true;
  tls_current_ = f;
  return f;
}

Fiber* Fiber::Create(size_t stack_size, void (*entry)(void*), void* arg) {
  auto* f = new Fiber();
  f->entry_ = entry;
  f->arg_ = arg;
  f->stack_.resize(stack_size);

  if (getcontext(&f->context_) == -1) {
    delete f;
    return nullptr;
  }
  f->context_.uc_stack.ss_sp = f->stack_.data();
  f->context_.uc_stack.ss_size = f->stack_.size();
  f->context_.uc_link = nullptr;
  // Trampoline reads entry_/arg_ from tls_current_ — no pointer splitting needed.
  makecontext(&f->context_, &Fiber::Trampoline, 0);
  return f;
}

/*static*/ void Fiber::Trampoline() {
  // tls_current_ was updated by SwitchTo before swapcontext returned here.
  Fiber* f = tls_current_;
  f->entry_(f->arg_);
}

void Fiber::SwitchTo(Fiber* target) {
  Fiber* from = tls_current_;
  tls_current_ = target;
  swapcontext(&from->context_, &target->context_);
}

void Fiber::Destroy() {
  // Thread fibers are destroyed from the owning thread itself.
  if (is_thread_fiber_) {
    tls_current_ = nullptr;
  } else {
    assert(this != tls_current_ && "Destroy called on the currently running fiber");
  }
  // No POSIX equivalent of ConvertFiberToThread; stack_ is freed by the vector destructor.
  delete this;
}

}  // namespace rex::thread

#endif  // REX_PLATFORM_ANDROID

#endif  // REX_PLATFORM_LINUX || REX_PLATFORM_MAC
