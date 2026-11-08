// A phone's saves follow its profile (xam_net.cpp's XNetStartup).
//
// The content root keeps a folder of saves per profile XUID, and the XUID comes from the
// RexNet identity key: B13EBABEBABEBABE with RexNet off, E0... with it on, and a new one for
// every new key (a reinstall, another phone, an imported save). Turning RexNet on or off, or a
// new key, left a phone's heroes in a folder its profile no longer reads, so they looked gone
// or went back to an older save.
//
// So once the profile is settled, a profile that was saved to less recently than another one
// (or never) takes a copy of the profile folder saved to most recently: every file copied, and
// each save header's XUID (XCONTENT_AGGREGATE_DATA::xuid, big-endian, once per header)
// rewritten to the new one. The source stays as it was, and the profile's own earlier saves
// are moved aside to `.<XUID>.replaced-<time>` (the newest three kept), a name nothing reads.
// The profile's own settings folder (content_root/title/profile/<name>) is copied the same way
// when it is missing. Renaming the display name changes neither: both follow the key.
#pragma once

#include <rex/platform.h>

#if REX_PLATFORM_ANDROID
#include <algorithm>
#include <array>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <optional>
#include <string>
#include <vector>

#include <fmt/format.h>

#include <rex/logging/macros.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xam/content_manager.h>
#include <rex/system/xam/user_profile.h>

