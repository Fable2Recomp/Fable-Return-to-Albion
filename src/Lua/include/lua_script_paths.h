#pragma once

#include <algorithm>
#include <filesystem>
#include <string>
#include <string_view>

namespace refii::lua {

inline bool StartsWithLuaPathPrefix(std::string_view value, std::string_view prefix) {
    return value.substr(0, prefix.size()) == prefix;
}

inline std::string CanonicalizeRetailLuaRelativeString(std::string_view value) {
    std::string normalized(value);
    std::replace(normalized.begin(), normalized.end(), '\\', '/');

    struct PrefixRewrite {
        std::string_view from;
        std::string_view to;
    };

    constexpr PrefixRewrite kRewrites[] = {
        {"override/gamescripts_r/scripts/", "override/gamescripts/scripts/"},
        {"override/scripts_r/", "override/scripts/"},
        {"data/scripts_r/", "data/scripts/"},
        {"gamescripts_r/scripts/", "gamescripts/scripts/"},
        {"scripts_r/", "scripts/"},
    };

    for (const PrefixRewrite& rewrite : kRewrites) {
        if (StartsWithLuaPathPrefix(normalized, rewrite.from)) {
            return std::string(rewrite.to) + normalized.substr(rewrite.from.size());
        }
    }

    return normalized;
}

inline std::filesystem::path CanonicalizeRetailLuaRelativePath(
    const std::filesystem::path& path) {
    return std::filesystem::path(CanonicalizeRetailLuaRelativeString(path.generic_string()));
}

}  // namespace refii::lua
