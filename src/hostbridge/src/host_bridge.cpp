// refii - host bridge
//
// A C ABI over the guest runtime, so Godot can host the recompiled guest IN ITS OWN
// PROCESS rather than spawning `refii` as a child.
//
// This is deliberately the thinnest possible surface. The first thing it has to answer
// is not "can the guest run" but "can the guest's ADDRESS SPACE exist here at all" —
// everything else is downstream of that, and if the answer is no the whole in-process
// approach is dead and we should find out in seconds rather than after wiring a window,
// an input pump and a frame loop.

#include <atomic>
#include <chrono>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <filesystem>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include <SDL3/SDL_events.h>

#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>
#include <rex/rex_app.h>
#include <rex/ui/windowed_app.h>
#include <rex/ui/windowed_app_context_sdl.h>

#include "../../host_input.h"

#if defined(_WIN32)
#define REFII_HOST_ABI_EXPORT __declspec(dllexport)
#else
#define REFII_HOST_ABI_EXPORT __attribute__((visibility("default")))
#endif

// Included rather than redeclared like the Bink entries below: the audio exports marshal
// VoiceRecord by value, and two hand-written copies of a struct layout is exactly the
// kind of thing that goes wrong silently.
#include "core/audio_state.h"

namespace refii::vfs {
// Defined in src/vfs/src/core/bink_frame_state.cpp.
uint32_t BinkFrameBuffersGuestAddress();
void RequestedBinkVideo(uint32_t* out_address, uint32_t* out_size,
                        uint32_t* out_generation, uint32_t* out_swapped);
uint32_t RequestedBinkVideoName(char* out, uint32_t capacity);
uint32_t FinishCurrentBinkMovie(uint8_t* base);
uint32_t BinkMovieState(uint8_t* base);

// Defined in src/vfs/src/hooks/guest_tick_pump.cpp — the ONLY route from this thread onto
// the guest's own tick thread. See that file for why a memory write cannot do this job.
uint32_t PostGuestTickAction(const char* name);
void GuestTickPumpCounters(uint64_t* out_ticks, uint64_t* out_drained,
                           uint32_t* out_queued);
int32_t GuestSavedGameCount();
void RefreshGuestSavedGameCount();
}  // namespace refii::vfs
#include "refii_host.h"

namespace {

// Copies into the caller's buffer and returns the length that WOULD have been written,
// so a caller can size a buffer with a null probe. Mirrors the convention the launcher
// ops ABI already uses.
int WriteOut(const std::string& text, char* out, std::size_t capacity) {
    const int needed = static_cast<int>(text.size());
    if (out != nullptr && capacity > 0) {
        const std::size_t copied = text.size() < capacity - 1 ? text.size() : capacity - 1;
        std::memcpy(out, text.data(), copied);
        out[copied] = '\0';
    }
    return needed;
}

/// The guest, once started. One per process — the runtime holds global state (the
/// memory map, the kernel, the MMIO handler), so a second boot in the same process is
/// not a thing that can work, and saying so is better than letting it half-run.
struct GuestSession {
    std::thread thread;
    std::atomic_bool running{false};
    std::atomic_bool started{false};
    std::atomic_int exit_code{-1};

    std::mutex status_mutex;
    std::string status{"not started"};

    void SetStatus(std::string text) {
        std::lock_guard<std::mutex> guard(status_mutex);
        status = std::move(text);
    }

    std::string GetStatus() {
        std::lock_guard<std::mutex> guard(status_mutex);
        return status;
    }
};

GuestSession& Session() {
    static GuestSession session;
    return session;
}

/// The guest when GODOT drives it, rather than the guest driving itself.
///
/// The difference matters. RunRefiiEmbedded owns a blocking SDL loop — on netplay,
/// SDL_WaitEvent — so hosting it means handing a thread over and getting it back when
/// the guest decides. Here the host keeps its thread: it initialises the guest once,
/// then pumps it a slice at a time from its own frame loop.

/// How long shutdown pumps the UI thread waiting for the guest to stop.
///
/// Generous on purpose: this runs once, when the player closes the game, and the
/// alternative to waiting is abandoning the runtime for the rest of the process.
constexpr std::chrono::seconds kShutdownPumpBudget{15};

struct HostedGuest {
    std::unique_ptr<rex::ui::SDLWindowedAppContext> context;
    std::unique_ptr<rex::ui::WindowedApp> app;
    bool initialized = false;
    bool finished = false;

    std::string status{"not initialized"};
};

HostedGuest& Hosted() {
    static HostedGuest hosted;
    return hosted;
}

void SetEnvironmentValue(const char* name, const char* value) {
#ifdef _WIN32
    ::_putenv_s(name, value);
#else
    ::setenv(name, value, 1);
#endif
}

/// Applies the switches that make the guest a simulation server rather than an app.
/// Shared by both entry points so they cannot drift.
void ApplyEmbeddedEnvironment() {
    // No window, no overlays, no presentation.
    SetEnvironmentValue("REX_EMBED_HEADLESS", "1");
}

/// Guest globals and device fields the no-GPU latch needs. All IDA-proven against
/// `default.xex`; see ArmGuestNoGpuMode for the proof chain.
constexpr uint32_t kGuestDevicePointer = 0x83360364u;  // g_D3DDevice_PresentModule
constexpr uint32_t kDeviceNoGpuFlagByte = 0x2ABDu;     // dev+0x2ABD
constexpr uint8_t kDeviceNoGpuFlagBit = 0x02u;         // "GPU is dead" latch
constexpr uint32_t kDeviceDebugConsumer = 0x541Cu;     // dev+0x541C, NULL in retail
constexpr uint32_t kDeviceOutstandingIbs = 0x2B00u;    // dev+0x2B00

bool g_no_gpu_armed = false;
uint32_t g_guest_device = 0;

/// Puts Fable 2's own D3D into the "no GPU present" mode it ships with.
///
/// THE PROBLEM. With no GPU behind it, the guest boots, initialises its ring buffer,
/// and then parks forever in `D3D_CDevice_BlockOnFence` (0x82242668) spinning on the
/// fence write-back at `*(*(dev+0x2A90))` that only a command processor would ever
/// advance. The spin does not yield — it pegs a core — and it is reached from
/// `D3DDevice_BlockUntilIdle` on the SECOND `D3D_InitializeRingBuffer` entry. That is
/// where every run has been stopping, ~34ms into boot, long before any game logic.
///
/// THE FIX IS THE GAME'S OWN. Bit 0x02 of `dev+0x2ABD` is a one-way latch that D3D
/// checks in three places:
///   * `D3D_CDevice_AddCallsToPrimaryBuffer` (0x822866F8) returns without emitting PM4,
///     without advancing the write index, and WITHOUT the doorbell store at 0x82286870
///     — the only `0x7FC80714` MMIO write in the image. D3D9 stops posting.
///   * `D3D_CDevice_SetFence` (0x822986EC) writes the fence and ring position into the
///     write-back block ITSELF, from the CPU, exactly as the GPU would have.
///   * `D3D_CBlocker_Check` (0x82BA1FF8) returns 0, so every GPU wait in the binary
///     exits on its first iteration — including the spin above.
/// Nothing in the executable ever clears the bit; a census of all 17 stores to
/// `dev+0x2ABD` found only `ori 2` and `ori 3` setters, and `StopWorkerQueue`'s
/// `andi 0xDF` preserves it.
///
/// WHY THE GUEST CANNOT DO IT ALONE. It tries. `D3D_CDevice_KickOff` sets this very bit
/// at 0x821E8FA0 when `VdIsHSIOTrainingSucceeded` reported failure — which our stub
/// does. But the store sits AFTER a `BlockOnFence` at 0x821E8F94, so the guest has to
/// survive the wait in order to reach the flag that would have skipped it. The hang
/// handler would also latch it, but only after `D3D_CBlocker_Check`'s 5000-tick
/// deadline, and that deadline is measured against a KTHREAD field the host never
/// advances, so it never expires. Hence: the host sets it.
///
/// SAFE TO ARM LATE. `D3D_CBlocker_Check` is re-evaluated every iteration of every
/// spin, so arming after the guest is already parked releases it just the same. That is
/// why polling from the step loop is sufficient and no hook is needed.
///
/// Requires `dev+0x541C == 0` (a debug/PIX command consumer, with no store anywhere in
/// the executable — NULL in retail). A non-NULL consumer would take precedence in both
/// `AddCallsToPrimaryBuffer` and `SetFence`, so the arm is skipped if one is present.
void ArmGuestNoGpuMode() {
    if (g_no_gpu_armed) {
        return;
    }

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return;
    }

    // Guest memory is big-endian; these are plain aligned loads the guest also does.
    const auto load_be32 = [&](uint32_t guest_address) {
        uint32_t raw = 0;
        std::memcpy(&raw, base + guest_address, sizeof(raw));
        return __builtin_bswap32(raw);
    };

    const uint32_t device = load_be32(kGuestDevicePointer);
    if (device == 0) {
        // Device not created yet; try again next step.
        return;
    }

    if (load_be32(device + kDeviceDebugConsumer) != 0) {
        REXLOG_WARN(
            "Refii host: dev+0x541C is non-NULL; leaving the no-GPU latch alone");
        g_no_gpu_armed = true;
        return;
    }

    uint8_t* flag = base + device + kDeviceNoGpuFlagByte;
    const uint8_t before = *flag;
    *flag = static_cast<uint8_t>(before | kDeviceNoGpuFlagBit);
    g_no_gpu_armed = true;
    g_guest_device = device;

    REXLOG_INFO(
        "Refii host: armed the guest's no-GPU mode (device=0x{:08X}, "
        "dev+0x2ABD 0x{:02X} -> 0x{:02X})",
        device, before, *flag);
}

/// Keeps `D3DDevice_BlockUntilIdle` satisfiable.
///
/// This is the ONE GPU wait the no-GPU latch does not cover. `D3DDevice_BlockUntilIdle`
/// (loop at 0x82BA27A4) spins on `dev+0x2B00 == 0` with no CBlocker, no watchdog and no
/// escape hatch of any kind — the only unguarded spin of its class in the binary:
///
///     82ba27a4  lwz   r11, 0x2B00(r31)
///     82ba27a8  cmpwi cr6, r11, 0
///     82ba27ac  bne   cr6, loc_82BA27A4
///
/// `D3D_CDevice_QueueIndirectBuffer` INCREMENTS it, and nothing in the guest ever
/// decrements it — on hardware the GPU retired those indirect buffers. The increment is
/// by zero at four of its five call sites, but `D3D_StopWorkerQueue` passes one, and
/// that is reached from `D3DDevice_EndTiling` — a path Fable 2 does use. So the counter
/// can become nonzero and then never come back down, wedging the guest permanently with
/// no diagnostic.
///
/// Zeroing it every step is correct precisely BECAUSE there is no GPU: with nothing to
/// retire indirect buffers, "outstanding" can only ever grow. The binary's own hang
/// handler does exactly this write at 0x82BA7678.
/// Retires the guest's command-stream rings, which nothing else does headless.
///
/// The guest reserves ring space through `sub_8222ABE0`, and when the ring looks full it
/// SPINS — `Sleep(0)` with no timeout and no GPU-dead check:
///
///     while ( a1[9] >= a1[8] ) {                       /*0x8222ACB4*/
///       if ( v6 + a1[8] <= a1[9] ) break;
///       sub_82CC2E80(0);                                // Sleep(0)
///       sub_821A5BD0(a1); }                             // re-read the reader window
///
/// and `sub_821A5BD0` sources that window from exactly one place:
///
///     v2 = a1[16];  a1[9] = v2;                         /*0x821A5BF4 / 0x821A5C04*/
///
/// `a1[16]` is +64, the CONSUMER's read-back. Neither function consults `dev+0x2ABD`, so
/// the no-GPU latch above does not release this one — it is a producer waiting on a
/// consumer, not on hardware. With Godot rendering and the guest's commands going nowhere,
/// there is no consumer, so the reserve waits forever. Measured: a New Game parks here
/// before `GameFlow_QueueCommand(CmdLevelChange)` at 0x823115D0, which is why the level
/// change is never even queued.
///
/// The retire itself is not invented here. It mirrors the guest's own
/// SceneStreamSyncPublishCursor + SceneStreamRefresh pair, the same mapping the renderer
/// module already implements in RepairFableSceneStreamPublishHandoff — that code is correct
/// but lives behind REFII_BUILD_RENDERER_HOOKS, which is OFF by design because Godot
/// renders. Draining is a command-stream contract, not renderer work, so it belongs here
/// where it is always built.
///
/// Discarding the commands is the right semantic rather than a shortcut: the guest is a
/// simulation server, it states what it would have drawn, and a null device consumes and
/// drops it.
///
/// Four slots, `dword_8349F9B0[0..3]`. Field map (shared with the renderer's):
///   +8 capacity · +12 read cursor · +16 visible end · +20 read limit · +24 last read
///   +28 read cap · +32 write cursor · +36 write consumed · +40 write mark · +52 write wrap
///   +60 published end · +64 READER CONSUMED · +68 published cap
void RetireGuestSceneStreams() {
    // OFF unless asked for: measured harmful (it stops the startup logos playing) and its
    // premise was refuted — the ring is never full when the reserve is blocked. Kept only
    // because the field map below is correct and a real consumer will want it.
    static const bool enabled = [] {
        const char* value = std::getenv("REFII_RETIRE_SCENE_STREAMS");
        return value != nullptr && std::string_view(value) == "1";
    }();
    if (!enabled) {
        return;
    }

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return;
    }

    const auto read32 = [base](uint32_t at) {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };

    const auto write32 = [base](uint32_t at, uint32_t value) {
        const uint32_t raw = __builtin_bswap32(value);
        std::memcpy(base + at, &raw, sizeof(raw));
    };

    // A cursor must land inside its own ring. Anything else means we are looking at a slot
    // that is not a scene stream — half-built, freed, or simply another kind of object —
    // and writing to it would be scribbling on guest memory.
    constexpr uint32_t kMaxRingBytes = 16u * 1024u * 1024u;
    const auto sane_offset = [](uint32_t offset, uint32_t cap) {
        return cap != 0u && cap <= kMaxRingBytes && offset <= cap;
    };

    constexpr uint32_t kSchedulerStreams = 0x8349F9B0u;

    for (uint32_t slot = 0; slot < 4u; ++slot) {
        const uint32_t object = read32(kSchedulerStreams + slot * 4u);
        if (object < 0x10000u || object >= 0xC0000000u) {
            continue;
        }

        const uint32_t capacity = read32(object + 8u);
        const uint32_t write_cursor = read32(object + 32u);
        const uint32_t write_consumed = read32(object + 36u);
        const uint32_t write_mark = read32(object + 40u);
        const uint32_t write_wrap = read32(object + 52u);
        const uint32_t published_end = read32(object + 60u);
        const uint32_t reader_consumed = read32(object + 64u);
        const uint32_t published_cap = read32(object + 68u);

        if (!sane_offset(write_cursor, capacity) ||
            !sane_offset(write_mark, capacity) ||
            !sane_offset(write_wrap, capacity) ||
            !sane_offset(reader_consumed, capacity)) {
            continue;
        }

        // Publish the writer's boundary and import the reader's, exactly as the guest's own
        // publish does. This does not consume unread messages by itself.
        uint32_t next_published_end = write_mark;
        uint32_t next_published_cap = write_wrap;
        if (reader_consumed < write_cursor || reader_consumed > write_wrap) {
            next_published_cap = capacity;
        }
        if (next_published_end == next_published_cap) {
            next_published_end = 0u;
        }

        // The refresh half. The renderer's version stops at `last_read - 1`, because when
        // the renderer is built there is a real consumer moving the read head and this only
        // has to IMPORT its position. Headless there is no such consumer, so importing a
        // head that never moves publishes a ring that never drains: measured, this left
        // reader_consumed pinned at 0xEF while the writer advanced, which stalls the moment
        // the writer wraps onto it.
        //
        // So the host asserts what a null device actually did: it consumed everything. The
        // read head follows the write head, and reader_consumed sits ONE BEHIND it — the
        // ring's own "empty" encoding, the same `x == 0 ? capacity - 1 : x - 1` the guest
        // uses. That satisfies both of `sub_8222ABE0`'s spins at once, which is the tell
        // that it is the right state rather than a number that happens to unblock one:
        // the pre-wrap loop wants `write >= read` and the post-wrap loop wants the reader
        // not to be trapping the writer, and "empty" is the only state that is both.
        const uint32_t next_reader_consumed =
            write_cursor == 0u ? capacity - 1u : write_cursor - 1u;

        if (!sane_offset(next_reader_consumed, capacity)) {
            continue;
        }

        bool changed = false;
        const auto store_if = [&](uint32_t at, uint32_t was, uint32_t now) {
            if (was != now) {
                write32(at, now);
                changed = true;
            }
        };

        // PRODUCER-FACING FIELDS ONLY.
        //
        // An earlier version also wrote the consumer's own read state — +12 read cursor,
        // +16 visible end, +20 read limit, +24 last read, +28 read cap — mirroring what the
        // renderer's repair does. That killed the process within seconds of guest init, and
        // an A/B with REFII_RETIRE_SCENE_STREAMS=0 confirmed the write was the cause.
        //
        // The reason is that the guest's consumer thread is NOT absent, only starved: it is
        // the one spinning at 0x822DFF2C inside sub_822DFE08, which our own drain logger has
        // been reporting for months. Moving its read head from Godot's thread while it is
        // mid-read hands it a position it never asked for. Publishing the writer's boundary
        // and the space the producer may use is safe; steering the reader is not.
        store_if(object + 36u, write_consumed, reader_consumed);
        store_if(object + 60u, published_end, next_published_end);
        store_if(object + 68u, published_cap, next_published_cap);
        store_if(object + 64u, reader_consumed, next_reader_consumed);

        // Once per slot, so a run says the drain engaged without printing every step.
        static bool announced[4] = {false, false, false, false};
        if (changed && !announced[slot]) {
            announced[slot] = true;
            REXLOG_INFO(
                "Refii host: retiring guest scene stream slot {} (object=0x{:08X} "
                "capacity=0x{:X} write=0x{:X} reader_consumed 0x{:X} -> 0x{:X})",
                slot, object, capacity, write_cursor, reader_consumed,
                next_reader_consumed);
        }
    }
}

void KeepGuestIdleCounterClear() {
    if (!g_no_gpu_armed || g_guest_device == 0) {
        return;
    }

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return;
    }

    uint32_t zero = 0;
    std::memcpy(base + g_guest_device + kDeviceOutstandingIbs, &zero, sizeof(zero));
}

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

}  // namespace

extern "C" {

/// ABI version. Bump on any breaking change so a stale library is detected rather than
/// crashing the editor.
///
/// 4 adds RefiiHostSetControllerState / RefiiHostGetVibration.
/// 5 adds RefiiHostGetVideoFrame.
/// 6 adds RefiiHostGetVideoPlanes (GPU-side colour conversion).
/// 7 adds RefiiHostGetRequestedVideo.
/// 8 adds RefiiHostGetRequestedVideoName.
/// 9 adds RefiiHostFinishMovie.
/// 10 adds RefiiHostGetFrontEndState.
/// 11 adds RefiiHostGetFrontEnd.
/// 12 adds RefiiHostGetUiElements (13 replaced a truncating slides-only form).
/// 14 adds RefiiHostMovieState.
/// 15 adds RefiiHostGetCamera.
/// 16 adds RefiiHostGetUiAttributes.
/// 17 widens RefiiHostGetFrontEnd to NINE words — the ninth is the active screen's vtable,
///    which is what names the screen the front end hands over to.
/// 18 adds a tenth: the GUI object's own vtable, without which the other nine cannot be
///    known to be about a front-end GUI at all.
/// 19 adds RefiiHostIsInGame and RefiiHostGetRenderFence — the guest's own in-game flag,
///    and the silent fence the level load waits on.
/// 20 adds RefiiHostGetLoadingScreen — the take-down handshake the game thread blocks on.
/// 23 adds RefiiHostGetStartLevel — the world/level/entrance the guest asked for.
/// 22 adds RefiiHostGetSceneStreams — the command-stream rings, read-only.
/// 21 adds RefiiHostGetInitMarkers — how far Game_InitDatabases has got, which it does not
///    say itself.
/// 24 adds RefiiHostGetEntities — every live entity's world transform and model.
/// 25 names the placement vtables RefiiHostGetEntities could not resolve.
/// 26 returns that unknown-vtable list as {vtable, count} pairs with an explicit length,
///    since a vtable word reading 0 is an observation the walk can make and 25's zero
///    terminator swallowed the rest of the list.
/// 27 adds RefiiHostGetEntityPose — a live character's bone pose and its parent table.
/// 28 adds RefiiHostGetEntityRestPose — the bind pose Godot needs to build a skin.
/// 29 widens RefiiHostGetUiElements to stride 4, adding the element TYPE byte so the ordinal
///    join between the guest walk and the .bgf can be CHECKED, not assumed.
/// 30 widens it to stride 5, adding the element FLAGS halfword — bit 0x1000 `globalactive`
///    is the guest's own draw gate.
/// 31 adds RefiiHostGetFrontEndModel — the front end as a MODEL (which entries exist, which
///    screen is up) rather than as widgets, for a natively rebuilt menu.
/// 33 adds RefiiHostGetEntityBoneNames. The guest skeleton and the model number their bones
///    differently (161 against 143 for the child hero), so the name is the only join.
/// 34 widens RefiiHostGetRenderFence with the five values that say WHICH arm holds the
///    engine-preloading flag, and renames its third word from "retired" to the deadline it
///    actually is.
/// 35 adds RefiiHostPostFrontEndAction and RefiiHostGuestTickPump — the first route for HOST
///    work onto the guest's own tick thread. A memory write cannot do this job: the
///    start-game commit ALLOCATES, and its globals are refcounted strings.
/// 36 adds RefiiHostGetEntityComponents — the {id, pointer} pairs one entity carries, which
///    is a live-memory fact no decompile answers.
/// 37 adds RefiiHostGetPresentations and gives RefiiHostGetUiElements a leading presentation
///    argument (0 keeps the old front-end meaning). The old walker went through `gui+188`, a
///    field only the derived front-end GUI has, so the HUD and the thirty in-game menus were
///    unreachable through it.
/// 38 widens RefiiHostGetPresentations to stride 5, adding whether the presentation is
///    actually SHOWING — the root element's `active` bit (0x2000), the freeze byte, and the
///    guest's own per-frame active-element count. Live in the registry is NOT visible; the
///    guest keeps closed menus standing up permanently.
/// 39 adds the presentation-level SUPPRESSION byte `pres+0x368` to the state word (bit 2).
///    That is the byte the scene draw itself reads, and the one the game writes to park a
///    menu; the update gate `+0x369` and the root `active` bit are not the whole contract.
/// 40 gives RefiiHostGetUiAttributes a leading presentation argument, matching
///    RefiiHostGetUiElements — opacity is how the game hides the in-game HUD, one
///    `setAttribute` of 0 on Scene.Layer multiplied down the tree.
/// 41 adds RefiiHostGetEntityMorphParts — the model hashes a COMPOSITE character is built
///    from, off CECGraphicAppearanceMorph (component 17). Those characters carry a live
///    skeleton and a zero model hash on component 6, so this is the only route to geometry.
/// 42 widens RefiiHostGetFrontEndModel to 11 words: how many saves the guest has ENUMERATED,
///    from the same vector its own "Load Game" branch tests.
/// 43 adds the audio surface. RefiiHostPullAudio drains the guest's MIXED output and, by
///    draining it, drives the guest's audio clock — see host_audio.cpp. The RefiiHostVoice*
///    calls expose individual voices so Godot can place each sound itself.
/// 44 adds RefiiHostGetSimTick (dword_83319650, the guest's own 15 Hz game tick) and WIDENS
///    RefiiHostGetEntityPose with `out_torn`. That widening is the reason this list matters:
///    changing a signature is as much an ABI break as adding an export, it went in without a
///    bump, and the two sides drifted to 45-against-44. The mismatch is not caught cleanly —
///    it segfaulted in UI startup with one line of log to explain it.
/// 45 adds RefiiHostGetLoadingScreenArt and RefiiHostWorldIsReady — which loading screen the
///    guest picked, and its own "the world is ready" byte.
/// 50 adds RefiiHostWorldIsActive — World+0xA8, the ONE "the world is up and populated" bit\n///    that both the travel and the load-from-save paths write. Replaces a host-side timeout.\n/// 49 adds RefiiHostDescribeGameFlowQueue — the queue as text, because a walk of it kept\n///    coming back empty while a level change was demonstrably in flight.\n/// 48 adds RefiiHostSetWorldBuildPending — the host answering "IsEnginePreLoading" so the\n///    guest waits in ITS OWN loading screen (state 7) until Godot has mounted the region.\n/// 47 adds RefiiHostGetLevelChange — the queued CmdLevelChange itself: its state word, its\n///    destination and its fullLoad flag. game+0x105 is a rising edge only (cleared in state 3,\n///    AFTER the state-2 entity teardown), so it cannot say when to free a scene.\n/// 46 adds RefiiHostGetCurrentLevel — the level the guest is STANDING IN, read from the live
///    World and Level objects. The start globals it replaces are written only by game-START
///    paths, so polling them cannot see a door, a teleporter or fast travel.
REFII_HOST_ABI_EXPORT int RefiiHostBridgeAbiVersion(void) { return 50; }

// ---------------------------------------------------------------------------------
// Audio
// ---------------------------------------------------------------------------------

/// Takes up to `max_frames` mixed frames as interleaved stereo float32.
///
/// THIS CALL IS THE GUEST'S AUDIO CLOCK. Every frame taken here credits the guest for one
/// more, which is what lets its mixer produce the next. Call it only with somewhere to put
/// the audio — draining faster than playback lets the guest run ahead of what is audible,
/// and not calling it at all eventually stalls the guest's audio thread (it recovers by
/// dropping, after a timeout, rather than wedging).
///
/// `out` must hold `max_frames * RefiiHostAudioFloatsPerFrame()` floats. Returns frames
/// written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostPullAudio(float* out, uint32_t max_frames) {
    return refii::vfs::PopHostAudioFrames(out, max_frames);
}

