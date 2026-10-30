// refii - ReXGlue Recompiled Project
//
// This file is yours to edit. 'rexglue migrate' will NOT overwrite it.
// Customize your app by overriding virtual hooks from rex::ReXApp.

#pragma once

// NOT generated/refii_init.h. That header is emitted from the player's own game image and
// does not exist on a build machine; the payload's image descriptor arrives through the
// loader instead.
#include "refii_payload/payload.h"

// RefiiGetAppRoot — the install directory the host states, rather than the executable
// folder the SDK would otherwise infer.
#include "refii_host.h"

#include <achievement/achievement_runtime.h>
#include <fmt/format.h>
#include <rex/cvar.h>
#include <rex/filesystem.h>
#include <rex/logging.h>
#include <rex/rex_app.h>
#include <rex/system/kernel_state.h>
#include <rex/system/user_module.h>
#include <rex/input/device_assignment.h>
#include <rex/input/input_system.h>
#include <rex/input/nop/nop_input_driver.h>

#include "host_audio.h"
#include "host_input.h"

#include <algorithm>
#include <span>
#include <cctype>
#include <cstdlib>
#include <fstream>
#include <memory>
#include <string>
#include <vector>

namespace {
inline void RefiiEnsureDirectoryExists(const std::filesystem::path &path) {
  if (path.empty()) {
    return;
  }

  std::error_code error;
  std::filesystem::create_directories(path, error);
}

/// Built-in `rexnet.toml`, written only when the install has none. Mirrors
/// Fable2/metadata/rexnet.toml; that file carries the reasoning for each value.
inline void RefiiWriteDefaultRexNetConfig(const std::filesystem::path &path) {
  std::error_code ec;
  if (std::filesystem::exists(path, ec)) {
    return;
  }
  std::ofstream out(path, std::ios::binary | std::ios::trunc);
  if (!out.is_open()) {
    return;
  }
  out << R"(# Written by the refii launcher on first launch. A shipped
# Fable2/metadata/rexnet.toml wins and replaces this once it is newer.

[game]
session_model = "private-invite"
# XRNM runs its own reliability; the game plane must stay unreliable.
game_channel = "unreliable"
# 0 = take the id from the XEX header (0x4D5307F1).
advertised_title_id = 0

# Ambient "Connected" tier. The loader defaults this off, so a title that
# wants passive presence has to say so here.
[shard]
enabled = true
cap = 255
surface_as_friends = true
)";
}

inline void
RefiiEnsureRuntimeDirectories(const rex::PathConfig &paths) {
  RefiiEnsureDirectoryExists(paths.config_path.parent_path());

  std::filesystem::path gameBase = paths.game_data_root;
  if (gameBase.filename() == "game") {
    gameBase = gameBase.parent_path();
  }

  RefiiEnsureDirectoryExists(gameBase);
  RefiiEnsureDirectoryExists(paths.user_data_root);
  RefiiEnsureDirectoryExists(paths.update_data_root);
  RefiiEnsureDirectoryExists(paths.cache_root);

  // Seed per-title metadata (rexnet.toml, ...) into the runtime, on every launch.
  //
  // Runtime::FindMetadataPath only searches under game_data_root, so an
  // install that never receives these files silently runs with all
  // per-title configuration inactive -- RexNet shard opt-in,
  // advertised_title_id, presence mappings -- and the sole symptom is one
  // warning at startup. That cost a debugging round on 2026-07-21, where a
  // packaged instance and a build-tree instance behaved differently for no
  // visible reason, and again on 2026-10-07 when the zip package turned out
  // never to have had the file: this ran only under packagings since removed.
  const std::filesystem::path runtime_metadata = gameBase / "metadata";
  RefiiEnsureDirectoryExists(runtime_metadata);

  // Shipped in the title's own folder (assets/<title>/metadata); in an exported app that IS the
  // runtime folder, and there is nothing to copy.
  const std::filesystem::path bundled_metadata =
      rex::filesystem::GetExecutableFolder() / "assets" / gameBase.filename() / "metadata";
  std::error_code ec;
  if (std::filesystem::exists(bundled_metadata, ec) &&
      !std::filesystem::equivalent(bundled_metadata, runtime_metadata, ec)) {
    for (const auto &entry :
         std::filesystem::directory_iterator(bundled_metadata, ec)) {
      if (ec) {
        break;
      }
      std::error_code entry_ec;
      if (!entry.is_regular_file(entry_ec) || entry_ec) {
        continue;
      }
      const std::filesystem::path dst =
          runtime_metadata / entry.path().filename();
      // Copy when absent, and refresh when the shipped copy is newer. These
      // are shipped data rather than user settings (user config lives in
      // the title's config file), so an updated build must not be shadowed by a stale copy
      // left over from a previous install.
      std::error_code stat_ec;
      bool stale = !std::filesystem::exists(dst, stat_ec);
      if (!stale) {
        const auto src_time =
            std::filesystem::last_write_time(entry.path(), stat_ec);
        const auto dst_time = std::filesystem::last_write_time(dst, stat_ec);
        stale = !stat_ec && src_time > dst_time;
      }
      if (stale) {
        std::error_code copy_ec;
        std::filesystem::copy_file(
            entry.path(), dst,
            std::filesystem::copy_options::overwrite_existing, copy_ec);
      }
    }
  }

  // A package that shipped no metadata at all still needs the title's config,
  // or ambient shards stay off and nothing forms on the LAN.
  RefiiWriteDefaultRexNetConfig(runtime_metadata / "rexnet.toml");
}

