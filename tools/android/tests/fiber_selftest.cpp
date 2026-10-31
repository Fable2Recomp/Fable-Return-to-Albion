#include <rex/thread/fiber.h>
#include <rex/platform/fpscr.h>

#include <array>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <limits>
#include <thread>

using rex::thread::Fiber;

extern "C" uint64_t refii_fiber_register_test(void (*fn)(void*), void* arg, uint64_t seed);

namespace {
thread_local uint64_t thread_cookie;
uint64_t ReadFpcr() {
  uint64_t value;
  asm volatile("mrs %0, fpcr" : "=r"(value) : : "memory");
  return value;
}
uint64_t ReadFpsr() {
  uint64_t value;
  asm volatile("mrs %0, fpsr" : "=r"(value) : : "memory");
  return value;
}
void WriteFp(uint64_t control, uint64_t status) {
  rex::platform::FPSCRPlatform::setcsr(static_cast<uint32_t>(control));
  asm volatile("msr fpsr, %0" : : "r"(status) : "memory");
}
void SwitchThunk(void* fiber) {
  Fiber::SwitchTo(static_cast<Fiber*>(fiber));
}

struct Result {
  uint64_t register_failure = 0;
  uint64_t fp_failure = 0;
  uint64_t stack_failure = 0;
  uint64_t tls_failure = 0;
  uint64_t lifecycle_failure = 0;
  uint64_t iterations = 0;
  uint64_t creations = 0;
  uint64_t handled_exceptions = 0;
};

struct EntryArgs {
  Fiber* main;
  Fiber* self;
  Result* result;
  uint64_t cookie;
  uint64_t seed;
  uint64_t fpcr;
  uint64_t fpsr;
  uint32_t iterations;
  bool done = false;
};

[[gnu::noinline]] void DeepEntry(EntryArgs* args, unsigned depth) {
  // A nontrivial suspended C++ stack across multiple recursive frames.
  volatile uint64_t canaries[64];
  for (unsigned i = 0; i < 64; ++i) {
    canaries[i] = args->seed ^ (uint64_t(depth) << 48) ^ (uint64_t(i) * 0x102030405ull);
  }
  if (depth) {
    DeepEntry(args, depth - 1);
  } else {
    for (uint32_t i = 0; i < args->iterations; ++i) {
      WriteFp(args->fpcr, args->fpsr);
      args->result->register_failure |= refii_fiber_register_test(
          &SwitchThunk, args->main, args->seed + i * 4099);
      if (ReadFpcr() != args->fpcr || ReadFpsr() != args->fpsr) {
        ++args->result->fp_failure;
      }
      if (thread_cookie != args->cookie || Fiber::Current() != args->self) {
        ++args->result->tls_failure;
      }
      for (unsigned c = 0; c < 64; ++c) {
        if (canaries[c] != (args->seed ^ (uint64_t(c) * 0x102030405ull))) {
          ++args->result->stack_failure;
        }
      }
      ++args->result->iterations;
    }
    // Exceptions may unwind within one fiber; they must never cross a switch.
    try {
      throw 17;
    } catch (int value) {
      if (value == 17) {
        ++args->result->handled_exceptions;
      }
    }
  }
  for (unsigned i = 0; i < 64; ++i) {
    if (canaries[i] != (args->seed ^ (uint64_t(depth) << 48) ^ (uint64_t(i) * 0x102030405ull))) {
      ++args->result->stack_failure;
    }
  }
}

void StressEntry(void* ptr) {
  auto* args = static_cast<EntryArgs*>(ptr);
  DeepEntry(args, 8);
  args->done = true;
  // Like guest FiberEntryPoint, a finished fiber yields to its main thread.
  Fiber::SwitchTo(args->main);
  std::terminate();
}

void OneYield(void* ptr) {
  Fiber::SwitchTo(static_cast<Fiber*>(ptr));
  std::terminate();
}

struct LateActivationArgs {
  Fiber* main = nullptr;
  Fiber* self = nullptr;
  bool activated = false;
};

void LateActivationEntry(void* ptr) {
  auto* args = static_cast<LateActivationArgs*>(ptr);
  args->activated = Fiber::Current() == args->self;
  Fiber::SwitchTo(args->main);
  std::terminate();
}

void RunWorker(Result& result, uint64_t cookie, std::atomic<unsigned>& ready,
               std::atomic<bool>& start) {
  thread_cookie = cookie;
  const uint64_t saved_fpcr = ReadFpcr(), saved_fpsr = ReadFpsr();
  Fiber* main = Fiber::ConvertCurrentThread();
  if (!main || Fiber::Current() != main || Fiber::ConvertCurrentThread() != main) {
    ++result.lifecycle_failure;
    ready.fetch_add(1);
    return;
  }
  // Self-switch is a valid no-op. Invalid input fails Create without corrupting TLS.
  Fiber::SwitchTo(main);
  if (Fiber::Create(65536, nullptr, nullptr) != nullptr ||
      Fiber::Create(std::numeric_limits<size_t>::max(), &OneYield, main) != nullptr) {
    ++result.lifecycle_failure;
  }
  constexpr unsigned kFibers = 3, kIterations = 10000;
  std::array<EntryArgs, kFibers> args{};
  std::array<Fiber*, kFibers> fibers{};
  const uint64_t base_control = saved_fpcr & ~(uint64_t{3} << 22) & ~(uint64_t{1} << 24);
  const uint64_t main_control = base_control | (uint64_t{3} << 22);
  const uint64_t main_status = 0x10;
  for (unsigned i = 0; i < kFibers; ++i) {
    args[i] = {main, nullptr, &result, cookie, cookie + 0x10000 * (i + 1),
               base_control | (uint64_t(i) << 22) | ((uint64_t(i) & 1) << 24),
               uint64_t{1} << i, kIterations};
    fibers[i] = Fiber::Create(256 * 1024, &StressEntry, &args[i]);
    args[i].self = fibers[i];
    if (!fibers[i]) {
      ++result.lifecycle_failure;
    }
  }
  ready.fetch_add(1, std::memory_order_release);
  while (!start.load(std::memory_order_acquire)) {
    std::this_thread::yield();
  }
  for (unsigned iteration = 0; iteration <= kIterations; ++iteration) {
    for (unsigned i = 0; i < kFibers; ++i) {
      if (!fibers[i]) continue;
      WriteFp(main_control, main_status);
      result.register_failure |= refii_fiber_register_test(
          &SwitchThunk, fibers[i], cookie + iteration * 8191);
      if (ReadFpcr() != main_control || ReadFpsr() != main_status) {
        ++result.fp_failure;
      }
      if (Fiber::Current() != main || thread_cookie != cookie) {
        ++result.tls_failure;
      }
    }
  }
  for (unsigned i = 0; i < kFibers; ++i) {
    if (fibers[i]) {
      if (!args[i].done) ++result.lifecycle_failure;
      fibers[i]->Destroy();
      ++result.creations;
    }
  }
  // Repeatedly destroy suspended stacks and also stacks never entered.
  for (unsigned i = 0; i < 1000; ++i) {
    Fiber* fiber = Fiber::Create(32768, &OneYield, main);
    if (!fiber) { ++result.lifecycle_failure; break; }
    if (i & 1) Fiber::SwitchTo(fiber);
    fiber->Destroy();
    ++result.creations;
  }
  main->Destroy();
  if (Fiber::Current() != nullptr) ++result.lifecycle_failure;
  main = Fiber::ConvertCurrentThread();
  if (!main) ++result.lifecycle_failure;
  else main->Destroy();
  WriteFp(saved_fpcr, saved_fpsr);
}
}  // namespace

