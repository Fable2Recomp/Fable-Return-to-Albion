#pragma once

#include <atomic>
#include <cstring>
#include <functional>
#include <mutex>
#include <string>
#include <thread>
#include <utility>

namespace refii::abi {

class Job {
public:
    void SetStatus(const std::string& message) {
        std::lock_guard<std::mutex> lock(mutex_);
        status_ = message;
    }

    std::string GetStatus() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return status_;
    }

    void SetError(const std::string& message) {
        std::lock_guard<std::mutex> lock(mutex_);
        error_ = message;
    }

    std::string GetError() const {
        std::lock_guard<std::mutex> lock(mutex_);
        return error_;
    }

    void SetProgress(unsigned long long completed, unsigned long long total) {
        completed_.store(completed, std::memory_order_relaxed);
        total_.store(total, std::memory_order_relaxed);
    }

    unsigned long long Completed() const { return completed_.load(std::memory_order_relaxed); }
    unsigned long long Total() const { return total_.load(std::memory_order_relaxed); }
    bool Running() const { return running_.load(std::memory_order_acquire); }
    bool Succeeded() const { return succeeded_.load(std::memory_order_acquire); }

    void Run(std::function<bool(Job&)> body) {
        running_.store(true, std::memory_order_release);
        worker_ = std::thread([this, body = std::move(body)]() {
            bool ok = false;
            try {
                ok = body(*this);
            } catch (const std::exception& exception) {
                SetError(std::string("unhandled exception: ") + exception.what());
                ok = false;
            } catch (...) {
                SetError("unhandled exception");
                ok = false;
            }
            succeeded_.store(ok, std::memory_order_release);
            running_.store(false, std::memory_order_release);
        });
    }

    ~Job() {
        if (worker_.joinable()) {
            worker_.join();
        }
    }

private:
    mutable std::mutex mutex_;
    std::string status_;
    std::string error_;
    std::atomic<unsigned long long> completed_{0};
    std::atomic<unsigned long long> total_{0};
    std::atomic<bool> running_{false};
    std::atomic<bool> succeeded_{false};
    std::thread worker_;
};

inline int CopyString(const std::string& value, char* buffer, int capacity) {
    const int needed = static_cast<int>(value.size());
    if (buffer == nullptr || capacity <= 0) {
        return needed;
    }
    const int copied = needed < capacity - 1 ? needed : capacity - 1;
    if (copied > 0) {
        std::memcpy(buffer, value.data(), static_cast<std::size_t>(copied));
    }
    buffer[copied] = '\0';
    return needed;
}

}  // namespace refii::abi

#define REFII_ABI_DEFINE_JOB_API(PREFIX)                                                     \
    REFII_ABI_EXPORT int PREFIX##_job_is_running(const void* job) {                          \
        return job != nullptr &&                                                             \
               static_cast<const refii::abi::Job*>(job)->Running() ? 1 : 0;                  \
    }                                                                                        \
    REFII_ABI_EXPORT int PREFIX##_job_succeeded(const void* job) {                           \
        return job != nullptr &&                                                             \
               static_cast<const refii::abi::Job*>(job)->Succeeded() ? 1 : 0;                \
    }                                                                                        \
    REFII_ABI_EXPORT unsigned long long PREFIX##_job_completed(const void* job) {            \
        return job == nullptr ? 0 : static_cast<const refii::abi::Job*>(job)->Completed();   \
    }                                                                                        \
    REFII_ABI_EXPORT unsigned long long PREFIX##_job_total(const void* job) {                \
        return job == nullptr ? 0 : static_cast<const refii::abi::Job*>(job)->Total();       \
    }                                                                                        \
    REFII_ABI_EXPORT int PREFIX##_job_status(const void* job, char* buffer, int capacity) {  \
        if (job == nullptr) {                                                                \
            return refii::abi::CopyString(std::string(), buffer, capacity);                  \
        }                                                                                    \
        return refii::abi::CopyString(                                                       \
            static_cast<const refii::abi::Job*>(job)->GetStatus(), buffer, capacity);        \
    }                                                                                        \
    REFII_ABI_EXPORT int PREFIX##_job_error(const void* job, char* buffer, int capacity) {   \
        if (job == nullptr) {                                                                \
            return refii::abi::CopyString(std::string(), buffer, capacity);                  \
        }                                                                                    \
        return refii::abi::CopyString(                                                       \
            static_cast<const refii::abi::Job*>(job)->GetError(), buffer, capacity);         \
    }