/// The frame geometry, so the host does not hard-code what the binary states.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostAudioSampleRate(void) { return refii::vfs::kGuestAudioSampleRate; }
REFII_HOST_ABI_EXPORT uint32_t RefiiHostAudioChannels(void) { return refii::vfs::kHostAudioChannels; }
REFII_HOST_ABI_EXPORT uint32_t RefiiHostAudioSamplesPerFrame(void) {
    return refii::vfs::kGuestAudioSamplesPerFrame;
}
REFII_HOST_ABI_EXPORT uint32_t RefiiHostAudioFloatsPerFrame(void) { return refii::vfs::kHostAudioFloatsPerFrame; }

/// Health. `pushed` should settle at 187.5/s (48000/256) once the guest is producing —
/// a rate that is not that means the clock is wrong however it sounds. `dropped` climbing
/// means nothing is draining.
REFII_HOST_ABI_EXPORT void RefiiHostAudioStats(uint32_t* out_queued_frames, uint64_t* out_pushed,
                         uint64_t* out_popped, uint64_t* out_dropped,
                         int* out_driver_installed) {
    if (out_queued_frames != nullptr) {
        *out_queued_frames = refii::vfs::HostAudioQueuedFrames();
    }
    uint64_t pushed = 0;
    uint64_t popped = 0;
    uint64_t dropped = 0;
    refii::vfs::HostAudioCounters(&pushed, &popped, &dropped);
    if (out_pushed != nullptr) {
        *out_pushed = pushed;
    }
    if (out_popped != nullptr) {
        *out_popped = popped;
    }
    if (out_dropped != nullptr) {
        *out_dropped = dropped;
    }
    if (out_driver_installed != nullptr) {
        *out_driver_installed = refii::vfs::HostAudioDriverInstalled() ? 1 : 0;
    }
}

/// One live voice, flattened for P/Invoke. Mirrors refii::vfs::VoiceRecord field for
/// field; keep the two in step.
struct RefiiHostVoice {
    uint32_t handle;
    uint32_t generation;
    uint32_t sample_rate;
    uint32_t channels;
    uint32_t started;
    uint32_t retired;
    float volume;
    float pitch_ratio;
    uint32_t queued_bytes;
    uint32_t submitted_seq;
    float pan_left;
    float pan_right;
};

/// Snapshots the live voices. Pass a null buffer to count. Returns how many exist, which
/// may exceed `capacity` — the host should grow and ask again rather than lose voices.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetVoices(RefiiHostVoice* out, uint32_t capacity) {
    static_assert(sizeof(RefiiHostVoice) == sizeof(refii::vfs::VoiceRecord),
                  "RefiiHostVoice must mirror VoiceRecord exactly; a mismatch marshals "
                  "garbage into C# rather than failing to build");

    if (out == nullptr) {
        return refii::vfs::TakeVoiceRecords(nullptr, 0);
    }
    return refii::vfs::TakeVoiceRecords(reinterpret_cast<refii::vfs::VoiceRecord*>(out),
                                        capacity);
}

/// Drains one voice's PCM as interleaved 16-bit samples. `generation` guards against the
/// guest recycling a handle: a mismatch returns 0 rather than the wrong sound's audio.
/// Returns bytes written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakeVoicePcm(uint32_t handle, uint32_t generation, void* out,
                               uint32_t capacity) {
    return refii::vfs::TakeVoicePcm(handle, generation, out, capacity);
}

/// True once the voice hooks have seen any voice at all — distinguishes "Stage 2 is built
/// but the guest has not reached audio" from "Stage 2 is not running".
REFII_HOST_ABI_EXPORT int RefiiHostVoiceCaptureActive(void) { return refii::vfs::VoiceCaptureActive() ? 1 : 0; }

/// Hands per-voice playback to the host. While set, the guest's MIXED frame is discarded
/// instead of queued — Godot is playing the individual voices and playing both would
/// double every sound. The mixer keeps running and keeps being credited, because that is
/// the clock and it does not stop being the clock just because nobody is listening.
REFII_HOST_ABI_EXPORT void RefiiHostSetVoicePlaybackOwned(int owned) {
    refii::vfs::SetVoiceCaptureOwnsOutput(owned != 0);
}

/// Asks the guest to perform a front-end action, BY NAME.
///
/// It does not happen here and it does not happen now. The name is queued, and the guest
/// picks it up on its own tick thread inside `sub_822C1BD0` — see guest_tick_pump.cpp. That
/// indirection is the entire point: this call is safe from Godot's thread precisely because
/// it touches no guest state, and the work lands where the guest's own code would do it.
///
/// Returns the queue depth after posting. A depth that climbs is a pump that has stopped
/// draining — usually because the front end is not up, which holds actions rather than
/// dropping them.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostPostFrontEndAction(const char* name) {
    return refii::vfs::PostGuestTickAction(name);
}

/// The front end's own storage criteria, so a host-drawn menu gates on what the guest gates on.
///
/// THREE NUMBERS, AND THEY COME FROM DIFFERENT PLACES ON PURPOSE.
///
/// `signed_in` is `(1 << *0x83496AF8) & *0x83496A70` — the active XAM user index against the
/// signed-in bitmask that `sub_8230AEE8` rebuilds on notification 0xA. Pure memory, so it is
/// read here rather than posted.
///
/// `limit` is `dword_83319620`. The image holds 150; `sub_82378618` — the front-end enter,
/// vtable slot 0x820A9260 — unconditionally stores 6 over it before the menu can dispatch. So
/// this is read live and is only meaningful once the front end has entered, which is the same
/// moment the count below becomes meaningful.
///
/// `count` CANNOT be read. It is the return value of `sub_82443308`, a guest call, so it comes
/// from the tick pump's sample — see SampleSavedGameCount in guest_tick_pump.cpp. -1 means the
/// pump has not taken one yet, and a caller must not read that as "no saves": the front-end
/// menu is drawn from this, and answering 0 too early is how Load Game goes missing on a
/// profile that has six saves.
///
/// These are the exact inputs to the guest's two decisions. Load Game spawns when
/// `signed_in && count > 0` (`sub_826E2220` at 0x826E2264..0x826E2278); New Game is refused
/// with GUI_REACHED_SAVE_GAME_LIMIT_WARNING when `count >= limit` (0x826E0148).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSaveState(uint32_t* out_signed_in, int32_t* out_count,
                               uint32_t* out_limit) {
    if (out_signed_in != nullptr) {
        *out_signed_in = 0;
    }
    if (out_count != nullptr) {
        *out_count = -1;
    }
    if (out_limit != nullptr) {
        *out_limit = 0;
    }

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    auto read32 = [base](uint32_t address) -> uint32_t {
        uint32_t value = 0;
        std::memcpy(&value, base + address, sizeof(value));
        return __builtin_bswap32(value);
    };

    constexpr uint32_t kActiveUserIndex = 0x83496AF8u;
    constexpr uint32_t kSignedInMask = 0x83496A70u;
    constexpr uint32_t kSaveSlotLimit = 0x83319620u;

    const uint32_t user = read32(kActiveUserIndex);
    const uint32_t mask = read32(kSignedInMask);

    if (out_signed_in != nullptr) {
        // Guarded shift: the index is a 0..3 XAM user index, but it is guest memory and a
        // shift by 32 or more is undefined rather than merely wrong.
        *out_signed_in = (user < 32u && ((mask >> user) & 1u) != 0u) ? 1u : 0u;
    }
    if (out_count != nullptr) {
        *out_count = refii::vfs::GuestSavedGameCount();
    }
    if (out_limit != nullptr) {
        *out_limit = read32(kSaveSlotLimit);
    }
    return 1;
}

/// Asks the pump to re-take the saved-game count on its next front-end tick.
REFII_HOST_ABI_EXPORT void RefiiHostRefreshSaveState() {
    refii::vfs::RefreshGuestSavedGameCount();
}

/// The guest's installed-language table, so a host drawing the language menu draws the real one.
///
/// Base `dword_83319930`, stride 96, count `dword_83319934`, current index `dword_83319938`.
/// Within a row, `+0` is the ASCII locale code ("en-uk") and `+88` is a `WCHAR*` DISPLAY NAME.
/// The NAME is what the guest spawns its menu entry with — `sub_826E1C98` hands it straight to
/// the spawner at 0x826E1E88 and the front end resolves it through Babel afterwards — so that
/// is the string a host wants, not the locale code.
///
/// Names are written to `names` as NUL-separated UTF-8 in table order, so the caller splits on
/// the terminators. Returns the number of entries reported, or 0 when the table is not up.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetLanguages(uint32_t* out_current, char* names, uint32_t capacity) {
    if (out_current != nullptr) {
        *out_current = 0;
    }
    if (names != nullptr && capacity > 0) {
        names[0] = '\0';
    }

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    auto read32 = [base](uint32_t address) -> uint32_t {
        uint32_t value = 0;
        std::memcpy(&value, base + address, sizeof(value));
        return __builtin_bswap32(value);
    };

    auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    constexpr uint32_t kLanguageTable = 0x83319930u;
    constexpr uint32_t kLanguageCount = 0x83319934u;
    constexpr uint32_t kLanguageCurrent = 0x83319938u;
    constexpr uint32_t kLanguageStride = 96u;
    constexpr uint32_t kLanguageNameOffset = 88u;

    const uint32_t table = read32(kLanguageTable);
    const uint32_t count = read32(kLanguageCount);
    if (!plausible(table) || count == 0 || count > 64u) {
        return 0;
    }

    if (out_current != nullptr) {
        *out_current = read32(kLanguageCurrent);
    }

    uint32_t written = 0;
    uint32_t reported = 0;
    for (uint32_t i = 0; i < count; ++i) {
        const uint32_t wide = read32(table + (i * kLanguageStride) + kLanguageNameOffset);
        ++reported;

        if (names == nullptr || capacity == 0) {
            continue;
        }

        // UTF-16 BE in guest memory, encoded out as UTF-8. Only the basic plane is handled:
        // a surrogate pair would be a language name outside it, which none of the shipped
        // locales are, and emitting a lone surrogate is worse than stopping at it.
        if (plausible(wide)) {
            for (uint32_t c = 0; c < 128u; ++c) {
                uint16_t unit = 0;
                std::memcpy(&unit, base + wide + (c * 2u), sizeof(unit));
                unit = __builtin_bswap16(unit);
                if (unit == 0) {
                    break;
                }

                char encoded[3];
                uint32_t length = 0;
                if (unit < 0x80u) {
                    encoded[0] = static_cast<char>(unit);
                    length = 1;
                } else if (unit < 0x800u) {
                    encoded[0] = static_cast<char>(0xC0u | (unit >> 6));
                    encoded[1] = static_cast<char>(0x80u | (unit & 0x3Fu));
                    length = 2;
                } else {
                    encoded[0] = static_cast<char>(0xE0u | (unit >> 12));
                    encoded[1] = static_cast<char>(0x80u | ((unit >> 6) & 0x3Fu));
                    encoded[2] = static_cast<char>(0x80u | (unit & 0x3Fu));
                    length = 3;
                }

                if (written + length + 1u >= capacity) {
                    break;
                }

                for (uint32_t b = 0; b < length; ++b) {
                    names[written++] = encoded[b];
                }
            }
        }

        if (written + 1u < capacity) {
            names[written++] = '\0';
        }
    }

    if (names != nullptr && written < capacity) {
        names[written] = '\0';
    }

    return reported;
}

/// Pump health: ticks observed, actions run, actions still queued.
///
/// Three numbers rather than one because they fail differently. Zero ticks is a hook that
/// never landed. Ticks climbing with a non-zero queue is the guest running but not yet ready
/// to take the action. Ticks climbing with an empty queue and no drains is nobody posting.
REFII_HOST_ABI_EXPORT void RefiiHostGuestTickPump(uint64_t* out_ticks, uint64_t* out_drained,
                            uint32_t* out_queued) {
    refii::vfs::GuestTickPumpCounters(out_ticks, out_drained, out_queued);
}

/// Publishes one controller's state, in XInput terms, for the guest to read.
///
/// Godot owns input the same way it owns the window: it reads the real device and pushes
/// a snapshot here, and the guest's XInput calls are served from it. Translating to Xbox
/// vocabulary happens once, on the way in.
///
/// `buttons` is a mask of X_INPUT_GAMEPAD_* bits; triggers are 0..255; thumbsticks are
/// -32768..32767 with +Y up, which is the Xbox convention and NOT Godot's screen-space
/// one — the caller flips it.
///
/// Safe to call every frame from the host's main thread while the guest reads on its
/// own; the slot is a seqlock, so neither side blocks the other.
REFII_HOST_ABI_EXPORT void RefiiHostSetControllerState(uint32_t user_index, int connected, uint32_t buttons,
                                 uint32_t left_trigger, uint32_t right_trigger,
                                 int32_t thumb_lx, int32_t thumb_ly, int32_t thumb_rx,
                                 int32_t thumb_ry) {
    const auto clamp_axis = [](int32_t value) {
        if (value < -32768) return static_cast<int16_t>(-32768);
        if (value > 32767) return static_cast<int16_t>(32767);
        return static_cast<int16_t>(value);
    };

    refii::host::ControllerState state;
    state.connected = connected != 0;
    state.buttons = static_cast<uint16_t>(buttons & 0xFFFFu);
    state.left_trigger = static_cast<uint8_t>(left_trigger > 255 ? 255 : left_trigger);
    state.right_trigger = static_cast<uint8_t>(right_trigger > 255 ? 255 : right_trigger);
    state.thumb_lx = clamp_axis(thumb_lx);
    state.thumb_ly = clamp_axis(thumb_ly);
    state.thumb_rx = clamp_axis(thumb_rx);
    state.thumb_ry = clamp_axis(thumb_ry);

    refii::host::PublishControllerState(user_index, state);
}

/// Copies the current Bink video frame out of guest memory as RGBA8.
///
/// Fable 2 decodes Bink on the CPU into four 8-bit LINEAR planes (Y, cR, cB, A) that the
/// GPU used to sample in place. With no GPU those planes are still produced and still
/// correct, so showing video needs no emulation at all — only this read and a colour
/// conversion. Layout confirmed against ExpandBink (0x832C49E8), whose FIRST ARGUMENT is
/// the BINKFRAMEBUFFERS block:
///
///   FB+4/+8    luma width/height        FB+12/+16  chroma width/height
///   FB+20      FrameNum (2 frames, alternating)
///   FB+24 + 48*n + 12*k + 4/+8         plane k Buffer / pitch  (k: 0=Y 1=cR 2=cB 3=A)
///
/// CHROMA IS NOT HALF LUMA — every dimension is independently (x+7) & ~7, so the chroma
/// sizes are read from the struct and the sample position is scaled by the real ratio.
///
/// Writes width*height*4 bytes of RGBA into `out_rgba`. Returns 1 on success, 0 when no
/// video is playing or the buffer is too small; `out_width`/`out_height` are filled in
/// either way when known, so a caller can size its buffer from a failed probe.
REFII_HOST_ABI_EXPORT int RefiiHostGetVideoFrame(uint8_t* out_rgba, uint32_t capacity, uint32_t* out_width,
                           uint32_t* out_height) {
    const uint32_t fb = refii::vfs::BinkFrameBuffersGuestAddress();
    if (fb == 0) {
        return 0;
    }

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto load_be32 = [&](uint32_t at) {
        uint32_t raw = 0;
        std::memcpy(&raw, base + at, sizeof(raw));
        return __builtin_bswap32(raw);
    };

    const uint32_t luma_width = load_be32(fb + 4);
    const uint32_t luma_height = load_be32(fb + 8);
    const uint32_t chroma_width = load_be32(fb + 12);
    const uint32_t chroma_height = load_be32(fb + 16);
    const uint32_t total_frames = load_be32(fb + 0);
    // Read the OTHER frame, not the one FrameNum names.
    //
    // TotalFrames is 2 because this is a double buffer, and ExpandBink decodes INTO the
    // new frame and then sets FrameNum to it (`*(FB+20) = v52 ^ 1` at 0x832c4b3c, with
    // the expander's destination taken from that same index). So FrameNum is the frame
    // being written; reading it races the decoder and produces a picture assembled from
    // two different moments — the Lionhead logo came out sheared and banded, which reads
    // as a stride bug but is tearing. The previous frame is complete and stable.
    const uint32_t current = load_be32(fb + 20) & 1u;
    const uint32_t frame = total_frames > 1 ? (current ^ 1u) : current;


    // A STRUCTURAL plausibility gate, not just a size clamp.
    //
    // The block is only meaningful while a movie is live. Once a logo finishes and the
    // player is reused the same memory reads as whatever came next, and a pure size
    // clamp happily accepts it: a run produced a correct 1280x720 frame and then
    // "2258x2257", which is under any sane cap and would have been uploaded as a frame.
    // TotalFrames is the strongest discriminator — Bink uses 1 or 2 — and the pitch and
    // chroma relations have to hold for any real plane set.
    if (total_frames == 0 || total_frames > 2) {
        return 0;
    }

    if (luma_width < 16 || luma_height < 16 || luma_width > 4096 || luma_height > 4096 ||
        chroma_width == 0 || chroma_height == 0 || chroma_width > luma_width ||
        chroma_height > luma_height) {
        return 0;
    }

    // Dimensions are published only once the block has PASSED validation. Reporting them
    // earlier meant a caller that ignored the return value sized a texture from a torn
    // read — one run allocated 2258x2257 from a dead player's memory.
    if (out_width != nullptr) {
        *out_width = luma_width;
    }
    if (out_height != nullptr) {
        *out_height = luma_height;
    }

    // A null buffer is a SIZE PROBE and succeeds: the caller is asking whether video is
    // live and how big it is. Returning 0 here would make "no video playing" and "your
    // buffer is too small" indistinguishable, and the caller would size against garbage.
    if (out_rgba == nullptr) {
        return 1;
    }

    if (capacity < luma_width * luma_height * 4u) {
        return 0;
    }

    const uint32_t plane_base = fb + 24u + (48u * frame);
    const uint32_t y_ptr = load_be32(plane_base + 4);
    const uint32_t y_pitch = load_be32(plane_base + 8);
    const uint32_t cr_ptr = load_be32(plane_base + 12 + 4);
    const uint32_t cr_pitch = load_be32(plane_base + 12 + 8);
    const uint32_t cb_ptr = load_be32(plane_base + 24 + 4);
    const uint32_t cb_pitch = load_be32(plane_base + 24 + 8);

    // A plane's pitch cannot be narrower than its own width, and the buffers must sit in
    // the guest's 32-bit space with room for the whole plane.
    if (y_ptr == 0 || cr_ptr == 0 || cb_ptr == 0) {
        return 0;
    }

    if (y_pitch < luma_width || cr_pitch < chroma_width || cb_pitch < chroma_width) {
        return 0;
    }

    const auto plane_fits = [](uint32_t ptr, uint32_t pitch, uint32_t rows) {
        const uint64_t end = (uint64_t)ptr + ((uint64_t)pitch * rows);
        return ptr != 0 && end <= 0x100000000ull;
    };

    if (!plane_fits(y_ptr, y_pitch, luma_height) ||
        !plane_fits(cr_ptr, cr_pitch, chroma_height) ||
        !plane_fits(cb_ptr, cb_pitch, chroma_height)) {
        return 0;
    }

    // One-shot, AFTER validation: reporting before it burned the single log on the
    // window between the movie-open hook (which runs before the original fills the
    // block) and BinkGetFrameBuffersInfo actually populating it.
    {
        static std::atomic<bool> announced{false};
        if (!announced.exchange(true)) {
            REXLOG_INFO(
                "Refii video: total={} luma={}x{} chroma={}x{} frame={} "
                "y={{0x{:08X} pitch {}}} cr={{0x{:08X} pitch {}}} cb={{0x{:08X} pitch {}}}",
                total_frames, luma_width, luma_height, chroma_width, chroma_height,
                frame, y_ptr, y_pitch, cr_ptr, cr_pitch, cb_ptr, cb_pitch);
        }
    }

    const uint8_t* y_plane = base + y_ptr;
    const uint8_t* cr_plane = base + cr_ptr;
    const uint8_t* cb_plane = base + cb_ptr;

    for (uint32_t row = 0; row < luma_height; ++row) {
        // Scale into chroma by the REAL ratio rather than assuming 2:1 — the planes are
        // 8-aligned independently, so at some sizes they are not exactly half.
        const uint32_t chroma_row =
            (uint32_t)((uint64_t)row * chroma_height / luma_height);
        const uint8_t* y_row = y_plane + ((uint64_t)row * y_pitch);
        const uint8_t* cr_row = cr_plane + ((uint64_t)chroma_row * cr_pitch);
        const uint8_t* cb_row = cb_plane + ((uint64_t)chroma_row * cb_pitch);
        uint8_t* out_row = out_rgba + ((uint64_t)row * luma_width * 4u);

        for (uint32_t col = 0; col < luma_width; ++col) {
            const uint32_t chroma_col =
                (uint32_t)((uint64_t)col * chroma_width / luma_width);

            // BT.601 studio-swing, which is what Bink encodes.
            const int32_t y = (int32_t)y_row[col] - 16;
            const int32_t u = (int32_t)cb_row[chroma_col] - 128;
            const int32_t v = (int32_t)cr_row[chroma_col] - 128;

            const int32_t c = 298 * y;
            const auto clamp = [](int32_t value) {
                if (value < 0) return (uint8_t)0;
                if (value > 255) return (uint8_t)255;
                return (uint8_t)value;
            };

            out_row[col * 4 + 0] = clamp((c + 409 * v + 128) >> 8);
            out_row[col * 4 + 1] = clamp((c - 100 * u - 208 * v + 128) >> 8);
            out_row[col * 4 + 2] = clamp((c + 516 * u + 128) >> 8);
            out_row[col * 4 + 3] = 255;
        }
    }

    return 1;
}

/// Copies the current Bink frame's PLANES out, for the GPU to combine.
///
/// The RGBA variant above converts 921,600 pixels on the CPU every frame. This hands the
/// three 8-bit planes over untouched — about 1.4 MB against 3.7 MB, with no arithmetic —
/// and lets a shader do the colour conversion where that work is free. It is also less
/// lossy: no intermediate rounding, and chroma is sampled by the GPU's own filtering
/// rather than by nearest-neighbour integer scaling.
///
/// Each plane is copied ROW BY ROW into a tightly packed destination, because the guest's
/// pitch is not the width — chroma here is 640 wide with a 768-byte pitch, and handing
/// that to a texture upload as-is produces a skewed image.
///
/// `out_desc` receives 6 uint32s: luma w, luma h, chroma w, chroma h, and the two plane
/// sizes actually written (luma, chroma). Returns 1 on success.
REFII_HOST_ABI_EXPORT int RefiiHostGetVideoPlanes(uint8_t* out_y, uint32_t y_capacity, uint8_t* out_cr,
                            uint32_t cr_capacity, uint8_t* out_cb, uint32_t cb_capacity,
                            uint32_t* out_desc) {
    const uint32_t fb = refii::vfs::BinkFrameBuffersGuestAddress();
    if (fb == 0) {
        return 0;
    }

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto load_be32 = [&](uint32_t at) {
        uint32_t raw = 0;
        std::memcpy(&raw, base + at, sizeof(raw));
        return __builtin_bswap32(raw);
    };

    const uint32_t luma_width = load_be32(fb + 4);
    const uint32_t luma_height = load_be32(fb + 8);
    const uint32_t chroma_width = load_be32(fb + 12);
    const uint32_t chroma_height = load_be32(fb + 16);
    const uint32_t total_frames = load_be32(fb + 0);

    // Same double-buffer rule as the RGBA path: FrameNum names the frame being written.
    const uint32_t current = load_be32(fb + 20) & 1u;
    const uint32_t frame = total_frames > 1 ? (current ^ 1u) : current;

    if (total_frames == 0 || total_frames > 2) {
        return 0;
    }

    if (luma_width < 16 || luma_height < 16 || luma_width > 4096 || luma_height > 4096 ||
        chroma_width == 0 || chroma_height == 0 || chroma_width > luma_width ||
        chroma_height > luma_height) {
        return 0;
    }

    const uint32_t plane_base = fb + 24u + (48u * frame);
    const uint32_t y_ptr = load_be32(plane_base + 4);
    const uint32_t y_pitch = load_be32(plane_base + 8);
    const uint32_t cr_ptr = load_be32(plane_base + 16);
    const uint32_t cr_pitch = load_be32(plane_base + 20);
    const uint32_t cb_ptr = load_be32(plane_base + 28);
    const uint32_t cb_pitch = load_be32(plane_base + 32);

    if (y_ptr == 0 || cr_ptr == 0 || cb_ptr == 0 || y_pitch < luma_width ||
        cr_pitch < chroma_width || cb_pitch < chroma_width) {
        return 0;
    }

    const auto plane_fits = [](uint32_t ptr, uint32_t pitch, uint32_t rows) {
        return ptr != 0 && ((uint64_t)ptr + ((uint64_t)pitch * rows)) <= 0x100000000ull;
    };

    if (!plane_fits(y_ptr, y_pitch, luma_height) ||
        !plane_fits(cr_ptr, cr_pitch, chroma_height) ||
        !plane_fits(cb_ptr, cb_pitch, chroma_height)) {
        return 0;
    }

    const uint32_t luma_bytes = luma_width * luma_height;
    const uint32_t chroma_bytes = chroma_width * chroma_height;

    if (out_desc != nullptr) {
        out_desc[0] = luma_width;
        out_desc[1] = luma_height;
        out_desc[2] = chroma_width;
        out_desc[3] = chroma_height;
        out_desc[4] = luma_bytes;
        out_desc[5] = chroma_bytes;
    }

    // Null buffers are a size probe, as with the RGBA path.
    if (out_y == nullptr || out_cr == nullptr || out_cb == nullptr) {
        return 1;
    }

    if (y_capacity < luma_bytes || cr_capacity < chroma_bytes ||
        cb_capacity < chroma_bytes) {
        return 0;
    }

    const auto copy_plane = [&](uint32_t ptr, uint32_t pitch, uint32_t width,
                                uint32_t height, uint8_t* dst) {
        for (uint32_t row = 0; row < height; ++row) {
            std::memcpy(dst + ((std::size_t)row * width),
                        base + ptr + ((std::size_t)row * pitch), width);
        }
    };

    copy_plane(y_ptr, y_pitch, luma_width, luma_height, out_y);
    copy_plane(cr_ptr, cr_pitch, chroma_width, chroma_height, out_cr);
    copy_plane(cb_ptr, cb_pitch, chroma_width, chroma_height, out_cb);
    return 1;
}

