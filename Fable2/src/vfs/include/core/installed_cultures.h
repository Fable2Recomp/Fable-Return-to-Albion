#pragma once

// The language folders this install ships, as the guest's XLip library takes them.
//
// `sub_82373E08` loads `game:\data\language\cultures.txt` (`sub_82BF2128`, rows parsed by
// `sub_82BF1E58`: code, display name, optional fallback) and registers `game:\data\language` as a
// root (`sub_82BEEC70`). `sub_82BEF360` enumerates the root's folders and keeps a folder only when
// `sub_82BF1268` finds its name among the codes (`sub_82BEF170`). A disc ships whatever subset it
// was mastered with — USA/Europe en-uk + cs-cz, hu-hu, ko-kr, pl-pl, sk-sk, zh-cht; the Spanish
// GOTY disc es-es alone — so every locale list here comes from the install, never a fixed set.
//
// Header-only: the host library and the payload's hooks both read it, and nothing crosses the
// module boundary but the inline code.

#include <algorithm>
#include <cctype>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <mutex>
#include <string>
#include <string_view>
#include <system_error>
#include <unordered_set>
#include <vector>

#include <rex/runtime.h>

namespace refii::vfs {

// The culture `sub_823728E0` remaps every language file to right after the component's own
// (`language\` -> `language\en-uk`, 0x82372AC4), and every cultures.txt row's last fallback.
inline constexpr std::string_view kFallbackCulture = "en-uk";

namespace installed_cultures_detail {

inline std::string Lower(std::string value) {
  std::ranges::transform(value, value.begin(), [](unsigned char ch) {
    return static_cast<char>(std::tolower(ch));
  });
  return value;
}

// UTF-16 with its 0xFEFF mark, as `sub_82BF0CE0` requires (the shipped file is big-endian);
// a file without one is read as 8-bit text. Only ASCII matters: codes are ASCII tags.
inline std::string DecodeCulturesText(const std::string& bytes) {
  if (bytes.size() >= 2) {
    const auto b0 = static_cast<unsigned char>(bytes[0]);
    const auto b1 = static_cast<unsigned char>(bytes[1]);
    const bool big = b0 == 0xFE && b1 == 0xFF;
    const bool little = b0 == 0xFF && b1 == 0xFE;
    if (big || little) {
      std::string text;
      text.reserve(bytes.size() / 2);
      for (size_t i = 2; i + 1 < bytes.size(); i += 2) {
        const auto hi = static_cast<unsigned char>(big ? bytes[i] : bytes[i + 1]);
        const auto lo = static_cast<unsigned char>(big ? bytes[i + 1] : bytes[i]);
        text.push_back(hi == 0 && lo < 0x80 ? static_cast<char>(lo) : '?');
      }
      return text;
    }
  }
  return bytes;
}

// The codes of `sub_82BF1E58`'s rows: blank and ';' lines skipped, fields split on tab or comma,
// a row needs its code and its display name.
inline std::unordered_set<std::string> ParseCultureCodes(const std::string& text) {
  std::unordered_set<std::string> codes;
  size_t start = 0;
  while (start <= text.size()) {
    size_t end = text.find_first_of("\r\n", start);
    if (end == std::string::npos) {
      end = text.size();
    }
    std::string_view line(text.data() + start, end - start);
    while (!line.empty() && (line.front() == ' ' || line.front() == '\t')) {
      line.remove_prefix(1);
    }
    if (!line.empty() && line.front() != ';') {
      const size_t code_end = line.find_first_of("\t,");
      if (code_end != std::string_view::npos && code_end > 0) {
        std::string_view rest = line.substr(code_end);
        while (!rest.empty() && (rest.front() == '\t' || rest.front() == ',')) {
          rest.remove_prefix(1);
        }
        if (!rest.empty()) {
          std::string code(line.substr(0, code_end));
          while (!code.empty() && code.back() == ' ') {
            code.pop_back();
          }
          if (!code.empty()) {
            codes.insert(Lower(std::move(code)));
          }
        }
      }
    }
    start = end + 1;
  }
  return codes;
}

inline std::filesystem::path LanguageRoot(const std::filesystem::path& game_root) {
  for (const std::filesystem::path& candidate :
       {game_root / "language", game_root / "data" / "language",
        game_root / "game" / "data" / "language"}) {
    std::error_code ec;
    if (std::filesystem::is_regular_file(candidate / "cultures.txt", ec)) {
      return candidate;
    }
  }
  return {};
}

// The install's culture folders: the fallback culture first when it ships (the guest's second
// remap), then the rest in name order. Empty when there is no cultures.txt to read.
inline std::vector<std::string> ReadInstalledCultures(const std::filesystem::path& game_root) {
  std::vector<std::string> installed;
  const std::filesystem::path language = LanguageRoot(game_root);
  if (language.empty()) {
    return installed;
  }

  std::ifstream file(language / "cultures.txt", std::ios::binary);
  const std::string bytes{std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>()};
  const std::unordered_set<std::string> codes = ParseCultureCodes(DecodeCulturesText(bytes));

  std::error_code ec;
  for (std::filesystem::directory_iterator it(language, ec), end; !ec && it != end;
       it.increment(ec)) {
    std::error_code type_ec;
    if (!it->is_directory(type_ec)) {
      continue;
    }
    std::string name = Lower(it->path().filename().string());
    if (codes.contains(name) &&
        std::find(installed.begin(), installed.end(), name) == installed.end()) {
      installed.push_back(std::move(name));
    }
  }

  std::ranges::sort(installed, [](const std::string& left, const std::string& right) {
    const bool left_fallback = left == kFallbackCulture;
    const bool right_fallback = right == kFallbackCulture;
    if (left_fallback != right_fallback) {
      return left_fallback;
    }
    return left < right;
  });
  return installed;
}

}  // namespace installed_cultures_detail

// The install's culture folders, read once the runtime names its game data root. A copy: the
// list is filled under the lock the first time a root is known, and read by any thread.
inline std::vector<std::string> InstalledLanguageCultures() {
  static std::mutex mutex;
  static std::vector<std::string> cultures;
  static bool resolved = false;

  std::lock_guard<std::mutex> lock(mutex);
  if (!resolved) {
    const auto* runtime = rex::Runtime::instance();
    if (runtime != nullptr && !runtime->game_data_root().empty()) {
      cultures = installed_cultures_detail::ReadInstalledCultures(runtime->game_data_root());
      resolved = true;
    }
  }
  return cultures;
}

inline bool IsInstalledLanguageCulture(std::string_view culture) {
  const std::vector<std::string> installed = InstalledLanguageCultures();
  return std::find(installed.begin(), installed.end(), culture) != installed.end();
}

}  // namespace refii::vfs
