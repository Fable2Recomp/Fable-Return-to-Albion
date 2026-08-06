#include "platform.h"

#include <curl/curl.h>

#include <fcntl.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <unistd.h>

#include <array>
#include <cerrno>
#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <limits.h>
#include <sstream>
#include <string>
#include <string_view>
#include <vector>

extern char** environ;

namespace {

struct CurlGlobalInit {
    CurlGlobalInit() {
        curl_global_init(CURL_GLOBAL_DEFAULT);
    }

    ~CurlGlobalInit() {
        curl_global_cleanup();
    }
};

CurlGlobalInit curlGlobalInit;

// Helper programs like zenity and kdialog belong to the host, but under an
// AppImage we run with the bundled lib directory first on LD_LIBRARY_PATH, so
// a spawned helper would load our libraries instead of its own and die on the
// first symbol it could not find. AppRun stashes the host's original search
// paths for us; hand those to children instead of our own.
//
// The block is built here in the parent on purpose. The launcher has worker
// threads, and allocating between fork() and exec() can deadlock on a malloc
// lock another thread held at the moment of the fork.
std::vector<std::string> BuildHostChildEnvironment() {
    std::vector<std::string> environment;
    const bool underAppRun = std::getenv("REFII_HOST_ENV_SAVED") != nullptr;

    for (char** entry = environ; entry != nullptr && *entry != nullptr; ++entry) {
        const std::string_view variable(*entry);
        if (variable.starts_with("REFII_HOST_ENV_SAVED=") ||
            variable.starts_with("REFII_HOST_PATH=") ||
            variable.starts_with("REFII_HOST_LD_LIBRARY_PATH=")) {
            continue;
        }
        if (underAppRun &&
            (variable.starts_with("PATH=") || variable.starts_with("LD_LIBRARY_PATH="))) {
            continue;
        }
        environment.emplace_back(variable);
    }

    if (underAppRun) {
        // An empty saved value means the host had it unset, so leave it out.
        const char* hostPath = std::getenv("REFII_HOST_PATH");
        if (hostPath != nullptr && hostPath[0] != '\0') {
            environment.emplace_back(std::string("PATH=") + hostPath);
        }
        const char* hostLibraryPath = std::getenv("REFII_HOST_LD_LIBRARY_PATH");
        if (hostLibraryPath != nullptr && hostLibraryPath[0] != '\0') {
            environment.emplace_back(std::string("LD_LIBRARY_PATH=") + hostLibraryPath);
        }
    }

    return environment;
}

std::vector<char*> MakeEnvironmentPointers(std::vector<std::string>& environment) {
    std::vector<char*> pointers;
    pointers.reserve(environment.size() + 1);
    for (std::string& variable : environment) {
        pointers.push_back(variable.data());
    }
    pointers.push_back(nullptr);
    return pointers;
}

std::string TrimTrailingNewlines(std::string value) {
    while (!value.empty() && (value.back() == '\n' || value.back() == '\r')) {
        value.pop_back();
    }
    return value;
}

std::filesystem::path FindExecutableInPath(const std::string& name) {
    if (name.find('/') != std::string::npos) {
        const std::filesystem::path directPath = name;
        if (std::filesystem::exists(directPath) && access(directPath.c_str(), X_OK) == 0) {
            return directPath;
        }
        return {};
    }

    const char* pathEnvironment = std::getenv("PATH");
    if (pathEnvironment == nullptr) {
        return {};
    }

    std::stringstream stream(pathEnvironment);
    std::string segment;
    while (std::getline(stream, segment, ':')) {
        if (segment.empty()) {
            continue;
        }

        const std::filesystem::path candidate = std::filesystem::path(segment) / name;
        if (std::filesystem::exists(candidate) && access(candidate.c_str(), X_OK) == 0) {
            return candidate;
        }
    }

    return {};
}

int RunProcessAndCaptureOutput(
    const std::vector<std::string>& arguments,
    std::string& output,
    const std::filesystem::path& workingDirectory = {}) {
    if (arguments.empty()) {
        return -1;
    }

    int pipeFileDescriptors[2] = { -1, -1 };
    if (pipe(pipeFileDescriptors) != 0) {
        return -1;
    }

    std::vector<std::string> childEnvironment = BuildHostChildEnvironment();
    std::vector<char*> childEnvironmentPointers = MakeEnvironmentPointers(childEnvironment);

    std::vector<char*> argv;
    argv.reserve(arguments.size() + 1);
    for (const std::string& argument : arguments) {
        argv.push_back(const_cast<char*>(argument.c_str()));
    }
    argv.push_back(nullptr);

    const pid_t processId = fork();
    if (processId == -1) {
        close(pipeFileDescriptors[0]);
        close(pipeFileDescriptors[1]);
        return -1;
    }

    if (processId == 0) {
        if (!workingDirectory.empty()) {
            chdir(workingDirectory.c_str());
        }

        dup2(pipeFileDescriptors[1], STDOUT_FILENO);
        dup2(pipeFileDescriptors[1], STDERR_FILENO);
        close(pipeFileDescriptors[0]);
        close(pipeFileDescriptors[1]);

        execvpe(argv[0], argv.data(), childEnvironmentPointers.data());
        _exit(127);
    }

    close(pipeFileDescriptors[1]);

    std::array<char, 4096> buffer = {};
    ssize_t bytesRead = 0;
    while ((bytesRead = read(pipeFileDescriptors[0], buffer.data(), buffer.size())) > 0) {
        output.append(buffer.data(), static_cast<std::size_t>(bytesRead));
    }

    close(pipeFileDescriptors[0]);

    int status = 0;
    waitpid(processId, &status, 0);
    if (WIFEXITED(status)) {
        return WEXITSTATUS(status);
    }

    return -1;
}

int RunProcess(
    const std::vector<std::string>& arguments,
    const std::filesystem::path& workingDirectory = {}) {
    std::string unusedOutput;
    return RunProcessAndCaptureOutput(arguments, unusedOutput, workingDirectory);
}

size_t CurlWriteToString(char* data, size_t size, size_t count, void* userData) {
    auto* output = static_cast<std::string*>(userData);
    output->append(data, size * count);
    return size * count;
}

size_t CurlWriteToFile(char* data, size_t size, size_t count, void* userData) {
    auto* stream = static_cast<std::ofstream*>(userData);
    stream->write(data, static_cast<std::streamsize>(size * count));
    return size * count;
}

struct ProgressContext {
    std::function<void(std::size_t, std::size_t)> callback;
};

int CurlProgressCallback(
    void* clientData,
    curl_off_t totalToDownload,
    curl_off_t nowDownloaded,
    curl_off_t,
    curl_off_t) {
    auto* context = static_cast<ProgressContext*>(clientData);
    if (context->callback) {
        context->callback(
            static_cast<std::size_t>(nowDownloaded),
            static_cast<std::size_t>(totalToDownload));
    }
    return 0;
}

bool ConfigureCurl(CURL* handle, const std::string& url, std::string* error) {
    if (handle == nullptr) {
        if (error) {
            *error = "Failed to initialize libcurl.";
        }
        return false;
    }

    curl_easy_setopt(handle, CURLOPT_URL, url.c_str());
    curl_easy_setopt(handle, CURLOPT_FOLLOWLOCATION, 1L);
    curl_easy_setopt(handle, CURLOPT_USERAGENT, "refii-launcher/1.0");
    curl_easy_setopt(handle, CURLOPT_FAILONERROR, 0L);
    curl_easy_setopt(handle, CURLOPT_SSL_VERIFYPEER, 1L);
    curl_easy_setopt(handle, CURLOPT_SSL_VERIFYHOST, 2L);
    return true;
}

}  // namespace