/// Copies the .bik the guest just handed to BinkOpen, so the HOST can play it.
///
/// For the startup logos the guest loads the file itself and passes a BUFFER, so this
/// hands the bytes straight over — no second read of a file already paid for. Every
/// movie AFTER the logos is opened by NAME instead; those carry no buffer and come back
/// from RefiiHostGetRequestedVideoName, which the host resolves itself.
///
/// Pass a null buffer to probe the size. `out_generation` changes on every request,
/// including a repeat of the same movie, because the startup sequence replays logos.
/// Returns 1 on success, 0 when nothing has been requested or the buffer is too small.
REFII_HOST_ABI_EXPORT int RefiiHostGetRequestedVideo(unsigned char* out, uint32_t capacity, uint32_t* out_size,
                               uint32_t* out_generation) {
    uint32_t address = 0;
    uint32_t size = 0;
    uint32_t generation = 0;
    uint32_t swapped = 0;
    refii::vfs::RequestedBinkVideo(&address, &size, &generation, &swapped);

    if (out_generation != nullptr) {
        *out_generation = generation;
    }
    if (out_size != nullptr) {
        *out_size = size;
    }

    if (address == 0 || size == 0) {
        return 0;
    }

    if (out == nullptr) {
        return 1;  // size probe
    }

    if (capacity < size) {
        return 0;
    }

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    // The guest's loader byte-swaps every DWORD on the way in — a big-endian console
    // reading a little-endian container — so the bytes in memory read 'iKIB' rather than
    // 'BIKi'. Handing that to FFmpeg decodes nothing. Swapping back on the way out
    // restores the file exactly as it is on disc.
    if (swapped != 0) {
        const uint32_t whole = size & ~3u;
        for (uint32_t at = 0; at < whole; at += 4) {
            uint32_t word = 0;
            std::memcpy(&word, base + address + at, sizeof(word));
            word = __builtin_bswap32(word);
            std::memcpy(out + at, &word, sizeof(word));
        }

        // A trailing partial dword keeps its order: it was never a whole word to swap.
        if (whole < size) {
            std::memcpy(out + whole, base + address + whole, size - whole);
        }

        return 1;
    }

    std::memcpy(out, base + address, size);
    return 1;
}

/// The PATH form of the same request — "game:\data\art\videos\....bik".
///
/// The startup logos come through as a buffer; everything after them is opened by name,
/// and the guest never loads those itself. The host resolves the path against the videos
/// folder it already indexes, which is also the only way a movie the guest opens but
/// never reads can be played at all.
///
/// `out_generation` is the SAME counter as the buffer form: a request is one or the
/// other, so the host takes whichever of the two has content for the current generation.
/// Returns the length written, 0 when the last request carried a buffer instead.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetRequestedVideoName(char* out, uint32_t capacity,
                                        uint32_t* out_generation) {
    uint32_t generation = 0;
    refii::vfs::RequestedBinkVideo(nullptr, nullptr, &generation, nullptr);
    if (out_generation != nullptr) {
        *out_generation = generation;
    }

    return refii::vfs::RequestedBinkVideoName(out, capacity);
}

/// Non-zero while the guest is IN its front end (title screen / main menu).
///
/// IDA-proven, and deliberately NOT the mode singleton `dword_83496AC0`. That global is a
/// pointer stored when `sub_8231A908` CONSTRUCTS the front-end mode (0x8231A988) and it
/// stays set forever after — so a host reading it would keep the menu on screen right
/// through gameplay. `byte_83496AC4` is the flag the front end actually maintains, with
/// exactly two writers: `= 1` in its OnEnter (0x82319360) and `= 0` in its OnExit
/// (0x82319B24).
///
/// The guest only enters that mode once every startup logo has played AND the "Permanent
/// Bank Mounter" handshake has completed — the boot-movie loop blocks at 0x82377D74 on
/// `sub_822F3B20` until the mounter sets its +5 byte. So this going true is the title
/// SAYING the boot sequence is over, rather than the host timing a movie and guessing.
/// Whether the guest is IN GAME, as the guest itself states it.
///
/// `byte_83496A5E` — set to 1 by `Game_InitDatabases` at 0x82311648 and cleared by the game
/// mode's shutdown at 0x82311860, with ~150 readers. This is the signal to use rather than
/// inferring from the front end going away: `dword_83496B08` is re-pointed at a 176-byte
/// base-only GUI at that moment, so every front-end field reads out of bounds.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostIsInGame(void) {
    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    return base == nullptr ? 0u : base[0x83496A5Eu];
}

/// The guest's SIMULATION TICK, as the guest itself counts it.
///
/// `dword_83319650` is incremented by the world update, and it is the only thing in the
/// process that states when the simulation has actually advanced. Everything else a host
/// might time against — the audio clock, Godot's frame delta, wall time — measures OUR
/// clocks, and the whole question this answers is how the guest's clock relates to ours.
///
/// Two uses, and they are why it is exported rather than inferred:
///
/// 1. RATE. Sampled against wall time it gives the simulation rate directly. The sim is
///    paced by the audio device (the guest blocks in `PushHostAudioFrame`), and Godot
///    presents on vsync; those are two independent clocks, so a state that advances N times
///    a second sampled M times a second shows every character stepping unevenly however
///    correct each individual sample is. Knowing N is the difference between fixing that and
///    guessing at it.
///
/// 2. COHERENCE. Read before and after a pose copy it brackets the read. The rig arrays are
///    memcpy'd out of guest memory from Godot's thread while the simulation thread writes
///    them, with no lock on either side — so a straddled read is possible by construction,
///    and this is the cheapest thing that can DETECT one. `RefiiHostGetCamera` solves the
///    same problem by reading twice and comparing, which costs a second copy of the payload;
///    a rig is up to 160 bones of 48 bytes and does not deserve that.
///
/// Zero means the guest is absent. Callers must compare for INEQUALITY and must not assume
/// the value only ever grows, or grows by one: whether a level load resets it is not settled,
/// and a rate derived from a backwards step would be nonsense reported as fact.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetSimTick(void) {
    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    uint32_t raw;
    std::memcpy(&raw, base + 0x83319650u, sizeof(raw));
    return __builtin_bswap32(raw);
}

/// The guest's live world clock — `GameTimeManager`'s day fraction.
///
/// THE SKY AND THE LIGHTING HAVE TO FOLLOW THIS OR THEY FOLLOW NOTHING. Everything the host
/// draws from the environment theme — sun direction, sun colour, ambient, fog, cloud tint — is
/// selected and blended by a day fraction, and until this existed that fraction was whatever
/// day-set entry happened to be chosen at level load, frozen for the lifetime of the region.
///
/// The object names itself: `sub_82515D98` serialises it under the class tag "GameTimeManager"
/// and passes each field beside its own literal — "Time" at +4, "DayCount" at +8, "TimeStep" at
/// +12. The Lua binding registrar agrees independently, `Timing.GetTimeOfDay` being
/// `sub_8288B918`: `return *(float *)(a1 + 4);`.
///
/// The chain was read out of `sub_8218EAF8`, the day-set theme evaluator, which is the caller
/// that hands the fraction to `sub_8218E2F0`:
///
///     8218eb24  lwz  r9, 0xC(r11)     ; root  -> X
///     8218eb34  lwz  r8, 0x58(r9)     ; X+0x58 -> Y
///     8218eb38  lwz  r7, 4(r8)        ; Y+4    -> W
///     8218eb3c  lwz  r6, 0x14(r7)     ; W+0x14 -> T = GameTimeManager
///     8218eb40  lfs  f31, 4(r6)       ; T+4    = the day fraction
///
/// USING THE SAME CHAIN AS THE THEME SELECTOR IS THE POINT. `sub_821DF670` reaches the same T
/// by the same offsets to aim the sun, so a host that blends its themes on this value
/// reproduces the guest's own selection rather than approximating it.
///
/// THE CHAIN IS RE-WALKED EVERY CALL, NEVER CACHED. `sub_8234F320` stores T into W+0x14 after
/// virtually destroying whatever was there, and W's destructor writes a NULL back at
/// `0x823523D8` — so the whole subtree is torn down and rebuilt across a world re-init, and a
/// cached pointer would be a use-after-free rather than a stale number.
///
/// Returns 0 when the guest is absent or the chain has not been built yet; the outputs are
/// untouched in that case. Every link is checked because the pointer walk crosses objects the
/// guest itself only builds at game-database init.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetTimeOfDay(float* out_fraction, int32_t* out_day, int32_t* out_frozen) {
    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto load_be32 = [&](uint32_t guest_address) {
        uint32_t raw = 0;
        std::memcpy(&raw, base + guest_address, sizeof(raw));
        return __builtin_bswap32(raw);
    };

    const uint32_t root = load_be32(0x83496AB8u);
    if (root == 0) {
        return 0;
    }

    const uint32_t x = load_be32(root + 0x0Cu);
    if (x == 0) {
        return 0;
    }

    const uint32_t y = load_be32(x + 0x58u);
    if (y == 0) {
        return 0;
    }

    const uint32_t world = load_be32(y + 0x04u);
    if (world == 0) {
        return 0;
    }

    const uint32_t timing = load_be32(world + 0x14u);
    if (timing == 0) {
        return 0;
    }

    // THE VTABLE IS THE IDENTITY CHECK. 0x820BF8A8 is a one-slot vtable with exactly one
    // construction site in the whole image (0x8234F2C4), so nothing else can present it. Without
    // this a half-built chain hands back whatever the allocator left behind, and a garbage day
    // fraction reads as the sun being in the wrong place rather than as a bad pointer.
    if (load_be32(timing + 0x00u) != 0x820BF8A8u) {
        return 0;
    }

    const uint32_t raw_time = load_be32(timing + 0x04u);
    float fraction = 0.0f;
    std::memcpy(&fraction, &raw_time, sizeof(fraction));

    if (!(fraction >= 0.0f) || !(fraction < 1000.0f)) {
        // NaN or nonsense: the field is a plain float written without synchronisation, and a
        // torn or uninitialised read is better refused than propagated into the sun's angle.
        return 0;
    }

    // TRANSIENTLY >= 1.0 BY CONSTRUCTION. `sub_82298460` advances the fraction at 0x82298508 and
    // does not wrap it until 0x82298548, so a read landing in those sixteen instructions sees a
    // value past the end of the day. The guest's own wrap is a subtract of the truncated integer.
    fraction -= std::floor(fraction);

    if (out_fraction != nullptr) {
        *out_fraction = fraction;
    }

    if (out_day != nullptr) {
        *out_day = static_cast<int32_t>(load_be32(timing + 0x08u));
    }

    if (out_frozen != nullptr) {
        // T+0x24 is the count inside the "StopTimeTracker" at T+0x1C, and `sub_82298460` gates
        // the whole advance on it being zero.
        *out_frozen = load_be32(timing + 0x24u) != 0 ? 1 : 0;
    }

    return 1;
}

/// The render-frame fence the LEVEL LOAD waits on — state 7 of `CmdLevelChange_Update`.
///
/// `CmdLevelChange_Update` (0x823D5AA8) is a nine-state polled machine on `cmd+44`, and
/// every one of its gates fails SILENTLY: it returns each tick, logs nothing, and the load
/// simply never advances. State 7 is the one a host with no GPU has to answer for:
///
///     v83 = mode->vtbl[+16](dword_83496AB8);          /*0x823D65C8*/  -> *(*(a1+12)+104)
///     if (!sub_82387B38(*(v83 + 4))) { *(cmd+44) = 8; }
///
///     int sub_82387B38(int a1) {                       // under the render critical section
///       if (*(double *)(a1 + 152) <= *(double *)(a1 + 176) || *(BYTE *)(a1 + 600))
///         return 1;                                    // BUSY
///       return 0;                                      // idle — the load may proceed
///     }
///
/// A submitted-vs-retired pair plus a busy byte. With nothing retiring frames it answers
/// BUSY forever and the level change parks in state 7. This reports the three values so a
/// run can say which of them is holding, instead of the load being invisible.
///
/// Reads the chain rather than calling the guest's virtual: slot +16 is literally
/// `return *(*(a1 + 12) + 104);`. Fills manager, submitted, DEADLINE, busy, and five
/// discriminator words. Returns 1 when the chain resolved.
///
/// `+176` IS NOT "RETIRED". It has two writers only — the ctor, and `sub_82388B48` under the
/// `+56` critical section — and it is a render DEADLINE BARRIER: "a frame has been requested
/// for time T, stay busy until the render thread passes T". `+152` is the render thread's
/// current frame time. So `+176` sitting still while `+152` climbs past it is the NORMAL
/// resting state, not a stall; it means nobody has posted a new deadline.
REFII_HOST_ABI_EXPORT int RefiiHostGetRenderFence(uint32_t* out_manager, double* out_submitted,
                            double* out_retired, uint32_t* out_busy,
                            uint32_t* out_arms) {
    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t at) {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };

    const auto read_double = [base](uint32_t at) {
        uint64_t raw = 0;
        std::memcpy(&raw, base + at, sizeof(raw));
        raw = __builtin_bswap64(raw);
        double value = 0.0;
        std::memcpy(&value, &raw, sizeof(value));
        return value;
    };

    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    const uint32_t mode = read32(0x83496AB8u);
    if (!plausible(mode)) {
        return 0;
    }

    const uint32_t owner = read32(mode + 12u);
    if (!plausible(owner)) {
        return 0;
    }

    const uint32_t frame = read32(owner + 104u);
    if (!plausible(frame)) {
        return 0;
    }

    // sub_82387B38's argument is *(v83 + 4), not v83.
    const uint32_t manager = read32(frame + 4u);
    if (!plausible(manager)) {
        return 0;
    }

    if (out_manager != nullptr) *out_manager = manager;
    if (out_submitted != nullptr) *out_submitted = read_double(manager + 152u);
    if (out_retired != nullptr) *out_retired = read_double(manager + 176u);
    if (out_busy != nullptr) *out_busy = base[manager + 600u];

    // WHICH ARM is holding the load, if any. Reads only — nothing here writes guest memory.
    //
    // The object is the "3D Engine" (0x260 bytes, ctor sub_823877E8), and sub_82387B38 is its
    // own `IsEnginePreLoading` — the name comes from the guest's Lua registration in
    // sub_82387DC0, not from a guess. So state 7 of the level change is literally
    // "while (IsEnginePreLoading()) wait".
    //
    // `+600` carries NO state of its own: sub_82388380 re-copies `byte_83496E20` into it every
    // render-thread iteration (`lbz r30, byte_83496E20` / `stb r30, 0x258(r31)`). That global
    // has exactly ONE writer, sub_82181B68, reachable only from the 3D Engine thread, and it
    // is decided by three arms — none of which touches a present, flip, fence or render
    // callback, so nothing in the producer depends on the GPU.
    //
    // Which arm is holding takes a discriminator, and these five values are it:
    //   view/vector cursor `engine+116` and `engine+124` — both -1 means nothing is
    //     recomputing the flag and it is a STALE 1 (sub_82388040 sets +124 to -1 during the
    //     level load; only a re-submitted view clears it),
    //   `0x83496E34` in 1..4 means the resource-preload arm is holding,
    //   `0x8349FB5C` non-zero means the streamed-texture-residency arm is holding,
    //   `0x83496E20` should equal +600, and a disagreement means the mirror itself is stale.
    //
    // The third arm walks the scene container and TIMES ITSELF OUT, so it cannot hold this
    // forever and is not worth reporting.
    if (out_arms != nullptr) {
        out_arms[0] = read32(manager + 116u);
        out_arms[1] = read32(manager + 124u);
        out_arms[2] = read32(0x83496E34u);
        out_arms[3] = read32(0x8349FB5Cu);
        out_arms[4] = base[0x83496E20u];
    }

    return 1;
}

/// The LOADING SCREEN's take-down handshake — the guest thread's one unbounded wait.
///
/// `sub_82361458` tears the loading screen down by asking for it and then blocking forever:
///
///     823614EC  stfs f0, 0xAC(r8)    ; obj+172 = 1.0f   fade
///     823614F0  stb  r10, 0xB0(r8)   ; obj+176 = 1      shutdown REQUESTED
///     823614FC  bl   RtlLeaveCriticalSection
///     82361528  li   r4, -1          ; INFINITE
///     8236152C  lwz  r3, 0xB4(r11)   ; obj+180 = the event
///     82361530  bl   sub_821969C8    ; WaitForSingleObjectEx(h, INFINITE, FALSE)
///
/// The only thing that ever signals that event is `sub_8235E5A0`, and only after the fade
/// counts 1.0 down to ~0 at 1/15 per tick — FIFTEEN ticks. Those ticks come from
/// `sub_82388380` on the guest's "3D Engine" thread, which ticks the loading screen and
/// then PRESENTS. With the renderer off and the guest never presenting, that loop does not
/// run, the fade never moves, the event is never set, and the game thread waits forever
/// with no log line and no error.
///
/// So this reports the handshake itself: fade, "shutdown requested", and the "done" latch
/// (obj+177) that `sub_8235E5A0` sets alongside the signal. A fade pinned at 1.0 with
/// requested=1 and done=0 IS the stall, stated by the guest's own fields.
///
/// Chain: `dword_83496AB8` -> +8 (the shared context) -> +36 (the manager) -> +4 (the
/// 400-byte screen, 0 when none). Fills manager, screen, fade, requested, done. Returns 1
/// when a screen object exists.
REFII_HOST_ABI_EXPORT int RefiiHostGetLoadingScreen(uint32_t* out_manager, uint32_t* out_screen, float* out_fade,
                              uint32_t* out_requested, uint32_t* out_done) {
    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t at) {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };

    const auto read_float = [&read32](uint32_t at) {
        const uint32_t raw = read32(at);
        float value = 0.0f;
        std::memcpy(&value, &raw, sizeof(value));
        return value;
    };

    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    // The same priority the guest's own render loop uses at 0x8238860C: the game mode
    // first, then the front-end mode. Both carry the SAME context at +8, so the manager is
    // one object either way — but reading whichever is live avoids a null during the swap.
    uint32_t context = 0;
    for (uint32_t mode_global : {0x83496AB8u, 0x83496AC0u}) {
        const uint32_t mode = read32(mode_global);
        if (!plausible(mode)) {
            continue;
        }

        const uint32_t candidate = read32(mode + 8u);
        if (plausible(candidate)) {
            context = candidate;
            break;
        }
    }

    if (context == 0) {
        return 0;
    }

    const uint32_t manager = read32(context + 36u);
    if (!plausible(manager)) {
        return 0;
    }

    if (out_manager != nullptr) *out_manager = manager;

    // manager+4 is 0 when no loading screen exists. That is not a failure — it is the
    // answer "there is no screen to be waiting on".
    const uint32_t screen = read32(manager + 4u);
    if (out_screen != nullptr) *out_screen = screen;
    if (!plausible(screen)) {
        return 0;
    }

    if (out_fade != nullptr) *out_fade = read_float(screen + 172u);
    if (out_requested != nullptr) *out_requested = base[screen + 176u];
    if (out_done != nullptr) *out_done = base[screen + 177u];
    return 1;
}

/// WHICH loading screen the guest picked, so a host can draw the art the guest chose.
///
/// THE LOADING SCREEN IS NOT GAMEFACE. `sub_8235F108` submits its elements as direct textured
/// quads through `Render_GpuAccessLock_Enter` → `sub_821E1F40`; nothing in the module
/// (0x8235C000..0x82362000) touches a presentation. So there is no screen to mirror element by
/// element — the host draws the same textures itself, which is what makes this export the whole
/// interface rather than a hint.
///
/// TYPE is `*(u32*)(screen + 304)`, verified in `sub_8235F2F8`'s own switch at 0x8235F328. It
/// selects the art: 6 childhood, 7/8 saving, 9 loading, 0xA quitting, 0xB pubgames, 0xC
/// keystone, 0xD magedice, 0xE wizardstower; 1 is the gendered childhood pair chosen from
/// `g_StartHeroCreatureName`; 0/2/3/4/5 derive it from the destination region.
///
/// NAME is `screen + 272`, and it is an **LHString HANDLE, not a character buffer** — every
/// writer is `sub_822651A0(a1 + 272, <LHString>)`. So it needs TWO dereferences: the word at
/// +272 is a body pointer and the body's own first word is the `char*`. Reading it as a buffer,
/// or with one dereference, yields garbage that looks like a plausible string — the same shape
/// of mistake that once had every movie reported as already abandoned.
///
/// Returns 1 when a name was read. The raw pointers are reported too, so a run where the
/// indirection is wrong shows the addresses rather than silently producing an empty name.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetLoadingScreenArt(uint32_t* out_type, uint32_t* out_body, char* name,
                                      uint32_t capacity) {
    if (out_type != nullptr) *out_type = 0;
    if (out_body != nullptr) *out_body = 0;
    if (name != nullptr && capacity > 0) name[0] = '\0';

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t at) {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };

    const auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    uint32_t manager = 0;
    uint32_t screen = 0;
    float fade = 0.0f;
    uint32_t requested = 0;
    uint32_t done = 0;
    if (RefiiHostGetLoadingScreen(&manager, &screen, &fade, &requested, &done) == 0) {
        return 0;
    }

    if (out_type != nullptr) *out_type = read32(screen + 304u);

    const uint32_t body = read32(screen + 272u);
    if (out_body != nullptr) *out_body = body;
    if (!plausible(body)) {
        return 0;
    }

    const uint32_t text = read32(body);
    if (!plausible(text) || name == nullptr || capacity == 0) {
        return 0;
    }

    uint32_t i = 0;
    for (; i + 1u < capacity; ++i) {
        const char c = static_cast<char>(base[text + i]);
        if (c == '\0') {
            break;
        }
        name[i] = c;
    }
    name[i] = '\0';
    return i > 0 ? 1u : 0u;
}

/// "The world is loaded and ready", which is NOT the loading screen coming down.
///
/// A byte at `game + 0x105`, where `game = *(u32*)(0x83496AB8 + 0x0C)`. `CmdLevelChange_Update`
/// (0x823D5AA8) clears it when the real load begins (state 3, 0x823D60C8) and sets it when the
/// nine-state machine finishes (state 9, 0x823D6704); the game object's constructor
/// (`sub_82310508`) starts it at 1 — "ready" is the BOOT state, before any world exists.
///
/// It IS read by the guest, at 0x82276E84 in the game tick `sub_82276D10`, which gates work on
/// `+0x104 && +0x105`. An earlier version of this comment claimed it was publish-only with no
/// reader in the image; that was wrong. Polling is still safe because we only ever READ it —
/// but nothing may ever WRITE it from the host, which "publish-only" invited.
///
/// USE IT AS A RISING EDGE ONLY. It is cleared in state 3 AFTER the blocking load returns,
/// while the entity teardown happened back in state 2 — so its falling edge is far too late to
/// free a scene by, and there is a window where it reads 1 with the old entities already gone.
/// `RefiiHostGetLevelChange` carries the command's own state word for that.
///
/// Distinct from `game + 0x104`, "a level change is pending", and from the Lua `IsLevelLoading`,
/// which compares the front gameflow command's type id to 4 and is therefore FALSE during a
/// load-from-save (type 5).
REFII_HOST_ABI_EXPORT uint32_t RefiiHostWorldIsReady(void) {
    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    uint32_t mode = 0;
    std::memcpy(&mode, base + 0x83496AB8u, sizeof(mode));
    mode = __builtin_bswap32(mode);
    if (mode < 0x10000u || mode >= 0xC0000000u) {
        return 0;
    }

    uint32_t game = 0;
    std::memcpy(&game, base + mode + 0x0Cu, sizeof(game));
    game = __builtin_bswap32(game);
    if (game < 0x10000u || game >= 0xC0000000u) {
        return 0;
    }

    return base[game + 0x105u] != 0 ? 1u : 0u;
}

