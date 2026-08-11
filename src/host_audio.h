// refii - host-owned audio output
//
// Godot owns audio, exactly as it owns the window, input and the world. This is the seam
// the guest plays through: its mixer submits a finished 5.1 frame, we fold it and hand it
// to Godot, and Godot's consumption of that frame is what lets the guest mix the next one.
//
// That last clause is the whole design. The guest's audio clock is not a timer — it is
// the output device's consumption rate. One semaphore credit per frame consumed wakes
// AudioSystem::WorkerThreadMain, which re-enters the guest's own render-driver callback,
// which mixes and submits again. Under SDL that credit came from SDL's playback thread.
// Here it comes from Godot, which is why Godot ends up owning the guest's audio timeline
// and not merely its speakers.
//
// It exists because ReXApp::SetupPresentation installs the SDL audio system BEFORE the
// headless branch, so an embedded guest opened a real host audio device out from under
// Godot and played to it on its own clock.

#pragma once

#include <memory>

#include <rex/audio/audio_driver.h>
#include <rex/audio/audio_system.h>
#include <rex/system/interfaces/audio.h>
#include <rex/thread.h>

namespace refii::host {

/// Takes the guest's mixed frames and queues them for Godot.
///
/// Deliberately thin: the queue, the pacing and the counters live in refii_vfs so that
/// the host bridge — a different library — reads the same state this writes.
class HostAudioDriver final : public rex::audio::AudioDriver {
public:
    HostAudioDriver(rex::memory::Memory* memory, rex::thread::Semaphore* semaphore);
    ~HostAudioDriver() override;

    bool Initialize();
    void Shutdown();

    void SubmitFrame(uint32_t samples_ptr) override;

private:
    /// Credits the guest for frames Godot has taken. Installed into refii_vfs as a plain
    /// function pointer so the state library needs no knowledge of this type.
    static void CreditFrames(void* context, uint32_t frames);

    /// Periodically writes the frame counters to the GUEST LOG, which is a file and
    /// therefore survives; Godot's stdout has been truncated before the in-game section on
    /// every run so far, taking the only numbers worth reading with it.
    void ReportHealth();

    uint64_t frames_submitted_ = 0;

    rex::thread::Semaphore* semaphore_ = nullptr;

    /// Whether THIS driver owns the ring and the clock. The guest registers one
    /// render-driver client, but if a second ever appeared it would find the sink taken;
    /// it then credits its own frames immediately and plays nothing, so it runs silently
    /// rather than stalling behind a ring it can never drain.
    bool owns_sink_ = false;
};

/// The audio system Godot installs in place of the SDK's SDL one.
class HostAudioSystem final : public rex::audio::AudioSystem {
public:
    explicit HostAudioSystem(rex::runtime::FunctionDispatcher* function_dispatcher);
    ~HostAudioSystem() override;

    static std::unique_ptr<rex::audio::AudioSystem> Create(
        rex::runtime::FunctionDispatcher* function_dispatcher);

    rex::X_STATUS CreateDriver(size_t index, rex::thread::Semaphore* semaphore,
                               rex::audio::AudioDriver** out_driver) override;
    void DestroyDriver(rex::audio::AudioDriver* driver) override;
};

}  // namespace refii::host
