// refii — the hosted guest's lifecycle: the memory probe, both boot paths, the step, the
// shutdown, and the title's part in each (TitleCallbacks, host_core.h).

#include <algorithm>
#include <atomic>
#include <cctype>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <csignal>
#ifndef _WIN32
#include <dlfcn.h>
#include <execinfo.h>
#include <fcntl.h>
#include <sys/syscall.h>
#include <sys/resource.h>
#include <setjmp.h>
#include <ucontext.h>
#include <unistd.h>
#include <ctime>
#endif
#include <cstring>
#include <span>
#include <exception>
#include <filesystem>
#include <fstream>
#include <set>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <vector>

#include <SDL3/SDL_events.h>
#include <SDL3/SDL_hints.h>
#include <SDL3/SDL_error.h>

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <dbghelp.h>
#endif

#include <achievement/achievement_runtime.h>
#include <rex/chrono/clock.h>
#include <rex/cvar.h>
#include <rex/logging.h>

namespace rex {
/// Defined in the runtime's logging.cpp and not declared by its header; the crash reporters
/// need it, since a record left in spdlog's queue when the process dies is never written.
void FlushLogging();
}  // namespace rex
#include <rex/system/kernel_state.h>

#include "core/host_asset_reader.h"
#include "core/precise_sleep.h"
#include "core/payload_bridge.h"
#include "core/rex_payload_bridge.h"
#include <rex/memory/utils.h>
#include <rex/system/mmio_handler.h>
#include <rex/system/xmemory.h>
#include <rex/rex_app.h>
#include <rex/ui/windowed_app.h>
#include <rex/ui/windowed_app_context_sdl.h>

#include <rex/filesystem/entry.h>
#include <rex/filesystem/vfs.h>
#include <rex/perf/counter.h>
#include <rex/runtime.h>
#include <rex/system/thread_state.h>
#include <rex/system/xsocket.h>
#include <rex/system/xthread.h>

#include <array>
#include <set>
#include <utility>
#ifdef REXGLUE_ENABLE_PROFILING
#include <tracy/Tracy.hpp>
#include <tracy/TracyC.h>
#endif

#include "host_input.h"
#include "refii_host.h"

#include "host_abi.h"
#include "host_core.h"
#include "host_tracy.h"
#include "hang_watchdog.h"
#include "lifecycle_android.h"
#include "core/traced_mutex.h"
#include "core/audio_state.h"
#include "core/mod_values.h"
#include "core/event_relay.h"
#include "core/player_state.h"

using namespace refii::hostcore;

#if defined(__GNUC__) || defined(__clang__)
#pragma GCC visibility push(hidden)
#endif
namespace refii::hostcore {

namespace {
TitleCallbacks g_title{};
}  // namespace

void RegisterTitle(const TitleCallbacks& callbacks) { g_title = callbacks; }

const TitleCallbacks& Title() { return g_title; }

std::vector<std::string> SplitArguments(const char* packed) {
    std::vector<std::string> arguments;
    if (packed == nullptr) {
        return arguments;
    }

    // Newline-separated: an argument can contain spaces (a path), so splitting on
    // whitespace would corrupt exactly the inputs that matter.
    std::istringstream stream{std::string(packed)};
    std::string line;
    while (std::getline(stream, line)) {
        if (!line.empty()) {
            arguments.push_back(line);
        }
    }
    return arguments;
}

}  // namespace refii::hostcore
#if defined(__GNUC__) || defined(__clang__)
#pragma GCC visibility pop
#endif

