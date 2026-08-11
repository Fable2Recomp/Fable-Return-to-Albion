#include "host_audio.h"

#include <bit>
#include <cstring>
#include <vector>

#include <rex/logging.h>

#include "core/audio_state.h"

namespace refii::host {

// X_STATUS_SUCCESS and friends are macros that cast to an unqualified X_STATUS, so the
// name has to be visible where they are used — the same reason refii_app.h opens the
// namespace inside its input factory.
using namespace rex;

namespace {

/// How long the guest's audio thread will wait for Godot to take a frame before giving
/// up on it.
///
/// Generous on purpose. Any real Godot hitch — a level load, a shader compile — is well
/// under this, and during those we WANT the guest to wait, because waiting is what keeps
/// the simulation and the audio in step. Exceeding it means the consumer is absent
/// rather than late: no GameAudio node in the scene, or a shell that has gone away. In
/// that case the frame is dropped and the guest credited anyway, because a wedged audio
/// worker stalls NLHAudio at its packet cap and takes the game down with it.
constexpr uint32_t kConsumerTimeoutMs = 250;

/// How often the driver reports its own health, in submitted frames. 1875 frames is 10
/// seconds at the guest's 187.5 Hz.
///
/// This exists because Godot's stdout is not a reliable record. Four runs in a row were
/// truncated before their in-game section, so the level-load window — the one case worth
/// measuring — was never visible. The guest log is a file and survives, so the numbers go
/// there too rather than only to a console that scrolls away.
constexpr uint64_t kHealthReportFrames = 1875;

/// One big-endian float out of the guest's frame.
float SwapGuestFloat(const float* planar, size_t index) {
    uint32_t bits = 0;
    std::memcpy(&bits, planar + index, sizeof(bits));
    bits = __builtin_bswap32(bits);
    return std::bit_cast<float>(bits);
}

/// Folds the guest's planar big-endian 5.1 frame down to interleaved stereo.
///
/// Written here rather than calling rex::audio::conversion because that header's fast path
/// is SSSE3 and refii_host is not built with that target feature. Widening the ISA baseline
/// of a whole library for one 256-sample loop is the wrong trade — this runs 187 times a
/// second over 1536 floats and does not need vectorising.
///
/// The channel order is XAudio's documented 5.1 layout: front left, front right, centre,
/// LFE, back left, back right. Centre is split evenly across both sides, LFE is discarded,
/// and the sum is scaled by 1/2.5 so a fully-loaded matrix cannot clip. Note that the SDK's
/// own scalar fallback reads back-left and back-right the other way round from its SSE
/// path; this follows the SSE path, which matches the documented order.
void FoldGuestFrameToStereo(const float* planar, uint32_t samples_per_channel, float* out) {
    for (uint32_t i = 0; i < samples_per_channel; i++) {
        const float fl = SwapGuestFloat(planar, (0 * samples_per_channel) + i);
        const float fr = SwapGuestFloat(planar, (1 * samples_per_channel) + i);
        const float fc = SwapGuestFloat(planar, (2 * samples_per_channel) + i);
        const float bl = SwapGuestFloat(planar, (4 * samples_per_channel) + i);
        const float br = SwapGuestFloat(planar, (5 * samples_per_channel) + i);

        const float centre_halved = fc * 0.5f;
        out[(i * 2) + 0] = (fl + bl + centre_halved) * (1.0f / 2.5f);
        out[(i * 2) + 1] = (fr + br + centre_halved) * (1.0f / 2.5f);
    }
}

}  // namespace

HostAudioDriver::HostAudioDriver(rex::memory::Memory* memory,
                                 rex::thread::Semaphore* semaphore)
    : rex::audio::AudioDriver(memory), semaphore_(semaphore) {}

HostAudioDriver::~HostAudioDriver() {
    Shutdown();
}

bool HostAudioDriver::Initialize() {
    owns_sink_ = refii::vfs::SetHostAudioCreditSink(&HostAudioDriver::CreditFrames, this);
    if (!owns_sink_) {
        // Still a usable driver — it just has nowhere to play. Returning false here would
        // fail the client's registration outright, which is a worse outcome than a second
        // client running silently.
        REXLOG_WARN("refii audio: host sink already owned; this client will run silently");
        return true;
    }

    refii::vfs::SetHostAudioDriverInstalled(true);
    REXLOG_INFO(
        "refii audio: host driver installed; guest output is {} Hz {} ch, {} samples per "
        "frame, folded to stereo for Godot",
        refii::vfs::kGuestAudioSampleRate, refii::vfs::kGuestAudioChannels,
        refii::vfs::kGuestAudioSamplesPerFrame);
    return true;
}

void HostAudioDriver::Shutdown() {
    if (!owns_sink_) {
        return;
    }
    owns_sink_ = false;
    refii::vfs::SetHostAudioCreditSink(nullptr, nullptr);
    refii::vfs::SetHostAudioDriverInstalled(false);
}

void HostAudioDriver::CreditFrames(void* context, uint32_t frames) {
    auto* driver = static_cast<HostAudioDriver*>(context);
    if (driver == nullptr || driver->semaphore_ == nullptr || frames == 0) {
        return;
    }

    // One credit per frame consumed, exactly as the SDL driver does from its playback
    // thread. This is the guest's audio clock; releasing more than was taken lets the
    // guest run ahead of what is audible.
    driver->semaphore_->Release(static_cast<int>(frames), nullptr);
}

void HostAudioDriver::SubmitFrame(uint32_t frame_ptr) {
    const auto* input_frame = memory_->TranslateVirtual<float*>(frame_ptr);
    if (input_frame == nullptr) {
        return;
    }

    if (!owns_sink_) {
        // Another client holds the ring. Credit immediately so this one keeps running
        // rather than stalling behind a queue it can never drain.
        CreditFrames(this, 1);
        return;
    }

    // Thread-local so the guest's audio worker reuses one buffer instead of allocating
    // 187 times a second, and so a future second render-driver client cannot share it.
    static thread_local std::vector<float> stereo(refii::vfs::kHostAudioFloatsPerFrame);

    // The guest hands us PLANAR BIG-ENDIAN float32 — 256 samples per channel, 1024-byte
    // channel stride. This both deinterleaves and byte-swaps.
    FoldGuestFrameToStereo(input_frame, refii::vfs::kGuestAudioSamplesPerFrame,
                           stereo.data());

    // ALWAYS PUSH, even when the host is playing individual voices instead of this mix.
    //
    // There used to be a short-circuit here that credited immediately and returned when
    // per-voice playback owned the output. It looked reasonable — why queue a frame nobody
    // will play? — and it silently deleted the guest's audio clock, because
    // PushHostAudioFrame is the ONLY blocking call in this path. Credited on submit with
    // nothing to wait for, the guest's audio worker free-ran at CPU speed, produced PCM far
    // faster than real time, and overflowed every per-voice ring downstream.
    //
    // Silencing the mix is the HOST's job: it keeps consuming at 48 kHz and pushes zeros
    // instead. The clock is then identical in both modes, which is the point — Stage 2
    // should change what reaches the speakers, not how time passes.
    if (!refii::vfs::PushHostAudioFrame(stereo.data(), kConsumerTimeoutMs)) {
        // Dropped rather than queued. Credit anyway so the guest keeps running; the ring
        // has already logged why.
        CreditFrames(this, 1);
    }

    ReportHealth();
}

void HostAudioDriver::ReportHealth() {
    if (++frames_submitted_ % kHealthReportFrames != 0) {
        return;
    }

    uint64_t pushed = 0;
    uint64_t popped = 0;
    uint64_t dropped = 0;
    refii::vfs::HostAudioCounters(&pushed, &popped, &dropped);

    // The submit count IS the elapsed time here — the guest only gets to submit when the
    // host consumes, so 1875 frames is 10 seconds of real playback by definition. That is
    // what makes this a useful clock check rather than just a counter dump.
    REXLOG_INFO(
        "refii audio: {} frames submitted, {} queued by host, {} played, {} dropped, "
        "{} waiting",
        frames_submitted_, pushed, popped, dropped, refii::vfs::HostAudioQueuedFrames());

    if (refii::vfs::VoiceCaptureActive()) {
        uint32_t live = 0;
        uint64_t held = 0;
        uint64_t taken = 0;
        uint64_t snapshots = 0;
        refii::vfs::VoiceCaptureStats(&live, &held, &taken, &snapshots);

        // `held` climbing while `taken` stays at zero means the host is snapshotting the
        // voices but never draining them — which is a different fault from the host not
        // seeing the voices at all, and the two are indistinguishable without both numbers.
        REXLOG_INFO(
            "refii audio: voices {} live, {} bytes held, {} bytes taken by host, "
            "{} snapshots",
            live, held, taken, snapshots);
    }
}

HostAudioSystem::HostAudioSystem(rex::runtime::FunctionDispatcher* function_dispatcher)
    : rex::audio::AudioSystem(function_dispatcher) {}

HostAudioSystem::~HostAudioSystem() = default;

std::unique_ptr<rex::audio::AudioSystem> HostAudioSystem::Create(
    rex::runtime::FunctionDispatcher* function_dispatcher) {
    return std::make_unique<HostAudioSystem>(function_dispatcher);
}

rex::X_STATUS HostAudioSystem::CreateDriver(size_t index, rex::thread::Semaphore* semaphore,
                                            rex::audio::AudioDriver** out_driver) {
    if (out_driver == nullptr) {
        return X_STATUS_INVALID_PARAMETER;
    }

    auto* driver = new HostAudioDriver(memory_, semaphore);
    if (!driver->Initialize()) {
        driver->Shutdown();
        delete driver;
        return X_STATUS_UNSUCCESSFUL;
    }

    REXLOG_INFO("refii audio: render driver client {} bound to the host sink", index);
    *out_driver = driver;
    return X_STATUS_SUCCESS;
}

void HostAudioSystem::DestroyDriver(rex::audio::AudioDriver* driver) {
    auto* host_driver = dynamic_cast<HostAudioDriver*>(driver);
    if (host_driver == nullptr) {
        return;
    }
    host_driver->Shutdown();
    delete host_driver;
}

}  // namespace refii::host