namespace rex::kernel::xam::android_saves {

namespace fs = std::filesystem;

// How many of a profile's replaced save folders are kept.
constexpr size_t kReplacedKept = 3;

inline std::optional<uint64_t> XuidOfFolder(const fs::path& folder) {
  const std::string name = folder.filename().string();
  if (name.size() != 16) {
    return std::nullopt;
  }
  uint64_t xuid = 0;
  for (char c : name) {
    int digit;
    if (c >= '0' && c <= '9') {
      digit = c - '0';
    } else if (c >= 'A' && c <= 'F') {
      digit = c - 'A' + 10;
    } else if (c >= 'a' && c <= 'f') {
      digit = c - 'a' + 10;
    } else {
      return std::nullopt;
    }
    xuid = (xuid << 4) | static_cast<uint64_t>(digit);
  }
  return xuid;
}

// When `profile` was last saved to, or nullopt when it has no hero save header under
// `profile/<title>/Headers`. Saving rewrites a slot's files but its header only when the slot
// is made, so the time is the newest file outside Headers.
inline std::optional<fs::file_time_type> LastSaved(const fs::path& profile, const std::string& title) {
  std::error_code ec;
  const fs::path saves = profile / title;
  bool has_header = false;
  for (fs::recursive_directory_iterator it(saves / "Headers", ec), end; !ec && !has_header && it != end;
       it.increment(ec)) {
    has_header = it->is_regular_file(ec) && it->path().extension() == ".header";
  }
  if (!has_header) {
    return std::nullopt;
  }
  ec.clear();
  std::optional<fs::file_time_type> newest;
  for (fs::recursive_directory_iterator it(saves, ec), end; !ec && it != end; it.increment(ec)) {
    if (it.depth() == 0 && it->path().filename() == "Headers") {
      it.disable_recursion_pending();
    } else if (it->is_regular_file(ec)) {
      const auto written = it->last_write_time(ec);
      if (!ec && (!newest || written > *newest)) {
        newest = written;
      }
    }
  }
  return newest.value_or(fs::file_time_type::min());
}

inline std::array<uint8_t, 8> BigEndian(uint64_t xuid) {
  std::array<uint8_t, 8> bytes{};
  for (int i = 0; i < 8; ++i) {
    bytes[i] = static_cast<uint8_t>(xuid >> (56 - 8 * i));
  }
  return bytes;
}

// `bytes` with its one copy of `from` (big-endian) replaced by `to`; false, and unchanged,
// unless `from` occurs exactly once.
inline bool RewriteXuid(std::vector<uint8_t>& bytes, uint64_t from, uint64_t to) {
  const auto old_bytes = BigEndian(from);
  const auto found = std::search(bytes.begin(), bytes.end(), old_bytes.begin(), old_bytes.end());
  if (found == bytes.end() ||
      std::search(found + 1, bytes.end(), old_bytes.begin(), old_bytes.end()) != bytes.end()) {
    return false;
  }
  const auto new_bytes = BigEndian(to);
  std::copy(new_bytes.begin(), new_bytes.end(), found);
  return true;
}

// Copies the tree `from` into `to` (created), rewriting each `.header`'s XUID and keeping each
// file's time. Returns how many files were copied, or nullopt on the first failure.
inline std::optional<int> CopyProfile(const fs::path& from, const fs::path& to, uint64_t from_xuid,
                                      uint64_t to_xuid, int& unrewritten) {
  std::error_code ec;
  fs::create_directories(to, ec);
  if (ec) {
    return std::nullopt;
  }
  int files = 0;
  // A folder's time is set after everything in it is written, which would move it again. The
  // title lists a profile's saves newest first by their folders' times (content_manager.cpp's
  // ListContent), so a copy left with every folder at the time of the copy shuffled the order.
  std::vector<std::pair<fs::path, fs::file_time_type>> folders;
  for (fs::recursive_directory_iterator it(from, ec), end; it != end; it.increment(ec)) {
    if (ec) {
      return std::nullopt;
    }
    const fs::path target = to / fs::relative(it->path(), from, ec);
    if (ec) {
      return std::nullopt;
    }
    if (it->is_directory(ec)) {
      fs::create_directories(target, ec);
      if (!ec) {
        folders.emplace_back(target, it->last_write_time(ec));
      }
      if (ec) {
        return std::nullopt;
      }
      continue;
    } else if (it->path().extension() == ".header") {
      std::ifstream in(it->path(), std::ios::binary);
      std::vector<uint8_t> bytes((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
      if (!in.good() && !in.eof()) {
        return std::nullopt;
      }
      if (!RewriteXuid(bytes, from_xuid, to_xuid)) {
        ++unrewritten;
      }
      std::ofstream out(target, std::ios::binary | std::ios::trunc);
      out.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
      if (!out.good()) {
        return std::nullopt;
      }
      ++files;
    } else {
      fs::copy_file(it->path(), target, fs::copy_options::skip_existing, ec);
      ++files;
    }
    if (ec) {
      return std::nullopt;
    }
    fs::last_write_time(target, it->last_write_time(ec), ec);
    ec.clear();
  }
  // Deepest first, so setting a folder's time does not move its parent's again.
  for (auto folder = folders.rbegin(); folder != folders.rend(); ++folder) {
    fs::last_write_time(folder->first, folder->second, ec);
    ec.clear();
  }
  return files;
}

// Removes all but the newest kReplacedKept of `xuid`'s replaced save folders under `root`.
inline void PruneReplaced(const fs::path& root, uint64_t xuid) {
  std::error_code ec;
  const std::string prefix = fmt::format(".{:016X}.replaced-", xuid);
  std::vector<fs::path> replaced;
  for (fs::directory_iterator it(root, ec), end; !ec && it != end; it.increment(ec)) {
    if (it->path().filename().string().rfind(prefix, 0) == 0) {
      replaced.push_back(it->path());
    }
  }
  // The suffix is seconds since the epoch at a fixed width, so names sort by age.
  std::sort(replaced.begin(), replaced.end());
  for (size_t i = 0; i + kReplacedKept < replaced.size(); ++i) {
    fs::remove_all(replaced[i], ec);
  }
}

// Gives the profile `xuid`, whose settings folder is `profile_settings`
// (ContentManager::ResolveGameUserContentPath), the saves of the profile last saved to, when
// that is another profile.
inline void Adopt(const fs::path& profile_settings, uint64_t xuid) {
  std::error_code ec;
  const fs::path title_folder = profile_settings.parent_path().parent_path();
  const fs::path root = title_folder.parent_path();
  const std::string title = title_folder.filename().string();
  const fs::path mine = root / fmt::format("{:016X}", xuid);
  const auto mine_saved = LastSaved(mine, title);

  fs::path source;
  uint64_t source_xuid = 0;
  std::optional<fs::file_time_type> source_saved;
  for (fs::directory_iterator it(root, ec), end; !ec && it != end; it.increment(ec)) {
    const auto other = XuidOfFolder(it->path());
    if (!other || *other == xuid || !it->is_directory(ec)) {
      continue;
    }
    const auto saved = LastSaved(it->path(), title);
    if (saved && (!source_saved || *saved > *source_saved)) {
      source = it->path();
      source_xuid = *other;
      source_saved = saved;
    }
  }

  // A copy keeps its files' times, so a profile that took a copy is no longer older than its
  // source, and only a later save elsewhere moves the saves again.
  if (source_saved && (!mine_saved || *source_saved > *mine_saved)) {
    // Staged beside the root's profiles under a name that is not a XUID, then moved into
    // place: a failure part way leaves no half profile to be read as this one's.
    const fs::path staging = root / fmt::format(".{:016X}.adopting", xuid);
    fs::remove_all(staging, ec);
    int unrewritten = 0;
    const auto copied = CopyProfile(source, staging, source_xuid, xuid, unrewritten);
    fs::path replaced;
    bool placed = false;
    if (copied) {
      if (fs::exists(mine, ec)) {
        const auto now = std::chrono::duration_cast<std::chrono::seconds>(
            std::chrono::system_clock::now().time_since_epoch());
        replaced = root / fmt::format(".{:016X}.replaced-{:012}", xuid, now.count());
        fs::rename(mine, replaced, ec);
      }
      if (!ec) {
        fs::rename(staging, mine, ec);
        placed = !ec;
        if (!placed && !replaced.empty()) {
          fs::rename(replaced, mine, ec);
          replaced.clear();
        }
      }
    }
    fs::remove_all(staging, ec);
    if (placed) {
      REXKRNL_INFO("[saves] profile {:016X} {}: copied {} file(s) from {:016X}{}{}", xuid,
                   mine_saved ? "was saved to before another" : "had no saves", *copied, source_xuid,
                   replaced.empty() ? std::string()
                                    : fmt::format(", its own kept in {}", replaced.filename().string()),
                   unrewritten ? fmt::format(", {} header(s) without one XUID kept as they were",
                                             unrewritten)
                               : std::string());
      PruneReplaced(root, xuid);
    } else {
      REXKRNL_WARN("[saves] profile {:016X}: copying {:016X}'s newer saves failed, nothing changed",
                   xuid, source_xuid);
    }
  }

  // The profile's settings (controls, brightness): the newest other profile's, when it has none.
  if (!fs::exists(profile_settings, ec)) {
    fs::path newest_settings;
    std::optional<fs::file_time_type> newest_time;
    for (fs::directory_iterator it(profile_settings.parent_path(), ec), end; !ec && it != end;
         it.increment(ec)) {
      if (it->path() == profile_settings || !it->is_directory(ec)) {
        continue;
      }
      const auto written = it->last_write_time(ec);
      if (!ec && (!newest_time || written > *newest_time)) {
        newest_settings = it->path();
        newest_time = written;
      }
    }
    if (!newest_settings.empty()) {
      fs::copy(newest_settings, profile_settings, fs::copy_options::recursive, ec);
      REXKRNL_INFO("[saves] profile settings '{}' copied from '{}'{}",
                   profile_settings.filename().string(), newest_settings.filename().string(),
                   ec ? fmt::format(" (failed: {})", ec.message()) : std::string());
    }
  }
}

// Once a process, when the guest starts its network stack: by then the profile has its
// RexNet identity, and the title has not yet listed its saves.
inline void AdoptForSignedInProfile(system::KernelState* kernel_state) {
  static bool done = false;
  if (done || kernel_state == nullptr || kernel_state->user_profile() == nullptr ||
      kernel_state->content_manager() == nullptr) {
    return;
  }
  done = true;
  Adopt(kernel_state->content_manager()->ResolveGameUserContentPath(),
        kernel_state->user_profile()->xuid());
}

}  // namespace rex::kernel::xam::android_saves
#endif  // REX_PLATFORM_ANDROID
