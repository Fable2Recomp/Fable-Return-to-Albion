#include "filedownloader.h"

#include "platform.h"

#include <filesystem>
#include <iostream>
#include <thread>

void FileDownloader::DownloadFileAsync(
    const std::string& url,
    const std::string& localPath,
    std::shared_ptr<DownloadProgress> progress,
    std::function<void()> onSuccessCallback) {
    std::thread([url, localPath, progress, onSuccessCallback]() {
        const bool success = DownloadFile(url, localPath, progress);
        progress->isComplete.store(true);
        if (success && onSuccessCallback) {
            onSuccessCallback();
        } else if (!success && !progress->hasError.load()) {
            progress->hasError.store(true);
            progress->errorMessage = "Download failed";
        }
    }).detach();
}

bool FileDownloader::DownloadFile(
    const std::string& url,
    const std::string& localPath,
    std::shared_ptr<DownloadProgress> progress) {
    std::string error;
    const bool success = Platform::DownloadFile(
        url,
        localPath,
        [progress](std::size_t downloaded, std::size_t total) {
            progress->bytesDownloaded.store(downloaded);
            progress->totalBytes.store(total);
        },
        &error);

    if (!success) {
        progress->hasError.store(true);
        progress->errorMessage = error.empty() ? "Download failed" : error;
    }

    return success;
}
