// refii - the ReXGlue code generator as a library. See the header.
#include <refii_recompiler/recompiler.h>

#include <atomic>
#include <chrono>
#include <cstring>
#include <exception>
#include <filesystem>
#include <memory>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include <fmt/format.h>
#include <spdlog/sinks/callback_sink.h>
#include <spdlog/spdlog.h>

#include <rex/codegen/manifest.h>
#include <rex/codegen/progress_reporter.h>
#include <rex/codegen/project_recompiler.h>
#include <rex/logging.h>
#include <rex/result.h>
#include <rex/version.h>

#if defined(_WIN32)
#define REFII_RECOMPILER_EXPORT __declspec(dllexport)
#else
#define REFII_RECOMPILER_EXPORT __attribute__((visibility("default")))
#endif

namespace {

namespace fs = std::filesystem;

// Thrown out of the generator when the callback asks for a stop. The SDK's codegen is
// single-threaded and holds its state in RAII containers and streams, so unwinding
// through it leaves nothing but partial output files, which the caller deletes.
struct StopRequested {};

// Forwards the SDK's progress interface to the C callback. Every call arrives on the
// thread that drives the generator, which is the thread that called codegen.
class CallbackReporter final : public rex::codegen::ProgressReporter {
 public:
  CallbackReporter(refii_recompiler_progress_fn progress, void* user,
                   std::atomic<bool>* stop_requested)
      : progress_(progress), user_(user), stop_requested_(stop_requested) {}

  void binaryInfo(const rex::codegen::BinaryInfo& info) override {
    Emit(REFII_RECOMPILER_EVENT_BINARY,
         fmt::format("{} title={:08X} v{}.{}.{}.{}", info.name, info.title_id,
                     info.version_major, info.version_minor, info.version_build,
                     info.version_qfe),
         0, 0);
  }

  void moduleStarted(std::string_view name, std::size_t index, std::size_t total) override {
    Emit(REFII_RECOMPILER_EVENT_MODULE_STARTED, std::string(name), static_cast<int32_t>(index),
         static_cast<int32_t>(total));
  }

  void phaseChanged(std::string_view name) override {
    Emit(REFII_RECOMPILER_EVENT_PHASE, std::string(name), 0, 0);
  }

  void moduleFinished(std::chrono::milliseconds elapsed) override {
    Emit(REFII_RECOMPILER_EVENT_MODULE_FINISHED, std::string(),
         static_cast<int32_t>(elapsed.count()), 0);
  }

  void projectPhaseStarted(std::string_view name) override {
    Emit(REFII_RECOMPILER_EVENT_PROJECT_PHASE, std::string(name), 0, 0);
  }

  void projectPhaseFinished() override {
    Emit(REFII_RECOMPILER_EVENT_PROJECT_PHASE_FINISHED, std::string(), 0, 0);
  }

  void Emit(int32_t event, const std::string& detail, int32_t index, int32_t total) {
    if (progress_ && progress_(user_, event, detail.c_str(), index, total) != 0) {
      stop_requested_->store(true);
    }
    // A stop asked for from a log line is picked up here, at the next event the generator
    // can be unwound from.
    if (stop_requested_->load()) {
      throw StopRequested{};
    }
  }

 private:
  refii_recompiler_progress_fn progress_;
  void* user_;
  std::atomic<bool>* stop_requested_;
};

// The generator's log lines, delivered through the same callback for the length of one
// run.
//
// THE CALLER OWNS THE PROCESS'S LOGGING, not this library. The launcher runs codegen
// before the guest is started, and the guest's own startup is what configures the
// runtime's logging for the process (rex_app's InitLogging, which rebuilds every logger
// from scratch). So this only does what that startup can undo: it brings the category
// loggers into existence with InitLoggingEarly, which is idempotent and is the same
// first step the SDK's app takes, swaps each logger's sinks for a callback sink while
// the generator runs, and puts the originals back after. It never calls InitLogging or
// ShutdownLogging -- the latter clears the category registry out from under every
// REXLOG_CAT site in the process.
class ScopedLogCapture {
 public:
  ScopedLogCapture(refii_recompiler_progress_fn progress, void* user,
                   std::atomic<bool>* stop_requested) {
    if (!progress) {
      return;
    }

    rex::InitLoggingEarly();

    // NOT THROWN FROM HERE: spdlog catches what a sink throws and reports it to its error
    // handler, so a stop asked for on a log line is recorded and raised at the next event.
    sink_ = std::make_shared<spdlog::sinks::callback_sink_mt>(
        [progress, user, stop_requested](const spdlog::details::log_msg& message) {
          auto level = spdlog::level::to_string_view(message.level);
          std::string line = fmt::format(
              "[{}] [{}] {}", std::string_view(level.data(), level.size()),
              std::string_view(message.logger_name.data(), message.logger_name.size()),
              std::string_view(message.payload.data(), message.payload.size()));
          if (progress(user, REFII_RECOMPILER_EVENT_LOG, line.c_str(), 0, 0) != 0) {
            stop_requested->store(true);
          }
        });
    sink_->set_level(spdlog::level::trace);

    spdlog::apply_all([this](std::shared_ptr<spdlog::logger> logger) {
      saved_.emplace_back(logger, logger->sinks());
      logger->sinks() = std::vector<spdlog::sink_ptr>{sink_};
    });
  }

