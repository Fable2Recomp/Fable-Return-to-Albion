#pragma once
#include <filesystem>
#include <fstream>
#include <mutex>
#include <sstream>
#include <string>
#include <vector>

class LogSystem {
private:
    static LogSystem* instance;
    std::vector<std::string> logLines;
    std::mutex logMutex;
    std::filesystem::path persistentLogPath;
    std::ofstream persistentLogFile;
    static const size_t maxLogLines = 1000;

    LogSystem() = default;

public:
    static LogSystem& GetInstance();

    bool InitializePersistentLog(const std::filesystem::path& logPath,
                                 std::string* error = nullptr);
    void AddLog(const std::string& message);
    void Flush();
    void Clear();
    void Render(bool* showLogWindow = nullptr);

    std::filesystem::path GetPersistentLogPath();
    std::vector<std::string> GetLogLines();
};

class LogStreambuf : public std::streambuf {
private:
    std::ostringstream buffer;
    std::streambuf* originalBuffer;

public:
    LogStreambuf(std::streambuf* original);
    ~LogStreambuf();

protected:
    virtual int overflow(int c) override;
    virtual int sync() override;
};