namespace Platform {

std::string GetConfigSuffix() {
    return "Linux";
}

std::string GetDefaultExecutableName() {
    return "refii";
}

std::string GetDefaultExecutableRelativePath() {
    return "out/build/linux-amd64-relwithdebinfo/refii";
}

bool OpenUrl(const std::string& url, std::string* error) {
    if (FindExecutableInPath("xdg-open").empty()) {
        if (error) {
            *error = "xdg-open is not installed.";
        }
        return false;
    }

    const int exitCode = RunProcess({ "xdg-open", url });
    if (exitCode != 0 && error) {
        *error = "xdg-open failed with exit code " + std::to_string(exitCode) + ".";
    }
    return exitCode == 0;
}

bool SelectDirectory(const std::string& title, std::string& path, std::string* error) {
    if (!FindExecutableInPath("zenity").empty()) {
        std::string output;
        const int exitCode = RunProcessAndCaptureOutput(
            { "zenity", "--file-selection", "--directory", "--title", title },
            output);
        if (exitCode == 0) {
            path = TrimTrailingNewlines(output);
            return !path.empty();
        }
        return false;
    }

    if (!FindExecutableInPath("kdialog").empty()) {
        std::string output;
        const int exitCode = RunProcessAndCaptureOutput(
            { "kdialog", "--getexistingdirectory", ".", "--title", title },
            output);
        if (exitCode == 0) {
            path = TrimTrailingNewlines(output);
            return !path.empty();
        }
        return false;
    }

    if (error) {
        *error = "Neither zenity nor kdialog is installed, so the launcher cannot open a folder picker.";
    }
    return false;
}

bool SelectIsoFile(std::string& path, std::string* error) {
    if (!FindExecutableInPath("zenity").empty()) {
        std::string output;
        const int exitCode = RunProcessAndCaptureOutput(
            {
                "zenity",
                "--file-selection",
                "--title",
                "Select Game ISO",
                "--file-filter",
                "ISO files | *.iso"
            },
            output);
        if (exitCode == 0) {
            path = TrimTrailingNewlines(output);
            return !path.empty();
        }
        return false;
    }

    if (!FindExecutableInPath("kdialog").empty()) {
        std::string output;
        const int exitCode = RunProcessAndCaptureOutput(
            { "kdialog", "--getopenfilename", ".", "*.iso|ISO files", "--title", "Select Game ISO" },
            output);
        if (exitCode == 0) {
            path = TrimTrailingNewlines(output);
            return !path.empty();
        }
        return false;
    }

    if (error) {
        *error = "Neither zenity nor kdialog is installed, so the launcher cannot open an ISO picker.";
    }
    return false;
}

bool SelectXexpFile(std::string& path, std::string* error) {
    if (!FindExecutableInPath("zenity").empty()) {
        std::string output;
        const int exitCode = RunProcessAndCaptureOutput(
            {
                "zenity",
                "--file-selection",
                "--title",
                "Select Fable 2 XEXP",
                "--file-filter",
                "XEXP files | *.xexp"
            },
            output);
        if (exitCode == 0) {
            path = TrimTrailingNewlines(output);
            return !path.empty();
        }
        return false;
    }

    if (!FindExecutableInPath("kdialog").empty()) {
        std::string output;
        const int exitCode = RunProcessAndCaptureOutput(
            { "kdialog", "--getopenfilename", ".", "*.xexp|XEXP files", "--title", "Select Fable 2 XEXP" },
            output);
        if (exitCode == 0) {
            path = TrimTrailingNewlines(output);
            return !path.empty();
        }
        return false;
    }

    if (error) {
        *error = "Neither zenity nor kdialog is installed, so the launcher cannot open a XEXP picker.";
    }
    return false;
}

void EnableWindowEffects(GLFWwindow*) {
}

std::string HttpGet(const std::string& url, std::string* error) {
    std::string response;
    CURL* handle = curl_easy_init();
    if (!ConfigureCurl(handle, url, error)) {
        if (handle != nullptr) {
            curl_easy_cleanup(handle);
        }
        return {};
    }

    curl_easy_setopt(handle, CURLOPT_WRITEFUNCTION, &CurlWriteToString);
    curl_easy_setopt(handle, CURLOPT_WRITEDATA, &response);

    const CURLcode result = curl_easy_perform(handle);
    long statusCode = 0;
    curl_easy_getinfo(handle, CURLINFO_RESPONSE_CODE, &statusCode);
    curl_easy_cleanup(handle);

    if (result != CURLE_OK) {
        if (error) {
            *error = curl_easy_strerror(result);
        }
        return {};
    }

    if (statusCode < 200 || statusCode >= 300) {
        if (error) {
            *error = "HTTP Error " + std::to_string(statusCode);
        }
        return {};
    }

    return response;
}

bool DownloadFile(
    const std::string& url,
    const std::filesystem::path& localPath,
    const std::function<void(std::size_t, std::size_t)>& onProgress,
    std::string* error) {
    try {
        const std::filesystem::path parent = localPath.parent_path();
        if (!parent.empty()) {
            std::filesystem::create_directories(parent);
        }
    } catch (const std::exception& exception) {
        if (error) {
            *error = exception.what();
        }
        return false;
    }

    std::ofstream output(localPath, std::ios::binary);
    if (!output.is_open()) {
        if (error) {
            *error = "Failed to open the destination file.";
        }
        return false;
    }

    CURL* handle = curl_easy_init();
    if (!ConfigureCurl(handle, url, error)) {
        output.close();
        if (handle != nullptr) {
            curl_easy_cleanup(handle);
        }
        return false;
    }

    ProgressContext progressContext = { onProgress };
    curl_easy_setopt(handle, CURLOPT_WRITEFUNCTION, &CurlWriteToFile);
    curl_easy_setopt(handle, CURLOPT_WRITEDATA, &output);
    curl_easy_setopt(handle, CURLOPT_XFERINFOFUNCTION, &CurlProgressCallback);
    curl_easy_setopt(handle, CURLOPT_XFERINFODATA, &progressContext);
    curl_easy_setopt(handle, CURLOPT_NOPROGRESS, 0L);

    const CURLcode result = curl_easy_perform(handle);
    long statusCode = 0;
    curl_easy_getinfo(handle, CURLINFO_RESPONSE_CODE, &statusCode);
    curl_easy_cleanup(handle);
    output.close();

    if (result != CURLE_OK) {
        if (error) {
            *error = curl_easy_strerror(result);
        }
        return false;
    }

    if (statusCode < 200 || statusCode >= 300) {
        if (error) {
            *error = "HTTP Error " + std::to_string(statusCode);
        }
        return false;
    }

    return true;
}

bool LaunchProcess(
    const std::filesystem::path& workingDirectory,
    const std::filesystem::path& executablePath,
    const std::vector<std::string>& arguments,
    ProcessHandle& outHandle,
    std::string* error) {
    outHandle.nativeHandle = nullptr;
    outHandle.processId = 0;
    outHandle.hasExitStatus = false;
    outHandle.exitCode = 0;
    outHandle.termSignal = 0;
    outHandle.errorMessage.clear();

    int errorPipe[2] = {-1, -1};
    if (pipe(errorPipe) != 0) {
        if (error) {
            *error = std::strerror(errno);
        }
        return false;
    }

    const int pipeFlags = fcntl(errorPipe[1], F_GETFD);
    if (pipeFlags != -1) {
        fcntl(errorPipe[1], F_SETFD, pipeFlags | FD_CLOEXEC);
    }

    const pid_t processId = fork();
    if (processId == -1) {
        close(errorPipe[0]);
        close(errorPipe[1]);
        if (error) {
            *error = std::strerror(errno);
        }
        return false;
    }

    if (processId == 0) {
        close(errorPipe[0]);

        if (!workingDirectory.empty()) {
            if (chdir(workingDirectory.c_str()) != 0) {
                const int childError = errno;
                write(errorPipe[1], &childError, sizeof(childError));
                _exit(127);
            }
        }

        std::vector<std::string> argvStorage;
        argvStorage.reserve(arguments.size() + 1);
        argvStorage.push_back(executablePath.string());
        argvStorage.insert(argvStorage.end(), arguments.begin(), arguments.end());

        std::vector<char*> argv;
        argv.reserve(argvStorage.size() + 1);
        for (std::string& argument : argvStorage) {
            argv.push_back(argument.data());
        }
        argv.push_back(nullptr);

        execv(executablePath.c_str(), argv.data());
        const int childError = errno;
        write(errorPipe[1], &childError, sizeof(childError));
        _exit(127);
    }

    close(errorPipe[1]);

    int childError = 0;
    const ssize_t bytesRead = read(errorPipe[0], &childError, sizeof(childError));
    close(errorPipe[0]);
    if (bytesRead > 0) {
        int status = 0;
        waitpid(processId, &status, 0);
        const std::string childMessage = std::strerror(childError);
        outHandle.errorMessage = childMessage;
        if (error) {
            *error = childMessage;
        }
        return false;
    }

    outHandle.processId = static_cast<std::uint64_t>(processId);
    outHandle.nativeHandle = nullptr;
    return true;
}

bool IsProcessRunning(ProcessHandle& handle) {
    if (handle.processId == 0) {
        return false;
    }

    int status = 0;
    const pid_t result = waitpid(static_cast<pid_t>(handle.processId), &status, WNOHANG);
    if (result == 0) {
        return true;
    }

    handle.hasExitStatus = true;
    handle.exitCode = 0;
    handle.termSignal = 0;
    handle.errorMessage.clear();
    if (result == -1) {
        handle.errorMessage = std::strerror(errno);
    } else if (WIFEXITED(status)) {
        handle.exitCode = WEXITSTATUS(status);
    } else if (WIFSIGNALED(status)) {
        handle.termSignal = WTERMSIG(status);
    }

    handle.processId = 0;
    handle.nativeHandle = nullptr;
    return false;
}

void CloseProcessHandle(ProcessHandle& handle) {
    if (handle.processId != 0) {
        int status = 0;
        waitpid(static_cast<pid_t>(handle.processId), &status, WNOHANG);
    }
    handle.processId = 0;
    handle.nativeHandle = nullptr;
}

std::filesystem::path GetExecutableDirectory() {
    std::array<char, PATH_MAX> buffer = {};
    const ssize_t length = readlink("/proc/self/exe", buffer.data(), buffer.size() - 1);
    if (length <= 0) {
        return std::filesystem::current_path();
    }

    buffer[static_cast<std::size_t>(length)] = '\0';
    return std::filesystem::path(buffer.data()).parent_path();
}

std::filesystem::path GetExecutablePath() {
    std::array<char, PATH_MAX> buffer = {};
    const ssize_t length = readlink("/proc/self/exe", buffer.data(), buffer.size() - 1);
    if (length <= 0) {
        return GetExecutableDirectory() / "refii-launcher";
    }

    buffer[static_cast<std::size_t>(length)] = '\0';
    return std::filesystem::path(buffer.data());
}

}  // namespace Platform