/// Whether Godot is still building a region the guest must not be let into yet.
static std::atomic_bool g_world_build_pending{false};

/// The 3D Engine object — the thing state 7's fence is asked about.
///
/// `sub_823881C0` names this thread `"3D Engine"` at 0x823881D8, and the object is 608 bytes
/// (ctor `sub_823877E8`). The chain is a pure memory walk; the guest reaches it through a
/// virtual call whose body `sub_8220C1A0` is just `return *(*(a1 + 12) + 104);`, so nothing has
/// to be CALLED to follow it:
///
///     g      = *(0x83496AB8)
///     Game   = *(g    + 0x0C)
///     P      = *(Game + 0x68)      /* the 8-byte holder, filled at 0x82310CAC */
///     engine = *(P    + 0x04)      /* 823D65CC: lwz r3, 4(r3) off the CALL'S RESULT */
static uint32_t engine_object(const uint8_t* base) {
    const auto u32 = [base](uint32_t at) {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };
    const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
        return (at >= 0x10000u && at < 0xC0000000u) ? u32(at + offset) : 0u;
    };

    uint32_t at = u32(0x83496AB8u);
    at = step(at, 0x0Cu);
    at = step(at, 0x68u);
    at = step(at, 0x04u);
    return (at >= 0x10000u && at < 0xC0000000u) ? at : 0;
}

/// Arms or releases the render fence state 7 waits on, by writing the engine's `+176`.
///
/// THE FENCE, AND WHY THIS IS FOLLOWING THE CONTRACT RATHER THAN POKING. `sub_82387B38` is
/// bound to Lua by the shipped title as **"IsEnginePreLoading"**, with its blocking twin
/// `sub_82387CE0` as **"WaitForEngineToPreLoad"**. State 7 of `CmdLevelChange_Update` polls it
/// (0x823D65D0) and branches to the default case — which does nothing at all — while it is
/// nonzero. The title is asking its renderer whether the renderer has caught up. Godot IS the
/// renderer, and the honest answer while a region is still being built is "no".
///
/// The predicate is `engine+152 <= engine+176 || engine+600`, and it means "the engine's render
/// clock has not yet passed the last posted hand-off". `+152` is that clock, rewritten by the
/// 3D Engine thread every iteration (0x823885A0), and `+600` mirrors `byte_83496E20` the same
/// way (0x823885A4) — writing either is pointless. **`+176` is the one field with a single
/// writer (`sub_82388B48` @0x82388B80) and a single reader (the fence itself)**, and stamping
/// it then waiting is the guest's OWN idiom: `sub_821987B8` case 9 does exactly that at
/// 0x821988F0 / 0x8219892C.
///
/// POLARITY IS THE ONE THING THAT MUST NOT BE WRONG — 1 means BUSY. Held three ways: the spin
/// is `while (sub_82387B38(a1)) SleepEx(0,1)`, the call site does `bne -> default` on nonzero,
/// and the Lua name is a question whose "yes" is 1. Inverted, this deadlocks the title.
///
/// RE-ASSERTED EVERY TICK, because the world's own Update (`sub_8231D288`) stamps `+176` with
/// the real hand-off time as it drains the pending `.engine_level` — a hold written once would
/// be wiped the first time that ran.
///
/// RELEASE WRITES 0.0, not the current clock: the comparison is `<=`, so writing `+152` itself
/// would leave the fence closed.
static void apply_world_build_hold(const uint8_t* base);

/// ON by default. `REFII_HOLD_GUEST_FOR_WORLD=0` disables it if it ever misbehaves.
///
/// CAN HOLDING THIS WEDGE THE GUEST? No, and the reasoning is worth keeping because the shape
/// of it says otherwise. `sub_821987B8` runs from the main loop at 0x822771F0 every tick and
/// BEFORE the state machine at 0x82277264, and two of its cases spin on this same predicate —
/// case 9 calls `WaitForEngineToPreLoad` (0x8219892C) after RESTAMPING `+176` with the current
/// time at 0x821988F0, then waits for the render clock `+152` to pass that stamp. Since `+152`
/// comes from `sub_82B6FCE8`, which derives from D3D frame sync, that spin looks unbreakable
/// with no GPU.
///
/// IT IS NOT: the 3D Engine thread keeps its clock running headless — the value climbs steadily
/// in `[guest-fence] submitted=` — so the spin drains on its own.
///
/// What remains is bounded and intended: while WE hold, the spin waits for our release, and we
/// release on every path out of a build — success, failure and teardown. The wait is therefore
/// never longer than our own world build, which is the entire point of the mechanism.
static bool world_build_hold_enabled() {
    static const bool enabled = [] {
        const char* value = std::getenv("REFII_HOLD_GUEST_FOR_WORLD");
        return value == nullptr || value[0] != '0';
    }();
    return enabled;
}

static void apply_world_build_hold(const uint8_t* base) {
    if (!world_build_hold_enabled()) {
        return;
    }

    const uint32_t engine = engine_object(base);
    if (engine == 0) {
        return;
    }

    // 1e30 seconds is far beyond any render clock the title will ever reach, and finite — an
    // infinity here would make the guest's own comparison behave differently under fcmpu.
    const double value = g_world_build_pending.load(std::memory_order_relaxed) ? 1e30 : 0.0;

    uint64_t bits = 0;
    std::memcpy(&bits, &value, sizeof(bits));
    bits = __builtin_bswap64(bits);
    std::memcpy(const_cast<uint8_t*>(base) + engine + 0xB0u, &bits, sizeof(bits));
}

/// THE ONE SIGNAL THAT COVERS BOTH LOAD PATHS: the world is activated and populated.
///
/// `World + 0xA8`, and it is what `game+0x105` only pretends to be. Set by the activator
/// `sub_823640E0` — `mr r25, r3` then `stb r10(=1), 0xA8(r25)` at **0x8236410C** — and cleared
/// by the world teardown `sub_82364698` at 0x82364ADC, which `World_LoadLevel_Execute` calls
/// through `sub_8231CB90` at the start of every load.
///
/// BOTH PATHS REACH IT, which is the whole point. `sub_82350F80` is the only caller of the
/// activator and has exactly two callers of its own: `CmdLevelChange_Update` state 5
/// (0x823D6210) for travel, and `CmdLoadSaveGame_Update` state 2 (0x823D7920) for a
/// load-from-save. So one poll answers "is the world up" however the guest got there — no
/// per-mechanism special case, and nothing to walk.
///
/// WHY NOT `game+0x105`: it IS written on both paths, but only at the TERMINAL state, behind
/// the render fence and the loading-screen gates (states 4/5 of the save command, 7/8 of the
/// travel one). A Godot-hosted front end may never satisfy those, so it reads false while the
/// world is in fact fully loaded and its entities activated — the load command has finished its
/// work and parked. It also BOOTS AS 1 (game ctor, 0x82310644), so it is only meaningful once
/// it has been seen to go 0.
///
/// WHY NOT `LevelInstance + 0xE0`: it looks ideal — written 1 on both paths right after
/// activation — but it is a CONSUMED one-shot. `sub_8231D288` reads it at 0x8231D2BC and clears
/// it at 0x8231D3D0, conditionally on an entrance string being non-empty. Polling it races the
/// consumer and its lifetime depends on level data.
///
/// OFFSETS, EACH AGAINST ITS OWN BASE, and note this is NOT the object `world_manager()`
/// returns — that one takes `+0x08` then `+0x04` from the same parent, this takes `+0x00`:
///
///     g     = *(0x83496AB8)
///     game  = *(g    + 0x0C)
///     A     = *(game + 0x58)
///     B     = *(A    + 0x04)      /* what sub_82350F80 is called with */
///     World = *(B    + 0x00)      /* what it passes on as sub_823640E0's arg0 */
///     flag  =  (uint8)World[0xA8]
///
/// Every hop is null before `Game_InitDatabases` has run, so each is checked. Returns 0 when
/// the world is down, mid-load, or does not exist yet — all three mean the same thing to a
/// host: do not mount against it.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostWorldIsActive(void) {
    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto u32 = [base](uint32_t at) {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };
    const auto plausible = [](uint32_t at) {
        return at >= 0x10000u && at < 0xC0000000u;
    };
    const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
        return plausible(at) ? u32(at + offset) : 0u;
    };

    uint32_t at = u32(0x83496AB8u);
    at = step(at, 0x0Cu);
    at = step(at, 0x58u);
    at = step(at, 0x04u);
    at = step(at, 0x00u);
    if (!plausible(at)) {
        return 0;
    }

    return base[at + 0xA8u] != 0 ? 1u : 0u;
}

/// Describes the gameflow command queue as text, for when a walk of it comes back empty.
///
/// PRINT THE QUEUE RATHER THAN GUESS AT IT AGAIN. When `RefiiHostGetLevelChange` finds nothing
/// the question is always the same — wrong chain, wrong link offset, or genuinely no such
/// command — and each answer costs a run to test. Dumping the nodes and their command vtables
/// settles all three at once, which is how the load-from-save path was found to queue no
/// level-change command at all.
///
/// Writes `<count> node(s): vtable@node ...` and never fails loudly — an empty or unreadable
/// queue is itself the answer.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostDescribeGameFlowQueue(char* out, uint32_t capacity) {
    if (out == nullptr || capacity == 0) {
        return 0;
    }

    out[0] = '\0';

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto u32 = [base](uint32_t at) {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };
    const auto plausible = [](uint32_t at) {
        return at >= 0x10000u && at < 0xC0000000u;
    };
    const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
        return plausible(at) ? u32(at + offset) : 0u;
    };

    const uint32_t p = step(u32(0x83496AB8u), 0x0Cu);
    const uint32_t holder = step(p, 0x80u);
    const uint32_t gameflow = step(holder, 0x04u);

    std::string text;
    char scratch[128];

    std::snprintf(scratch, sizeof(scratch), "p=0x%08X holder=0x%08X gf=0x%08X", p, holder,
                  gameflow);
    text += scratch;

    if (plausible(gameflow)) {
        const uint32_t sentinel = u32(gameflow + 0x04u);
        std::snprintf(scratch, sizeof(scratch), " sentinel=0x%08X", sentinel);
        text += scratch;

        // BOTH LINK DIRECTIONS, because which one walks this ring is exactly what is in doubt.
        // A doubly linked node makes one of them the way forward and the other the way back;
        // printing both costs nothing and settles it in one run instead of one run per guess.
        for (int direction = 0; direction < 2; ++direction) {
            const uint32_t link = direction == 0 ? 0x04u : 0x00u;
            std::snprintf(scratch, sizeof(scratch), " | +%u:", link);
            text += scratch;

            uint32_t node = plausible(sentinel) ? u32(sentinel + link) : 0u;
            for (uint32_t i = 0; i < 8u; ++i) {
                if (!plausible(node) || node == sentinel) {
                    text += node == sentinel ? " <end>" : " <bad>";
                    break;
                }

                const uint32_t command = u32(node + 0x08u);
                std::snprintf(scratch, sizeof(scratch), " [%08X vt=%08X]", command,
                              plausible(command) ? u32(command) : 0u);
                text += scratch;

                if (text.size() + 64u >= capacity) {
                    break;
                }

                node = u32(node + link);
            }
        }
    }

    const size_t copied = text.size() < capacity - 1 ? text.size() : capacity - 1;
    std::memcpy(out, text.data(), copied);
    out[copied] = '\0';
    return 1;
}

/// Holds the guest in its own loading screen while Godot builds the region.
///
/// Set it when a build starts and CLEAR IT WHEN THE REGION MOUNTS — including on the failure
/// path. A hold that is never released parks the guest at state 7 forever with its loading
/// music playing, which looks exactly like a hung load because that is what it is.
///
/// SAFE TO HOLD FOR TENS OF SECONDS, checked rather than hoped: no state of the nine reads the
/// command's start timestamp `cmd+56` and none contains an elapsed-time comparison, an assert
/// or a force-advance; the 3D Engine thread's only cross-thread wait is a one-shot display-init
/// gate; and the guest starts one of five random loading tracks in state 1 and fades it out over
/// 1000 ms in state 9 — an arbitrarily long load is the designed case. Audio keeps running
/// because state 7 is a poll on a thread that keeps ticking and the mixer is not one of the
/// title's own threads.
REFII_HOST_ABI_EXPORT void RefiiHostSetWorldBuildPending(uint32_t pending) {
    g_world_build_pending.store(pending != 0u, std::memory_order_relaxed);

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return;
    }

    uint8_t* base = memory->virtual_membase();
    if (base != nullptr) {
        apply_world_build_hold(base);
    }
}

/// THE LEVEL CHANGE ITSELF, read off the command the guest queued for it.
///
/// `RefiiHostWorldIsReady` is a good "the world has settled" latch and a BAD "a load has
/// started" one, and the difference is where a host frees its scene. The entity teardown
/// happens in **state 2** — `sub_823D6798` @0x823D5F9C -> `sub_8231CB90` @0x823D6B1C ->
/// `sub_82364698`, which walks every entity, tests bit 0x08 of `entity+0x90` ("destroy on
/// level change"), calls DestroyEntity and then drains the pending list through
/// `sub_822D46C8` — after which the old entities are unlinked and freed. But `game+0x105` is
/// not cleared until **state 3**, at 0x823D60C8, AFTER the blocking load has already returned.
///
/// So there is a window spanning state 2 and all of state 3 where the ready byte still reads 1
/// and the entities it implies are gone. A host that frees on the falling edge of that byte
/// keeps drawing a region whose guest-side entities no longer exist, and reads freed memory to
/// do it. The command's own state word is the honest signal.
///
/// THE QUEUE WALK. Verified from `World_RequestLevelChange` (0x82350B20), which emits the whole
/// chain inline before it queues:
///
///     v19 = *(p + 0x80)                  /* p = *( *(0x83496AB8) + 0x0C ), same base as above */
///     gf  = *(v19 + 4)                   /* the object GameFlow_QueueCommand is called with */
///     sentinel = *(gf + 4)
///     node     = *(sentinel + 4)         /* empty when node == sentinel */
///     cmd      = *(node + 8)
///
/// NOTE THE BASE: the gameflow lives at `p + 0x80`, NOT at `*(0x83496AB8) + 0x80`. One extra
/// hop, and quoting the offset against the wrong base is how this project has shipped a
/// plausible-looking wrong answer before.
///
/// COMMAND FIELDS, from the ctor `sub_823D5620` decompiled in full rather than taken on report:
///
///     *(cmd + 0x00) = &vtbl_CmdLevelChange (0x820AECE8)   <- the identity test
///      cmd + 0x14   = world name     Rep*   /*0x823D5664*/
///      cmd + 0x18   = level name     Rep*   /*0x823D5670*/
///      cmd + 0x1C   = entrance name  Rep*   /*0x823D567C*/
///      cmd + 0x24   = travel type           /*0x823D56B0*/  1 new game, 2 door, 5 teleporter,
///                                                           3/4 fast travel, 16 reload in place
///      cmd + 0x2C   = state, starts at 1    /*0x823D56BC*/  1..9, never 0
///      cmd + 0x32   = fullLoad, starts at 1 /*0x823D56C4*/
///
/// `cmd+0x1C` is the entrance and not a guess: state 5 copies it straight into the live level
/// at 0x823D61EC — `sub_822651A0(*(u32*)(level+4) + 176, cmd + 28)` — and Level+176 is the slot
/// the level ctor seeds with "PlayerStart".
///
/// FULLLOAD IS LOAD-BEARING FOR THE HOST. When `cmd+0x32` is 0 the guest does NOT tear anything
/// down: state 3 takes the `sub_823170C8` branch instead of the blocking load, and
/// `sub_82364698` is not reachable from it at all. That is a reposition inside the RESIDENT
/// level, so a host must leave its scene standing — freeing it there would rebuild a region the
/// guest never unloaded.
///
/// We only ever read a vtable pointer and a few words, so this is polling, not participation.
/// Returns 1 when a level change is at the head of the queue, 0 otherwise.
///
/// Forward-declared because the shared string reader is defined with the other level accessors
/// further down, and this belongs beside `RefiiHostWorldIsReady` — the call it exists to correct.
static bool read_guest_name(const uint8_t* base, uint32_t global, char* out, uint32_t capacity);

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetLevelChange(char* world, char* level, char* entrance, uint32_t capacity,
                                 uint32_t* out_state, uint32_t* out_travel_type,
                                 uint32_t* out_full_load) {
    if (out_state != nullptr) *out_state = 0u;
    if (out_travel_type != nullptr) *out_travel_type = 0u;
    if (out_full_load != nullptr) *out_full_load = 0u;

    if (capacity == 0) {
        return 0;
    }

    if (world != nullptr) world[0] = '\0';
    if (level != nullptr) level[0] = '\0';
    if (entrance != nullptr) entrance[0] = '\0';

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto u32 = [base](uint32_t at) {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };
    const auto plausible = [](uint32_t at) {
        return at >= 0x10000u && at < 0xC0000000u;
    };
    const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
        return plausible(at) ? u32(at + offset) : 0u;
    };

    const uint32_t mode = u32(0x83496AB8u);
    const uint32_t p = step(mode, 0x0Cu);
    const uint32_t gameflow = step(step(p, 0x80u), 0x04u);
    if (!plausible(gameflow)) {
        return 0;
    }

    const uint32_t sentinel = u32(gameflow + 0x04u);
    if (!plausible(sentinel)) {
        return 0;
    }

    // WALK THE QUEUE — the level change is not always at the head.
    //
    // Checking only the first node reported "no command in flight" while a level change was
    // very much in flight, and the host then waited for a state that could never arrive.
    // `World_RequestLevelChange` itself queues a SECOND command in front of it: at 0x82350C94
    // it allocates a 24-byte object with vtable `off_820AECA0` and queues that (0x82350CC8)
    // before constructing and queueing the level change (0x82350CF8 / 0x82350D08). Whatever
    // sits at the head is therefore not the command we are looking for.
    //
    // The list is circular through the sentinel, so a node equal to it ends the walk. The step
    // cap is a corruption guard, not a queue-length assumption: a torn `next` pointer would
    // otherwise spin here forever, inside a call the host makes every frame.
    constexpr uint32_t kMaxQueueWalk = 64u;

    uint32_t command = 0;
    uint32_t node = u32(sentinel + 0x04u);
    for (uint32_t step_count = 0; step_count < kMaxQueueWalk; ++step_count) {
        if (!plausible(node) || node == sentinel) {
            break;
        }

        // ONLY CmdLevelChange. 0x820AED0C IS NOT A SECOND FLAVOUR OF IT.
        //
        // The queue dump shows a load-from-save carrying `[vt=820AED0C] [vt=8200247C]
        // [vt=82002914]` and no CmdLevelChange at all, and it is tempting to accept 0x820AED0C
        // too since it sits in the same contiguous run of command vtables. It is NOT the same
        // shape: `Game_InitDatabases` allocates that object with `li r3, 0xC` at 0x823114C8 —
        // **twelve bytes**, holding a vtable at +0, `sub_823DA258` at +4 and one word at +8.
        // Reading `+0x2C` for a state or `+0x14` for a destination off it is thirty bytes past
        // the end of the allocation, and would return whatever the allocator had there.
        //
        // An adjacent vtable is not evidence of a shared layout. The initial load simply has no
        // level-change command to find, and the caller's own fallbacks cover that.
        const uint32_t candidate = u32(node + 0x08u);
        if (plausible(candidate) && u32(candidate) == 0x820AECE8u) {
            command = candidate;
            break;
        }

        // THROUGH +4, which is the link the guest itself follows. `GameFlow_QueueCommand`
        // reaches its node as `*(*(gf + 4) + 4)` and ends on `node == sentinel`, so +4 is the
        // pointer that walks this ring; +0 would be the other direction of a doubly-linked
        // node and is not what any reader in the image uses. The ring is circular either way,
        // so following the guest's own link visits every command.
        node = u32(node + 0x04u);
    }

    if (command == 0) {
        return 0;  // no level change anywhere in the queue
    }

    if (out_state != nullptr) *out_state = u32(command + 0x2Cu);
    if (out_travel_type != nullptr) *out_travel_type = u32(command + 0x24u);
    if (out_full_load != nullptr) *out_full_load = base[command + 0x32u] != 0 ? 1u : 0u;

    read_guest_name(base, command + 0x14u, world, capacity);
    read_guest_name(base, command + 0x1Cu, entrance, capacity);
    read_guest_name(base, command + 0x18u, level, capacity);
    return 1;
}

/// How far the guest has got through starting a game, as four globals it maintains itself.
///
/// `Game_InitDatabases` (0x823108B8) is where a New Game actually begins, and it is silent:
/// it neither logs nor sets any progress flag until its very last statement. These four
/// bracket it:
///
///   out[0] `g_GDBManager`   0x83496DA0 — the game-database manager. The front end's Leave
///          NULLS it (`GDBManager_UnloadFilesByTagAndRebuild` then `g_GDBManager = 0` at
///          0x82319BC8) and Game_InitDatabases rebuilds it. Zero after the handover means
///          the databases have been torn down and not yet come back.
///   out[1] `byte_83496A5E`  — set 1 at 0x82311648, the LAST statement of the function.
///   out[2] `dword_83496AB8` — the game mode, built by sub_823130D8.
///   out[3] `dword_83496AC0` — the front-end mode; its own destructor zeroes it at
///          0x8231AA54, so 0 here means the front end is genuinely gone rather than hidden.
///
/// Together they say which side of the handover we are on and whether the databases came
/// back — without which "the guest went quiet" cannot be told apart from "the guest is
/// loading".
REFII_HOST_ABI_EXPORT void RefiiHostGetInitMarkers(uint32_t* out) {
    if (out == nullptr) {
        return;
    }

    for (int i = 0; i < 4; ++i) {
        out[i] = 0;
    }

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return;
    }

    const auto read32 = [base](uint32_t at) {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };

    out[0] = read32(0x83496DA0u);
    out[1] = base[0x83496A5Eu];
    out[2] = read32(0x83496AB8u);
    out[3] = read32(0x83496AC0u);
}

