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

RefiiIoQueue& RefiiIoWorkQueue() {
  static RefiiIoQueue queue;
  return queue;
}

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

void RefiiIoWorkerLoop(uint32_t worker_index) {
  // Guarded: this file compiles into the payload, where Tracy is deliberately absent, and a
  // direct tracy:: call is a C++ symbol crossing to the host. The macro forms elsewhere in
  // this file compile away on their own; this one was called by name.
#ifdef TRACY_ENABLE
  tracy::SetThreadName("Refii VFS I/O");
#endif
  RefiiIoQueue& queue = RefiiIoWorkQueue();
  for (;;) {
    RefiiIoJob job;
    {
      ZoneScopedN("RefiiVFS/IoWorkerWait");
      std::unique_lock<std::mutex> lock(queue.mutex);
      queue.cv.wait(lock,
                    [&queue] { return queue.stop_requested || !queue.jobs.empty(); });
      if (queue.stop_requested) {
        break;
      }
      job = std::move(queue.jobs.front());
      queue.jobs.pop_front();
      queue.active.fetch_add(1, std::memory_order_relaxed);
    }

    uint32_t read = 0;
    try {
      if (job.base != nullptr && job.record != nullptr &&
          job.request_guest != 0) {
        ZoneScopedN("RefiiVFS/IoWorkerRead");
        ZoneValue(job.record->requested_bytes);
        {
          std::lock_guard<std::mutex> lock(job.record->mutex);
          job.record->started_at = std::chrono::steady_clock::now();
          StoreGuestU32(job.base, job.request_guest + 24, 0);
          StoreGuestU32(job.base, job.request_guest + 124, 0);
          StoreGuestU32(job.base, job.request_guest + 176, 0);
        }
        read = ReadRefiiHostStreamForReadRequest(job.ctx, job.base, job.record);
        CompleteRefiiReadRequest(
            job.ctx, job.base, job.record, job.request_guest, read);
      }
    } catch (const std::exception& ex) {
      REXLOG_WARN("Refii I/O worker failed request=0x{:08X} worker={} "
                  "error={}",
                  job.request_guest,
                  worker_index,
                  ex.what());
      CompleteRefiiReadRequest(
          job.ctx, job.base, job.record, job.request_guest, 0);
    } catch (...) {
      REXLOG_WARN("Refii I/O worker failed request=0x{:08X} worker={} "
                  "error=<unknown>",
                  job.request_guest,
                  worker_index);
      CompleteRefiiReadRequest(
          job.ctx, job.base, job.record, job.request_guest, 0);
    }

    FinishRefiiIoJobPromise(job.completion);
    queue.completed.fetch_add(1, std::memory_order_relaxed);
    const uint32_t active =
        queue.active.fetch_sub(1, std::memory_order_relaxed) - 1;
    TracyPlot("Refii VFS Active IO Workers", static_cast<int64_t>(active));
  }
}

void EnsureRefiiIoWorkersStarted() {
  static std::once_flag start_once;
  std::call_once(start_once, [] {
    constexpr uint32_t kRefiiIoWorkerCount = 2;
    RefiiIoQueue& queue = RefiiIoWorkQueue();
    std::lock_guard<std::mutex> lock(queue.mutex);
    for (uint32_t index = 0; index < kRefiiIoWorkerCount; ++index) {
      queue.workers.emplace_back(RefiiIoWorkerLoop, index);
    }
    REXLOG_INFO("Refii VFS I/O queue started workers={}",
                kRefiiIoWorkerCount);
  });
}