// The title's XDBF resource — the section named by the title id inside the executable
// module — handed to the achievement seam once, for Godot to parse. The definitions, the
// strings and the icons are all in it; nothing native reads them any more.
inline void RefiiPublishTitleXdbf(rex::system::KernelState *kernel_state) {
  if (kernel_state == nullptr) {
    return;
  }

  const rex::system::object_ref<rex::system::UserModule> module =
      kernel_state->GetExecutableModule();
  if (!module) {
    return;
  }

  uint32_t resource_data = 0;
  uint32_t resource_size = 0;
  if (XFAILED(module->GetSection(fmt::format("{:08X}", module->title_id()).c_str(),
                                 &resource_data, &resource_size)) ||
      resource_data == 0 || resource_size == 0) {
    return;
  }

  const uint8_t *bytes = kernel_state->memory()->TranslateVirtual(resource_data);
  refii::achievement::SetTitleXdbf(
      std::span<const uint8_t>(bytes, static_cast<size_t>(resource_size)));
}
} // namespace

class RefiiApp : public rex::ReXApp {
public:
  using rex::ReXApp::ReXApp;

  static std::unique_ptr<rex::ui::WindowedApp>
  Create(rex::ui::WindowedAppContext &ctx) {
    return std::unique_ptr<RefiiApp>(
        new RefiiApp(ctx, "refii", refii::payload::ImageConfig()));
  }

  void OnPreSetup(rex::RuntimeConfig &config) override {
    // Embedded, the HOST owns input.
    //
    // The SDK's default input system builds an SDL driver whose subsystems are
    // initialised in OnWindowAvailable. An embedded guest deliberately creates no
    // window, so that never fires and every XInput entry point is left uninitialised —
    // which aborted the process the moment Fable 2 polled controller capabilities during
    // startup. Swapping the whole input system is cleaner than teaching the SDL driver
    // to be windowless, and it is the same shape as the window: Godot reads the real
    // device and the guest is served a snapshot.
    //
    // Desktop retains the SDK's NOP fallback. Android gameplay uses the host driver
    // alone: NOP otherwise contributes a second always-successful user-zero state
    // and defeats exact single-driver copy receipts. Tool mode retains its fallback.
    config.input_factory =
        [](bool tool_mode) -> std::unique_ptr<rex::system::IInputSystem> {
      // X_STATUS_SUCCESS is a macro that casts to an unqualified X_STATUS, so it can
      // only be used where that name is visible.
      using namespace rex;

      auto input = std::make_unique<rex::input::InputSystem>(nullptr);
      if (!tool_mode) {
        auto host_driver =
            std::make_unique<refii::host::HostInputDriver>(nullptr, 0);
        if (host_driver->Setup() == X_STATUS_SUCCESS) {
#ifdef __ANDROID__
          rex::input::SetInputStateCopyObserver(input.get(),
              &refii::host::HostInputDriver::ObserveStateCopy, host_driver.get());
#endif
          input->AddDriver(std::move(host_driver));
        }
      }

#ifdef __ANDROID__
      if (tool_mode) {
        input->AddDriver(
            std::make_unique<rex::input::nop::NopInputDriver>(nullptr, 1));
      }
#else
      input->AddDriver(
          std::make_unique<rex::input::nop::NopInputDriver>(nullptr, 1));
#endif
      // THE POLICY IS NOT OPTIONAL. The v0.10 InputSystem answers every query
      // DEVICE_NOT_CONNECTED until an assignment maps device ordinals to guest users;
      // the SDK's own factory ends the same way. SlotAssignment: ordinal N feeds
      // user N, which is the contract HostInputDriver enumerates for.
      input->SetDeviceAssignment(std::make_unique<rex::input::SlotAssignment>());
      return input;
    };
    REXLOG_INFO("Refii: embedded — host-fed controller input installed");

    // Embedded, the HOST owns audio too — and for a reason beyond tidiness.
    //
    // ReXApp::SetupPresentation installs the SDL audio system BEFORE the headless
    // branch, so an embedded guest opened a real host audio device and played to it on
    // its own clock while Godot drew on another. That is not a latency problem to tune
    // out; it is two clocks.
    //
    // The guest's audio clock is the output device's CONSUMPTION rate: one semaphore
    // credit per frame consumed wakes the audio worker, which re-enters the guest's
    // render-driver callback, which mixes the next frame. Handing that credit to Godot
    // makes Godot the clock, which is the whole point — and is also why the device
    // cannot simply be closed to silence it: with nothing consuming, the guest's audio
    // thread stops being pumped and NLHAudio wedges at its ten-packet cap.
    config.audio_factory =
        [](rex::runtime::FunctionDispatcher* function_dispatcher)
        -> std::unique_ptr<rex::system::IAudioSystem> {
      return refii::host::HostAudioSystem::Create(function_dispatcher);
    };
    REXLOG_INFO("Refii: embedded — host-fed audio output installed");

    // No guest GPU at all: Godot draws. Left to the SDK, an empty graphics system defaults
    // gpu_plugin to "xenos" and dlopens librexgpu-xenosd.so from beside the executable, which
    // embedded is Godot's directory, and the boot fails.
    config.graphics.reset();
    REXLOG_INFO("Refii: embedded — no guest graphics system; the host draws");
  }