/// Every LIVE entity's world transform and model, for Godot to draw.
///
/// This is the plan's phase 1 — the state feed — and it is a PURE MEMORY READ. No guest
/// function is called, nothing is written, and it is safe to run from Godot's thread while
/// the guest simulates: a torn read costs one frame of one entity, never a crash.
///
/// THE LIST. `0x83496B28` holds a POINTER to the manager, not the header — that is the first
/// trap, and reading it as the header walks garbage. The header is the 12 bytes at mgr+0x08:
/// front +0x08, back +0x0C, live count +0x10. The node is embedded at entity+0x08 as
/// {next, prev, owner}, so the owner back-pointer is node+0x08. Verified against the guest's
/// own walk, sub_8238D3A0: `lwz r10, dword_83496B28` / `lwz r11, 8(r10)` / `lwz r11, 8(r6)`.
///
/// WHICH ENTITIES COUNT. `entity+0x90 & 0x40` is the LIVE bit — set at construction
/// (sub_8238B320 stores 0xCC) and cleared only by the despawn sub_8238D828 — and it is the
/// game's own universal validity guard, tested at 3714 sites against one or two for every
/// other bit. There is NO separate visible/hidden flag anywhere on an entity; drawability is
/// membership, so the second half of the filter is `entity+0x7C != 0`, the same thing despawn
/// gates its deregistration on.
///
/// THE TRANSFORM. entity+0x7C is the placement component ("EC-2"), whose slot 0x40 returns a
/// position and 0x44 an orientation, both sret — caller's buffer in r3, `this` in r4, 16 bytes
/// written (sub_8222CD80 is the whole idiom: `li r11,0x20; lvx128 v0,r4,r11; stvx128 v0,r0,r3`).
/// We do NOT call them. There are exactly twelve EC-2 vtables and between them only four
/// position bodies and three orientation bodies, so the read is a switch on the vtable
/// pointer — a `.rdata` constant, stable across runs.
///
/// An UNKNOWN vtable is reported, never guessed. Only three delegate classes were enumerated
/// and the writer of EC2+0x20 was never found, so the set cannot be proven closed, and
/// defaulting to some plausible offset would put entities at silently wrong positions.
/// Unknown means skipped and counted.
///
/// THE MODEL. Carried by a graphic-appearance component, not by the entity: id 4 static mesh
/// at +0x18, id 6 animated at +0x20, ids 10 and 11 at +0x14. The value is a resource-name
/// hash, written once at load. Id 5 (multiple-mesh) holds a vector and is skipped.
///
/// Fills `stride` dwords per entity; returns how many were written.
///
/// `out_unknown_vtables` receives {vtable, entity count} PAIRS and the number of pairs comes
/// back in `out_unknown_vtable_count`. The count is explicit rather than a zero terminator
/// because zero is a value the walk can legitimately record — a placement component whose
/// vtable word reads 0 is the under-construction case, and terminating on it buries every
/// later entry. The per-vtable tally is what makes the list actionable: one address standing
/// for 300 entities is a different decision from thirty standing for a dozen each.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntities(uint32_t* out, uint32_t max_entities, uint32_t* out_unknown,
                              uint32_t* out_unknown_vtables, uint32_t max_unknown_vtables,
                              uint32_t* out_unknown_vtable_count) {
    constexpr uint32_t kEntityStride = 12u;
    constexpr uint16_t kNoDirect = 0xFFFFu;

    /// The twelve EC-2 vtables, and how each states its placement.
    /// `pos`/`ori` are byte offsets into the component; kNoDirect means "not a direct read".
    struct Ec2Layout {
        uint32_t vtable;
        uint16_t pos;
        uint16_t ori;
        bool hybrid;    // sub_82213A08: cached at +0x70 unless it defers to the physics body
        bool delegate;  // sub_8220DDB0: the placement lives on the object at +0x20
    };

    static constexpr Ec2Layout kEc2Layouts[] = {
    {0x82006F88u, 0x20u, kNoDirect, false, false},  // orientation is a global
    {0x82007258u, 0x20u, 0x30u, false, false},
    {0x820E6048u, 0x20u, 0x30u, false, false},
    {0x820CA190u, 0x40u, 0x30u, false, false},
    {0x82007038u, 0x70u, kNoDirect, true, false},
    {0x820071A0u, 0x70u, kNoDirect, true, false},
    {0x820C4B90u, 0x70u, kNoDirect, true, false},
    {0x820070F0u, kNoDirect, kNoDirect, false, true},
    {0x82007308u, kNoDirect, kNoDirect, false, true},
    {0x820073B8u, kNoDirect, kNoDirect, false, true},
    {0x820C6E58u, kNoDirect, kNoDirect, false, true},
    {0x820CB140u, kNoDirect, kNoDirect, false, true},  // the abstract base
};

    /// The delegate targets — the physics/proxy objects an EC-2 hands its placement to.
    ///
    /// The EC-2 getters do NOT call these at slot 0x40/0x44. `sub_8220DDB0` loads the object
    /// from EC2+0x20 and dispatches slot 0x0C for position; `sub_8220B940` dispatches slot
    /// 0x10 for orientation. A delegate vtable read as if it answered at 0x40 lands in the
    /// middle of a real vtable and names no class at all.
    ///
    /// `hop` is a CHAIN, not a special case, because the guest's is: a getter that is not a
    /// plain load holds a pointer at some offset and asks THAT object instead, and the object
    /// it names can do the same again. `0x82006A40` hops through +0x38 and dispatches on what
    /// it finds; `0x82005B80` hops through +0x2C and reads the vectors straight out.
    ///
    /// `dispatch` says which kind: true means look the object at `hop` up in this table again,
    /// false means read `pos`/`ori` out of it directly.
    ///
    /// A NULL at a hop is not a failure — the getters fall back to `flt_82099484`, which is
    /// 0.0, so the object genuinely is at the origin.
    static constexpr uint16_t kNoHop = 0xFFFFu;

    struct DelegateLayout {
        uint32_t vtable;
        uint16_t pos;
        uint16_t ori;
        uint16_t hop;
        bool dispatch;
    };

    static constexpr DelegateLayout kDelegateLayouts[] = {
        // slot 0x0C = sub_8255AFE0 `li r11,0x50`, slot 0x10 = sub_828CA2A0 `ld 0x60/0x68`.
        {0x82006B30u, 0x50u, 0x60u, kNoHop, false},
        // slot 0x0C = sub_8222A4B0 `li r11,0x40`, slot 0x10 = sub_822082F8 `ld 0x50/0x58`.
        // 90 of the 388 unplaceable entities in BWSSlums were this class.
        {0x820069C8u, 0x40u, 0x50u, kNoHop, false},
        // slot 0x0C = sub_8220B9E0 `lwz r4,0x38(r4)` then slot 0x14; slot 0x10 = sub_82232B70,
        // same hop then slot 0x18. 298 of the 388 were this one.
        {0x82006A40u, 0u, 0u, 0x38u, true},
        // slot 0x14 = sub_8257A608 `li r11,0xF0`, slot 0x18 = sub_8257A618 `ld 0x100/0x108`.
        // Reached from 0x82006A40's hop and from the hybrid's +0x38.
        {0x82005460u, 0xF0u, 0x100u, kNoHop, false},
        // slot 0x14 = sub_8220B9C8 `lwz r11,0x2C(r4); li r10,0x110`, slot 0x18 = sub_82232B38,
        // same hop then +0x150. The other thing 0x82006A40's +0x38 can hold — and what all
        // 298 of them actually held.
        {0x82005B80u, 0x110u, 0x150u, 0x2Cu, false},
    };

    /// The delegate layout for a vtable, or null when it is not one we know.
    const auto find_delegate = [&](uint32_t vtable) -> const DelegateLayout* {
        for (const DelegateLayout& candidate : kDelegateLayouts) {
            if (candidate.vtable == vtable) {
                return &candidate;
            }
        }
        return nullptr;
    };

    /// The graphic-appearance components, and where each keeps its model hash.
    struct ModelSlot {
        uint8_t id;
        uint16_t offset;
    };

    static constexpr ModelSlot kModelSlots[] = {
        {4u, 0x18u},   // static mesh
        {6u, 0x20u},   // animated mesh
        {10u, 0x14u},  // portal
        {11u, 0x14u},  // screen-facing sprite
    };

    // Cleared up front so every early return below leaves the caller with "nothing unknown"
    // rather than whatever the last call wrote.
    if (out_unknown != nullptr) {
        *out_unknown = 0u;
    }

    if (out_unknown_vtable_count != nullptr) {
        *out_unknown_vtable_count = 0u;
    }

    if (out == nullptr || max_entities == 0u) {
        return 0u;
    }

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    // Guest pointers are big-endian and the address space is 32-bit; anything outside it is a
    // torn read rather than an object, so every hop is bounds-checked.
    const auto read32 = [base](uint32_t at) -> uint32_t {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };
    const auto read8 = [base](uint32_t at) -> uint8_t { return base[at]; };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };
    const auto read_vector = [&](uint32_t at, uint32_t* dst, int lanes) {
        for (int i = 0; i < lanes; ++i) {
            dst[i] = read32(at + (uint32_t)(i * 4));
        }
    };

    /// Reads four floats and accepts them ONLY if they are a unit quaternion.
    ///
    /// A ROTATION QUATERNION IS UNIT BY DEFINITION, and that is the test — not "some lane is
    /// non-zero", which is what the orientation fallback used to ask. That question passes for
    /// any field that merely has bits in it, and the field at `placement+0xE0` is not always a
    /// quaternion: measured live in BWSMarket, every composite character came back
    /// `(0, 0, z, w)` with x and y EXACTLY zero and z/w astronomically large or zero —
    /// `(0, 0, -8.98e34, 0)`, `(0, 0, 3.23e25, -3.57e17)`, `(0, 0, 19169.12, -0)`. The leading
    /// pair of zeros is the giveaway: that is a 16-byte read of something whose first eight
    /// bytes are not part of a rotation at all.
    ///
    /// Downstream that garbage is invisible rather than loud. `|q|` of infinity or zero trips
    /// the host's degenerate guard and yields identity, while `(0,0,19169,0)` normalises to a
    /// clean 180° — so the characters simply stand in one fixed direction and nothing reports a
    /// fault. Rejecting here lets the NEXT source have its turn instead.
    const auto unit_quaternion_at = [&](uint32_t at, uint32_t* dst) -> bool {
        float q[4];
        for (int i = 0; i < 4; ++i) {
            const uint32_t bits = read32(at + (uint32_t)(i * 4));
            std::memcpy(&q[i], &bits, sizeof(q[i]));
            if (!std::isfinite(q[i])) {
                return false;
            }
        }

        const float length = (q[0] * q[0]) + (q[1] * q[1]) + (q[2] * q[2]) + (q[3] * q[3]);
        if (!(length > 0.98f && length < 1.02f)) {
            return false;
        }

        for (int i = 0; i < 4; ++i) {
            dst[i] = read32(at + (uint32_t)(i * 4));
        }

        return true;
    };

    /// Walks a delegate chain to the object that actually holds the vectors.
    ///
    /// Returns false when the chain reaches a class this table does not know, leaving the
    /// offending vtable in `unrecognised` so the caller can NAME it rather than guess an
    /// offset. `source` comes back 0 when the chain bottomed out on the guest's own null
    /// fallback — that is the origin, and a real answer.
    const auto follow_delegate = [&](uint32_t object, uint32_t& source, uint16_t& pos,
                                     uint16_t& ori, uint32_t& unrecognised) -> bool {
        // Bounded. The longest real chain is body -> +0x38 -> +0x2C; a cycle in guest memory
        // must not become a hang in a read that runs every frame.
        for (int depth = 0; depth < 4; ++depth) {
            const DelegateLayout* layout = find_delegate(read32(object));
            if (layout == nullptr) {
                unrecognised = read32(object);
                return false;
            }

            if (layout->hop == kNoHop) {
                source = object;
                pos = layout->pos;
                ori = layout->ori;
                return true;
            }

            const uint32_t next = read32(object + layout->hop);
            if (!plausible(next)) {
                source = 0u;  // the guest's own zero-vector fallback
                pos = kNoDirect;
                ori = kNoDirect;
                return true;
            }

            if (!layout->dispatch) {
                source = next;
                pos = layout->pos;
                ori = layout->ori;
                return true;
            }

            object = next;
        }

        unrecognised = read32(object);
        return false;
    };

    const uint32_t manager = read32(0x83496B28u);
    if (!plausible(manager)) {
        return 0u;
    }

    uint32_t written = 0u;
    uint32_t unknown = 0u;
    uint32_t unknown_vtable_count = 0u;

    // Naming the vtables we do not know is the whole point of counting them: it turns "27% of
    // entities are unplaceable" into a short list of addresses to look up and add.
    //
    // `max_unknown_vtables` is a pair capacity, not a word capacity.
    const auto note_unknown = [&](uint32_t vtable) {
        ++unknown;
        if (out_unknown_vtables == nullptr) {
            return;
        }
        for (uint32_t i = 0; i < unknown_vtable_count; ++i) {
            if (out_unknown_vtables[i * 2u] == vtable) {
                ++out_unknown_vtables[(i * 2u) + 1u];
                return;  // distinct only — the pair carries the tally
            }
        }
        if (unknown_vtable_count < max_unknown_vtables) {
            out_unknown_vtables[unknown_vtable_count * 2u] = vtable;
            out_unknown_vtables[(unknown_vtable_count * 2u) + 1u] = 1u;
            ++unknown_vtable_count;
        }
    };

    uint32_t node = read32(manager + 8u);

    // The live count bounds the walk, so a corrupt link cannot spin forever.
    const uint32_t counted = read32(manager + 0x10u);
    const uint32_t guard = counted > 0u && counted < 65536u ? counted + 16u : 65536u;

    for (uint32_t steps = 0u; plausible(node) && written < max_entities && steps < guard;
         ++steps) {
        const uint32_t next = read32(node + 0u);
        const uint32_t entity = read32(node + 8u);
        node = next;

        if (!plausible(entity)) {
            break;
        }

        if ((read8(entity + 0x90u) & 0x40u) == 0u) {
            continue;  // not LIVE
        }

        const uint32_t placement = read32(entity + 0x7Cu);
        if (!plausible(placement)) {
            continue;  // no placement component == not drawable
        }

        const uint32_t vtable = read32(placement);
        const Ec2Layout* layout = nullptr;
        for (const Ec2Layout& candidate : kEc2Layouts) {
            if (candidate.vtable == vtable) {
                layout = &candidate;
                break;
            }
        }

        if (layout == nullptr) {
            note_unknown(vtable);  // loudly, not guessed
            continue;
        }

        uint32_t position[4] = {0u, 0u, 0u, 0u};
        uint32_t orientation[4] = {0u, 0u, 0u, 0x3F800000u};  // identity (x,y,z,w)
        bool resolved = false;

        uint32_t source = placement;
        uint16_t position_offset = layout->pos;
        uint16_t orientation_offset = layout->ori;

        /// A CHARACTER'S FACING, out of its Havok shape phantom.
        ///
        /// THIS IS WHY EVERY HUMAN IN THE STREET FACED THE SAME WAY. The character placement
        /// classes (`CECPhysicsSimulationCharacterControlled` 0x820071A0 and
        /// `...CharacterNavigator` 0x82007038) do not store an orientation. Their getter
        /// `sub_82211110` hops `comp+0x20` -> `+0x38` and dispatches slot 0x18 on what it
        /// finds, which is a proxy with vtable 0x82005720 — a class this table did not know, so
        /// the chain walk failed and every character got the identity fallback, every frame.
        ///
        /// Position hid the bug. `sub_82213A08` caches the position at `comp+0x70` and falls
        /// back to it, so positions were right and people walked around correctly; there is no
        /// equivalent cache for orientation, so a missed delegate is total.
        ///
        /// Verified in-line, not taken from a report — the proxy's two getters differ only in
        /// the offset they read out of the SAME object:
        ///
        ///   sub_82210F88 (slot 0x18, orientation): lwz r11, 0x2C(r4) / addi r4, r11, 0xA0
        ///   sub_82213A80 (slot 0x14, position):    lwz r11, 0x2C(r4) / li r10, 0xD0
        ///
        /// So `proxy+0x2C` is an `hkpShapePhantom` and its `hkTransform` starts at `+0xA0`:
        /// three column-major `float4` basis vectors (world X, Y, Z) then the translation at
        /// `+0xD0`. There is NO stored quaternion — the guest derives one per call.
        ///
        /// Offsets are measured from the PROXY (`controller+0x38`), not the placement component.
        const auto read_phantom_quaternion = [&](uint32_t proxy, uint32_t* out) -> bool {
            if (!plausible(proxy) || read32(proxy) != 0x82005720u) {
                return false;
            }

            const uint32_t phantom = read32(proxy + 0x2Cu);
            if (!plausible(phantom)) {
                return false;
            }

            // Column-major: element (row, col) is float [col * 4 + row].
            float m[12];
            for (int i = 0; i < 12; ++i) {
                const uint32_t bits = read32(phantom + 0xA0u + (uint32_t)(i * 4));
                std::memcpy(&m[i], &bits, sizeof(m[i]));
                if (!std::isfinite(m[i])) {
                    return false;
                }
            }

            const float m00 = m[0], m10 = m[1], m20 = m[2];
            const float m01 = m[4], m11 = m[5], m21 = m[6];
            const float m02 = m[8], m12 = m[9], m22 = m[10];

            // Shepperd's method — the largest-denominator branch, so it never divides by a
            // value near zero. The guest's own extraction (`quaternionFromRotatation`
            // @0x82D56728) takes `x = m21 - m12`, `y = m02 - m20`, `z = m10 - m01`, which is
            // this indexing exactly; it is only reproduced rather than called because any
            // correct extraction yields the same ROTATION, and q and -q are the same rotation.
            float x, y, z, w;
            const float trace = m00 + m11 + m22;
            if (trace > 0.0f) {
                const float s = std::sqrt(trace + 1.0f) * 2.0f;
                w = 0.25f * s;
                x = (m21 - m12) / s;
                y = (m02 - m20) / s;
                z = (m10 - m01) / s;
            } else if (m00 > m11 && m00 > m22) {
                const float s = std::sqrt(1.0f + m00 - m11 - m22) * 2.0f;
                w = (m21 - m12) / s;
                x = 0.25f * s;
                y = (m01 + m10) / s;
                z = (m02 + m20) / s;
            } else if (m11 > m22) {
                const float s = std::sqrt(1.0f + m11 - m00 - m22) * 2.0f;
                w = (m02 - m20) / s;
                x = (m01 + m10) / s;
                y = 0.25f * s;
                z = (m12 + m21) / s;
            } else {
                const float s = std::sqrt(1.0f + m22 - m00 - m11) * 2.0f;
                w = (m10 - m01) / s;
                x = (m02 + m20) / s;
                y = (m12 + m21) / s;
                z = 0.25f * s;
            }

            if (!std::isfinite(x) || !std::isfinite(y) || !std::isfinite(z) ||
                !std::isfinite(w)) {
                return false;
            }

            // AND IT HAS TO BE UNIT. `isfinite` is not enough: a garbage basis is still made of
            // finite floats, and Shepperd's turns one into a finite non-rotation that then
            // silently becomes a bogus facing. The extraction of a real rotation matrix is unit
            // by construction, so this only rejects input that was never a rotation.
            const float length = (x * x) + (y * y) + (z * z) + (w * w);
            if (!(length > 0.98f && length < 1.02f)) {
                return false;
            }

            // HOST ORDER, LIKE EVERY OTHER LANE — this used to byte-swap "back to the guest's
            // byte order, because everything downstream byte-swaps", and nothing downstream
            // does. `read32` has already swapped on the way in, the array is initialised to a
            // host-order `0x3F800000`, and the C# side reads each word straight through
            // `BitConverter.Int32BitsToSingle`. The extra swap was the whole facing bug.
            //
            // It hid because of what a character's quaternion looks like. Facing is yaw-only
            // about Z, so the real value is `(0, 0, sin, cos)` — and `bswap(0) == 0`, so the two
            // zero lanes survived the corruption untouched. That is why every sample read
            // `(0, 0, junk, junk)` with x and y EXACTLY zero: not a wrong offset, not a missing
            // delegate, just the two lanes that a byte swap cannot damage sitting next to the
            // two it destroys. `0.7f = 0x3F333333` comes back as `0x3333333F`, about 4.2e7.
            const float lanes[4] = {x, y, z, w};
            for (int i = 0; i < 4; ++i) {
                std::memcpy(&out[i], &lanes[i], sizeof(out[i]));
            }

            return true;
        };

        if (layout->hybrid) {
            // The two halves gate DIFFERENTLY and must be asked separately.
            //
            // sub_82213A08 (position) defers to the sub-object only when the component has
            // NOT latched +0xA4; otherwise the cached vector at +0x70 wins. sub_82211110
            // (orientation) has no +0xA4 test at all — it delegates whenever the chain is
            // there, and falls back to identity, never to +0x70.
            //
            // Tying them to one branch returns identity for every latched entity, which looks
            // exactly like "the orientation offset is wrong" and is really the wrong gate.
            const uint32_t body = read32(placement + 0x20u);
            const uint32_t sub = plausible(body) ? read32(body + 0x38u) : 0u;

            uint32_t chained = 0u;
            uint16_t chained_pos = kNoDirect;
            uint16_t chained_ori = kNoDirect;
            uint32_t unrecognised = 0u;

            // THE CHARACTER PROXY FIRST. `sub` here is what `sub_82211110` dispatches slot 0x18
            // on, and for a character that is the Havok proxy — which holds a transform, not a
            // quaternion, so the generic delegate walk cannot express it and falls through. That
            // failure is silent and total: no orientation, identity forever, every human in the
            // street facing the same way while walking around perfectly correctly.
            if (read_phantom_quaternion(sub, orientation)) {
                orientation_offset = kNoDirect;

                // Position still comes from the component's own cache below. The phantom carries
                // it too, at +0xD0, and the two agreeing is what would prove the whole chain —
                // but the cached read already works, and swapping a working read for a new one
                // on the same frame as a fix makes a regression unattributable.
            } else if (unit_quaternion_at(placement + 0xE0u, orientation)) {
                // THE COMPONENT'S OWN CACHE, second and only if it has ever been written.
                //
                // `sub_825989D8` (vtable +0x74, SetOrientation) stores the quaternion at
                // comp+0xE0 as well as pushing it to the physics proxy, so it is the guest's
                // own record of the facing. It is NOT per-frame — the constructor
                // `sub_825967B8` leaves it all zeros rather than identity, and only an explicit
                // set ever writes it — so it is a fallback, never the primary. All-zero is the
                // "never written" state and is not a rotation; taking it would be worse than
                // identity, hence the guard.
                read_vector(placement + 0xE0u, orientation, 4);
                orientation_offset = kNoDirect;
            } else if (plausible(sub) &&
                follow_delegate(sub, chained, chained_pos, chained_ori, unrecognised) &&
                chained != 0u && chained_ori != kNoDirect) {
                // Orientation always follows the chain when it exists — but still only if what
                // is there is a rotation. Same invariant as the two sources above; a chain that
                // resolves to the wrong field is no more usable than one that fails to resolve.
                if (unit_quaternion_at(chained + chained_ori, orientation)) {
                    orientation_offset = kNoDirect;
                }

                if (read8(placement + 0xA4u) == 0u) {
                    source = chained;
                    position_offset = chained_pos;
                }
            }
        } else if (layout->delegate) {
            const uint32_t body = read32(placement + 0x20u);
            if (!plausible(body)) {
                // A null body is the guest's own zero-vector/identity case, a real answer.
                resolved = true;
            } else {
                uint32_t chained = 0u;
                uint16_t chained_pos = kNoDirect;
                uint16_t chained_ori = kNoDirect;
                uint32_t unrecognised = 0u;

                if (!follow_delegate(body, chained, chained_pos, chained_ori, unrecognised)) {
                    note_unknown(unrecognised);  // named, never guessed
                    continue;
                }

                if (chained == 0u) {
                    resolved = true;  // the chain bottomed out at the origin
                } else {
                    source = chained;
                    position_offset = chained_pos;
                    orientation_offset = chained_ori;
                }
            }
        }

        if (!resolved && position_offset != kNoDirect) {
            read_vector(source + position_offset, position, 3);
            resolved = true;
        }

        if (!resolved) {
            note_unknown(vtable);
            continue;
        }

        // THE DIRECT READ, AND THE ONE THAT WAS STILL LETTING GARBAGE THROUGH.
        //
        // The three sources above now each check that what they found is a rotation, but this
        // path is taken by every layout that names an orientation offset outright — and it was
        // reading four floats and trusting them. Measured after the other three were guarded:
        // characters still arrived as `(0, 0, -3.43e21, 0)`, 142,800 of them refused downstream,
        // because THIS is where theirs came from.
        //
        // Leaving `orientation` at the identity it was initialised to is the honest outcome: a
        // field that does not hold a unit quaternion is not an orientation, whatever the layout
        // table says its offset is.
        if (orientation_offset != kNoDirect &&
            !unit_quaternion_at(source + orientation_offset, orientation)) {
            orientation[0] = 0u;
            orientation[1] = 0u;
            orientation[2] = 0u;
            orientation[3] = 0x3F800000u;  // identity (x,y,z,w), guest byte order
        }

        // The model hash, off whichever graphic-appearance component the entity carries.
        uint32_t model = 0u;
        const uint32_t components = read32(entity + 0x48u);
        const uint32_t components_end = read32(entity + 0x4Cu);
        const uint32_t accelerator = read32(entity + 0x8Cu);

        for (const ModelSlot& slot : kModelSlots) {
            const uint32_t word = read32(entity + 0x24u + ((uint32_t)(slot.id) / 32u) * 4u);
            if (((word >> (slot.id % 32u)) & 1u) == 0u) {
                continue;
            }

            uint32_t component = 0u;
            if (plausible(accelerator) && plausible(components)) {
                component = read32(components + 8u * read8(accelerator + slot.id) + 4u);
            } else if (plausible(components) && components_end > components) {
                for (uint32_t at = components; at + 8u <= components_end; at += 8u) {
                    if (read32(at) == slot.id) {
                        component = read32(at + 4u);
                        break;
                    }
                }
            }

            if (plausible(component)) {
                model = read32(component + slot.offset);
                if (model != 0u) {
                    break;
                }
            }
        }

        uint32_t* row = out + (written * kEntityStride);
        row[0] = position[0];
        row[1] = position[1];
        row[2] = position[2];
        row[3] = orientation[0];
        row[4] = orientation[1];
        row[5] = orientation[2];
        row[6] = orientation[3];
        row[7] = model;
        row[8] = entity;
        row[9] = read32(entity + 0x14u);  // UniqueID, high
        row[10] = read32(entity + 0x18u); // UniqueID, low
        row[11] = read32(entity + 0x1Cu); // GDB record hash
        ++written;
    }

    if (out_unknown != nullptr) {
        *out_unknown = unknown;
    }

    if (out_unknown_vtable_count != nullptr) {
        *out_unknown_vtable_count = unknown_vtable_count;
    }

    return written;
}

