#include "logsystem.h"

#include <imgui.h>

#include <chrono>
#include <ctime>
#include <iomanip>
#include <iostream>

namespace {

std::string BuildLogTimestamp() {
    const auto now = std::chrono::system_clock::now();
    const auto time_t = std::chrono::system_clock::to_time_t(now);
    const auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(
        now.time_since_epoch()) % 1000;

    std::tm timeInfo = {};
#if defined(_WIN32)
    localtime_s(&timeInfo, &time_t);
#else
    localtime_r(&time_t, &timeInfo);
#endif

    std::ostringstream stream;
    stream << std::put_time(&timeInfo, "%Y-%m-%d %H:%M:%S")
           << '.' << std::setfill('0') << std::setw(3) << ms.count();
    return stream.str();
}

}  // namespace

LogSystem* LogSystem::instance = nullptr;

LogSystem& LogSystem::GetInstance() {
    if (!instance) {
        instance = new LogSystem();
    }
    return *instance;
}

bool LogSystem::InitializePersistentLog(const std::filesystem::path& logPath,
                                        std::string* error) {
    std::lock_guard<std::mutex> lock(logMutex);

    std::error_code createError;
    const std::filesystem::path parentPath = logPath.parent_path();
    if (!parentPath.empty()) {
        std::filesystem::create_directories(parentPath, createError);
    }
    if (createError) {
        if (error != nullptr) {
            *error = "Failed to create launcher log directory: " + createError.message();
        }
        return false;
    }

    persistentLogFile.close();
    persistentLogFile.clear();
    persistentLogFile.open(logPath, std::ios::out | std::ios::trunc);
    if (!persistentLogFile.is_open()) {
        if (error != nullptr) {
            *error = "Failed to open launcher log file: " + logPath.string();
        }
        persistentLogPath.clear();
        return false;
    }

    persistentLogPath = logPath;
    persistentLogFile << "=== refii launcher session started "
                      << BuildLogTimestamp() << " ===" << std::endl;
    persistentLogFile.flush();
    return true;
}

void LogSystem::AddLog(const std::string& message) {
    std::lock_guard<std::mutex> lock(logMutex);

    logLines.push_back(message);

    if (logLines.size() > maxLogLines) {
        logLines.erase(logLines.begin());
    }

    if (persistentLogFile.is_open()) {
        persistentLogFile << '[' << BuildLogTimestamp() << "] " << message << std::endl;
        persistentLogFile.flush();
    }
}

void LogSystem::Flush() {
    std::lock_guard<std::mutex> lock(logMutex);
    if (persistentLogFile.is_open()) {
        persistentLogFile.flush();
    }
}

void LogSystem::Clear() {
    std::lock_guard<std::mutex> lock(logMutex);
    logLines.clear();
}

void LogSystem::Render(bool* showLogWindow) {
    if (!showLogWindow || !*showLogWindow) {
        return;
    }

    ImGui::PushStyleColor(ImGuiCol_WindowBg, ImVec4(0.0f, 0.0f, 0.0f, 0.95f));
    ImGui::PushStyleColor(ImGuiCol_ChildBg, ImVec4(0.0f, 0.0f, 0.0f, 0.95f));
    ImGui::PushStyleColor(ImGuiCol_Text, ImVec4(1.0f, 1.0f, 1.0f, 1.0f));

    if (ImGui::Begin("Console Log", showLogWindow)) {
        if (ImGui::Button("Clear")) {
            Clear();
        }
        ImGui::SameLine();

        static bool autoScroll = true;
        ImGui::Checkbox("Auto-scroll", &autoScroll);

        ImGui::Separator();

        if (ImGui::BeginChild("LogContent", ImVec2(0, 0), false)) {
            std::lock_guard<std::mutex> lock(logMutex);

            for (const auto& line : logLines) {
                ImGui::TextWrapped("%s", line.c_str());
            }

            if (autoScroll && ImGui::GetScrollY() >= ImGui::GetScrollMaxY()) {
                ImGui::SetScrollHereY(1.0f);
            }
        }
        ImGui::EndChild();
    }
    ImGui::End();

    ImGui::PopStyleColor(3);
}

std::filesystem::path LogSystem::GetPersistentLogPath() {
    std::lock_guard<std::mutex> lock(logMutex);
    return persistentLogPath;
}

std::vector<std::string> LogSystem::GetLogLines() {
    std::lock_guard<std::mutex> lock(logMutex);
    return logLines;
}

LogStreambuf::LogStreambuf(std::streambuf* original) : originalBuffer(original) {
  // eh
}

LogStreambuf::~LogStreambuf() {
  // eh
}

int LogStreambuf::overflow(int c) {
    if (c != EOF) {
        buffer << static_cast<char>(c);
        if (originalBuffer) {
            originalBuffer->sputc(c);
        }
    }
    return c;
}

int LogStreambuf::sync() {
    std::string line = buffer.str();
    if (!line.empty()) {
        while (!line.empty() && (line.back() == '\n' || line.back() == '\r')) {
            line.pop_back();
        }
        if (!line.empty()) {
            LogSystem::GetInstance().AddLog(line);
        }
        buffer.str("");
        buffer.clear();
    }
    
    if (originalBuffer) {
        return originalBuffer->pubsync();
    }
    return 0;
}
