#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

struct RefiiIoJob {
  PPCContext ctx{};
  uint8_t* base = nullptr;
  uint32_t request_guest = 0;
  std::shared_ptr<RefiiReadRequestRecord> record;
  std::shared_ptr<std::promise<void>> completion;
};

struct RefiiIoQueue;
void ShutdownRefiiIoQueue(RefiiIoQueue& queue);

struct RefiiIoQueue {
  std::mutex mutex;
  std::condition_variable cv;
  std::deque<RefiiIoJob> jobs;
  std::atomic<uint64_t> queued{0};
  std::atomic<uint64_t> completed{0};
  std::atomic<uint32_t> active{0};
  bool stop_requested = false;
  std::vector<std::thread> workers;

  ~RefiiIoQueue() {
    ShutdownRefiiIoQueue(*this);
  }
};

void FinishRefiiIoJobPromise(const std::shared_ptr<std::promise<void>>& promise) {
  if (promise == nullptr) {
    return;
  }

  try {
    promise->set_value();
  } catch (const std::future_error&) {
  }
}

void ShutdownRefiiIoQueue(RefiiIoQueue& queue) {
  std::vector<RefiiIoJob> pending;
  std::vector<std::thread> workers_to_join;
  {
    std::lock_guard<std::mutex> lock(queue.mutex);
    if (queue.stop_requested) {
      return;
    }
    queue.stop_requested = true;
    while (!queue.jobs.empty()) {
      pending.push_back(std::move(queue.jobs.front()));
      queue.jobs.pop_front();
    }
    workers_to_join.swap(queue.workers);
    queue.cv.notify_all();
  }

  for (auto& job : pending) {
    FinishRefiiIoJobPromise(job.completion);
  }

  for (auto& worker : workers_to_join) {
    if (worker.joinable()) {
      worker.join();
    }
  }
}

void StartRefiiReadRequestAsync(
    PPCContext ctx,
    uint8_t* base,
    uint32_t record_guest,
    const std::shared_ptr<RefiiReadRequestRecord>& record) {
  if (base == nullptr || record == nullptr || record_guest == 0 ||
      record->stream_guest == 0 || record->buffer_guest == 0) {
    return;
  }

  // Runs on the caller's (guest's) thread: `CompleteRefiiReadRequest` runs guest functions to
  // signal the request's event, so it must never run on a host worker. A request whose
  // destination is a stream descriptor arrives before the guest has filled it in (base word
  // still 0), so it is deferred until the guest asks for the result — see
  // PerformRefiiReadRequestNow.
  if (record->uses_stream_destination &&
      LoadGuestU32(base, record->buffer_guest) == 0u) {
    // A deferred request must be collected later — the release path completes it; otherwise the
    // guest's refill loop (`sub_82A20EE8`, back edge at 0x82A2101C) spins forever.
    {
      std::lock_guard<std::mutex> lock(record->mutex);
      record->deferred_for_destination = true;
    }
    REXLOG_WARN("Refii read DEFERRED: request=0x{:08X} stream=0x{:08X} offset={} "
                "requested={} — the destination descriptor at 0x{:08X} is still null. If no "
                "collect follows, the guest reader will spin.",
                record_guest, record->stream_guest, record->offset,
                record->requested_bytes, record->buffer_guest);
    return;
  }

  PerformRefiiReadRequestNow(ctx, base, record, record_guest);
}

void PerformRefiiReadRequestNow(
    PPCContext& ctx,
    uint8_t* base,
    const std::shared_ptr<RefiiReadRequestRecord>& record,
    uint32_t record_guest) {
  if (base == nullptr || record == nullptr || record_guest == 0) {
    return;
  }
  {
    std::lock_guard<std::mutex> lock(record->mutex);
    if (record->completed) {
      return;
    }
  }

  const uint32_t read = ReadRefiiHostStreamForReadRequest(ctx, base, record);
  CompleteRefiiReadRequest(ctx, base, record, record_guest, read);
}
}  // namespace detail
}  // namespace refii::vfs