/// The COMPONENTS one entity carries, as {id, pointer} pairs.
///
/// Which components an entity carries is a live-memory fact, and no decompile answers it. A
/// character whose component 6 holds `*(comp6 + 0x20) == 0` has no model hash there and can
/// only be drawn through the parts list on `CECGraphicAppearanceMorph` (component id 17) —
/// see RefiiHostGetEntityMorphParts — so which of the two applies has to be measured first.
///
/// This reads no offset INSIDE any component, only the list, so it depends on no layout
/// beyond the walk itself.
///
/// The walk is the entity's own component vector at `entity+0x48`..`entity+0x4C`, 8-byte
/// `{id, pointer}` pairs — the same walk `RefiiHostGetEntities` falls back to when the
/// accelerator at `entity+0x8C` is not plausible.
///
/// Writes `count * 2` words: id then pointer. Returns the number of PAIRS written, which is
/// capped at `max_pairs`; 0 means the entity has no readable component list.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityComponents(uint32_t entity, uint32_t* out_pairs,
                                      uint32_t max_pairs) {
    if (out_pairs == nullptr || max_pairs == 0u) {
        return 0u;
    }

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    if (!plausible(entity)) {
        return 0u;
    }

    const uint32_t components = read32(entity + 0x48u);
    const uint32_t components_end = read32(entity + 0x4Cu);
    if (!plausible(components) || components_end <= components) {
        return 0u;
    }

    // Bounded against a torn read the same way the entity walk is: a component vector that
    // claims to be enormous is a bad pointer pair, not an entity with thousands of parts.
    const uint32_t span = components_end - components;
    if (span > 0x4000u) {
        return 0u;
    }

    uint32_t written = 0u;
    for (uint32_t at = components; at + 8u <= components_end && written < max_pairs;
         at += 8u) {
        out_pairs[written * 2u] = read32(at);
        out_pairs[(written * 2u) + 1u] = read32(at + 4u);
        ++written;
    }

    return written;
}

/// The model hashes a COMPOSITE character is assembled from.
///
/// WHY THIS EXISTS. 29 of BWSSlums's 138 characters have a live 160-bone skeleton and a model
/// hash of exactly zero at comp6+0x20, so nothing draws them — they are people missing from
/// the street. `sub_82229BE0` proves zero is terminal on component 6:
///
///   82229ee4  lwz    r11, 0x14(r28)     ; r28 = comp6+12, so this is comp6+0x20
///   82229ef0  ble    cr6, loc_8222A3B8  ; hash == 0 -> branch past the ENTIRE mesh block
///
/// Their geometry is on `CECGraphicAppearanceMorph`, component id 17 — confirmed, not assumed:
/// `sub_8263C078` sets `v13[1] = 17` beside the class-name string in the 264-class registry,
/// and all 29 were measured to carry id 17.
///
/// TWO LISTS, AND WE READ THE ONE THAT IS ACTUALLY FILLED. Verified in `sub_8217FA50`:
///
///   comp17+0x28..0x2C  stride 4   the SOURCE hashes, written by the property loader
///   comp17+0x38..0x3C  stride 24  the BUILT part records, +0 = the same hash
///
/// `sub_8217FA50` maps the first to the second — `(a1[11] - a1[10]) >> 2` walks the source and
/// `sub_824C6388` pushes 24-byte records — and it is reached from `sub_8220DE88`, the per-frame
/// DRAW virtual. Our guest never draws, so the built list may never be populated at all, while
/// the source list is filled at construction from HeadModel / TorsoModel / LegsModel /
/// *Accessories / AdditionalModelFiles on the CharacterCreationRecord.
///
/// So the source list is what is returned, and the built count comes back separately rather
/// than being assumed either way. If `out_built` is non-zero the draw path does run here and
/// the 24-byte records are available too; if it is zero, this export is the only way in.
///
/// All offsets are measured from the COMPONENT POINTER. Writes up to `max_hashes` model
/// resource-name hashes and returns how many were written.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityMorphParts(uint32_t entity, uint32_t* out_hashes,
                                      uint32_t max_hashes, uint32_t* out_built) {
    if (out_built != nullptr) {
        *out_built = 0u;
    }

    if (out_hashes == nullptr || max_hashes == 0u) {
        return 0u;
    }

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };
    const auto read8 = [base](uint32_t at) -> uint8_t { return base[at]; };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    if (!plausible(entity)) {
        return 0u;
    }

    // The Morph component, through the entity's own lookup contract: presence bitmask of 8
    // dwords at entity+0x24, then either the byte index table at entity+0x8C or a walk of the
    // {id, ptr} pairs. Same shape RefiiHostGetEntities uses for the graphic-appearance slots.
    constexpr uint8_t kMorphComponentId = 17u;

    const uint32_t present = read32(entity + 0x24u + ((uint32_t)(kMorphComponentId) / 32u) * 4u);
    if (((present >> (kMorphComponentId % 32u)) & 1u) == 0u) {
        return 0u;
    }

    const uint32_t components = read32(entity + 0x48u);
    const uint32_t components_end = read32(entity + 0x4Cu);
    const uint32_t accelerator = read32(entity + 0x8Cu);

    uint32_t morph = 0u;
    if (plausible(accelerator) && plausible(components)) {
        morph = read32(components + 8u * read8(accelerator + kMorphComponentId) + 4u);
    } else if (plausible(components) && components_end > components) {
        for (uint32_t at = components; at + 8u <= components_end; at += 8u) {
            if (read32(at) == kMorphComponentId) {
                morph = read32(at + 4u);
                break;
            }
        }
    }

    if (!plausible(morph)) {
        return 0u;
    }

    // The BUILT list, reported only. Stride 24, first dword is the same hash.
    const uint32_t built_begin = read32(morph + 0x38u);
    const uint32_t built_end = read32(morph + 0x3Cu);
    if (out_built != nullptr && plausible(built_begin) && built_end > built_begin &&
        ((built_end - built_begin) % 24u) == 0u && (built_end - built_begin) <= 0x4000u) {
        *out_built = (built_end - built_begin) / 24u;
    }

    // The SOURCE list, returned. Stride 4.
    const uint32_t begin = read32(morph + 0x28u);
    const uint32_t end = read32(morph + 0x2Cu);
    if (!plausible(begin) || end <= begin || ((end - begin) % 4u) != 0u ||
        (end - begin) > 0x4000u) {
        return 0u;
    }

    uint32_t written = 0u;
    for (uint32_t at = begin; at + 4u <= end && written < max_hashes; at += 4u) {
        const uint32_t hash = read32(at);
        if (hash != 0u) {
            out_hashes[written++] = hash;
        }
    }

    return written;
}

/// One live character's SKELETON POSE, so Godot can drive a Skeleton3D from the guest.
///
/// The placement feed puts a prop where the guest says it is; this is the rest of what a
/// CHARACTER needs. Like the placement read it calls no guest code — the pose the animation
/// system evaluated is left in a stable, component-owned heap array, and we copy it out.
///
/// WHERE IT IS. The animated-mesh component is id 6 (`CECGraphicAppearanceAnimatedMesh`),
/// and it owns a vector of 48-byte bone transforms at comp+0x2C: begin +0x30, end +0x34, so
/// the bone count is `(end - begin) / 48`.
///
/// WHY THAT ARRAY AND NOT THE POSE OBJECT AT comp+0x24. Both exist and only one is the live
/// finished pose. `sub_821F2C78` XMemCpys the freshly evaluated pose INTO comp+0x30 —
/// `XMemCpy(*(a1+48), *(a2+4), 48 * ((*(a1+52) - *(a1+48)) / 48))` — and its caller
/// `sub_8257D8B8` then runs `sub_821F57D0(*(a1+48), *(a1+148), a1+112, 0)` to turn it from
/// local into MODEL space in place, stamps `*(a1+20) = 2` and sets the dirty byte at
/// `*(a1+228)`. So this array is per-frame, and by the time the frame is over it is already
/// object-space: no parent walk on our side.
///
/// `comp+0x14` STATES the space — 1 local, 2 model — and it is returned rather than assumed.
/// A pose read mid-update is local, and silently treating those bones as model-space is the
/// kind of plausible-and-wrong result that reads as a rigging bug.
///
/// ELEMENT LAYOUT, 48 bytes: quaternion at +0 (w in lane 3), translation at +0x10, scale at
/// +0x20. NOTE this is NOT Havok's order — `hkQsTransform` is translation first, and the two
/// are the same size, so one reader cannot serve both.
///
/// The PARENT table comes from the skeleton at comp+0x94: entries of 8 bytes between
/// skel+0x2C and skel+0x30, parent index (-1 for a root) in the first dword. Bones are
/// ordered parent-before-child.
///
/// Fills 12 floats per bone and one parent index per bone. Returns the bone count, or 0 when
/// the entity carries no animated mesh — which is the common case and not an error.
static uint32_t RefiiHostReadEntityRig(uint32_t entity, float* out_transforms,
                                       int32_t* out_parents, uint32_t max_bones,
                                       uint32_t* out_space, uint32_t* out_torn, bool rest);

/// `out_torn` is set when the read could not be made coherent — see the snapshot comment in
/// `RefiiHostReadEntityRig`. The bones are still filled in on a torn read, because the
/// caller is better placed to decide between last frame's pose and this one.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityPose(uint32_t entity, float* out_transforms, int32_t* out_parents,
                                uint32_t max_bones, uint32_t* out_space, uint32_t* out_torn) {
    return RefiiHostReadEntityRig(entity, out_transforms, out_parents, max_bones, out_space,
                                  out_torn, false);
}

/// The REST pose of a character's rig — the bind pose Godot needs to build its skin.
///
/// Separate from the live pose because it is read ONCE per model and never changes, while
/// the live pose is read every frame. Same bone ordering and same parent table, so the two
/// index the same rig.
///
/// It is stored LOCAL and has to be composed through the parent chain to be useful, which is
/// the opposite of the live pose. Verified rather than assumed: `sub_82B73170` computes the
/// count as `(*(skel+148) - *(skel+144)) / 48`, copies those elements to a scratch buffer,
/// and runs the local-to-model converter `sub_82B734B0` ON THE COPY — you do not convert
/// something that is already converted.
///
/// Bones come back parent-before-child, so one forward pass composes them.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityRestPose(uint32_t entity, float* out_transforms,
                                    int32_t* out_parents, uint32_t max_bones) {
    return RefiiHostReadEntityRig(entity, out_transforms, out_parents, max_bones, nullptr,
                                  nullptr, true);
}

/// The shared body of the two rig reads above.
///
/// One implementation, because the only difference between them is WHICH 48-byte array on
/// the same component they take — the entity walk, the component lookup, the bounds checks
/// and the parent table are identical, and two copies of that would be two things to keep
/// in agreement.
static uint32_t RefiiHostReadEntityRig(uint32_t entity, float* out_transforms,
                                       int32_t* out_parents, uint32_t max_bones,
                                       uint32_t* out_space, uint32_t* out_torn, bool rest) {
    constexpr uint8_t kAnimatedMeshId = 6u;

    if (out_space != nullptr) {
        *out_space = 0u;
    }

    // Clean by default: every path that returns without reaching the snapshot returns no
    // bones at all, and "torn" must not read as true for a rig that was never read.
    if (out_torn != nullptr) {
        *out_torn = 0u;
    }

    if (out_transforms == nullptr || out_parents == nullptr || max_bones == 0u) {
        return 0u;
    }

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };
    const auto read8 = [base](uint32_t at) -> uint8_t { return base[at]; };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };
    if (!plausible(entity)) {
        return 0u;
    }

    // Same component lookup the model-hash read uses: the id bitmask gates it, the
    // accelerator resolves it in one step, and the linear scan is the fallback for an
    // entity that has not built one.
    if (((read32(entity + 0x24u) >> kAnimatedMeshId) & 1u) == 0u) {
        return 0u;
    }

    const uint32_t components = read32(entity + 0x48u);
    const uint32_t components_end = read32(entity + 0x4Cu);
    const uint32_t accelerator = read32(entity + 0x8Cu);

    uint32_t component = 0u;
    if (plausible(accelerator) && plausible(components)) {
        component = read32(components + 8u * read8(accelerator + kAnimatedMeshId) + 4u);
    } else if (plausible(components) && components_end > components) {
        for (uint32_t at = components; at + 8u <= components_end; at += 8u) {
            if (read32(at) == kAnimatedMeshId) {
                component = read32(at + 4u);
                break;
            }
        }
    }

    if (!plausible(component)) {
        return 0u;
    }

    // The parent table is the skeleton's, and it has to agree with whichever array we read
    // or the two are describing different rigs.
    const uint32_t skeleton = read32(component + 0x94u);
    if (!plausible(skeleton)) {
        return 0u;
    }

    // The live pose hangs off the COMPONENT; the rest pose off the SKELETON. Same element
    // size, same bone order, different owner — and quoting one against the other's base is
    // exactly the mistake that has cost this project a shipped-wrong offset before.
    const uint32_t pose = rest ? read32(skeleton + 0x90u) : read32(component + 0x30u);
    const uint32_t pose_end = rest ? read32(skeleton + 0x94u) : read32(component + 0x34u);
    if (!plausible(pose) || pose_end <= pose) {
        return 0u;  // sized but never filled — the character has not been posed yet
    }

    uint32_t bones = (pose_end - pose) / 48u;
    if (bones > max_bones) {
        bones = max_bones;
    }

    const uint32_t bone_table = read32(skeleton + 0x2Cu);
    const uint32_t bone_table_end = read32(skeleton + 0x30u);
    if (!plausible(bone_table) || bone_table_end <= bone_table ||
        (bone_table_end - bone_table) / 8u < bones) {
        return 0u;
    }

    // A COHERENT SNAPSHOT, because the guest rewrites this array underneath us.
    //
    // THE ARRAY IS WRITTEN IN PLACE AND THERE IS NOTHING TO SNAPSHOT INSTEAD. `sub_821F2C78`
    // XMemCpy's the pose in, modifiers mutate it, and `sub_821F57D0` then converts it from
    // parent-relative to model space BONE BY BONE over the same memory. The pointer at
    // `comp+0x30` is never swapped; there is no double buffer, no index and no generation
    // counter anywhere in the class. We read this from Godot's thread while the simulation
    // thread runs free, so a straddled read is possible by construction.
    //
    // AND A TORN RIG IS NOT A SMALL ERROR. During `sub_821F57D0` the array holds two
    // different SPACES at once — bones behind its cursor are model-space, bones ahead of it
    // are still parent-relative. Read then, part of the skeleton is interpreted in the wrong
    // space entirely, which is a character exploding for one frame rather than a bone being
    // slightly stale.
    //
    // NEITHER MARKER THE COMPONENT CARRIES CAN GATE THIS, and both fail the same way. The
    // flag at `comp+0x14` is stamped to 1 at the TAIL of `sub_821F2C78` (0x821F2F7C), so it
    // still reads 2 from the previous frame for the whole duration of the memcpy — a flag
    // check would accept exactly the tear it exists to catch. `dword_83319650` trails the
    // entire world update, so it does not move during one either. A marker that lags its own
    // payload is worse than no marker, because it reports confidence it has not earned.
    //
    // So: read the payload twice and require the two to agree, which assumes nothing about
    // either marker. This is what `RefiiHostGetCamera` already does for the same reason. An
    // idle character memcpy'd back to identical values compares equal, and that is correct —
    // identical bytes are not torn in any sense that matters.
    constexpr uint32_t kMaxSnapshotBones = 256u;
    if (bones > kMaxSnapshotBones) {
        bones = kMaxSnapshotBones;
    }

    static thread_local uint8_t first[kMaxSnapshotBones * 48u];
    static thread_local uint8_t second[kMaxSnapshotBones * 48u];
    const size_t span = (size_t)bones * 48u;

    bool coherent = false;
    for (int attempt = 0; attempt < 4 && !coherent; ++attempt) {
        std::memcpy(first, base + pose, span);
        std::memcpy(second, base + pose, span);
        coherent = std::memcmp(first, second, span) == 0;
    }

    // REPORTED, NOT HIDDEN. A caller that knows the read was torn can hold last frame's pose
    // for that entity, which is always better than drawing a rig assembled from two of them.
    // Swallowing it here would leave the host unable to tell a stale pose from a broken one.
    if (out_torn != nullptr) {
        *out_torn = coherent ? 0u : 1u;
    }

    for (uint32_t i = 0u; i < bones; ++i) {
        float* row = out_transforms + (i * 12u);
        for (uint32_t lane = 0u; lane < 12u; ++lane) {
            uint32_t bits = 0u;
            std::memcpy(&bits, first + (i * 48u) + (lane * 4u), sizeof(bits));
            bits = __builtin_bswap32(bits);
            std::memcpy(&row[lane], &bits, sizeof(row[lane]));
        }

        out_parents[i] = (int32_t)read32(bone_table + (i * 8u));
    }

    if (out_space != nullptr) {
        *out_space = read32(component + 0x14u);
    }

    return bones;
}

/// The guest's own NAME for each of a character's bones.
///
/// THIS IS WHAT MAKES A CHARACTER DRAWABLE. The `.mdl` numbers its bones one way and the
/// guest's runtime skeleton numbers them another — 143 against 161 for the child hero, and
/// 39 against 39 with only 11 of the parent links agreeing for a chicken. A mesh's
/// BLENDINDICES address the model's table, so binding them to a skeleton built in the
/// guest's order attaches every vertex to the wrong bone and the character renders as a fan
/// of spikes. The name is the only key both sides share.
///
/// WHERE THE CHARACTERS ARE. `sub_821B5508` is the guest's own find-bone-by-name and is the
/// ground truth for all of this:
///
///     v3 = *(skel + 44);                    // skel+0x2C, bone table begin
///     v4 = (*(skel + 48) - v3) >> 3;        // 8 bytes per entry
///     for ( i = (int *)(v3 + 4); ; i += 2 ) // &bone[0].name, stride 8
///         if ( *i == v5 ) ...               // *a2, compared WITHOUT a dereference
///
/// so the dword at `bone + 4` IS the string node, not a handle to be resolved. The node's
/// own layout comes from the comparator it falls back on when the pointers differ,
/// `LHString_CBasicString_char_operator` @ 0x8226D790, which is called with those same two
/// node pointers:
///
///     return *(_DWORD *)(a1 + 4) == *(_DWORD *)(a2 + 4)
///         && sub_8226D7F8(*(_BYTE **)a1, *(_BYTE **)a2) == 0;
///
/// — `node + 4` is the byte length and `*(char**)node` is NUL-terminated ASCII. Plain
/// characters, in memory, in the shipped build: a strcmp cannot work otherwise.
///
/// A null node is legitimate and means an unnamed bone, not a failure — the guest degrades
/// it to the shared empty string at 0x8208FFDF, which is a single 0x00 byte. Names are also
/// INTERNED through a global set, so two bones with the same name share one node; that is
/// why the scan above has a pointer fast path, and it is why a caller must never key a map
/// on the pointer.
///
/// Fills `max_bones * stride` bytes, one NUL-padded name per bone. Returns the bone count,
/// or 0 when the entity carries no animated mesh.
///
/// The STRIDE is the caller's, not a constant here, so there is exactly one owner of it. A
/// matching pair of constants either side of a P/Invoke is a thing that drifts, and it
/// drifts silently: the reader simply starts finding the tail of the previous name.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetEntityBoneNames(uint32_t entity, char* out_names, uint32_t stride,
                                     uint32_t max_bones) {
    constexpr uint8_t kAnimatedMeshId = 6u;

    if (out_names == nullptr || max_bones == 0u || stride < 2u) {
        return 0u;
    }

    std::memset(out_names, 0, (size_t)max_bones * stride);

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };
    const auto read8 = [base](uint32_t at) -> uint8_t { return base[at]; };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    if (!plausible(entity) || ((read32(entity + 0x24u) >> kAnimatedMeshId) & 1u) == 0u) {
        return 0u;
    }

    const uint32_t components = read32(entity + 0x48u);
    const uint32_t components_end = read32(entity + 0x4Cu);
    const uint32_t accelerator = read32(entity + 0x8Cu);

    uint32_t component = 0u;
    if (plausible(accelerator) && plausible(components)) {
        component = read32(components + 8u * read8(accelerator + kAnimatedMeshId) + 4u);
    } else if (plausible(components) && components_end > components) {
        for (uint32_t at = components; at + 8u <= components_end; at += 8u) {
            if (read32(at) == kAnimatedMeshId) {
                component = read32(at + 4u);
                break;
            }
        }
    }

    if (!plausible(component)) {
        return 0u;
    }

    const uint32_t skeleton = read32(component + 0x94u);
    if (!plausible(skeleton)) {
        return 0u;
    }

    const uint32_t table = read32(skeleton + 0x2Cu);
    const uint32_t table_end = read32(skeleton + 0x30u);
    if (!plausible(table) || table_end <= table) {
        return 0u;
    }

    uint32_t bones = (table_end - table) / 8u;
    if (bones > max_bones) {
        bones = max_bones;
    }

    for (uint32_t i = 0u; i < bones; ++i) {
        char* slot = out_names + ((size_t)i * stride);
        const uint32_t node = read32(table + (i * 8u) + 4u);
        if (!plausible(node)) {
            continue;  // unnamed — the guest's own empty-string case, not an error
        }

        const uint32_t chars = read32(node);
        uint32_t length = read32(node + 4u);
        if (!plausible(chars)) {
            continue;
        }

        // The length is the guest's, so it is clamped rather than trusted: a torn read of a
        // string being rebuilt would otherwise walk off into whatever follows it.
        if (length > stride - 1u) {
            length = stride - 1u;
        }

        for (uint32_t c = 0u; c < length; ++c) {
            const uint8_t byte = read8(chars + c);
            if (byte == 0u) {
                break;
            }

            slot[c] = (char)byte;
        }
    }

    return bones;
}

/// The command-stream rings, read-only. `sub_8222ABE0` blocks when the reader traps the
/// writer, and these four numbers per slot are the whole condition.
///
/// Fills 4 slots x 6 words: object, capacity, write cursor (+32), write consumed (+36),
/// reader consumed (+64), last read (+24). A zero object means the slot is empty.
REFII_HOST_ABI_EXPORT void RefiiHostGetSceneStreams(uint32_t* out) {
    if (out == nullptr) {
        return;
    }

    for (int i = 0; i < 24; ++i) {
        out[i] = 0;
    }

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return;
    }

    const auto read32 = [base](uint32_t at) {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };

    for (uint32_t slot = 0; slot < 4u; ++slot) {
        const uint32_t object = read32(0x8349F9B0u + slot * 4u);
        if (object < 0x10000u || object >= 0xC0000000u) {
            continue;
        }

        uint32_t* row = out + slot * 6u;
        row[0] = object;
        row[1] = read32(object + 8u);
        row[2] = read32(object + 32u);
        row[3] = read32(object + 36u);
        row[4] = read32(object + 64u);
        row[5] = read32(object + 24u);
    }
}

/// One guest LHString global to a NUL-terminated ASCII buffer.
///
/// The global holds a REP POINTER, not characters — `sub_821F0240` copies `*a2` and then
/// bumps a refcount at `Rep+12` (0x821F02A4 / 0x821F02B4). Reading the Rep as if it were the
/// text is what made this return empty and skip the world build silently.
///
/// THE REP LAYOUT, decompiled rather than guessed. `sub_82208778` is the whole definition —
/// it is the only thing that ever fills one:
///
///   v7 = alloc((len + 4) & ~3);   *a1 = v7;      // +0x00 = the CHARACTER BUFFER
///   a1[1] = len;                                 // +0x04 = length
///   a1[2] = (2 * ((len + 4) & ~3)) | (a1[2] & 1);// +0x08 = capacity<<1 | flag
///   *(BYTE *)(v7 + len) = 0;                     // NUL, so it is narrow, not UTF-16
///   XMemCpy(*a1, a2, len);                       // one byte per character
///
/// Read the layout, do not probe for it. A heuristic that tries candidate pointers against
/// two encodings and takes whichever comes out printable does not fail loudly when it is
/// wrong — it returns plausible rubbish, and one global read as "TravelRS_Coach", "P ",
/// "Albion" and "Mh" across four consecutive runs.
static bool read_guest_name(const uint8_t* base, uint32_t global, char* out,
                            uint32_t capacity) {
    if (out == nullptr || capacity == 0) {
        return false;
    }

    const auto read32 = [base](uint32_t at) {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };

    const uint32_t rep = read32(global);
    if (rep < 0x10000u || rep >= 0xC0000000u) {
        return false;
    }

    const uint32_t chars = read32(rep);
    const uint32_t length = read32(rep + 4u);
    if (chars < 0x10000u || chars >= 0xC0000000u || length == 0u || length >= capacity) {
        out[0] = '\0';
        return false;
    }

    for (uint32_t i = 0; i < length; ++i) {
        const char c = static_cast<char>(base[chars + i]);

        // A name here is short and printable. Anything else means the Rep was freed or torn
        // under us, and half a name is worse than none.
        if (c < 0x20 || c > 0x7E) {
            out[0] = '\0';
            return false;
        }

        out[i] = c;
    }

    out[length] = '\0';
    return true;
}

/// Where the guest is going. `sub_82379628` — the sole new-game entry — writes these before
/// it latches the mode change, into four CONSECUTIVE globals:
///
///     g_StartWorldName    = "Albion"                 0x83497584
///     g_StartLevelName    = "BWSSlums"               0x83497588
///     g_StartScenarioName = "DefaultScenario"        0x8349758C   (0x8237972C)
///     g_StartEntranceName = "QC010_ChildhoodStart"   0x83497590
///
/// Fills three NUL-terminated buffers. Returns 1 when the level name came out non-empty —
/// that is the one a caller cannot proceed without. The scenario has its own entry point
/// because adding a parameter here would break the ABI for a value most callers do not want.
REFII_HOST_ABI_EXPORT int RefiiHostGetStartLevel(char* world, char* level, char* entrance, uint32_t capacity) {
    if (capacity == 0) {
        return 0;
    }

    if (world != nullptr) world[0] = '\0';
    if (level != nullptr) level[0] = '\0';
    if (entrance != nullptr) entrance[0] = '\0';

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    read_guest_name(base, 0x83497584u, world, capacity);
    read_guest_name(base, 0x83497590u, entrance, capacity);
    return read_guest_name(base, 0x83497588u, level, capacity) ? 1 : 0;
}

