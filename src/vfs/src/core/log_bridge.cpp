// refii — the host side of the payload's log bridge.
//
// The payload cannot hold an spdlog::logger* or pass an fmt arg pack: it is compiled on the
// player's machine, and on Windows by a compiler whose C++ ABI is not ours. So the shadowed
// rex/logging/macros.h formats payload-side and calls these three C entries instead.
//
// ONE SINK, ONE FILE. The payload's lines land in the host's own logger, at the same
// category and level, with the payload's __FILE__/__LINE__ preserved — the point of
// bridging rather than giving the payload its own spdlog is that the log stays single.
//
// Lives in refii_vfs rather than the SDK: the payload already links it, and this needs no
// shadow source to be built.

#include <cstddef>
#include <cstdint>

#include <rex/logging.h>

namespace {

spdlog::logger* LoggerFor(uint16_t category) {
  return ::rex::GetLoggerRaw(::rex::LogCategoryId(category));
}

}  // namespace

extern "C" int32_t refii_bridge_log_should(uint16_t category, int32_t level) {
  spdlog::logger* logger = LoggerFor(category);
  return logger != nullptr &&
                 logger->should_log(static_cast<spdlog::level::level_enum>(level))
             ? 1
             : 0;
}

extern "C" void refii_bridge_log_write(uint16_t category, int32_t level, const char* file,
                                       int32_t line, const char* function,
                                       const char* message, size_t length) {
  spdlog::logger* logger = LoggerFor(category);
  if (logger == nullptr || message == nullptr) {
    return;
  }

  // "{}" and the text as an ARGUMENT, never as the format string: the payload has already
  // formatted, and a message containing braces would otherwise be parsed a second time.
  logger->log(spdlog::source_loc{file, static_cast<int>(line), function},
              static_cast<spdlog::level::level_enum>(level), "{}",
              std::string_view(message, length));
}

extern "C" uint16_t refii_bridge_log_register_category(const char* name) {
  return ::rex::RegisterLogCategory(name).id;
}
