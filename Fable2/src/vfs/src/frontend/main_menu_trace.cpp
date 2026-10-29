#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

std::mutex& RefiiMainMenuBlockerTraceMutex() {
  static std::mutex mutex;
  return mutex;
}

RefiiMainMenuBlockerTrace& RefiiMainMenuBlockerTraceState() {
  static RefiiMainMenuBlockerTrace trace;
  return trace;
}

void RecordRefiiMainMenuBlockerEvent(std::string_view kind,
                                     std::string message) {
  if (message.empty()) {
    return;
  }

  std::lock_guard<std::mutex> lock(RefiiMainMenuBlockerTraceMutex());
  auto& trace = RefiiMainMenuBlockerTraceState();
  const auto now = std::chrono::steady_clock::now();
  if (kind == "texture_hash") {
    if (trace.last_texture_hash.empty()) {
      trace.last_texture_hash = std::move(message);
    } else if (trace.last_texture_hash.find(message) == std::string::npos) {
      if (trace.last_texture_hash.size() < 1024) {
        trace.last_texture_hash += " | ";
        trace.last_texture_hash += message;
      } else {
        trace.last_texture_hash = std::move(message);
      }
    }
    trace.texture_hash_at = now;
  } else if (kind == "texture_selection") {
    trace.last_texture_selection = std::move(message);
    trace.texture_selection_at = now;
  } else if (kind == "wrapper_view") {
    trace.last_wrapper_view = std::move(message);
    trace.wrapper_view_at = now;
  } else if (kind == "wrapper_slot36") {
    trace.last_wrapper_slot36 = std::move(message);
    trace.wrapper_slot36_at = now;
  } else if (kind == "texture_parser") {
    trace.last_texture_parser = std::move(message);
    trace.texture_parser_at = now;
  } else if (kind == "texture_stream_build") {
    trace.last_texture_stream_build = std::move(message);
    trace.texture_stream_build_at = now;
  } else if (kind == "loading_frame") {
    trace.last_loading_frame = std::move(message);
    trace.loading_frame_at = now;
  }
}

std::string FormatRefiiMainMenuBlockerTraceForLog() {
  std::lock_guard<std::mutex> lock(RefiiMainMenuBlockerTraceMutex());
  const auto& trace = RefiiMainMenuBlockerTraceState();
  const auto now = std::chrono::steady_clock::now();
  auto format_entry =
      [&](std::string_view label,
          const std::string& value,
          std::chrono::steady_clock::time_point timestamp) {
        if (value.empty()) {
          return fmt::format("{}=none", label);
        }
        const auto age_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                                now - timestamp)
                                .count();
        return fmt::format("{}=[{} age_ms={}]", label, value, age_ms);
      };
  return fmt::format("{} {} {} {} {} {} {}",
                     format_entry("hash", trace.last_texture_hash,
                                  trace.texture_hash_at),
                     format_entry("texture", trace.last_texture_selection,
                                  trace.texture_selection_at),
                     format_entry("wrapper", trace.last_wrapper_view,
                                  trace.wrapper_view_at),
                     format_entry("slot36", trace.last_wrapper_slot36,
                                  trace.wrapper_slot36_at),
                     format_entry("parser", trace.last_texture_parser,
                                  trace.texture_parser_at),
                     format_entry("texture_build",
                                  trace.last_texture_stream_build,
                                  trace.texture_stream_build_at),
                     format_entry("loading_frame",
                                  trace.last_loading_frame,
                                  trace.loading_frame_at));
}
}  // namespace detail
}  // namespace refii::vfs
