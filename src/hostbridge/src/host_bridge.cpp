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
#include <cstddef>
#include <cstdint>
#include <cstdio>
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

namespace refii::vfs {
// Defined in src/vfs/src/core/bink_frame_state.cpp.
uint32_t BinkFrameBuffersGuestAddress();
void RequestedBinkVideo(uint32_t* out_address, uint32_t* out_size,
                        uint32_t* out_generation, uint32_t* out_swapped);
uint32_t RequestedBinkVideoName(char* out, uint32_t capacity);
uint32_t FinishCurrentBinkMovie(uint8_t* base);
uint32_t BinkMovieState(uint8_t* base);
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

/// Applies the switches that make the guest a simulation server rather than an app.
/// Shared by both entry points so they cannot drift.
void ApplyEmbeddedEnvironment() {
    // No window, no overlays, no presentation.
    ::setenv("REX_EMBED_HEADLESS", "1", 1);
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
int RefiiHostBridgeAbiVersion(void) { return 23; }

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
void RefiiHostSetControllerState(uint32_t user_index, int connected, uint32_t buttons,
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
int RefiiHostGetVideoFrame(uint8_t* out_rgba, uint32_t capacity, uint32_t* out_width,
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
    // live and how big it is. Returning 0 here made "no video playing" and "your buffer
    // is too small" indistinguishable, which is how the garbage dimensions above got
    // used in the first place.
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
int RefiiHostGetVideoPlanes(uint8_t* out_y, uint32_t y_capacity, uint8_t* out_cr,
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
int RefiiHostGetRequestedVideo(unsigned char* out, uint32_t capacity, uint32_t* out_size,
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
uint32_t RefiiHostGetRequestedVideoName(char* out, uint32_t capacity,
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
uint32_t RefiiHostIsInGame(void) {
    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    uint8_t* base = memory->virtual_membase();
    return base == nullptr ? 0u : base[0x83496A5Eu];
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
/// `return *(*(a1 + 12) + 104);`. Fills manager, submitted, retired, busy. Returns 1 when
/// the chain resolved.
int RefiiHostGetRenderFence(uint32_t* out_manager, double* out_submitted,
                            double* out_retired, uint32_t* out_busy) {
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
int RefiiHostGetLoadingScreen(uint32_t* out_manager, uint32_t* out_screen, float* out_fade,
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
void RefiiHostGetInitMarkers(uint32_t* out) {
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

/// The command-stream rings, read-only. `sub_8222ABE0` blocks when the reader traps the
/// writer, and these four numbers per slot are the whole condition.
///
/// Fills 4 slots x 6 words: object, capacity, write cursor (+32), write consumed (+36),
/// reader consumed (+64), last read (+24). A zero object means the slot is empty.
void RefiiHostGetSceneStreams(uint32_t* out) {
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

/// Where the guest is going. `sub_82379628` — the sole new-game entry — writes these before
/// it latches the mode change:
///
///     g_StartWorldName    = "Albion"                 0x83497584
///     g_StartLevelName    = "BWSSlums"               0x83497588
///     g_StartEntranceName = "QC010_ChildhoodStart"   0x83497590
///
/// Each global holds a Rep pointer; characters are read best-effort as ASCII then UTF-16BE.
///
/// Fills three NUL-terminated buffers. Returns 1 when the level name came out non-empty —
/// that is the one a caller cannot proceed without.
int RefiiHostGetStartLevel(char* world, char* level, char* entrance, uint32_t capacity) {
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

    uint8_t* base = memory->virtual_membase();
    if (base == nullptr) {
        return 0;
    }

    const auto read32 = [base](uint32_t at) {
        uint32_t value = 0;
        std::memcpy(&value, base + at, sizeof(value));
        return __builtin_bswap32(value);
    };

    // ASCII first, then UTF-16BE. A name here is short, printable and non-empty; anything
    // else means we read the wrong encoding and should say nothing rather than something.
    const auto read_name = [&](uint32_t global, char* out) {
        if (out == nullptr) {
            return false;
        }

        // The global holds a REP POINTER, not characters — `sub_821F0240` copies `*a2` and
        // then bumps a refcount at `Rep+12` (0x821F02A4 / 0x821F02B4). Reading the Rep as
        // if it were the text is what made this return empty and skip the world build
        // silently.
        const uint32_t rep = read32(global);
        if (rep < 0x10000u || rep >= 0xC0000000u) {
            return false;
        }

        const auto printable = [](char c) { return c >= 0x20 && c <= 0x7E; };

        // {data, length, capacity, refcount} leaves two plausible homes for the characters:
        // inline after the 16-byte header, or behind the pointer at +0. Try both rather
        // than pick — a wrong guess here reads plausible rubbish instead of failing.
        const uint32_t inline_chars = rep + 16u;
        const uint32_t indirect = read32(rep);
        const uint32_t candidates[] = {inline_chars, indirect, rep};

        for (uint32_t pointer : candidates) {
        if (pointer < 0x10000u || pointer >= 0xC0000000u) {
            continue;
        }

        for (int wide = 0; wide < 2; ++wide) {
            uint32_t written = 0;
            for (uint32_t i = 0; i + 1 < capacity; ++i) {
                const char c = wide != 0
                    ? static_cast<char>(base[pointer + i * 2u + 1u])
                    : static_cast<char>(base[pointer + i]);
                if (c == '\0') {
                    break;
                }
                if (!printable(c)) {
                    written = 0;
                    break;
                }
                out[written++] = c;
            }

            if (written != 0) {
                out[written] = '\0';
                return true;
            }
        }
        }

        out[0] = '\0';
        return false;
    };

    read_name(0x83497584u, world);
    read_name(0x83497590u, entrance);
    return read_name(0x83497588u, level) ? 1 : 0;
}

uint32_t RefiiHostGetFrontEndState(void) {
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
uint32_t RefiiHostGetUiElements(uint32_t* words, uint32_t word_capacity,
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

    const uint32_t gui = read32(0x83496B08u);
    if (!plausible(gui)) {
        return 0;
    }

    const uint32_t active = read32(gui + 188u);
    if (!plausible(active)) {
        return 0;
    }

    const uint32_t presentation = read32(active + 20u);
    if (!plausible(presentation)) {
        return 0;
    }

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

        if (words != nullptr && (found * 3u + 2u) < word_capacity) {
            words[found * 3u] = read32(element);
            words[found * 3u + 1u] = base[element + 43u];
            words[found * 3u + 2u] = offset;
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
/// Reading the second one as though it were the first is how a run reports "the front end
/// went nowhere" when what actually happened is that the game started. So the caller must
/// check the vtable before believing `which`.
///
/// Returns 1 when the GUI exists.
int RefiiHostGetFrontEnd(uint32_t* out) {
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
uint32_t RefiiHostGetUiAttributes(const uint32_t* ids, uint32_t id_count,
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

    const uint32_t gui = read32(0x83496B08u);
    if (!plausible(gui)) {
        return 0;
    }

    const uint32_t active = read32(gui + 188u);
    if (!plausible(active)) {
        return 0;
    }

    const uint32_t presentation = read32(active + 20u);
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
/// CAVEAT, and the reason this logs before it drives anything: the structure is a PULL
/// CACHE. sub_82211598 refreshes it only when guest code calls it and a generation counter
/// has moved, and every caller is a culling or visibility query. Whether any of them still
/// runs when the guest never draws is UNPROVEN — if none do, this freezes at its last
/// value silently. The push-driven copy on the camera manager at +0xE0 is the fallback.
///
/// Fills 8 floats: pos xyz, up xyz... actually 11: pos[3], up[3], fwd[3], hfov, flag.
/// Returns 1 on a coherent read, 0 if the two reads disagreed or the guest is absent.
int RefiiHostGetCamera(float* out) {
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
uint32_t RefiiHostMovieState(void) {
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
uint32_t RefiiHostFinishMovie(void) {
    auto* memory = rex::system::kernel_memory();
    if (memory == nullptr) {
        return 0;
    }

    return refii::vfs::FinishCurrentBinkMovie(memory->virtual_membase());
}

/// The rumble the guest last asked for, 0..65535 per motor. Returns 1 on success.
int RefiiHostGetVibration(uint32_t user_index, uint32_t* out_left, uint32_t* out_right) {
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
int RefiiHostProbeGuestMemory(char* out, std::size_t capacity) {
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
int RefiiHostStart(const char* game_directory, const char* arguments,
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
    ::setenv("REX_EMBED_HEADLESS", "1", 1);

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
    ::setenv("REX_RENDERER_BACKEND", "plume", 1);

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
int RefiiHostIsRunning(void) { return Session().running.load() ? 1 : 0; }

/// The guest's exit code, or -1 while it is still running or was never started.
int RefiiHostExitCode(void) { return Session().exit_code.load(); }

/// A human-readable status line. Same buffer convention as the probe.
int RefiiHostStatus(char* out, std::size_t capacity) {
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
int RefiiHostInitialize(const char* game_directory, const char* arguments,
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
int RefiiHostStep(void) {
    HostedGuest& hosted = Hosted();
    if (!hosted.initialized || hosted.finished) {
        return 0;
    }

    try {
        SDL_Event event;
        while (SDL_PollEvent(&event)) {
            // Deliberately dropped: with no window there is nothing to route them to.
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
int RefiiHostHostedStatus(char* out, std::size_t capacity) {
    return WriteOut(Hosted().status, out, capacity);
}

/// Tears the hosted guest down. Safe to call more than once.
///
/// Without this the guest's threads are still live when the host process exits, which
/// is what produced the abort on teardown.
int RefiiHostShutdown(char* out, std::size_t capacity) {
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

int RefiiHostJoin(char* out, std::size_t capacity) {
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