  bool OnNeedsImGuiDrawerWithoutPresenter() const override {
    return true;
  }


  void OnPostLaunchModule(rex::system::XThread *thread) override {
    (void)thread;
    if (runtime() != nullptr) {
      RefiiPublishTitleXdbf(runtime()->kernel_state());
    }
  }

  // Set game and update paths relative to game_directory.
  // The assets layout is: game_directory/game/ and game_directory/update/.
  // Accept both `<game_root>/` and `<game_root>/game` to keep launch args
  // forgiving.
  void OnConfigurePaths(rex::PathConfig &paths) override {
    // WHAT THE HOST STATED WINS, and it is checked first for that reason. Hosted by
    // Godot, GetExecutableFolder() is Godot's directory: the SDK's defaults put the
    // config, the saves and the cache beside whatever binary is hosting us, which in a
    // dev tree is the user's Godot install. The launcher writes the running title's
    // config at the app root and the guest has to read that same file — the host states
    // it whole, because its name is the title's and this runtime serves every title.
    if (const std::filesystem::path &configPath = RefiiGetConfigPath(); !configPath.empty()) {
      paths.config_path = configPath;
      RefiiEnsureDirectoryExists(paths.config_path.parent_path());
    }
    if (const std::filesystem::path &appRoot = RefiiGetAppRoot(); !appRoot.empty()) {
      if (paths.user_data_root.empty()) {
        paths.user_data_root = appRoot / "assets" / "save";
      }
      if (paths.cache_root.empty()) {
        paths.cache_root = appRoot / "cache";
      }
    }

    auto base = paths.game_data_root;
    if (base.empty()) {
      base = "assets";
    }
    if (base.filename() == "game" || base.filename() == "data") {
      base = base.parent_path();
    }

    if (base.is_relative() && !std::filesystem::exists(base)) {
      for (std::filesystem::path probe :
           {std::filesystem::current_path(),
            rex::filesystem::GetExecutableFolder()}) {
        bool resolved = false;
        for (int i = 0; i < 8 && !probe.empty(); ++i) {
          const std::filesystem::path candidate = probe / base;
          if (std::filesystem::exists(candidate)) {
            base = candidate;
            resolved = true;
            break;
          }
          const std::filesystem::path parent = probe.parent_path();
          if (parent == probe) {
            break;
          }
          probe = parent;
        }
        if (resolved) {
          break;
        }
      }
    }

    if (base.filename() == "game") {
      base = base.parent_path();
    }
#if defined(__ANDROID__)
    // A privately imported Android dump can be a flat directory of any name,
    // with default.xex/default.xexp beside data/. Preserve the normal assets/
    // game + assets/update layout when the supplied base contains no XEX.
    std::error_code direct_game_error;
    const bool direct_game_root = std::filesystem::is_regular_file(
        base / "default.xex", direct_game_error);
    paths.game_data_root = direct_game_root ? base : base / "game";
    if (paths.update_data_root.empty()) {
      paths.update_data_root = direct_game_root ? base : base / "update";
    }
#else
    paths.game_data_root = base / "game";
    if (paths.update_data_root.empty()) {
      paths.update_data_root = base / "update";
    }
#endif

    // EVERY LAUNCH. The guest reads rexnet.toml from under game_data_root and nothing else
    // puts one there; while this ran only under the AppImage and Flatpak packagings (since
    // removed), the zip package and the dev tree booted without it and XNetStartup logged
    // "no rexnet.toml found for this title" on every run.
    RefiiEnsureRuntimeDirectories(paths);
  }
};
