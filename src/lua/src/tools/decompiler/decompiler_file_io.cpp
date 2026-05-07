#include "tools/decompiler/decompiler_file_io.h"

#include <algorithm>
#include <cctype>
#include <fstream>
#include <iterator>

namespace refii::lua::internal {
namespace {

bool IsWindowsReservedName(std::string_view name) {
    if (name.empty()) {
        return false;
    }
    if (name == "con" || name == "prn" || name == "aux" || name == "nul" ||
        name == "clock$") {
        return true;
    }
    if ((name.size() == 4 && name[0] == 'c' && name[1] == 'o' && name[2] == 'm' &&
         std::isdigit(static_cast<unsigned char>(name[3])) != 0) ||
        (name.size() == 4 && name[0] == 'l' && name[1] == 'p' && name[2] == 't' &&
         std::isdigit(static_cast<unsigned char>(name[3])) != 0)) {
        return true;
    }
    return false;
}

std::string LowercaseAscii(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return value;
}

bool IsWindowsCompatibleFilename(std::string_view filename) {
    if (filename.empty()) {
        return false;
    }
    for (unsigned char ch : filename) {
        if (ch <= 0x1F || ch == '<' || ch == '>' || ch == ':' || ch == '"' || ch == '/' ||
            ch == '\\' || ch == '|' || ch == '?' || ch == '*') {
            return false;
        }
    }
    if (filename.back() == ' ' || filename.back() == '.') {
        return false;
    }
    return !IsWindowsReservedName(LowercaseAscii(std::string(filename)));
}

bool IsSafeRelativePath(const std::filesystem::path& path) {
    const std::filesystem::path candidate = path.relative_path().lexically_normal();
    if (candidate.empty()) {
        return false;
    }
    for (const auto& component : candidate) {
        const std::string part = component.generic_string();
        if (part.empty() || part == "." || part == ".." || !IsWindowsCompatibleFilename(part)) {
            return false;
        }
    }
    return true;
}

}  // namespace

bool ReadTextFile(const std::filesystem::path& path, std::string& text) {
    std::ifstream stream(path, std::ios::binary);
    if (!stream) {
        return false;
    }

    text.assign(std::istreambuf_iterator<char>(stream), std::istreambuf_iterator<char>());
    return stream.good() || stream.eof();
}

bool WriteTextFile(const std::filesystem::path& path, std::string_view text) {
    if (!IsSafeRelativePath(path)) {
        return false;
    }
    if (path.has_parent_path()) {
        std::error_code create_error;
        const std::filesystem::path parent = path.parent_path();
        if (!parent.empty()) {
            std::filesystem::create_directories(parent, create_error);
            if (create_error) {
                return false;
            }
        }
    }

    std::ofstream stream(path, std::ios::binary | std::ios::trunc);
    if (!stream) {
        return false;
    }

    stream.write(text.data(), static_cast<std::streamsize>(text.size()));
    return static_cast<bool>(stream);
}

}  // namespace refii::lua::internal