/// Runs and joins the actual Fiber API stress suite; returns thread-local UTF-8
/// diagnostics valid until this OS thread calls the test again.
extern "C" __attribute__((visibility("default"))) const char* refii_android_fiber_selftest() {
  static thread_local char report[4096];
  std::array<Result, 4> results{};
  std::array<std::thread, 4> workers;
  std::atomic<unsigned> ready{0};
  std::atomic<bool> start{false};
  const auto begin = std::chrono::steady_clock::now();
  for (unsigned i = 0; i < workers.size(); ++i) {
    workers[i] = std::thread([&, i] {
      RunWorker(results[i], 0x123400000000ull + i * 0x1000000, ready, start);
    });
  }
  while (ready.load(std::memory_order_acquire) != workers.size()) {
    std::this_thread::yield();
  }
  start.store(true, std::memory_order_release);
  for (auto& worker : workers) worker.join();
  Result total;
  for (const auto& result : results) {
    total.register_failure |= result.register_failure;
    total.fp_failure += result.fp_failure;
    total.stack_failure += result.stack_failure;
    total.tls_failure += result.tls_failure;
    total.lifecycle_failure += result.lifecycle_failure;
    total.iterations += result.iterations;
    total.creations += result.creations;
    total.handled_exceptions += result.handled_exceptions;
  }

  // Actual XThread destruction can happen after its owning OS thread exits.
  // Also verify that a created-but-never-activated fiber may first run on a
  // different thread; ownership is assigned by the first SwitchTo, not Create.
  LateActivationArgs late_args;
  late_args.self = Fiber::Create(32768, &LateActivationEntry, &late_args);
  if (!late_args.self) {
    ++total.lifecycle_failure;
  } else {
    ++total.creations;
    std::thread owner([&] {
      late_args.main = Fiber::ConvertCurrentThread();
      if (late_args.main) Fiber::SwitchTo(late_args.self);
    });
    owner.join();
    if (!late_args.main || !late_args.activated) ++total.lifecycle_failure;
    auto* cleanup_caller = Fiber::ConvertCurrentThread();
    late_args.self->Destroy();
    if (late_args.main) late_args.main->Destroy();
    if (!cleanup_caller || Fiber::Current() != cleanup_caller) ++total.lifecycle_failure;
    if (cleanup_caller) cleanup_caller->Destroy();
  }
  const auto duration = std::chrono::duration<double, std::milli>(
      std::chrono::steady_clock::now() - begin).count();
  const bool passed = !total.register_failure && !total.fp_failure && !total.stack_failure &&
                      !total.tls_failure && !total.lifecycle_failure &&
                      total.iterations == 120000 && total.creations == 4013 &&
                      total.handled_exceptions == 12;
  std::snprintf(report, sizeof(report),
      "FIBER_SELFTEST %s\nthreads=4 suspended_iterations=%llu switches=%llu "
      "stack_creations=%llu handled_exceptions=%llu elapsed_ms=%.3f\n"
      "first_activation_on_other_thread=%s exited_thread_fiber_cleanup=checked\n"
      "gpr_fp_register_failure_mask=0x%llx fpcr_fpsr_failures=%llu "
      "stack_failures=%llu tls_failures=%llu lifecycle_failures=%llu\n"
      "backend=Boost.Context-1.89.0-arm64-fcontext+rex-FPCR-FPSR guarded_RW_stacks\n",
      passed ? "PASS" : "FAIL", (unsigned long long)total.iterations,
      (unsigned long long)(total.iterations * 2 + 24 + 4000 + (late_args.activated ? 2 : 0)),
      (unsigned long long)total.creations, (unsigned long long)total.handled_exceptions,
      duration, late_args.activated ? "PASS" : "FAIL", (unsigned long long)total.register_failure,
      (unsigned long long)total.fp_failure, (unsigned long long)total.stack_failure,
      (unsigned long long)total.tls_failure, (unsigned long long)total.lifecycle_failure);
  return report;
}
