#include "platform.h"

#include <windows.h>
#include <commdlg.h>
#include <dwmapi.h>
#include <shellapi.h>
#include <shlobj.h>
#include <winhttp.h>

#define GLFW_EXPOSE_NATIVE_WIN32
#include <GLFW/glfw3.h>
#include <GLFW/glfw3native.h>

#include <filesystem>
#include <fstream>
#include <sstream>
#include <vector>

namespace {

std::wstring Utf8ToWide(const std::string& input) {
    if (input.empty()) {
        return {};
    }

    const int length = MultiByteToWideChar(CP_UTF8, 0, input.c_str(), -1, nullptr, 0);
    if (length <= 0) {
        return {};
    }

    std::wstring output(static_cast<std::size_t>(length), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, input.c_str(), -1, output.data(), length);
    output.resize(static_cast<std::size_t>(length - 1));
    return output;
}

std::string WideToUtf8(const std::wstring& input) {
    if (input.empty()) {
        return {};
    }

    const int length = WideCharToMultiByte(CP_UTF8, 0, input.c_str(), -1, nullptr, 0, nullptr, nullptr);
    if (length <= 0) {
        return {};
    }

    std::string output(static_cast<std::size_t>(length), '\0');
    WideCharToMultiByte(CP_UTF8, 0, input.c_str(), -1, output.data(), length, nullptr, nullptr);
    output.resize(static_cast<std::size_t>(length - 1));
    return output;
}

struct ParsedUrl {
    std::wstring host;
    std::wstring path;
    INTERNET_PORT port = INTERNET_DEFAULT_HTTPS_PORT;
    bool secure = true;
};

bool ParseUrl(const std::string& url, ParsedUrl& parsed, std::string* error) {
    const std::string httpsPrefix = "https://";
    const std::string httpPrefix = "http://";

    std::string working = url;
    if (working.rfind(httpsPrefix, 0) == 0) {
        parsed.secure = true;
        parsed.port = INTERNET_DEFAULT_HTTPS_PORT;
        working.erase(0, httpsPrefix.size());
    } else if (working.rfind(httpPrefix, 0) == 0) {
        parsed.secure = false;
        parsed.port = INTERNET_DEFAULT_HTTP_PORT;
        working.erase(0, httpPrefix.size());
    } else {
        if (error) {
            *error = "Only http:// and https:// URLs are supported.";
        }
        return false;
    }

    const std::size_t slashPos = working.find('/');
    std::string host = slashPos == std::string::npos ? working : working.substr(0, slashPos);
    std::string path = slashPos == std::string::npos ? "/" : working.substr(slashPos);

    const std::size_t colonPos = host.find(':');
    if (colonPos != std::string::npos) {
        try {
            parsed.port = static_cast<INTERNET_PORT>(std::stoi(host.substr(colonPos + 1)));
            host = host.substr(0, colonPos);
        } catch (...) {
            if (error) {
                *error = "The URL port is invalid.";
            }
            return false;
        }
    }

    parsed.host = Utf8ToWide(host);
    parsed.path = Utf8ToWide(path);
    return !parsed.host.empty() && !parsed.path.empty();
}

std::string QuoteArgument(const std::string& argument) {
    if (argument.find_first_of(" \t\"") == std::string::npos) {
        return argument;
    }

    std::string quoted = "\"";
    for (char character : argument) {
        if (character == '"') {
            quoted += "\\\"";
        } else {
            quoted += character;
        }
    }
    quoted += '"';
    return quoted;
}

bool EnsureParentDirectory(const std::filesystem::path& path, std::string* error) {
    try {
        const std::filesystem::path parent = path.parent_path();
        if (!parent.empty()) {
            std::filesystem::create_directories(parent);
        }
        return true;
    } catch (const std::exception& exception) {
        if (error) {
            *error = exception.what();
        }
        return false;
    }
}

}  // namespace

