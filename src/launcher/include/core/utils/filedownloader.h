#pragma once

#include <atomic>
#include <functional>
#include <memory>
#include <string>

struct DownloadProgress {
    std::atomic<size_t> bytesDownloaded;
    std::atomic<size_t> totalBytes;
    std::atomic<bool> isComplete;
    std::atomic<bool> hasError;
    std::string errorMessage;
    
    DownloadProgress() : bytesDownloaded(0), totalBytes(0), isComplete(false), hasError(false) {}
    
    float GetPercentage() const {
        if (totalBytes.load() == 0) return 0.0f;
        return (float)bytesDownloaded.load() / (float)totalBytes.load() * 100.0f;
    }
};

class FileDownloader {
public:
    static void DownloadFileAsync(
        const std::string& url,
        const std::string& localPath,
        std::shared_ptr<DownloadProgress> progress,
        std::function<void()> onSuccessCallback = nullptr);

private:
    static bool DownloadFile(
        const std::string& url,
        const std::string& localPath,
        std::shared_ptr<DownloadProgress> progress);
};