extern "C" {
/// Reserves the guest address space in THIS process and reports where it landed.
///
/// Returns 1 on success. On failure returns 0 and `out` carries the reason.
///
/// This is the gate for hosting the guest inside Godot. `rex::memory::Memory::Initialize` maps a
/// 4.5 GB file-backed region (the full 4 GB guest space plus 512 MB physical) and then
/// tries to place its views at 1<<32 .. 1<<63, taking the first base where every view
/// lands. It is NOT a single fixed address — that is the assumption worth killing early,
/// because it makes "Godot already owns that range" a 32-way retry rather than a hard
/// failure.
///
/// The reservation is released again before returning: this answers whether the space
/// CAN be taken, and holding 4.5 GB of the editor's address space to prove it would be
/// its own problem.
REFII_HOST_ABI_EXPORT int RefiiHostProbeGuestMemory(char* out, std::size_t capacity) {
    try {
        auto memory = std::make_unique<rex::memory::Memory>();
        if (!memory->Initialize()) {
            WriteOut("rex::memory::Memory::Initialize failed: no continuous 4.5 GB block was "
                     "available at any of the 32 candidate bases",
                     out, capacity);
            return 0;
        }

        const auto* virtual_base = memory->virtual_membase();
        const auto* physical_base = memory->physical_membase();

        char buffer[256];
        std::snprintf(buffer, sizeof(buffer),
                      "virtual_membase=%p physical_membase=%p (offset 0x%llx)",
                      static_cast<const void*>(virtual_base),
                      static_cast<const void*>(physical_base),
                      static_cast<unsigned long long>(physical_base - virtual_base));

        WriteOut(buffer, out, capacity);
        return 1;
    } catch (const std::exception& error) {
        WriteOut(std::string("exception: ") + error.what(), out, capacity);
        return 0;
    } catch (...) {
        WriteOut("unknown exception while reserving guest memory", out, capacity);
        return 0;
    }
}

/// Boots the guest on a dedicated thread and returns immediately.
///
/// Returns 1 when the thread was started — NOT when the guest is up. Booting loads the
/// XEX, maps the guest space and runs its init; it takes seconds and can fail long after
/// this returns, so the caller polls <see cref="RefiiHostIsRunning"/> and
/// <see cref="RefiiHostStatus"/> rather than treating a 1 here as success.
///
/// <param name="arguments">Newline-separated. Not space-separated: an argument can be a
/// path with spaces, and this tree has several.</param>
///
/// ONE PER PROCESS. The runtime owns global state — the guest memory map, the kernel
/// object table, the MMIO signal handler — so a second boot cannot work and is refused
/// rather than left to corrupt the first.
REFII_HOST_ABI_EXPORT int RefiiHostStart(const char* game_directory, const char* arguments,
                   char* out, std::size_t capacity) {
    GuestSession& session = Session();

    bool expected = false;
    if (!session.started.compare_exchange_strong(expected, true)) {
        WriteOut("guest already started in this process; the runtime's global state "
                 "cannot be booted twice",
                 out, capacity);
        return 0;
    }

    if (game_directory == nullptr || game_directory[0] == '\0') {
        session.started.store(false);
        WriteOut("no game directory given", out, capacity);
        return 0;
    }

    std::filesystem::path directory{game_directory};
    std::error_code exists_error;
    if (!std::filesystem::is_directory(directory, exists_error)) {
        session.started.store(false);
        WriteOut("game directory does not exist: " + directory.string(), out, capacity);
        return 0;
    }

    std::vector<std::string> parsed = SplitArguments(arguments);

    ApplyEmbeddedEnvironment();

    // The host owns presentation. Two switches, and they are not the same thing:
    //
    //   REX_EMBED_HEADLESS=1  no window, no ImGui drawer, no presentation setup.
    //
    // NOT REX_HEADLESS: that name maps to the existing `headless` KERNEL cvar, which
    // suppresses XAM UI dialogs. Setting it changed guest dialog behaviour as a silent
    // side effect of asking for no window.
    //
    // REX_EMBED_HEADLESS is also what tells RefiiApp::OnPreSetup there is no guest GPU: without
    // it the SDK defaults gpu_plugin to "xenos" and dlopens librexgpu-xenosd.so from beside
    // the executable, which embedded is Godot's directory, and the boot fails.
    SetEnvironmentValue("REX_EMBED_HEADLESS", "1");

    // Pass the game directory as an explicit flag, not just positionally.
    //
    // Embedded, GetExecutableFolder() is the HOST's directory — Godot's — so the guest
    // looks for its config beside the Godot binary, finds none, and falls back to a
    // relative default ("assets/game") resolved against a working directory that has no
    // game in it. Nothing about the guest's own layout applies once it is a library
    // inside someone else's process, so the host has to say where things are.
    bool has_game_root = false;
    for (const std::string& argument : parsed) {
        if (argument.rfind("--game_data_root", 0) == 0) {
            has_game_root = true;
            break;
        }
    }
    if (!has_game_root) {
        parsed.emplace_back("--game_data_root=" + directory.string());
    }

    session.running.store(true);
    session.SetStatus("booting");

    session.thread = std::thread([&session, directory, parsed]() {
        int result = -1;
        try {
            result = RunRefiiEmbedded(directory, parsed);
            session.SetStatus("exited with code " + std::to_string(result));
        } catch (const std::exception& error) {
            session.SetStatus(std::string("threw: ") + error.what());
        } catch (...) {
            session.SetStatus("threw an unknown exception");
        }

        session.exit_code.store(result);
        session.running.store(false);
    });

    WriteOut("started; poll RefiiHostIsRunning / RefiiHostStatus", out, capacity);
    return 1;
}

/// 1 while the guest thread is alive.
REFII_HOST_ABI_EXPORT int RefiiHostIsRunning(void) { return Session().running.load() ? 1 : 0; }

/// 1 while a guest is up and its memory is safe to read, BY EITHER BOOT PATH.
///
/// WHY THIS EXISTS, and it is worth stating plainly because the absence of it cost a whole
/// character pipeline. There are two ways to bring the guest up and they set different flags:
///
///   `RefiiHostStart`      — runs the guest on its OWN thread; sets `Session().running`.
///   `RefiiHostInitialize` — the EMBEDDED path the game uses, driven a slice per frame by
///                           `RefiiHostStep`; sets `Hosted().initialized` and never touches
///                           `Session().running`.
///
/// `RefiiHostIsRunning` only ever answers for the first. Every host accessor that guarded
/// itself with it — entity poses, rest poses, bone names, locomotion, the language table —
/// therefore returned "no guest" on every frame of the RUNNING GAME while the guest was
/// perfectly alive, and did so silently. A run showed the shape of it exactly: 3205 drawn
/// entities with no skeleton, `characters=0 bones=0`, and the report itself concluding "a node
/// whose rest pose the bridge will not return". Nothing was wrong with any of those readers.
///
/// So this is the predicate an accessor wants: not "did anyone call Start", but "is there a
/// guest whose address space I may read". `RefiiHostIsRunning` keeps its narrower meaning for
/// the callers that genuinely ask about the session THREAD — chiefly waiting for it to exit.
REFII_HOST_ABI_EXPORT int RefiiHostIsGuestUp(void) {
    return (Session().running.load() || Hosted().initialized) ? 1 : 0;
}

/// The guest's exit code, or -1 while it is still running or was never started.
REFII_HOST_ABI_EXPORT int RefiiHostExitCode(void) { return Session().exit_code.load(); }

/// A human-readable status line. Same buffer convention as the probe.
REFII_HOST_ABI_EXPORT int RefiiHostStatus(char* out, std::size_t capacity) {
    return WriteOut(Session().GetStatus(), out, capacity);
}

/// Joins the guest thread, if it has finished.
///
/// Does NOT ask the guest to stop — there is no clean external shutdown wired through
/// this ABI yet, and pretending otherwise would leave a caller believing it had asked.
/// Call it after <see cref="RefiiHostIsRunning"/> reports 0.
/// Initialises the guest ON THE CALLING THREAD and returns once it is up.
///
/// This is the "owned by Godot" entry point: the host keeps its own thread and drives
/// the guest with <see cref="RefiiHostStep"/>, instead of handing a thread to the
/// guest's own blocking loop.
///
/// SYNCHRONOUS, and it takes seconds — it loads the XEX and runs the guest's init. Call
/// it from the host's main thread and accept the stall: SDL wants its main thread, and
/// the previous arrangement (guest on a worker) left that unresolved.
///
/// Returns 1 when the guest is initialised and ready to be stepped.
#ifdef __linux__
/// Unlinks guest memory arenas whose process is gone.
///
/// `rex::system::Memory::Initialize` backs the guest's 4 GB + 512 MB address space with a POSIX
/// shared-memory object named `xenia_memory_<host tick count>` (system/xmemory.cpp). /dev/shm is
/// RAM, and an shm object outlives its creator: a run that aborts or is killed never unlinks its
/// arena, so every crashed session permanently costs whatever that arena had touched. Measured
/// 2026-08-19 — 16 abandoned arenas, 5.9 GB resident, on a machine that had pushed 26 GB into
/// swap by then. The symptom is not "a leak", it is the next run stuttering and dying.
///
/// ONLY THE UNMAPPED ONES. Running a second instance is legitimate, so an arena goes only when
/// no live process maps it — /proc/<pid>/maps names the object. If /proc cannot be read, nothing
/// is removed: this must never guess a live arena is dead.
void ReclaimAbandonedGuestArenas() {
    static constexpr std::string_view kArenaPrefix = "xenia_memory_";

    std::error_code error;
    std::filesystem::directory_iterator shm{"/dev/shm", error};
    if (error) {
        return;
    }

    std::vector<std::filesystem::path> candidates;
    for (const std::filesystem::directory_entry& entry : shm) {
        if (entry.path().filename().string().rfind(kArenaPrefix, 0) == 0) {
            candidates.push_back(entry.path());
        }
    }
    if (candidates.empty()) {
        return;
    }

    std::error_code proc_error;
    std::filesystem::directory_iterator processes{"/proc", proc_error};
    if (proc_error) {
        return;
    }

    std::set<std::string> live;
    for (const std::filesystem::directory_entry& process : processes) {
        const std::string pid = process.path().filename().string();
        if (pid.empty() || !std::all_of(pid.begin(), pid.end(),
                                        [](unsigned char c) { return std::isdigit(c) != 0; })) {
            continue;
        }
        std::ifstream maps{process.path() / "maps"};
        if (!maps) {
            continue;  // exited between the listing and the open, or not ours to read
        }
        std::string line;
        while (std::getline(maps, line)) {
            const std::size_t at = line.find(kArenaPrefix);
            if (at == std::string::npos) {
                continue;
            }
            std::size_t end = line.find_first_of(" \t", at);
            if (end == std::string::npos) {
                end = line.size();
            }
            live.insert(line.substr(at, end - at));
        }
    }

    std::size_t reclaimed = 0;
    for (const std::filesystem::path& candidate : candidates) {
        if (live.count(candidate.filename().string()) != 0) {
            continue;
        }
        std::error_code remove_error;
        if (std::filesystem::remove(candidate, remove_error)) {
            ++reclaimed;
        }
    }

    if (reclaimed != 0) {
        std::fprintf(stderr, "[refii] reclaimed %zu abandoned guest memory arena(s) in /dev/shm\n",
                     reclaimed);
    }
}
#endif  // __linux__

REFII_HOST_ABI_EXPORT int RefiiHostInitialize(const char* game_directory, const char* arguments,
                        char* out, std::size_t capacity) {
    HostedGuest& hosted = Hosted();

    if (hosted.initialized) {
        WriteOut("already initialized", out, capacity);
        return 0;
    }

    if (game_directory == nullptr || game_directory[0] == '\0') {
        WriteOut("no game directory given", out, capacity);
        return 0;
    }

    std::filesystem::path directory{game_directory};
    std::error_code exists_error;
    if (!std::filesystem::is_directory(directory, exists_error)) {
        WriteOut("game directory does not exist: " + directory.string(), out, capacity);
        return 0;
    }

    try {
        StartAndroidPlatform();  // lifecycle_android.h
#ifdef __linux__
        ReclaimAbandonedGuestArenas();
#endif
        ApplyEmbeddedEnvironment();
        ReadyAndroidSdl();  // lifecycle_android.h

        // argv[0] then the flags. The game directory goes as an explicit flag, not
        // positionally: embedded, GetExecutableFolder() is the HOST's directory, so the
        // guest finds no config of its own and would fall back to a relative default.
        std::vector<std::string> owned;
        owned.emplace_back("refii");
        for (const std::string& argument : SplitArguments(arguments)) {
            owned.push_back(argument);
        }
        owned.push_back("--game_data_root=" + directory.string());
        // The title's config file names its logs: refii.toml -> refii_NNN.log, refiii.toml ->
        // refiii_NNN.log, in the one logs folder both titles share.
        if (const std::string stem = RefiiGetConfigPath().stem().string(); !stem.empty()) {
            owned.push_back("--log_prefix=" + stem);
        }

        std::vector<char*> argv;
        argv.reserve(owned.size());
        for (std::string& argument : owned) {
            argv.push_back(argument.data());
        }

        rex::cvar::Init(static_cast<int>(argv.size()), argv.data());
        rex::cvar::ApplyEnvironment();

        hosted.context = std::make_unique<rex::ui::SDLWindowedAppContext>();
        if (!hosted.context->Initialize()) {
            hosted.status = std::string("SDL context init failed: ") + SDL_GetError();
            WriteOut(hosted.status, out, capacity);
            return 0;
        }

        hosted.app = CreateRefiiApp(*hosted.context);
        if (!hosted.app) {
            hosted.status = "CreateRefiiApp returned null";
            WriteOut(hosted.status, out, capacity);
            return 0;
        }

        if (!hosted.app->OnInitialize()) {
            hosted.status = "guest OnInitialize failed";
            WriteOut(hosted.status, out, capacity);
            return 0;
        }

        hosted.initialized = true;
        InstallLastResortFaultHandler();

        // Windows: the process timer period, raised to 1 ms BEFORE any pacing thread starts.
        // The runtime's own Sleep(1) and every guest timed wait with a millisecond timeout are
        // quantised to it, and nothing else in the process is guaranteed to have raised it; at
        // the 15.6 ms default a title's 1 ms polls run at 64 Hz. A no-op elsewhere.
        refii::RaiseTimerResolution();

        // The title's start-up work, its pacing threads among it.
        if (refii::hostcore::Title().on_initialized != nullptr) {
            refii::hostcore::Title().on_initialized();
        }

        hosted.status = "running";
        WriteOut("initialized; drive it with RefiiHostStep", out, capacity);
        return 1;
    } catch (const std::exception& error) {
        hosted.status = std::string("threw: ") + error.what();
        WriteOut(hosted.status, out, capacity);
        return 0;
    } catch (...) {
        hosted.status = "threw an unknown exception";
        WriteOut(hosted.status, out, capacity);
        return 0;
    }
}

/// Advances the guest by one host frame. Returns 1 while it is still running.
///
/// This is the loop body the guest would otherwise run for itself, minus the blocking
/// wait. Headless there are no window or input events to dispatch — the only event that
/// matters is the wakeup that carries pending UI-thread work, and
/// ExecutePendingFunctionsFromUIThread services that directly. The queue is still
/// drained so SDL does not accumulate events nobody reads.
REFII_HOST_ABI_EXPORT int RefiiHostStep(void) {
    HostedGuest& hosted = Hosted();
    if (!hosted.initialized || hosted.finished) {
        return 0;
    }

    try {
        SDL_Event event;
        while (SDL_PollEvent(&event)) {
            // Deliberately dropped: with no window there is nothing to route them to.
        }

        if (refii::hostcore::Title().before_step != nullptr) {
            refii::hostcore::Title().before_step();
        }

        hosted.context->ExecutePendingFunctionsFromUIThread();

        if (refii::hostcore::Title().after_step != nullptr) {
            refii::hostcore::Title().after_step();
        }

        if (hosted.context->HasQuitFromUIThread()) {
            hosted.finished = true;
            hosted.status = "guest requested quit";
            return 0;
        }

        return 1;
    } catch (const std::exception& error) {
        hosted.finished = true;
        hosted.status = std::string("step threw: ") + error.what();
        return 0;
    } catch (...) {
        hosted.finished = true;
        hosted.status = "step threw an unknown exception";
        return 0;
    }
}

/// Status of the hosted guest, for diagnostics.
REFII_HOST_ABI_EXPORT int RefiiHostHostedStatus(char* out, std::size_t capacity) {
    return WriteOut(Hosted().status, out, capacity);
}

/// Tears the hosted guest down. Safe to call more than once.
///
/// Without this the guest's threads are still live when the host process exits, which
/// is what produced the abort on teardown.
REFII_HOST_ABI_EXPORT int RefiiHostShutdown(char* out, std::size_t capacity) {
    HostedGuest& hosted = Hosted();

    if (refii::hostcore::Title().on_shutdown != nullptr) {
        refii::hostcore::Title().on_shutdown();
    }

    if (!hosted.initialized) {
        WriteOut("not initialized", out, capacity);
        return 1;
    }

    try {
        // Ask the runtime to stop, then PUMP while it does.
        //
        // TerminateTitle runs on its own thread, but it queues work back to the UI thread
        // — and this call is made from that thread. Blocking here instead of pumping is
        // therefore self-defeating: the work that would finish the shutdown is sitting in
        // a queue that only this thread drains, so the grace period always expires and
        // the runtime is abandoned with its threads live.
        auto* app = dynamic_cast<rex::ReXApp*>(hosted.app.get());
        if (app != nullptr) {
            app->RequestShutdown();
        } else if (hosted.context && !hosted.context->HasQuitFromUIThread()) {
            hosted.context->QuitFromUIThread();
        }

        bool stopped = app == nullptr;
        if (app != nullptr && hosted.context) {
            const auto deadline =
                std::chrono::steady_clock::now() + kShutdownPumpBudget;
            while (std::chrono::steady_clock::now() < deadline) {
                SDL_Event event;
                while (SDL_PollEvent(&event)) {
                }

                hosted.context->ExecutePendingFunctionsFromUIThread();

                if (app->ShutdownThreadsFinished()) {
                    stopped = true;
                    break;
                }

                refii::PreciseSleepFor(std::chrono::milliseconds(1));
            }
        }

        // Only once the guest has stopped: OnDestroy ends in runtime_.reset(), which frees
        // every XThread, and a straggler (one spinning on a D3D fence never reaches a kernel
        // wait, so TerminateTitle cannot stop it) would read the freed object on its next
        // kernel call. The leak path below must leave the runtime intact.
        if (stopped) {
            if (hosted.app) {
                hosted.app->InvokeOnDestroy();
            }
#ifndef _WIN32
            fault::Reinstall();
#endif

            // The guest really stopped, so its memory can go back and a later session is
            // possible within this process.
            hosted.app.reset();
            hosted.context.reset();
            rex::ShutdownLogging();

            hosted.initialized = false;
            hosted.finished = true;
            hosted.status = "shut down";
            WriteOut(hosted.status, out, capacity);
            return 1;
        }

        // DELIBERATELY LEAKED, not reset.
        //
        // Standalone, refii tears the runtime down and then the process ends, so nothing
        // outlives the wreckage. Hosted, the process carries on — and the guest's title
        // threads routinely miss the shutdown grace period, so destroying the app and
        // context here runs their destructors underneath threads still using them. That
        // is a SIGSEGV in the host: the launcher crashed on closing the game window,
        // having previously been killed outright by the runtime's own std::_Exit.
        //
        // Logging is left up for the same reason: ShutdownLogging frees loggers a
        // straggler may still write through.
        //
        // The cost is that the runtime's memory is held until the process exits. That is
        // affordable because a session is single-shot regardless — the runtime keeps
        // process-global state (memory map, kernel, MMIO handler) and cannot be started
        // twice, which GameWindow already enforces.
        (void)hosted.app.release();
        (void)hosted.context.release();

        hosted.initialized = false;
        hosted.finished = true;
        // Name what is stuck. "Play twice in one session" is blocked on exactly this,
        // and a bare "left resident" gives the next person nothing to start from.
        const std::string pending =
            app != nullptr ? app->ShutdownThreadsPending() : std::string("unknown");
        hosted.status =
            "shut down (runtime left resident; still running: " +
            (pending.empty() ? std::string("none") : pending) + ")";
        WriteOut(hosted.status, out, capacity);
        return 1;
    } catch (const std::exception& error) {
        WriteOut(std::string("shutdown threw: ") + error.what(), out, capacity);
        return 0;
    } catch (...) {
        WriteOut("shutdown threw an unknown exception", out, capacity);
        return 0;
    }
}

REFII_HOST_ABI_EXPORT int RefiiHostJoin(char* out, std::size_t capacity) {
    GuestSession& session = Session();

    if (session.running.load()) {
        WriteOut("still running", out, capacity);
        return 0;
    }

    if (session.thread.joinable()) {
        session.thread.join();
    }

    WriteOut(session.GetStatus(), out, capacity);
    return 1;
}
}  // extern "C"