bool EnqueueRefiiIoRead(PPCContext ctx,
                        uint8_t* base,
                        uint32_t request_guest,
                        const std::shared_ptr<RefiiReadRequestRecord>& record,
                        const std::shared_ptr<std::promise<void>>& completion) {
  if (base == nullptr || request_guest == 0 || record == nullptr) {
    return false;
  }

  EnsureRefiiIoWorkersStarted();

  RefiiIoQueue& queue = RefiiIoWorkQueue();
  uint64_t queued = 0;
  size_t depth = 0;
  {
    ZoneScopedN("RefiiVFS/IoQueuePush");
    std::lock_guard<std::mutex> lock(queue.mutex);
      if (queue.stop_requested) {
        return false;
      }
    {
      std::lock_guard<std::mutex> record_lock(record->mutex);
      record->queued_at = std::chrono::steady_clock::now();
      record->started_at = {};
      record->completed_at = {};
    }
    queue.jobs.push_back(
        RefiiIoJob{ctx, base, request_guest, record, completion});
    queued = queue.queued.fetch_add(1, std::memory_order_relaxed) + 1;
    depth = queue.jobs.size();
  }
  queue.cv.notify_one();
  TracyPlot("Refii VFS Queue Depth", static_cast<int64_t>(depth));
  TracyPlot("Refii VFS Active IO Workers",
            static_cast<int64_t>(queue.active.load(std::memory_order_relaxed)));

  if (LooseAssetOverrideTracingEnabled() ||
      ShouldLogLooseStreamOperation("RefiiIoQueue_Enqueue",
                                    request_guest,
                                    16)) {
    REXLOG_INFO("Refii VFS I/O queued request=0x{:08X} stream=0x{:08X} "
                "offset={} requested={} queue_depth={} active={} queued={} "
                "completed={}",
                request_guest,
                record->stream_guest,
                record->offset,
                record->requested_bytes,
                depth,
                queue.active.load(std::memory_order_relaxed),
                queued,
                queue.completed.load(std::memory_order_relaxed));
  }
  return true;
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

  auto completion = std::make_shared<std::promise<void>>();
  std::shared_future<void> async_read = completion->get_future().share();
  {
    std::lock_guard<std::mutex> lock(record->mutex);
    record->async_read = async_read;
  }

  if (!EnqueueRefiiIoRead(ctx, base, record_guest, record, completion)) {
    FinishRefiiIoJobPromise(completion);
  }
}

bool WaitForRefiiReadRequest(
    const std::shared_ptr<RefiiReadRequestRecord>& record) {
  ZoneScopedN("RefiiVFS/WaitReadRequest");
  if (record == nullptr) {
    return false;
  }

  std::shared_future<void> async_read;
  {
    std::lock_guard<std::mutex> lock(record->mutex);
    async_read = record->async_read;
  }
  if (async_read.valid()) {
    const auto wait_started = std::chrono::steady_clock::now();
    async_read.wait();
    if (LooseAssetOverrideTracingEnabled() ||
        ShouldLogLooseStreamOperation("ReadRequest_WaitTiming",
                                      record->request_guest,
                                      16)) {
      std::chrono::steady_clock::time_point queued_at{};
      std::chrono::steady_clock::time_point completed_at{};
      uint32_t requested = 0;
      uint32_t read = 0;
      {
        std::lock_guard<std::mutex> lock(record->mutex);
        queued_at = record->queued_at;
        completed_at = record->completed_at;
        requested = record->requested_bytes;
        read = record->bytes_read;
      }
      const auto wait_finished = std::chrono::steady_clock::now();
      const auto zero = std::chrono::steady_clock::time_point{};
      const int64_t total_ms =
          queued_at == zero || completed_at == zero
              ? -1
              : std::chrono::duration_cast<std::chrono::milliseconds>(
                    completed_at - queued_at)
                    .count();
      const int64_t wait_ms =
          std::chrono::duration_cast<std::chrono::milliseconds>(
              wait_finished - wait_started)
              .count();
      REXLOG_INFO("Refii read request wait timing request=0x{:08X} "
                  "stream=0x{:08X} requested={} read={} wait_ms={} "
                  "queue_to_complete_ms={}",
                  record->request_guest,
                  record->stream_guest,
                  requested,
                  read,
                  wait_ms,
                  total_ms);
    }
  }
  return true;
}


}  // namespace detail
}  // namespace refii::vfs