  ~ScopedLogCapture() {
    for (auto& [logger, sinks] : saved_) {
      logger->sinks() = std::move(sinks);
    }
  }

  ScopedLogCapture(const ScopedLogCapture&) = delete;
  ScopedLogCapture& operator=(const ScopedLogCapture&) = delete;

 private:
  std::shared_ptr<spdlog::sinks::callback_sink_mt> sink_;
  std::vector<std::pair<std::shared_ptr<spdlog::logger>, std::vector<spdlog::sink_ptr>>> saved_;
};

void WriteError(char* error, size_t capacity, std::string_view message) {
  if (!error || capacity == 0) {
    return;
  }
  size_t length = message.size() < capacity - 1 ? message.size() : capacity - 1;
  std::memcpy(error, message.data(), length);
  error[length] = '\0';
}

// The body of `rexglue --force codegen <manifest>`: the manifest check, the load, the
// run and the summary line, minus the terminal.
rex::Result<void> Codegen(const fs::path& manifest_path, CallbackReporter& reporter) {
  if (!rex::codegen::ManifestConfig::IsManifest(manifest_path)) {
    return rex::Err<void>(rex::ErrorCategory::Config,
                          fmt::format("{} is not a manifest: no [project] section",
                                      manifest_path.generic_string()));
  }

  auto manifest = rex::codegen::ManifestConfig::Load(manifest_path);
  if (!manifest) {
    return rex::Err<void>(rex::ErrorCategory::Config,
                          fmt::format("Failed to load manifest {}",
                                      manifest_path.generic_string()));
  }

  rex::codegen::ProjectRecompiler recompiler(std::move(*manifest));
  rex::codegen::ProjectRecompilerOptions options{
      // --force: the install-time build is always a clean build into an empty directory,
      // and a generator that stops to ask about existing files has nobody to ask.
      .force = true,
      .reporter = &reporter,
      // The launcher deleted the output directory before calling; there is no stamp to
      // honour and a stale one must not skip the run.
      .ignoreStamp = true,
      // Same spelling the CLI folds into its fingerprint.
      .sdkVersion = REXGLUE_VERSION_FLOOR "-" REXGLUE_VERSION_CHANNEL,
  };

  auto result = recompiler.Run(options);
  if (!result) {
    return result;
  }

  REXLOG_INFO("Codegen summary: {} written, {} unchanged, {} deleted, {} module(s) up to date",
              recompiler.writtenFiles().size(), recompiler.unchangedFiles().size(),
              recompiler.deletedFiles().size(), recompiler.skippedModules().size());
  return rex::Ok();
}

}  // namespace

extern "C" {

REFII_RECOMPILER_EXPORT int32_t refii_recompiler_v1_abi_version(void) {
  return REFII_RECOMPILER_ABI_VERSION;
}

REFII_RECOMPILER_EXPORT const char* refii_recompiler_v1_sdk_version(void) {
  return REXGLUE_VERSION_STRING;
}

REFII_RECOMPILER_EXPORT int32_t refii_recompiler_v1_codegen(const char* manifest_path,
                                                            refii_recompiler_progress_fn progress,
                                                            void* user, char* error,
                                                            size_t error_capacity) {
  if (!manifest_path || !*manifest_path) {
    WriteError(error, error_capacity, "no manifest path was given");
    return REFII_RECOMPILER_FAILED;
  }

  // Exceptions stop here. The caller is managed code reached through P/Invoke, and a C++
  // exception crossing that boundary is a process abort with no message.
  try {
    std::atomic<bool> stop_requested{false};
    ScopedLogCapture capture(progress, user, &stop_requested);
    CallbackReporter reporter(progress, user, &stop_requested);

    auto result = Codegen(fs::path(manifest_path), reporter);
    if (!result) {
      WriteError(error, error_capacity, result.error().what());
      return REFII_RECOMPILER_FAILED;
    }
    return REFII_RECOMPILER_OK;
  } catch (const StopRequested&) {
    WriteError(error, error_capacity, "stopped");
    return REFII_RECOMPILER_STOPPED;
  } catch (const std::exception& exception) {
    WriteError(error, error_capacity, exception.what());
    return REFII_RECOMPILER_FAILED;
  } catch (...) {
    WriteError(error, error_capacity, "the generator raised an unknown exception");
    return REFII_RECOMPILER_FAILED;
  }
}

}  // extern "C"