namespace Platform {

std::string GetConfigSuffix() {
    return "Windows";
}

std::string GetDefaultExecutableName() {
    return "refii.exe";
}

std::string GetDefaultExecutableRelativePath() {
    return "out/build/win-amd64-relwithdebinfo/refii.exe";
}

bool OpenUrl(const std::string& url, std::string* error) {
    const auto result = reinterpret_cast<std::intptr_t>(
        ShellExecuteA(nullptr, "open", url.c_str(), nullptr, nullptr, SW_SHOWNORMAL));
    if (result <= 32 && error) {
        *error = "Failed to open the URL.";
    }
    return result > 32;
}

bool SelectDirectory(const std::string& title, std::string& path, std::string* error) {
    BROWSEINFOW browseInfo = {};
    const std::wstring wideTitle = Utf8ToWide(title);
    browseInfo.lpszTitle = wideTitle.c_str();
    browseInfo.ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE;

    LPITEMIDLIST itemIdList = SHBrowseForFolderW(&browseInfo);
    if (itemIdList == nullptr) {
        return false;
    }

    wchar_t buffer[MAX_PATH] = {};
    const bool success = SHGetPathFromIDListW(itemIdList, buffer) == TRUE;
    CoTaskMemFree(itemIdList);

    if (!success) {
        if (error) {
            *error = "Failed to read the selected directory.";
        }
        return false;
    }

    path = WideToUtf8(buffer);
    return true;
}

bool SelectIsoFile(std::string& path, std::string* error) {
    OPENFILENAMEW dialog = {};
    wchar_t buffer[MAX_PATH] = {};
    wchar_t filter[] = L"ISO Files\0*.iso\0All Files\0*.*\0";

    dialog.lStructSize = sizeof(dialog);
    dialog.lpstrFile = buffer;
    dialog.nMaxFile = MAX_PATH;
    dialog.lpstrFilter = filter;
    dialog.nFilterIndex = 1;
    dialog.Flags = OFN_PATHMUSTEXIST | OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR;

    if (GetOpenFileNameW(&dialog) != TRUE) {
        return false;
    }

    path = WideToUtf8(buffer);
    if (path.empty() && error) {
        *error = "Failed to read the selected ISO file.";
    }
    return !path.empty();
}

bool SelectXexpFile(std::string& path, std::string* error) {
    OPENFILENAMEW dialog = {};
    wchar_t buffer[MAX_PATH] = {};
    wchar_t filter[] = L"XEXP Files\0*.xexp\0All Files\0*.*\0";

    dialog.lStructSize = sizeof(dialog);
    dialog.lpstrFile = buffer;
    dialog.nMaxFile = MAX_PATH;
    dialog.lpstrFilter = filter;
    dialog.nFilterIndex = 1;
    dialog.Flags = OFN_PATHMUSTEXIST | OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR;

    if (GetOpenFileNameW(&dialog) != TRUE) {
        return false;
    }

    path = WideToUtf8(buffer);
    if (path.empty() && error) {
        *error = "Failed to read the selected XEXP file.";
    }
    return !path.empty();
}

void EnableWindowEffects(GLFWwindow* window) {
    HWND windowHandle = glfwGetWin32Window(window);
    if (windowHandle == nullptr) {
        return;
    }

    MARGINS margins = { -1 };
    DwmExtendFrameIntoClientArea(windowHandle, &margins);

    const HMODULE user32 = LoadLibraryA("user32.dll");
    if (user32 == nullptr) {
        return;
    }

    struct AccentPolicy {
        int accentState;
        int flags;
        int color;
        int animationId;
    };

    struct WindowCompositionAttributeData {
        int attribute;
        PVOID data;
        SIZE_T sizeOfData;
    };

    enum AccentState {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_BLURBEHIND = 3
    };

    using SetWindowCompositionAttributeFn =
        BOOL(WINAPI*)(HWND, WindowCompositionAttributeData*);

    const auto setWindowCompositionAttribute =
        reinterpret_cast<SetWindowCompositionAttributeFn>(
            GetProcAddress(user32, "SetWindowCompositionAttribute"));
    if (setWindowCompositionAttribute != nullptr) {
        AccentPolicy policy = { ACCENT_ENABLE_BLURBEHIND, 0, 0, 0 };
        WindowCompositionAttributeData data = { 19, &policy, sizeof(policy) };
        setWindowCompositionAttribute(windowHandle, &data);
    }

    FreeLibrary(user32);
}

std::string HttpGet(const std::string& url, std::string* error) {
    ParsedUrl parsedUrl;
    if (!ParseUrl(url, parsedUrl, error)) {
        return {};
    }

    std::string result;

    HINTERNET session = WinHttpOpen(
        L"refii-launcher/1.0",
        WINHTTP_ACCESS_TYPE_DEFAULT_PROXY,
        WINHTTP_NO_PROXY_NAME,
        WINHTTP_NO_PROXY_BYPASS,
        0);
    if (session == nullptr) {
        if (error) {
            *error = "Failed to initialize the HTTP session.";
        }
        return {};
    }

    HINTERNET connection = WinHttpConnect(session, parsedUrl.host.c_str(), parsedUrl.port, 0);
    if (connection == nullptr) {
        WinHttpCloseHandle(session);
        if (error) {
            *error = "Failed to connect to the remote host.";
        }
        return {};
    }

    const DWORD flags = parsedUrl.secure ? WINHTTP_FLAG_SECURE : 0;
    HINTERNET request = WinHttpOpenRequest(
        connection,
        L"GET",
        parsedUrl.path.c_str(),
        nullptr,
        WINHTTP_NO_REFERER,
        WINHTTP_DEFAULT_ACCEPT_TYPES,
        flags);
    if (request == nullptr) {
        WinHttpCloseHandle(connection);
        WinHttpCloseHandle(session);
        if (error) {
            *error = "Failed to create the HTTP request.";
        }
        return {};
    }

    const std::wstring headers = L"User-Agent: refii-launcher/1.0\r\n";
    WinHttpAddRequestHeaders(request, headers.c_str(), -1, WINHTTP_ADDREQ_FLAG_ADD);

    BOOL requestSent = WinHttpSendRequest(
        request,
        WINHTTP_NO_ADDITIONAL_HEADERS,
        0,
        WINHTTP_NO_REQUEST_DATA,
        0,
        0,
        0);
    if (!requestSent || !WinHttpReceiveResponse(request, nullptr)) {
        WinHttpCloseHandle(request);
        WinHttpCloseHandle(connection);
        WinHttpCloseHandle(session);
        if (error) {
            *error = "Failed to download the response.";
        }
        return {};
    }

    DWORD statusCode = 0;
    DWORD statusCodeSize = sizeof(statusCode);
    WinHttpQueryHeaders(
        request,
        WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
        WINHTTP_HEADER_NAME_BY_INDEX,
        &statusCode,
        &statusCodeSize,
        WINHTTP_NO_HEADER_INDEX);
    if (statusCode != 200) {
        WinHttpCloseHandle(request);
        WinHttpCloseHandle(connection);
        WinHttpCloseHandle(session);
        if (error) {
            *error = "HTTP Error " + std::to_string(statusCode);
        }
        return {};
    }

    DWORD availableBytes = 0;
    DWORD downloadedBytes = 0;
    do {
        availableBytes = 0;
        if (!WinHttpQueryDataAvailable(request, &availableBytes) || availableBytes == 0) {
            break;
        }

        std::vector<char> buffer(static_cast<std::size_t>(availableBytes));
        if (WinHttpReadData(request, buffer.data(), availableBytes, &downloadedBytes)) {
            result.append(buffer.data(), downloadedBytes);
        }
    } while (availableBytes > 0);

    WinHttpCloseHandle(request);
    WinHttpCloseHandle(connection);
    WinHttpCloseHandle(session);

    return result;
}

bool DownloadFile(
    const std::string& url,
    const std::filesystem::path& localPath,
    const std::function<void(std::size_t, std::size_t)>& onProgress,
    std::string* error) {
    if (!EnsureParentDirectory(localPath, error)) {
        return false;
    }

    ParsedUrl parsedUrl;
    if (!ParseUrl(url, parsedUrl, error)) {
        return false;
    }

    HINTERNET session = WinHttpOpen(
        L"refii-launcher/1.0",
        WINHTTP_ACCESS_TYPE_DEFAULT_PROXY,
        WINHTTP_NO_PROXY_NAME,
        WINHTTP_NO_PROXY_BYPASS,
        0);
    if (session == nullptr) {
        if (error) {
            *error = "Failed to initialize the HTTP session.";
        }
        return false;
    }

    HINTERNET connection = WinHttpConnect(session, parsedUrl.host.c_str(), parsedUrl.port, 0);
    if (connection == nullptr) {
        WinHttpCloseHandle(session);
        if (error) {
            *error = "Failed to connect to the remote host.";
        }
        return false;
    }

    const DWORD flags = parsedUrl.secure ? WINHTTP_FLAG_SECURE : 0;
    HINTERNET request = WinHttpOpenRequest(
        connection,
        L"GET",
        parsedUrl.path.c_str(),
        nullptr,
        WINHTTP_NO_REFERER,
        WINHTTP_DEFAULT_ACCEPT_TYPES,
        flags);
    if (request == nullptr) {
        WinHttpCloseHandle(connection);
        WinHttpCloseHandle(session);
        if (error) {
            *error = "Failed to create the HTTP request.";
        }
        return false;
    }

    const std::wstring headers = L"User-Agent: refii-launcher/1.0\r\n";
    WinHttpAddRequestHeaders(request, headers.c_str(), -1, WINHTTP_ADDREQ_FLAG_ADD);

    BOOL requestSent = WinHttpSendRequest(
        request,
        WINHTTP_NO_ADDITIONAL_HEADERS,
        0,
        WINHTTP_NO_REQUEST_DATA,
        0,
        0,
        0);
    if (!requestSent || !WinHttpReceiveResponse(request, nullptr)) {
        WinHttpCloseHandle(request);
        WinHttpCloseHandle(connection);
        WinHttpCloseHandle(session);
        if (error) {
            *error = "Failed to download the response.";
        }
        return false;
    }

    DWORD statusCode = 0;
    DWORD statusCodeSize = sizeof(statusCode);
    WinHttpQueryHeaders(
        request,
        WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
        WINHTTP_HEADER_NAME_BY_INDEX,
        &statusCode,
        &statusCodeSize,
        WINHTTP_NO_HEADER_INDEX);
    if (statusCode != 200) {
        WinHttpCloseHandle(request);
        WinHttpCloseHandle(connection);
        WinHttpCloseHandle(session);
        if (error) {
            *error = "HTTP Error " + std::to_string(statusCode);
        }
        return false;
    }

    DWORD contentLength = 0;
    DWORD contentLengthSize = sizeof(contentLength);
    if (!WinHttpQueryHeaders(
            request,
            WINHTTP_QUERY_CONTENT_LENGTH | WINHTTP_QUERY_FLAG_NUMBER,
            WINHTTP_HEADER_NAME_BY_INDEX,
            &contentLength,
            &contentLengthSize,
            WINHTTP_NO_HEADER_INDEX)) {
        contentLength = 0;
    }

    std::ofstream output(localPath, std::ios::binary);
    if (!output.is_open()) {
        WinHttpCloseHandle(request);
        WinHttpCloseHandle(connection);
        WinHttpCloseHandle(session);
        if (error) {
            *error = "Failed to open the destination file.";
        }
        return false;
    }

    std::size_t totalDownloaded = 0;
    DWORD availableBytes = 0;
    DWORD downloadedBytes = 0;
    do {
        availableBytes = 0;
        if (!WinHttpQueryDataAvailable(request, &availableBytes) || availableBytes == 0) {
            break;
        }

        std::vector<char> buffer(static_cast<std::size_t>(availableBytes));
        if (!WinHttpReadData(request, buffer.data(), availableBytes, &downloadedBytes)) {
            WinHttpCloseHandle(request);
            WinHttpCloseHandle(connection);
            WinHttpCloseHandle(session);
            if (error) {
                *error = "Failed while reading the download stream.";
            }
            return false;
        }

        output.write(buffer.data(), downloadedBytes);
        totalDownloaded += downloadedBytes;
        if (onProgress) {
            onProgress(totalDownloaded, contentLength);
        }
    } while (availableBytes > 0);

    WinHttpCloseHandle(request);
    WinHttpCloseHandle(connection);
    WinHttpCloseHandle(session);
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

    STARTUPINFOA startupInfo = {};
    startupInfo.cb = sizeof(startupInfo);

    PROCESS_INFORMATION processInfo = {};

    std::string commandLine = QuoteArgument(executablePath.string());
    for (const std::string& argument : arguments) {
        commandLine += " " + QuoteArgument(argument);
    }

    std::vector<char> mutableCommandLine(commandLine.begin(), commandLine.end());
    mutableCommandLine.push_back('\0');

    const std::string workingDirectoryString = workingDirectory.string();
    const BOOL success = CreateProcessA(
        nullptr,
        mutableCommandLine.data(),
        nullptr,
        nullptr,
        FALSE,
        0,
        nullptr,
        workingDirectoryString.empty() ? nullptr : workingDirectoryString.c_str(),
        &startupInfo,
        &processInfo);

    if (!success) {
        if (error) {
            *error = "CreateProcess failed with error " + std::to_string(GetLastError()) + ".";
        }
        return false;
    }

    CloseHandle(processInfo.hThread);
    outHandle.nativeHandle = processInfo.hProcess;
    outHandle.processId = static_cast<std::uint64_t>(processInfo.dwProcessId);
    return true;
}

bool IsProcessRunning(ProcessHandle& handle) {
    if (handle.nativeHandle == nullptr) {
        return false;
    }

    DWORD exitCode = 0;
    if (!GetExitCodeProcess(static_cast<HANDLE>(handle.nativeHandle), &exitCode)) {
        handle.hasExitStatus = true;
        handle.exitCode = 0;
        handle.termSignal = 0;
        handle.errorMessage =
            "GetExitCodeProcess failed with error " + std::to_string(GetLastError()) + ".";
        CloseProcessHandle(handle);
        return false;
    }

    if (exitCode != STILL_ACTIVE) {
        handle.hasExitStatus = true;
        handle.exitCode = static_cast<int>(exitCode);
        handle.termSignal = 0;
        handle.errorMessage.clear();
        CloseProcessHandle(handle);
        return false;
    }

    return true;
}

void CloseProcessHandle(ProcessHandle& handle) {
    if (handle.nativeHandle != nullptr) {
        CloseHandle(static_cast<HANDLE>(handle.nativeHandle));
        handle.nativeHandle = nullptr;
    }
    handle.processId = 0;
}

std::filesystem::path GetExecutablePath() {
    wchar_t buffer[MAX_PATH] = {};
    const DWORD length = GetModuleFileNameW(nullptr, buffer, MAX_PATH);
    if (length == 0) {
        return GetExecutableDirectory() / "refii-launcher.exe";
    }
    return std::filesystem::path(buffer);
}

std::filesystem::path GetExecutableDirectory() {
    wchar_t buffer[MAX_PATH] = {};
    const DWORD length = GetModuleFileNameW(nullptr, buffer, MAX_PATH);
    if (length == 0) {
        return std::filesystem::current_path();
    }
    return std::filesystem::path(buffer).parent_path();
}

}  // namespace Platform