/// The world manager, from the one fixed global that reaches it.
///
/// Six hops, and every one of them was quoted out of `sub_823BBA70` (0x823BBA8C onward), which
/// emits the whole chain in seven consecutive instructions. Two other call sites — the savegame
/// loader at 0x82447B44 and the Lua registrar at 0x82458FFC — emit the identical form, which is
/// what a canonical accessor looks like when it has been inlined everywhere.
///
/// NOT CACHED across a level load: the chain is rebuilt, so a stale pointer here is a pointer
/// into a freed object.
static uint32_t world_manager(const uint8_t* base) {
    const auto u32 = [base](uint32_t at) {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };

    const auto step = [&](uint32_t at, uint32_t offset) -> uint32_t {
        if (at < 0x10000u || at >= 0xC0000000u) {
            return 0;
        }
        return u32(at + offset);
    };

    uint32_t at = u32(0x83496AB8u);
    at = step(at, 0x0Cu);
    at = step(at, 0x58u);
    at = step(at, 0x04u);
    at = step(at, 0x08u);
    at = step(at, 0x04u);
    return (at >= 0x10000u && at < 0xC0000000u) ? at : 0;
}

/// WHERE THE GUEST ACTUALLY IS — as opposed to where a New Game asked it to go.
///
/// `RefiiHostGetStartLevel` reads `g_StartWorldName`/`g_StartLevelName` (0x83497584/88), and a
/// host that polls those to notice a level change is reading the wrong thing. Every writer of
/// those globals is a GAME-START path: the CRT static initialisers ("TestWorld"/"TW_Level1"),
/// the `SetInitialWorldName`/`SetInitialLevelName` Lua natives, the new-game entry
/// `sub_82379628`, `Frontend_SetStartGameTarget`, the dev override, the network-join handler
/// and the debug level-select menu.
///
/// NOT ONE TRANSITION PATH TOUCHES THEM. Doors and teleporters (`sub_8283C7F8`), fast travel
/// (`sub_82519B08`), the `World.LoadLevel` Lua native (`sub_82350D20`) and a level reload
/// (`sub_8231C988`) all reach `World_RequestLevelChange` (0x82350B20), which puts the
/// destination on the COMMAND OBJECT at cmd+0x14/+0x18/+0x1C and never in a global. So polling
/// the start globals notices a new game and nothing else — and it appears to work in a dev
/// build only because the debug menu happens to rewrite them before it calls `Debug.LoadLevel`.
///
/// This reads the standing World and Level instead, which is mechanism-independent: new game,
/// load-save, door, teleporter, fast travel and network join all land here through the single
/// `World_LoadLevel_Execute` (0x8231C608) -> Level ctor (0x82354228) path.
///
/// OFFSETS, EACH AGAINST ITS OWN BASE, verified in the decompile rather than taken on report:
///
///     world = world_manager(base)   // despite the name this IS the World: the helper's last
///                                   // two hops are *(mgr+8) then +4, which is exactly the
///                                   // `*(*(a1+8)+4)` that 0x82350B64 dereferences
///       world + 0x14  -> Rep, world name    (written by `sub_822651A0(a1+20, a2)` @0x8231C664)
///       world + 0x24  -> holder; *(holder+4) = Level
///     level + 0xAC -> Rep, level name       (`sub_821F0240(a1+172, a3)` @0x823542E0)
///     level + 0xB0 -> Rep, entrance name    (ctor seeds it "PlayerStart" @0x823542F4; state 5
///                                            of CmdLevelChange_Update overwrites it)
///     level + 0xB4 =  u32 key               (`*(a1+180) = a2`, and the caller passes
///                                            `World_ComputeWorldLevelFnv1Key(a1,a2,a3)`)
///
/// THE KEY IS THE THING TO POLL. It is one aligned dword that changes on exactly the
/// transitions that reload a level, so a host can compare it frame to frame without parsing a
/// string or caring which mechanism moved the player. The names are for humans and for
/// resolving the region; the key is the edge.
///
/// `out_key` receives it. Returns 1 when a level is standing, 0 when none is — which is the
/// ordinary state before the first load and during the window when the old Level has been
/// released and the new one not yet constructed.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetCurrentLevel(char* world, char* level, char* entrance, uint32_t capacity,
                                  uint32_t* out_key) {
    if (out_key != nullptr) {
        *out_key = 0u;
    }

    if (capacity == 0) {
        return 0;
    }

    if (world != nullptr) world[0] = '\0';
    if (level != nullptr) level[0] = '\0';
    if (entrance != nullptr) entrance[0] = '\0';

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto u32 = [base](uint32_t at) {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };

    const uint32_t world_object = world_manager(base);
    if (world_object == 0) {
        return 0;
    }

    const uint32_t holder = u32(world_object + 0x24u);
    if (holder < 0x10000u || holder >= 0xC0000000u) {
        return 0;  // no level standing
    }

    const uint32_t level_object = u32(holder + 4u);
    if (level_object < 0x10000u || level_object >= 0xC0000000u) {
        return 0;
    }

    if (out_key != nullptr) {
        *out_key = u32(level_object + 0xB4u);
    }

    read_guest_name(base, world_object + 0x14u, world, capacity);
    read_guest_name(base, level_object + 0xB0u, entrance, capacity);

    // The LEVEL name is the one a caller cannot proceed without, so it decides the result.
    return read_guest_name(base, level_object + 0xACu, level, capacity) ? 1 : 0;
}

/// The scenario the guest is ACTUALLY in, for the level it currently has loaded.
///
/// This is the live answer, where RefiiHostGetStartScenario is only the value a new game began
/// with. `Layers.ActivateScenario` rewrites it and reloads the level, so a quest advancing the
/// story changes which map and which placements the level has.
///
/// The lookup is the guest's own: the level caches its FNV-1 key at level+180, and the scenario
/// map is a vector of {u32 key, LHString name} at worldMgr+88..+92. THE SORT IS LAZY — worldMgr
/// +101 is a dirty flag and `sub_82321228` only sorts when it is set, so a host polling between
/// an insert and the flush must scan linearly or it will silently miss the entry it wants.
REFII_HOST_ABI_EXPORT int RefiiHostGetActiveScenario(char* scenario, uint32_t capacity) {
    if (scenario == nullptr || capacity == 0) {
        return 0;
    }

    scenario[0] = '\0';

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto u32 = [base](uint32_t at) {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };

    const uint32_t wm = world_manager(base);
    if (wm == 0) {
        return 0;
    }

    const uint32_t holder = u32(wm + 36u);
    if (holder < 0x10000u || holder >= 0xC0000000u) {
        return 0;  // no level loaded
    }

    const uint32_t level = u32(holder + 4u);
    if (level < 0x10000u || level >= 0xC0000000u) {
        return 0;
    }

    const uint32_t key = u32(level + 180u);
    const uint32_t first = u32(wm + 88u);
    const uint32_t last = u32(wm + 92u);
    if (first < 0x10000u || last < first || ((last - first) % 8u) != 0u) {
        return 0;
    }

    // Linear either way. The vector is at most a few dozen entries, and a binary search that is
    // wrong when the dirty flag is set is worse than a scan that is always right.
    for (uint32_t at = first; at + 8u <= last; at += 8u) {
        if (u32(at) != key) {
            continue;
        }

        return read_guest_name(base, at + 4u, scenario, capacity) ? 1 : 0;
    }

    return 0;
}

/// The FNV-1 hashes of the layers the guest currently has active.
///
/// Layers gate ENTITY INSTANTIATION — the `.engine_level` marks elements AlwaysOn or
/// InitiallyOn and `Layers.ActivateLayer` turns the rest on — so a host that places everything
/// it finds will place things the guest has not.
///
/// The container is an MSVC `std::list<uint32_t>`, which the overflow guard gives away by
/// raising "list<T> too long" (`sub_82322878`). worldMgr+60 is the _Myproxy and reads as a
/// perfectly plausible pointer; the head is +64 and the size is +68. Nodes are 12 bytes,
/// {_Next, _Prev, u32 hash}.
///
/// There is a SECOND identically shaped list at worldMgr+72; it is not this one and its meaning
/// is unproven.
///
/// Returns how many hashes were written. Hashes are FNV-1 over the layer name's EXACT bytes —
/// no lowercasing and no separator normalisation, unlike the world+level key.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetActiveLayers(uint32_t* out, uint32_t capacity) {
    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto u32 = [base](uint32_t at) {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };

    const uint32_t wm = world_manager(base);
    if (wm == 0) {
        return 0;
    }

    const uint32_t head = u32(wm + 64u);
    if (head < 0x10000u || head >= 0xC0000000u) {
        return 0;
    }

    const uint32_t size = u32(wm + 68u);
    if (size > 4096u) {
        return 0;  // not a list we recognise
    }

    uint32_t written = 0;
    uint32_t node = u32(head);
    for (uint32_t guard = 0; guard <= size + 8u; ++guard) {
        if (node == head || node < 0x10000u || node >= 0xC0000000u) {
            break;
        }

        if (out != nullptr && written < capacity) {
            out[written] = u32(node + 8u);
        }

        ++written;
        node = u32(node);
    }

    return written;
}

/// THE SCENARIO, which is what actually picks the map.
///
/// `Level_BuildDataWorldsFilePath` (0x8250EED0) builds
/// `Data\Worlds\<World>\<Level>\<Scenario>\<Scenario>.list` and the loader takes every
/// .ghf that manifest names — `Level_LoadHeightfieldGhfEntry` (0x8250ECE0) does no filtering
/// of its own. So a level with five heightfields has five scenarios, and without this value a
/// host has to guess which one; picking by name order put a childhood entrance in chapter 2's
/// Bowerstone, which reads as the wrong season because the seasons ARE the scenarios.
///
/// This returns the START scenario. The LIVE one is a world-manager map keyed by
/// FNV-1(world + level) that Lua's `Layers.ActivateScenario` writes, and it is not exposed
/// yet — so this is right for a new game and stale once a quest moves the story on.
REFII_HOST_ABI_EXPORT int RefiiHostGetStartScenario(char* scenario, uint32_t capacity) {
    if (scenario == nullptr || capacity == 0) {
        return 0;
    }

    scenario[0] = '\0';

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    const uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    return read_guest_name(base, 0x8349758Cu, scenario, capacity) ? 1 : 0;
}

REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFrontEndState(void) {
    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    // byte_83496AC4 — one byte, so endianness does not enter into it.
    return base[0x83496AC4u];
}

/// Every element of the active screen: its identity, its slide, and its live TEXT.
///
/// The text matters as much as the slide. A shipped `.bgf` carries placeholders — the
/// dialog panel authors "DEFAULT_TEXT_ID" — and the real string is written at runtime by
/// SetText (`sub_82425DD0`, which stores to the record at `**(element+4) + 140` and marks
/// `record+144` "set externally"). Other labels are tags the guest resolves through a
/// table that is NOT the Babel book — `GUI_CONTINUE_WITHOUT_SAVING` is absent from
/// `book.babel` under either FNV-1 variant. Reading what the element actually holds makes
/// both cases moot: it is the string the game is displaying, whatever produced it.
///
/// The string is a refcounted Rep: `+0` UTF-16 data, `+4` length in code units, `+8`
/// capacity, `+12` refcount. The characters are BIG-endian, like everything else the
/// guest wrote (`sub_822D6260` compares them with `lhz`, stride 2).
///
/// Two buffers so one walk fills both:
///   `words`  — 3 per element: {name hash, current slide, text offset}, where the offset
///              is an index into `text` in UTF-16 units, or 0xFFFFFFFF for no text.
///   `text`   — the strings, host-endian UTF-16, each NUL-terminated.
///
/// Returns the number of ELEMENTS available. Pass nulls to size the buffers.
/// The front end as a MODEL, not as widgets — what a natively rebuilt menu needs.
///
/// The element tree is not where the front end's meaning lives, so mirroring it is not enough.
/// The main menu has no C++ item list and no C++ highlight: items, order,
/// nesting and the highlight are all inside the shipped Lua object `g_Menu`. `sub_826E2220`
/// SPAWNS seven items into it one call at a time and keeps none of them, and root-menu
/// Up/Down never touch C++ state at all — they forward strings to Lua.
///
/// So there is nothing to read that says "item 3 is highlighted". A native menu owns its own
/// highlight; what it needs from the guest is which entries EXIST, because the guest's own
/// spawn conditions decide that and a host list that disagrees would offer a menu the guest
/// will not honour.
///
/// The two spawn conditions, at the instruction level:
///   signed in  — `lwz r4, dword_83496AF8` / `lwz r10, dword_83496A70` / `slw r7, r8, r4` /
///                `and r5, r7, r10`, and a zero result skips the Load Game spawn entirely.
///   languages  — `lwz r11, dword_83319934` / `cmplwi cr6, r11, 1` / `ble` past the spawn,
///                so the Language entry exists only for MORE THAN ONE installed language.
///
/// LOAD GAME IS NOT HERE, and that is deliberate. Its condition is signed-in AND at least one
/// save, and the save count comes from `sub_82443308`, which is a live
/// `XamContentCreateEnumerator` — a guest CALL, not a memory read. Everything in this bridge
/// is call-free by design, so the count is left to the caller, which in this port owns
/// storage anyway. Reporting a guessed count would be worse than reporting none.
///
/// Fills 11 words. Returns 1 when the front-end GUI is up, 0 otherwise.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetFrontEndModel(uint32_t* out) {
    if (out == nullptr) {
        return 0u;
    }

    for (int i = 0; i < 11; ++i) {
        out[i] = 0u;
    }

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0u;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0u;
    }

    const auto read32 = [base](uint32_t at) -> uint32_t {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };
    const auto plausible = [](uint32_t address) {
        return address >= 0x10000u && address < 0xC0000000u;
    };

    const uint32_t gui = read32(0x83496B08u);
    if (!plausible(gui) || read32(gui) != 0x820A924Cu) {
        return 0u;  // the base-only instance the game republishes — no front end here
    }

    const uint32_t title = read32(gui + 180u);
    const uint32_t menu = read32(gui + 184u);
    const uint32_t active = read32(gui + 188u);

    const uint32_t user = read32(0x83496AF8u);
    const uint32_t signed_mask = read32(0x83496A70u);
    const bool signed_in = user < 32u && ((1u << user) & signed_mask) != 0u;

    out[0] = signed_in ? 1u : 0u;
    out[1] = user;
    out[2] = read32(0x83319934u);  // installed language count; > 1 spawns "Language"
    out[3] = read32(0x83319938u);  // current language index

    // The "new downloadable content" glyph: shown when the flag is set and the available
    // count differs from the count already seen.
    const uint32_t available = read32(0x83496B20u);
    const uint32_t seen = read32(0x83496B24u);
    out[4] = (base[0x8331963Eu] != 0u && (int32_t)available > 0 && available != seen) ? 1u : 0u;

    // WHICH SCREEN, by pointer identity. There is no title state that means "the menu is up" —
    // state 11 is consumed and reset to 0 in the same update, so reading it that way reports
    // the handover as never having happened.
    out[5] = (plausible(menu) && active == menu) ? 1u : 0u;
    out[6] = plausible(menu) ? read32(menu + 24u) : 0u;
    out[7] = plausible(title) ? read32(title + 24u) : 0u;
    out[8] = plausible(title) ? base[title + 80u] : 0u;  // the CAN_PRESS_A gate

    // The Load Game card cursor. NOT the menu highlight — it moves only on Left/Right and only
    // in menu state 5, and it indexes the save-card vector at menu+84.
    out[9] = plausible(menu) ? read32(menu + 72u) : 0u;

    // HOW MANY SAVES THE GUEST HAS ENUMERATED, from the same vector its own "Load Game"
    // branch tests before deciding whether to offer one:
    //
    //     826e03d0  lwz    r11, 8(r28)     ; end        (screen+88)
    //     826e03d8  lwz    r10, 4(r28)     ; begin      (screen+84)
    //     826e03dc  subf   r9, r10, r11
    //     826e03e0  srawi. r8, r9, 2       ; / 4 = count
    //     826e03e4  beq    -> "no save games"
    //
    // A std::vector of 4-byte entries, so the count is the byte span over 4. This is a stated
    // value read with no call, which a host-side guess at storage cannot be.
    //
    // CAVEAT, and it is why this is named "enumerated" rather than "saves": the guest fills
    // this vector when it enumerates, which it does on its own schedule. A zero here means
    // "the guest has not told us about any", NOT "there are none on the device" — the guest
    // decides that with a live XamContentCreateEnumerator, which is a call and not a read.
    uint32_t enumerated = 0;
    if (plausible(menu)) {
        const uint32_t begin = read32(menu + 84u);
        const uint32_t end = read32(menu + 88u);
        if (end >= begin && (end - begin) < 0x10000u) {
            enumerated = (end - begin) / 4u;
        }
    }
    out[10] = enumerated;

    return 1u;
}

/// Every Gameface presentation the guest currently has live — HUD, menus and front end.
///
/// ONE REGISTRY, NO SPECIAL CASES. The three owners keep their `CPresentation*` in three
/// different places (`wrapper+20` for the front end, `GUI+32 -> +4` for the HUD,
/// `mgr+12+4*id` for the menus), but every one of them is also appended to a single array on
/// the AKPlayer at `GUI+16`. Enumerating that array means the host never has to know which
/// kind of screen it is looking at — and `GUI+16` is a BASE-class field, zeroed in
/// `sub_823710A8` and populated by base-class code, so unlike `GUI+188` it survives the
/// object swap when the game takes the GUI over.
///
/// The array is DENSE: removal (`sub_82BEDC90`) memmoves the tail down and decrements the
/// count, so there are no holes to skip and indices are not stable across removals.
///
/// WHETHER IT IS ACTUALLY SHOWING is a separate question from whether it is live, and this
/// answers both. In game the guest keeps `pausemainmenu.bgf` and `menucycler.bgf` in the
/// registry permanently with valid roots while neither is on screen, so a host that draws
/// everything live puts a closed pause menu over the running game.
///
/// The gate is the ROOT ELEMENT's own `active` bit, read straight out of
/// `CPresentation::Update` (`sub_822AC880`):
///
///     822ac9c4  lwz    r4, 0x1F4(r30)     ; root = *(pres + 500)
///     822ac9d4  lhz    r11, 0x1C(r4)      ; the flags halfword
///     822ac9d8  rlwinm r10, r11, 0,18,18  ; bit 18 MSB-first == 0x2000
///     822ac9e0  beq    cr6, loc_822AC9EC  ; clear -> skip the entire tree walk
///
/// It is bit `0x2000` (`active`) and NOT `0x1000` (`globalactive`). That distinction is the
/// whole trap: the flag writer is a plain mask set/clear with no cross-linking, and the only
/// code that clears `0x1000` lives inside the tree walk that this gate skips — so a hidden
/// presentation keeps `globalactive` set on its root forever. Following `0x1000` here would
/// report every closed menu as visible.
///
/// `pres+873` is checked too, but it means FROZEN rather than hidden: non-zero returns before
/// the per-frame lists are wiped, so the presentation holds last frame's content instead of
/// becoming empty.
///
/// Five words per presentation — {address, root, name offset, state, active count} — where
/// state bit 0 is "root active", bit 1 is "frozen". The active count is the guest's own
/// answer: `pres+84` is a 48-byte array rebuilt every frame with exactly the globally-active
/// elements, so its count (at `pres+124`) is zero for a screen that is contributing nothing.
///
/// The names are written
/// into <paramref name="names"/> as NUL-separated ASCII. The NAME is the only discriminator
/// there is: every presentation shares one vtable (`off_82006D48`), so the class cannot tell
/// the HUD from a menu; the string at `presentation+12` can. It is an AKString — `char*` at
/// +12, u16 length at +16 — and it holds the path the factory was handed, e.g. the one ending
/// `HUD.bgf`. Match on a SUFFIX rather than the whole string: the path is built by
/// `LHFile_MakePathOSConformant` and its prefix and slash direction are not established.
///
/// Returns the number of live presentations, which may exceed what fitted.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetPresentations(uint32_t* words, uint32_t word_capacity,
                                   char* names, uint32_t name_capacity) {
    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    auto read32 = [base](uint32_t address) -> uint32_t {
        uint32_t value = 0;
        std::memcpy(&value, base + address, sizeof(value));
        return __builtin_bswap32(value);
    };

    auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    const uint32_t gui = read32(0x83496B08u);
    if (!plausible(gui)) {
        return 0;
    }

    const uint32_t player = read32(gui + 16u);
    if (!plausible(player)) {
        return 0;
    }

    const uint32_t array = read32(player + 72u);
    const uint32_t count = read32(player + 80u);
    if (!plausible(array)) {
        return 0;
    }

    // A ceiling on a count read out of guest memory, so a corrupted word cannot walk us off
    // into the address space. The shipped game keeps a handful live at once.
    constexpr uint32_t kMaxPresentations = 256u;
    constexpr uint32_t kMaxNameChars = 260u;

    uint32_t found = 0;
    uint32_t chars = 0;

    for (uint32_t i = 0; i < count && i < kMaxPresentations; ++i) {
        const uint32_t presentation = read32(array + i * 4u);
        if (!plausible(presentation)) {
            continue;
        }

        // Null-checked rather than assumed: nothing in the image stores to +500 except the
        // constructor's zero-init, so the pool is installed through a pointer to the +492
        // subobject and there is a window during load where a live presentation has no root.
        const uint32_t root = read32(presentation + 492u + 8u);

        uint32_t offset = 0xFFFFFFFFu;
        const uint32_t text = read32(presentation + 12u);
        uint16_t length = 0;
        std::memcpy(&length, base + presentation + 16u, sizeof(length));
        length = __builtin_bswap16(length);

        if (plausible(text) && length > 0 && length < kMaxNameChars) {
            offset = chars;
            for (uint16_t c = 0; c < length; ++c) {
                if (names != nullptr && chars < name_capacity) {
                    names[chars] = static_cast<char>(base[text + c]);
                }
                ++chars;
            }
            if (names != nullptr && chars < name_capacity) {
                names[chars] = '\0';
            }
            ++chars;
        }

        // Is it SHOWING? The root's own `active` bit, and the freeze byte.
        uint32_t state = 0u;
        if (plausible(root)) {
            uint16_t flags = 0;
            std::memcpy(&flags, base + root + 0x1Cu, sizeof(flags));
            if ((__builtin_bswap16(flags) & 0x2000u) != 0u) {
                state |= 1u;
            }
        }
        // The two presentation-level suppression bytes, INVERTED — 0 is live, 1 is parked.
        // The constructor zeroes both, so a presentation is born visible and the game parks
        // the ones it does not want.
        //
        //   +0x369 (873) gates the per-frame UPDATE. `CPresentation::Update` reads it first
        //                (`lbz r11, 0x369(r30)` at 0x822AC890) and returns immediately.
        //   +0x368 (872) gates the SCENE DRAW. `CScene`'s slot-5 fetches the owning
        //                presentation from `scene+4`, calls its vtable +0x60 — which is
        //                `sub_822CD108`, a two-instruction `lbz r3, 0x368(r3); blr` — and
        //                branches past the whole draw when it is non-zero.
        //
        // The pause menu and the menu cycler are loaded and then written (1,1); the HUD's
        // loader never touches either byte, which is exactly why only the HUD is on screen.
        if (base[presentation + 873u] != 0u) {
            state |= 2u;
        }
        if (base[presentation + 872u] != 0u) {
            state |= 4u;
        }

        // The guest's own count of globally-active elements this frame — zero for a screen
        // that is drawing nothing. Reported alongside the flag rather than instead of it, so
        // a disagreement between the two is visible rather than silently resolved.
        const uint32_t active_count = read32(presentation + 124u);

        if (words != nullptr && (found * 5u + 4u) < word_capacity) {
            words[found * 5u] = presentation;
            words[found * 5u + 1u] = plausible(root) ? root : 0u;
            words[found * 5u + 2u] = offset;
            words[found * 5u + 3u] = state;
            words[found * 5u + 4u] = active_count;
        }
        ++found;
    }

    return found;
}

