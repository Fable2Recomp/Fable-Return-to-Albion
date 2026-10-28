/** @file platform_selftest.cpp
 * In-APK semantic tests of the actual Android runtime platform sources.
 * No game data or native guest is required. Exported text names every check.
 */
#include <rex/main_android.h>
#include <rex/thread.h>
#include <rex/thread/timer_queue.h>
#include <rex/chrono/chrono_steady_cast.h>
#include <rex/string/numeric.h>

#include <atomic>
#include <bit>
#include <cerrno>
#include <cmath>
#include <mutex>
#include <sstream>
#include <string>
#include <system_error>
#include <thread>
#include <vector>

namespace {
using namespace std::chrono_literals;
using rex::thread::Event;
using rex::thread::Mutant;
using rex::thread::Semaphore;
using rex::thread::Thread;
using rex::thread::Timer;
using rex::thread::Wait;
using rex::thread::WaitHandle;
using rex::thread::WaitResult;

/** Collect test outcomes without aborting the APK at the first failed check. */
struct Checks {
  int passed = 0;
  int failed = 0;
  std::ostringstream details;
  void Check(bool success, const char* name) {
    success ? ++passed : ++failed;
    details << (success ? "PASS " : "FAIL ") << name << '\n';
  }
};

/** Validate parsing values, consumed ranges and failures independently of cvars. */
void TestNumeric(Checks& checks) {
  const int initial_errno = errno;
  errno = EDOM;
  const std::string first_input = "1.5";
  double first_value = 0.0;
  auto first_result = rex::string::detail::portable_float_from_chars(
      first_input.data(), first_input.data() + first_input.size(), first_value);
  checks.Check(first_result.ec == std::errc{} && first_value == 1.5 && errno == EDOM,
               "parser locale initialization preserves caller errno");
  errno = initial_errno;
  checks.Check(rex::string::from_string<float>("-12.5") == -12.5f, "float decimal sign");
  checks.Check(rex::string::from_string<double>("1.25e2") == 125.0, "double exponent");
  checks.Check(rex::string::from_string<float>("0x3F800000") == 1.0f, "float raw IEEE hex");
  auto vector = rex::string::from_string<rex::vec128_t>("(1.25,-2.5,3e1,0)");
  checks.Check(vector.f32[0] == 1.25f && vector.f32[1] == -2.5f &&
                   vector.f32[2] == 30.0f && vector.f32[3] == 0.0f,
               "vector decimal parsing");
  for (const std::string input : {"", "+1", " 1", "\t1", "invalid"}) {
    double value = 777.0;
    auto parsed = rex::string::detail::portable_float_from_chars(
        input.data(), input.data() + input.size(), value);
    checks.Check(parsed.ec == std::errc::invalid_argument && parsed.ptr == input.data() &&
                     value == 777.0,
                 "invalid parser input preserves value and pointer");
  }
  for (const std::string input : {"1e1000", "1e-1000"}) {
    double value = 777.0;
    auto parsed = rex::string::detail::portable_float_from_chars(
        input.data(), input.data() + input.size(), value);
    checks.Check(parsed.ec == std::errc::result_out_of_range && value == 777.0,
                 "parser range failure preserves value");
  }
  std::string suffix = "4.25suffix";
  double parsed_value = 0.0;
  auto parsed = rex::string::detail::portable_float_from_chars(
      suffix.data(), suffix.data() + suffix.size(), parsed_value);
  checks.Check(parsed.ec == std::errc{} && parsed.ptr == suffix.data() + 4 &&
                   parsed_value == 4.25,
               "parser consumes numeric prefix");
  std::string hex = "0x1p2";
  parsed = rex::string::detail::portable_float_from_chars(
      hex.data(), hex.data() + hex.size(), parsed_value);
  checks.Check(parsed.ec == std::errc{} && parsed.ptr == hex.data() + 1 && parsed_value == 0.0,
               "general parser does not consume C hex float");
  std::string long_number = "1." + std::string(200, '0') + "5";
  parsed = rex::string::detail::portable_float_from_chars(
      long_number.data(), long_number.data() + long_number.size(), parsed_value);
  checks.Check(parsed.ec == std::errc{} && parsed.ptr == long_number.data() + long_number.size() &&
                   parsed_value == 1.0,
               "long mantissa has no artificial range error");
  std::string subnormal = "1.401298464324817e-45";
  float small = 0.0f;
  auto small_result = rex::string::detail::portable_float_from_chars(
      subnormal.data(), subnormal.data() + subnormal.size(), small);
  checks.Check(small_result.ec == std::errc{} && std::bit_cast<uint32_t>(small) == 1,
               "representable float subnormal");
  const int previous_errno = errno;
  errno = EDOM;
  (void)rex::string::detail::portable_float_from_chars(
      suffix.data(), suffix.data() + suffix.size(), parsed_value);
  checks.Check(errno == EDOM, "parser preserves caller errno");
  errno = previous_errno;
}

/** Verify fixed epochs and the project conversion used by absolute timers. */
void TestChrono(Checks& checks) {
  using W = rex::chrono::WinSystemClock;
  using S = std::chrono::steady_clock;
  constexpr uint64_t epoch = 116444736000000000ULL;
  auto nt_epoch = W::from_sys(std::chrono::system_clock::time_point{});
  checks.Check(W::to_file_time(nt_epoch) == epoch, "Unix to NT epoch offset");
  checks.Check(W::to_sys(W::from_file_time(epoch)).time_since_epoch().count() == 0,
               "NT to Unix epoch offset");
  auto nt_due = W::now() + 250ms;
  auto steady_due = rex::chrono::ClockCast<S>(nt_due);
  auto remaining = steady_due - S::now();
  checks.Check(remaining > 200ms && remaining < 300ms, "absolute NT deadline becomes steady deadline");
  auto nt_roundtrip = rex::chrono::ClockCast<W>(steady_due);
  auto error = nt_roundtrip - nt_due;
  checks.Check(error > -10ms && error < 10ms, "steady and NT roundtrip bounded drift");
  auto past = rex::chrono::ClockCast<S>(W::now() - 100ms);
  checks.Check(past < S::now(), "past absolute deadline stays in the past");
  using X = rex::chrono::XSystemClock;
  const double original_scalar = rex::chrono::Clock::guest_time_scalar();
  for (double scalar : {0.5, 2.0}) {
    rex::chrono::Clock::set_guest_time_scalar(scalar);
    auto guest_due = X::now() + 100ms;
    auto host_due = rex::chrono::ClockCast<W>(guest_due);
    auto host_remaining = host_due - W::now();
    auto expected = std::chrono::duration_cast<W::duration>(100ms *
        (REXCVAR_GET(clock_no_scaling) ? 1.0 : scalar));
    checks.Check(host_remaining > expected - 10ms && host_remaining < expected + 10ms,
                 "guest absolute timer preserves existing duration scaling");
    auto guest_roundtrip = rex::chrono::ClockCast<X>(host_due);
    auto guest_error = guest_roundtrip - guest_due;
    checks.Check(guest_error > -20ms && guest_error < 20ms,
                 "guest and host epoch conversion roundtrip at nonunit scale");
  }
  rex::chrono::Clock::set_guest_time_scalar(original_scalar);
}

/** Validate ownership transfer on ordinary thread exit without a robust pthread API. */
void TestMutants(Checks& checks) {
  auto mutant = Mutant::Create(true);
  checks.Check(Wait(mutant.get(), false, 0ms) == WaitResult::kSuccess, "recursive mutant acquisition");
  checks.Check(mutant->Release(), "release one recursive level");
  std::atomic<bool> foreign_release{true};
  std::atomic<bool> foreign_timed_out{false};
  std::thread foreign([&] {
    foreign_release = mutant->Release();
    foreign_timed_out = Wait(mutant.get(), false, 10ms) == WaitResult::kTimeout;
  });
  foreign.join();
  checks.Check(!foreign_release && foreign_timed_out, "nonowner cannot release or acquire owned mutant");
  checks.Check(mutant->Release(), "final recursive release");
  checks.Check(Wait(mutant.get(), false, 0ms) == WaitResult::kSuccess && mutant->Release(),
               "released mutant transfers normally");

  auto abandoned = Mutant::Create(false);
  std::thread owner([&] { (void)Wait(abandoned.get(), false, 100ms); });
  owner.join();
  checks.Check(Wait(abandoned.get(), false, 0ms) == WaitResult::kAbandoned,
               "owner normal exit yields abandoned acquisition");
  checks.Check(abandoned->Release(), "abandoned acquisition grants ownership");
  checks.Check(Wait(abandoned.get(), false, 0ms) == WaitResult::kSuccess && abandoned->Release(),
               "abandonment consumed once");

  auto first = Event::CreateManualResetEvent(false);
  auto abandoned_multiple = Mutant::Create(false);
  std::thread multi_owner([&] { (void)Wait(abandoned_multiple.get(), false, 100ms); });
  multi_owner.join();
  WaitHandle* handles[]{first.get(), abandoned_multiple.get()};
  auto any = rex::thread::WaitMultiple(handles, 2, false, false, 0ms);
  checks.Check(any.first == WaitResult::kAbandoned && any.second == 1,
               "WaitAny preserves abandoned handle index");
  checks.Check(abandoned_multiple->Release(), "WaitAny abandonment grants ownership");
  std::thread all_owner([&] { (void)Wait(abandoned_multiple.get(), false, 100ms); });
  all_owner.join();
  first->Set();
  auto all = rex::thread::WaitMultiple(handles, 2, true, false, 100ms);
  checks.Check(all.first == WaitResult::kAbandoned && all.second == 1,
               "WaitAll acquires all and reports abandoned index");
  checks.Check(abandoned_multiple->Release(), "WaitAll abandonment grants ownership");

  auto acquired = Event::CreateManualResetEvent(false);
  auto observed_exit = Mutant::Create(false);
  std::thread delayed_owner([&] {
    (void)Wait(observed_exit.get(), false, 100ms);
    acquired->Set();
    std::this_thread::sleep_for(20ms);
  });
  (void)Wait(acquired.get(), false, 100ms);
  checks.Check(Wait(observed_exit.get(), false, 250ms) == WaitResult::kAbandoned,
               "blocked wait observes owner exit without signal");
  delayed_owner.join();
  checks.Check(observed_exit->Release(), "blocked abandoned acquisition ownership");

  int protected_counter = 0;
  std::atomic<int> stress_errors{0};
  std::vector<std::thread> workers;
  for (int i = 0; i < 4; ++i) {
    workers.emplace_back([&] {
      for (int j = 0; j < 200; ++j) {
        if (Wait(mutant.get(), false, 1000ms) != WaitResult::kSuccess) {
          ++stress_errors;
          return;
        }
        ++protected_counter;
        if (!mutant->Release()) ++stress_errors;
      }
    });
  }
  for (auto& worker : workers) worker.join();
  checks.Check(stress_errors == 0 && protected_counter == 800, "four-thread mutant contention 800 acquisitions");
}

/** Join an actual SDK host thread after its wait object becomes signaled. */
bool JoinRuntimeThread(Thread* thread) {
  // The SDK condition's post_execution() performs pthread_join while acquiring
  // the signaled wait object. A second direct join would be invalid.
  return thread && Wait(thread, false, 1000ms) == WaitResult::kSuccess;
}

/** Exercise SDK threads, unsupported remote termination and self exit. */
void TestThreads(Checks& checks) {
  auto entered = Event::CreateManualResetEvent(false);
  auto finish = Event::CreateManualResetEvent(false);
  auto worker = Thread::Create({}, [&] {
    entered->Set();
    (void)Wait(finish.get(), false, 1000ms);
  });
  checks.Check(worker && Wait(entered.get(), false, 1000ms) == WaitResult::kSuccess,
               "SDK worker created and entered");
  if (!worker) return;
  bool rejected = false;
  try {
    worker->Terminate(19);
  } catch (const std::system_error& error) {
    rejected = error.code() == std::errc::operation_not_supported;
  }
  checks.Check(rejected && Wait(worker.get(), false, 0ms) == WaitResult::kTimeout,
               "remote termination rejected without false finished state");
  finish->Set();
  checks.Check(JoinRuntimeThread(worker.get()), "SDK worker normal exit and join");
  checks.Check(Wait(worker.get(), false, 0ms) == WaitResult::kSuccess &&
                   Wait(worker.get(), false, 0ms) == WaitResult::kSuccess,
               "completed SDK thread permits repeated waits without repeated cleanup");
  auto self_mutant = Mutant::Create(false);
  auto self_exit = Thread::Create({}, [&] {
    (void)Wait(self_mutant.get(), false, 100ms);
    Thread::Exit(23);
  });
  checks.Check(JoinRuntimeThread(self_exit.get()), "SDK worker self exit and join");
  checks.Check(Wait(self_mutant.get(), false, 0ms) == WaitResult::kAbandoned && self_mutant->Release(),
               "SDK self exit abandons owned mutant");
}

/** Exercise event reset modes, semaphore bounds and real queued timers. */
void TestEventsAndTimers(Checks& checks) {
  auto event = Event::CreateAutoResetEvent(true);
  checks.Check(Wait(event.get(), false, 0ms) == WaitResult::kSuccess &&
                   Wait(event.get(), false, 0ms) == WaitResult::kTimeout,
               "auto event consumed once");
  auto manual = Event::CreateManualResetEvent(true);
  checks.Check(Wait(manual.get(), false, 0ms) == WaitResult::kSuccess &&
                   Wait(manual.get(), false, 0ms) == WaitResult::kSuccess,
               "manual event remains signaled");
  manual->Reset();
  checks.Check(Wait(manual.get(), false, 0ms) == WaitResult::kTimeout, "manual event reset");
  auto semaphore = Semaphore::Create(1, 2);
  int previous = -1;
  checks.Check(Wait(semaphore.get(), false, 0ms) == WaitResult::kSuccess &&
                   Wait(semaphore.get(), false, 0ms) == WaitResult::kTimeout &&
                   semaphore->Release(2, &previous) && previous == 0 &&
                   !semaphore->Release(1, nullptr),
               "semaphore count consumption and maximum");
  auto timer = Timer::CreateSynchronizationTimer();
  checks.Check(timer->SetOnceAfter(20ms) && Wait(timer.get(), false, 1000ms) == WaitResult::kSuccess,
               "relative timer fires");
  checks.Check(Wait(timer.get(), false, 0ms) == WaitResult::kTimeout, "synchronization timer resets");
  checks.Check(timer->SetOnceAt(rex::chrono::WinSystemClock::now() + 20ms) &&
                   Wait(timer.get(), false, 1000ms) == WaitResult::kSuccess,
               "absolute NT timer fires through project ClockCast");
  std::atomic<int> callbacks{0};
  checks.Check(timer->SetRepeatingAfter(10ms, 10ms, [&] { ++callbacks; }), "repeating timer armed");
  std::this_thread::sleep_for(65ms);
  timer->Cancel();
  // Cancellation synchronizes with a queued callback before destroying capture state.
  std::this_thread::sleep_for(20ms);
  int cancelled_count = callbacks;
  std::this_thread::sleep_for(25ms);
  checks.Check(cancelled_count >= 2 && callbacks == cancelled_count, "repeating timer fires and cancels");
}

/** Bound asynchronous object destruction checks without relying on an exact dispatch time. */
template <typename T>
bool WaitForExpired(const std::weak_ptr<T>& object) {
  const auto deadline = std::chrono::steady_clock::now() + 1000ms;
  while (!object.expired() && std::chrono::steady_clock::now() < deadline) {
    std::this_thread::sleep_for(1ms);
  }
  return object.expired();
}

/** Verify the real jthread timer dispatcher, cancellation synchronization and capture lifetime. */
void TestTimerQueue(Checks& checks) {
  using Item = rex::thread::TimerQueueWaitItem;
  using Clock = Item::clock;
  using rex::thread::QueueTimerOnce;
  using rex::thread::QueueTimerRecurring;
  auto disarm = [](const std::weak_ptr<Item>& item) {
    if (auto held = item.lock()) held->Disarm();
  };

  struct OnceState {
    std::atomic<int> callbacks{0};
    std::atomic<bool> userdata_matches{false};
    std::atomic<bool> deadline_respected{false};
    Clock::time_point due;
    std::unique_ptr<Event> fired = Event::CreateManualResetEvent(false);
  } once;
  once.due = Clock::now() + 30ms;
  auto once_item = QueueTimerOnce([&](void* userdata) {
    once.userdata_matches = userdata == &once;
    once.deadline_respected = Clock::now() >= once.due;
    ++once.callbacks;
    once.fired->Set();
  }, &once, once.due);
  const bool once_fired = Wait(once.fired.get(), false, 1000ms) == WaitResult::kSuccess;
  disarm(once_item);
  std::this_thread::sleep_for(20ms);
  checks.Check(once_fired && once.callbacks == 1 && once.userdata_matches && once.deadline_respected,
               "TimerQueue once preserves userdata, deadline and single execution");
  checks.Check(WaitForExpired(once_item), "TimerQueue completed once item is destroyed");

  std::atomic<int> cancelled_callbacks{0};
  auto cancelled_item = QueueTimerOnce([&](void*) { ++cancelled_callbacks; }, nullptr,
                                        Clock::now() + 30ms);
  disarm(cancelled_item);
  checks.Check(WaitForExpired(cancelled_item) && cancelled_callbacks == 0,
               "TimerQueue disarm before deadline suppresses callback and frees item");

  auto recurring_ready = Event::CreateManualResetEvent(false);
  std::atomic<int> recurring_callbacks{0};
  auto recurring_item = QueueTimerRecurring([&](void*) {
    if (++recurring_callbacks >= 3) recurring_ready->Set();
  }, nullptr, Clock::now() + 10ms, 10ms);
  const bool recurring_fired = Wait(recurring_ready.get(), false, 1000ms) == WaitResult::kSuccess;
  disarm(recurring_item);
  const int stopped_count = recurring_callbacks;
  std::this_thread::sleep_for(30ms);
  checks.Check(recurring_fired && stopped_count >= 3 && recurring_callbacks == stopped_count,
               "TimerQueue recurring timer stops after Disarm returns");
  checks.Check(WaitForExpired(recurring_item), "TimerQueue disarmed recurring item is destroyed");

  struct SelfState {
    std::atomic<bool> published{false};
    std::weak_ptr<Item> item;
    std::atomic<int> callbacks{0};
    std::atomic<bool> held_item{false};
    std::unique_ptr<Event> fired = Event::CreateManualResetEvent(false);
  } self;
  self.item = QueueTimerRecurring([&](void*) {
    // A release/acquire publication makes assignment safe even if the caller is descheduled.
    while (!self.published.load(std::memory_order_acquire)) std::this_thread::yield();
    if (auto held = self.item.lock()) {
      self.held_item = true;
      held->Disarm();
    }
    ++self.callbacks;
    self.fired->Set();
  }, nullptr, Clock::now() + 30ms, 10ms);
  self.published.store(true, std::memory_order_release);
  const bool self_fired = Wait(self.fired.get(), false, 1000ms) == WaitResult::kSuccess;
  disarm(self.item);
  std::this_thread::sleep_for(30ms);
  checks.Check(self_fired && self.held_item && self.callbacks == 1,
               "TimerQueue callback can self-disarm without deadlock or recurrence");
  checks.Check(WaitForExpired(self.item), "TimerQueue self-disarmed item is destroyed");

  auto entered = Event::CreateManualResetEvent(false);
  auto release = Event::CreateManualResetEvent(false);
  std::atomic<int> inflight_callbacks{0};
  std::atomic<bool> callback_finished{false};
  auto inflight_item = QueueTimerRecurring([&](void*) {
    ++inflight_callbacks;
    entered->Set();
    (void)Wait(release.get(), false, 1000ms);
    callback_finished = true;
  }, nullptr, Clock::now() + 10ms, 10ms);
  const bool callback_entered = Wait(entered.get(), false, 1000ms) == WaitResult::kSuccess;
  std::atomic<bool> disarm_started{false};
  std::atomic<bool> disarm_returned{false};
  std::thread cancelling_thread([&] {
    disarm_started = true;
    disarm(inflight_item);
    disarm_returned = true;
  });
  while (!disarm_started) std::this_thread::yield();
  std::this_thread::sleep_for(10ms);
  const bool cancellation_waited = !disarm_returned;
  release->Set();
  cancelling_thread.join();
  const int inflight_stopped_count = inflight_callbacks;
  std::this_thread::sleep_for(30ms);
  checks.Check(callback_entered && cancellation_waited && disarm_returned && callback_finished &&
                   inflight_callbacks == inflight_stopped_count,
               "TimerQueue external Disarm waits for an in-flight callback and prevents future access");
  checks.Check(WaitForExpired(inflight_item), "TimerQueue synchronized cancellation destroys item");

  auto captured = std::make_shared<int>(42);
  std::weak_ptr<int> captured_weak = captured;
  auto lifetime_fired = Event::CreateManualResetEvent(false);
  std::atomic<bool> captured_value{false};
  auto lifetime_item = QueueTimerOnce([captured, &captured_value, &lifetime_fired](void*) {
    captured_value = *captured == 42;
    lifetime_fired->Set();
  }, nullptr, Clock::now() + 30ms);
  captured.reset();
  const bool held_until_due = !captured_weak.expired();
  const bool lifetime_callback = Wait(lifetime_fired.get(), false, 1000ms) == WaitResult::kSuccess;
  disarm(lifetime_item);
  checks.Check(held_until_due && lifetime_callback && captured_value && WaitForExpired(captured_weak),
               "TimerQueue callback capture lives until invocation and is released afterwards");
}
}  // namespace