/// Walks one presentation's element tree.
///
/// <paramref name="requested_presentation"/> is a `CPresentation*` from
/// <see cref="RefiiHostGetPresentations"/>, or 0 for the front end's active screen — which is
/// what every caller meant before the HUD and the in-game menus needed reaching.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetUiElements(uint32_t requested_presentation,
                                uint32_t* words, uint32_t word_capacity,
                                uint16_t* text, uint32_t text_capacity) {
    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    auto read32 = [base](uint32_t address) -> uint32_t {
        uint32_t value = 0;
        std::memcpy(&value, base + address, sizeof(value));
        return __builtin_bswap32(value);
    };

    auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    // WHICH presentation. Zero keeps the old meaning — the front end's active screen — and
    // any other value is one the caller enumerated.
    //
    // The front-end chain below is NOT general and it is DEAD in game: `gui+188` is written
    // only by the derived GUI_FRONT_END constructor (`sub_82378230`), and once the game takes
    // over, the object republished at 0x83496B08 is the BASE class, allocated at 176 bytes
    // (`li r3, 0xB0` at 0x82310FE0) — so reading +188 there runs off the end of the
    // allocation. That is why the HUD and the thirty in-game menus were invisible here: not
    // because they are a different kind of object, but because this could only ever find one
    // screen, and only while the front end owned the GUI.
    uint32_t presentation = requested_presentation;
    if (presentation == 0u) {
        const uint32_t gui = read32(0x83496B08u);
        if (!plausible(gui)) {
            return 0;
        }

        const uint32_t active = read32(gui + 188u);
        if (!plausible(active)) {
            return 0;
        }

        // Front-end wrappers hold the CPresentation at +20. The HUD (`GUI+32` then `+4`) and
        // the menus (`mgr+12+4*id`) store it DIRECTLY — that indirection difference is what
        // RefiiHostGetPresentations exists to hide.
        presentation = read32(active + 20u);
    }

    if (!plausible(presentation)) {
        return 0;
    }

    // +492 is an embedded subobject and its `+8` is the root node — verified from two
    // one-instruction thunks that compose to it: `sub_82284360` is `addi r3, r3, 0x1EC; blr`
    // and `sub_830F3910` is `lwz r3, 8(r3); blr`. 0x1EC is 492, measured from the
    // CPresentation base. Identical for the HUD, the menus and the front end, because all
    // three come out of one factory (`sub_823DCB10`).
    const uint32_t root = read32(presentation + 492u + 8u);
    if (!plausible(root)) {
        return 0;
    }

    // The live text of one element, or "no text". Only TEXT elements (type 4) carry a
    // record; asking any other kind for one would be dereferencing whatever `+4` happens
    // to point at for that type.
    auto text_rep = [&](uint32_t element) -> uint32_t {
        if (base[element + 30u] != 4u) {
            return 0;
        }

        const uint32_t holder = read32(element + 4u);
        if (!plausible(holder)) {
            return 0;
        }

        const uint32_t record = read32(holder);
        if (!plausible(record)) {
            return 0;
        }

        const uint32_t rep = read32(record + 140u);
        return plausible(rep) ? rep : 0;
    };

    // Link-following, with NO auxiliary stack.
    //
    // The first version pushed siblings onto a fixed 64-entry stack and dropped subtrees
    // when it filled: it reported 289 of this screen's 3812 elements, so 92% of the UI
    // fell back to a guess while looking like live data. Walking child -> sibling and
    // climbing through the parent link at +12 has no such ceiling; the only bound is the
    // element count itself, which also breaks a corrupted cycle.
    constexpr uint32_t kMaxElements = 16384u;
    constexpr uint32_t kMaxChars = 512u;

    uint32_t found = 0;
    uint32_t chars = 0;
    uint32_t element = root;

    while (element != 0 && found < kMaxElements) {
        if (!plausible(element)) {
            break;
        }

        uint32_t offset = 0xFFFFFFFFu;
        const uint32_t rep = text_rep(element);
        if (rep != 0) {
            const uint32_t data = read32(rep);
            const uint32_t length = read32(rep + 4u);
            if (plausible(data) && length > 0 && length < kMaxChars) {
                offset = chars;
                for (uint32_t i = 0; i < length; ++i) {
                    uint16_t unit = 0;
                    std::memcpy(&unit, base + data + i * 2u, sizeof(unit));
                    unit = __builtin_bswap16(unit);
                    if (text != nullptr && chars < text_capacity) {
                        text[chars] = unit;
                    }
                    ++chars;
                }

                if (text != nullptr && chars < text_capacity) {
                    text[chars] = 0;
                }
                ++chars;
            }
        }

        // Stride 5: vtable, slide, text offset, type, FLAGS.
        //
        // The flags word is the one that decides whether an element is drawn, and reading it
        // replaces a host-side reimplementation of the rule with the guest's own answer.
        //
        // `element+0x1C` is a big-endian u16. Bit 0x2000 is `active`, the authored state the
        // .bgf carries and Lua's setAttribute writes; bit 0x1000 is `globalactive`, which the
        // runtime DERIVES every frame in sub_822849A0 as
        //     globalactive = active AND in-time-range AND parent's globalactive
        // and enforces structurally — that walk never enters a subtree whose parent failed.
        //
        // 0x1000 is the one to follow. sub_82188B70 copies exactly it onto the render node:
        //     lwz  r11, 4(r31)    ; node+4 = element
        //     lhz  r10, 0x1C(r11) ; the flags halfword
        //     extrwi r9, r10, 1,19; bit 0x1000
        //     stb  r9, 8(r31)     ; node+8, the draw gate
        // and sub_8229E400 tests node+8 before descending, so a zero prunes the whole subtree.
        //
        // Opacity is NOT a second gate. sub_8229E358 uses it only to choose the transparent
        // bucket — a fully transparent element is still traversed and still submitted, so an
        // element carrying no opacity attribute is not thereby hidden.
        //
        // The TYPE byte at +0x1E carries no live state; it is here so the ORDINAL JOIN between
        // the guest walk and the .bgf can be checked rather than assumed.
        //
        // Every piece of live state we apply — slide, text, opacity — is keyed by position:
        // the guest's depth-first walk order is taken to equal the .bgf's element order. That
        // was verified 77/77 on the title screen and never on the main menu, which has 3812
        // elements. A divergence anywhere would hand every element after it someone else's
        // state, which does not look like a join bug; it looks like a mostly-correct screen
        // with stray widgets on it, and that is exactly what we are looking at.
        //
        // Comparing the guest's type sequence against the file's localises any divergence to
        // an exact index, or proves there is none.
        if (words != nullptr && (found * 5u + 4u) < word_capacity) {
            uint16_t flags = 0;
            std::memcpy(&flags, base + element + 0x1Cu, sizeof(flags));

            words[found * 5u] = read32(element);
            words[found * 5u + 1u] = base[element + 43u];
            words[found * 5u + 2u] = offset;
            words[found * 5u + 3u] = base[element + 30u];
            words[found * 5u + 4u] = __builtin_bswap16(flags);
        }
        ++found;

        // Down first, then along, then climb until there is something along — the root
        // itself is never followed sideways, or the walk would escape into its siblings.
        const uint32_t child = read32(element + 20u);
        if (plausible(child)) {
            element = child;
            continue;
        }

        while (element != 0) {
            if (element == root) {
                element = 0;
                break;
            }

            const uint32_t sibling = read32(element + 16u);
            if (plausible(sibling)) {
                element = sibling;
                break;
            }

            const uint32_t parent = read32(element + 12u);
            element = plausible(parent) ? parent : 0;
        }
    }

    return found;
}

/// The live state of the guest's front end, for Godot to mirror.
///
/// Walks the one global the whole front end hangs off — `0x83496B08`, published by the
/// GUI base ctor at 0x8237132C — down to the screen the guest currently has active:
///
///     GUI+180 startup/title screen   (96 B,  vtable 0x820D09C4)
///     GUI+184 main menu              (232 B, vtable 0x820D0934)
///     GUI+188 THE ACTIVE ONE         (set at 0x8237834C, switched by sub_82379488)
///
/// `screen+24` is the screen's own state int. For the title screen the proven values are
/// 9 legal sequence, 0 idle (this is where "press A" lives), 8 attract video, 10 start
/// sequence playing, 11 hand over to the main menu, 12 dialog. `startup+80` is the flag
/// the "CAN_PRESS_A" presentation event sets at 0x826E2E38 — A is IGNORED until it is 1
/// (`if (a2 == 36 && *(BYTE*)(a1+80))` at 0x826E3078). `menu+72` is the selected index.
///
/// Reading rather than driving, deliberately: the guest owns this state machine. Godot
/// needs to know which screen is up and which item is highlighted so it can draw them.
///
/// Also reports the active screen's SCENE SLIDE, which is what actually decides what the
/// front end looks like. Its Lua navigates with `goToSlide(SCENE, n)` over
/// `{PressStart=1, Title=2, NewGame=3, LoadGame=4, Option=5, Multiplayer=6, Debug=7}`,
/// and `frontendmainmenu.bgf` carries exactly seven slides on its root — one page each.
/// Without it a host draws every page at once.
///
/// The slide lives in the element itself: gameplan GoToSlide (`sub_82BB5760`) stores the
/// index to `element+43` at 0x82BB5860, and the getter (`sub_82BB5990`) reads that same
/// byte back at 0x82BB59AC. The chain to the root element is
/// `screen+20` (CPresentation, vtable 0x82006D48) -> `+492` (element manager, its vtable
/// slot +68 is literally `return *(u32*)(mgr+8)`) -> the root.
///
/// Fills TEN words: gui, active screen, which (0 none / 1 title / 2 menu), state,
/// press-A-armed, cursor, scene slide, slide count, the ACTIVE SCREEN'S VTABLE, and THE
/// GUI OBJECT'S OWN VTABLE.
///
/// The GUI's vtable is not belt and braces — it is the only thing that says whether these
/// fields mean anything. `dword_83496B08` has TWO writers, both through the same base ctor
/// `sub_823710A8` (the store is at 0x8237132C):
///
///   - `sub_82378230`, the front-end GUI, whose derived part holds +180 title,
///     +184 menu, +188 active;
///   - `Game_InitDatabases` (0x82310FF0), which builds a BASE-ONLY instance. The base ctor
///     initialises to +172 and no further, so +180/+184/+188 on that object are not screen
///     pointers at all.
///
/// Reading the second as though it were the first reports "the front end went nowhere" for a
/// game that has started, so the caller must check the vtable before believing `which`.
///
/// Returns 1 when the GUI exists.
REFII_HOST_ABI_EXPORT int RefiiHostGetFrontEnd(uint32_t* out) {
    if (out == nullptr) {
        return 0;
    }

    for (int i = 0; i < 10; ++i) {
        out[i] = 0;
    }

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    // The guest is big-endian; every pointer and int below needs the swap.
    auto read32 = [base](uint32_t address) -> uint32_t {
        uint32_t value = 0;
        std::memcpy(&value, base + address, sizeof(value));
        return __builtin_bswap32(value);
    };

    // A guest pointer, not an offset into anything of ours — sanity-check it before
    // chasing it, or a half-built GUI walks us into unmapped memory.
    auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    const uint32_t gui = read32(0x83496B08u);
    if (!plausible(gui)) {
        return 0;
    }

    out[0] = gui;
    out[9] = read32(gui);  // the GUI object's vtable — which of the two kinds this is

    const uint32_t startup = read32(gui + 180u);
    const uint32_t menu = read32(gui + 184u);
    const uint32_t active = read32(gui + 188u);
    if (!plausible(active)) {
        return 1;
    }

    out[1] = active;
    out[2] = active == startup ? 1u : (active == menu ? 2u : 0u);
    out[3] = read32(active + 24u);
    out[8] = read32(active);  // the screen's vtable — its identity

    if (plausible(startup)) {
        out[4] = base[startup + 80u];
    }
    if (plausible(menu)) {
        out[5] = read32(menu + 72u);
    }

    // The scene slide, down the presentation's own element tree.
    const uint32_t presentation = read32(active + 20u);
    if (plausible(presentation)) {
        const uint32_t root = read32(presentation + 492u + 8u);
        if (plausible(root)) {
            out[6] = base[root + 43u];  // current slide
            out[7] = base[root + 42u];  // how many it has, for a sanity check
        }
    }

    return 1;
}

/// The LIVE attribute values of every element on the active screen.
///
/// This is what a .bgf alone cannot tell you. The shipped file holds the authored
/// attributes and the slides that override them, but the screen's Lua then moves and
/// hides things at runtime: Page.Initialize does `setAttribute(DimensionsImage,
/// "opacity", 0)`, Page.ArrangeItemsVertically writes "position.y" per item, DisplayBox
/// writes "textstring". A host that reads only the file draws all of that where the file
/// put it — which is a dialog panel sitting over the main menu that the game has hidden.
///
/// Layout verified directly, not taken second-hand (sub_822848E0, and sub_82284948 for
/// the value offset):
///
///   base  = (*(u16*)(elem + 28) & 1) ? elem + 68 : elem + 32
///   count = *(u8*)(elem + 31)
///   record = 8 bytes; id = *(u32*)(rec + 0) >> 5; VALUE = *(u32*)(rec + 4)
///
/// `ids` are sdbm hashes of the attribute names (sub_82189140), which is exactly what
/// Engine_GameHash.Sdbm computes — the caller passes them so the hash lives in one place.
///
/// Emits TWO words per (element, id) in walk order: the raw value and 1/0 for present.
/// The value is raw bits; a float attribute is a big-endian float, already swapped here.
/// Returns the number of ELEMENTS walked.
/// Reads named attributes off every element of one presentation.
///
/// <paramref name="requested_presentation"/> is a `CPresentation*` from
/// <see cref="RefiiHostGetPresentations"/>, or 0 for the front end's active screen. Same
/// parameterisation as RefiiHostGetUiElements and for the same reason: the front-end chain
/// this used to hardcode cannot reach the HUD, and `opacity` is exactly the attribute the
/// in-game UI needs — the game hides the whole HUD by writing it to 0 on one group.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetUiAttributes(uint32_t requested_presentation,
                                  const uint32_t* ids, uint32_t id_count,
                                  uint32_t* out, uint32_t out_capacity) {
    if (ids == nullptr || id_count == 0) {
        return 0;
    }

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    auto read32 = [base](uint32_t address) -> uint32_t {
        uint32_t value = 0;
        std::memcpy(&value, base + address, sizeof(value));
        return __builtin_bswap32(value);
    };

    auto plausible = [](uint32_t pointer) {
        return pointer >= 0x10000u && pointer < 0xC0000000u;
    };

    uint32_t presentation = requested_presentation;
    if (presentation == 0u) {
        const uint32_t gui = read32(0x83496B08u);
        if (!plausible(gui)) {
            return 0;
        }

        const uint32_t active = read32(gui + 188u);
        if (!plausible(active)) {
            return 0;
        }

        presentation = read32(active + 20u);
    }

    if (!plausible(presentation)) {
        return 0;
    }

    const uint32_t root = read32(presentation + 492u + 8u);
    if (!plausible(root)) {
        return 0;
    }

    // One attribute of one element, or "absent".
    auto find = [&](uint32_t element, uint32_t id, uint32_t* value) -> bool {
        const uint16_t flags = static_cast<uint16_t>(read32(element + 28u) >> 16);
        const uint32_t records = (flags & 1u) != 0 ? element + 68u : element + 32u;
        const uint32_t count = base[element + 31u];

        for (uint32_t i = 0; i < count; ++i) {
            const uint32_t record = records + i * 8u;
            if ((read32(record) >> 5) == id) {
                *value = read32(record + 4u);
                return true;
            }
        }

        return false;
    };

    constexpr uint32_t kMaxElements = 16384u;

    uint32_t found = 0;
    uint32_t element = root;

    while (element != 0 && found < kMaxElements) {
        if (!plausible(element)) {
            break;
        }

        for (uint32_t i = 0; i < id_count; ++i) {
            const uint32_t slot = (found * id_count + i) * 2u;
            uint32_t value = 0;
            const bool present = find(element, ids[i], &value);
            if (out != nullptr && (slot + 1u) < out_capacity) {
                out[slot] = value;
                out[slot + 1u] = present ? 1u : 0u;
            }
        }

        ++found;

        const uint32_t child = read32(element + 20u);
        if (plausible(child)) {
            element = child;
            continue;
        }

        while (element != 0) {
            if (element == root) {
                element = 0;
                break;
            }

            const uint32_t sibling = read32(element + 16u);
            if (plausible(sibling)) {
                element = sibling;
                break;
            }

            const uint32_t parent = read32(element + 12u);
            element = plausible(parent) ? parent : 0;
        }
    }

    return found;
}

/// The guest's camera, for Godot to render through.
///
/// Reads the snapshot at 0x8331A230. Layout is IDA-proven, and two details are not what a
/// reasonable guess would give:
///
///   +0x00  position   16-byte lanes, NOT 12 — three separate stvx128 at 0x8221161C,
///   +0x10  up         0x82211628 and 0x82211630. A packed-12 reader is wrong from the
///   +0x20  forward    second vector on. Only xyz is ever read by the guest (vmsum3fp128).
///   +0x30  HORIZONTAL field of view, radians (the "vertical" reading is wrong —
///          sub_82293820 derives vfov from it at 0x822938A8)
///   +0x34  a boolean whose meaning is UNPROVEN
///
/// World basis is right-handed with X right, Y FORWARD, Z up — proven from sub_821B0068
/// building [cross(fwd,up), fwd, up] as a proper rotation, and from the defaults
/// (fwd = +Y, up = +Z).
///
/// It is a PULL CACHE: sub_82211598 rewrites it only when the sim generation counter
/// dword_83319650 has moved. Whether any of its callers still run headless was the open
/// question; it is now SETTLED and the answer is yes. All ten are Lua natives
/// ("IsPositionInCameraFrustrum", "IsSphereInCameraFrustrum",
/// "IsEntityInCameraFrustrumWithMultiplier"), Kynapse path-object distance gates, effect
/// ticks and animation-LOD classifiers. None contains a device call, so none of them needs
/// the render path. dword_83319650 is a SIMULATION tick counter — incremented at the tail
/// of sub_821D4628 and sub_823D9898, both world updates — not a present counter.
///
/// It is still a copy of a copy. sub_82211598 reads through sub_821DD3B0 -> sub_82265F90,
/// and that one's own memo at cameraManager+0xD0 is stamped with KeTickCount, so the whole
/// chain refreshes at most once a millisecond. The camera manager's PUSHED block is the
/// better source and is laid out the same way, measured from the CAMERA MANAGER base:
/// +0xE0 position, +0xF0 up, +0x100 forward, +0x110 fov, +0x114 bool. sub_821B8FD8 writes
/// it — stvx128 at 0x821B917C for the position, sub_822BDD10 for up and forward, stfs/stb
/// at 0x821B91DC/0x821B91C4 — and sub_8233B6B0 drives it with force=1 once per sim tick.
/// Resolving the manager needs no guest call: Game = *(0x83496AB8+12), binary-search the
/// 12-byte entries between *(Game+0x9C)+8 and +12 for key 1 (key 2 when *(Game+0x8C)+0x34
/// and +0x36 are both set), take *(entry+4), then *(view+4).
///
/// Fills 8 floats: pos xyz, up xyz... actually 11: pos[3], up[3], fwd[3], hfov, flag.
/// Returns 1 on a coherent read, 0 if the two reads disagreed or the guest is absent.
REFII_HOST_ABI_EXPORT int RefiiHostGetCamera(float* out) {
    if (out == nullptr) {
        return 0;
    }

    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    constexpr uint32_t kCamera = 0x8331A230u;

    // Five unsynchronised stores with no barrier and no sequence counter, and the
    // "cached" memo is published BEFORE the payload (0x82211600, ahead of the vectors at
    // 0x8221161C onward) — so watching that memo for freshness is the worst available
    // signal. Reading twice and requiring agreement is the cheapest correct thing: the
    // window is nine instructions wide, so a retry almost never happens.
    auto snapshot = [&](uint8_t* into) {
        std::memcpy(into, base + kCamera, 0x38u);
    };

    uint8_t first[0x38];
    uint8_t second[0x38];
    bool coherent = false;
    for (int attempt = 0; attempt < 4 && !coherent; ++attempt) {
        snapshot(first);
        snapshot(second);
        coherent = std::memcmp(first, second, sizeof(first)) == 0;
    }

    if (!coherent) {
        return 0;
    }

    auto read_float = [&](uint32_t offset) {
        uint32_t bits = 0;
        std::memcpy(&bits, first + offset, sizeof(bits));
        bits = __builtin_bswap32(bits);
        float value = 0.0f;
        std::memcpy(&value, &bits, sizeof(value));
        return value;
    };

    for (int lane = 0; lane < 3; ++lane) {
        out[lane] = read_float(0x00u + lane * 4u);       // position
        out[3 + lane] = read_float(0x10u + lane * 4u);   // up
        out[6 + lane] = read_float(0x20u + lane * 4u);   // forward
    }

    out[9] = read_float(0x30u);          // horizontal FOV, radians
    out[10] = first[0x34u] != 0 ? 1.0f : 0.0f;
    return 1;
}

/// Whether the guest still wants the movie the host is showing.
///
/// 0 none, 1 still wanted, 2 the guest has ended it — by finishing its own decode or by
/// skipping, which is what a button press at the title does. The host stops on 2 rather
/// than playing to its own end, or it keeps a finished movie over whatever the guest has
/// moved on to.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostMovieState(void) {
    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    return refii::vfs::BinkMovieState(memory->virtual_membase());
}

/// Tells the guest the movie the host was showing has finished.
///
/// The guest's startup sequence blocks until every movie has played, and decides that
/// from its OWN Bink decode — work whose output nobody sees now that Godot draws the
/// picture, running on a clock that drifts from the one the player is watching. This
/// writes the same done byte the guest writes itself, so the sequence advances when the
/// movie visibly ends rather than tens of seconds later.
///
/// Returns the player address retired, or 0 when no movie was live.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostFinishMovie(void) {
    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    return refii::vfs::FinishCurrentBinkMovie(memory->virtual_membase());
}

/// The rumble the guest last asked for, 0..65535 per motor. Returns 1 on success.
REFII_HOST_ABI_EXPORT int RefiiHostGetVibration(uint32_t user_index, uint32_t* out_left, uint32_t* out_right) {
    uint16_t left = 0;
    uint16_t right = 0;
    if (!refii::host::TakeVibration(user_index, &left, &right)) {
        return 0;
    }

    if (out_left != nullptr) {
        *out_left = left;
    }
    if (out_right != nullptr) {
        *out_right = right;
    }
    return 1;
}

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
    // The renderer backend is NOT set here. `--renderer_backend=plume` on the command
    // line does not survive: SetupEnvironment loads refii.toml afterwards and the file's
    // value wins, which is why the guest still came up on the Xenia-derived graphics
    // system. Suppression is handled in SetupPresentation off the embedded flag instead,
    // where nothing can override it later.
    SetEnvironmentValue("REX_EMBED_HEADLESS", "1");

    // No guest GPU at all.
    //
    // RefiiApp::OnPreSetup only reaches `config.graphics.reset()` when renderer_backend
    // is exactly "plume". Anything else — including EMPTY — falls through, and on the
    // netplay SDK that branch then defaults gpu_plugin to "xenos" and tries to dlopen
    // librexgpu-xenosd.so from beside the executable. Embedded, "beside the executable"
    // is Godot's directory, so it fails there and takes the boot with it.
    //
    // Set through the environment, not the command line: cvar::Init parses argv first
    // but SetupEnvironment loads refii.toml afterwards and the file wins. The env is
    // applied by ApplyEnvironment (REX_<CVAR_NAME>) and, embedded, no refii.toml is
    // found beside the host binary to override it.
    SetEnvironmentValue("REX_RENDERER_BACKEND", "plume");

    // Pass the game directory as an explicit flag, not just positionally.
    //
    // Embedded, GetExecutableFolder() is the HOST's directory — Godot's — so the guest
    // looks for refii.toml beside the Godot binary, finds none, and falls back to a
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
        ApplyEmbeddedEnvironment();

        // argv[0] then the flags. The game directory goes as an explicit flag, not
        // positionally: embedded, GetExecutableFolder() is the HOST's directory, so the
        // guest finds no refii.toml of its own and would fall back to a relative default.
        std::vector<std::string> owned;
        owned.emplace_back("refii");
        for (const std::string& argument : SplitArguments(arguments)) {
            owned.push_back(argument);
        }
        owned.push_back("--game_data_root=" + directory.string());

        std::vector<char*> argv;
        argv.reserve(owned.size());
        for (std::string& argument : owned) {
            argv.push_back(argument.data());
        }

        rex::cvar::Init(static_cast<int>(argv.size()), argv.data());
        rex::cvar::ApplyEnvironment();

        hosted.context = std::make_unique<rex::ui::SDLWindowedAppContext>();
        if (!hosted.context->Initialize()) {
            hosted.status = "SDL context init failed";
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

        // RE-ASSERT THE RENDER-FENCE HOLD. The world's own Update stamps `engine+176` with the
        // real hand-off time whenever it drains the pending `.engine_level`, so a hold written
        // once at the start of a build is wiped the first time that runs and the guest walks
        // straight into a world Godot has not finished. Costs one pointer walk per step, and
        // only while a build is actually outstanding.
        if (g_world_build_pending.load(std::memory_order_relaxed)) {
            if (auto* memory = rex::system::kernel_memory()) {
                if (uint8_t* base = memory->virtual_membase()) {
                    apply_world_build_hold(base);
                }
            }
        }

        hosted.context->ExecutePendingFunctionsFromUIThread();

        // Cheap until it succeeds, then a single predictable branch.
        ArmGuestNoGpuMode();
        KeepGuestIdleCounterClear();
        // Runs on Godot's thread, which is the point: the guest thread that needs this is
        // the one spinning, so it cannot pump its own ring.
        RetireGuestSceneStreams();

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

                std::this_thread::sleep_for(std::chrono::milliseconds(1));
            }
        }

        if (hosted.app) {
            hosted.app->InvokeOnDestroy();
        }

        if (stopped) {
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