/** Run once before guest initialization; callers must not race guest clock state.
 * Result storage remains valid until the next call. No assets or saves are touched.
 */
extern "C" __attribute__((visibility("default"))) const char* refii_android_platform_selftest() {
  static std::mutex guard;
  static std::string report;
  std::lock_guard lock(guard);
  Checks checks;
  auto start = std::chrono::steady_clock::now();
  try {
    rex::InitializeAndroidPlatform();
    rex::InitializeAndroidPlatform();
    checks.Check(rex::GetAndroidApiLevel() >= 31, "actual Android API query and repeated initialization");
    TestNumeric(checks);
    TestChrono(checks);
    TestMutants(checks);
    TestThreads(checks);
    TestEventsAndTimers(checks);
    TestTimerQueue(checks);
  } catch (const std::exception& error) {
    checks.Check(false, "unexpected exception");
    checks.details << error.what() << '\n';
  }
  std::ostringstream result;
  result << (checks.failed ? "FAIL" : "PASS") << " Android platform semantic tests: "
         << checks.passed << " passed, " << checks.failed << " failed; "
         << std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - start).count() << " ms\n"
         << checks.details.str()
         << "LIMITATION remote asynchronous thread termination and live guest teardown unsupported\n";
  report = result.str();
  return report.c_str();
}
