// refii — audio state shared by the guest side and the host bridge.
//
// See include/core/audio_state.h for why this lives in librefii_vfs and not beside
// either of its writers.

#include "core/audio_state.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstring>
#include <mutex>
#include <vector>

#include <rex/logging.h>

namespace refii::vfs {

namespace {

// ---------------------------------------------------------------------------------
// Stage 1 — the mixed frame ring
// ---------------------------------------------------------------------------------

/// 64 frames is 341 ms at the guest's 5.333 ms quantum. Deep enough that a Godot frame
/// running long never starves the device, shallow enough that the guest cannot run far
/// ahead of what is audible — the ring is the pacing mechanism, so making it huge would
/// defeat its purpose.
constexpr uint32_t kFrameRingCapacity = 64;

struct HostAudioRing {
    std::mutex mutex;
    std::condition_variable space_available;

    std::vector<float> storage;
    uint32_t read_at = 0;
    uint32_t write_at = 0;
    uint32_t count = 0;

    HostAudioCreditFn credit_fn = nullptr;
    void* credit_context = nullptr;

    uint64_t pushed = 0;
    uint64_t popped = 0;
    uint64_t dropped = 0;

    /// Rate-limits the "consumer is absent" warning. A stalled consumer produces one of
    /// these every timeout, and a log line at 187 Hz is worse than no log line.
    uint64_t drops_reported = 0;

    HostAudioRing() {
        storage.resize(static_cast<size_t>(kFrameRingCapacity) * kHostAudioFloatsPerFrame,
                       0.0f);
    }
};

HostAudioRing& Ring() {
    static HostAudioRing ring;
    return ring;
}

std::atomic<bool> g_host_audio_driver_installed{false};

// ---------------------------------------------------------------------------------
// Stage 2 — per-voice state
// ---------------------------------------------------------------------------------

/// 64 KB of PCM per voice, allocated on first submit and released when the voice
/// retires. The guest caps itself at 10 packets in flight per voice (sub_82C597D0:
/// `if ( (unsigned int)v2[2] >= 0xA ) break;`), so this is several times the most the
/// guest can have outstanding.
constexpr uint32_t kVoicePcmCapacity = 64 * 1024;

struct VoiceSlot {
    VoiceRecord record{};
    std::vector<uint8_t> pcm;
    uint32_t pcm_read_at = 0;
    uint32_t pcm_write_at = 0;
    uint32_t pcm_count = 0;
    /// Set once DestroyVoice has been seen AND the host has been told; only then may the
    /// slot be reused, or a voice the host never learned about would vanish silently.
    bool reported_retired = false;
};

struct VoiceTable {
    std::mutex mutex;
    VoiceSlot slots[kMaxTrackedVoices];
    uint32_t next_generation = 1;
    uint64_t overflow_drops = 0;
    uint64_t pcm_drops = 0;
    uint64_t bytes_taken = 0;
    uint64_t snapshots = 0;
};

VoiceTable& Voices() {
    static VoiceTable table;
    return table;
}

std::atomic<bool> g_voice_capture_active{false};
std::atomic<bool> g_voice_capture_owns_output{false};

/// Callers hold VoiceTable::mutex.
VoiceSlot* FindSlot(VoiceTable& table, uint32_t handle) {
    if (handle == 0) {
        return nullptr;
    }
    for (auto& slot : table.slots) {
        if (slot.record.handle == handle && !slot.reported_retired) {
            return &slot;
        }
    }
    return nullptr;
}

}  // namespace

// ---------------------------------------------------------------------------------
// Stage 1
// ---------------------------------------------------------------------------------

bool SetHostAudioCreditSink(HostAudioCreditFn fn, void* context) {
    auto& ring = Ring();
    std::lock_guard<std::mutex> guard(ring.mutex);

    if (fn != nullptr && ring.credit_fn != nullptr && ring.credit_context != context) {
        REXLOG_WARN(
            "refii audio: a second render-driver client tried to take the host sink. There "
            "is one ring and one clock; the first client keeps them.");
        return false;
    }

    ring.credit_fn = fn;
    ring.credit_context = context;

    if (fn == nullptr) {
        // Nothing can credit queued frames any more, so holding them would strand the
        // guest behind a ring it can never drain.
        ring.read_at = 0;
        ring.write_at = 0;
        ring.count = 0;
        ring.space_available.notify_all();
    }

    return true;
}

bool PushHostAudioFrame(const float* interleaved_stereo, uint32_t timeout_ms) {
    if (interleaved_stereo == nullptr) {
        return false;
    }

    auto& ring = Ring();
    std::unique_lock<std::mutex> guard(ring.mutex);

    const bool have_space = ring.space_available.wait_for(
        guard, std::chrono::milliseconds(timeout_ms),
        [&ring] { return ring.count < kFrameRingCapacity; });

    if (!have_space) {
        // The consumer is gone, not merely late. Drop the frame and let the caller
        // credit the guest anyway: a wedged audio thread stalls NLHAudio at its packet
        // cap, and a silent game that runs is better than a stuck one.
        ring.dropped++;
        if (ring.dropped - ring.drops_reported >= 256 || ring.drops_reported == 0) {
            ring.drops_reported = ring.dropped;
            REXLOG_WARN(
                "refii audio: no consumer took a frame within {} ms; dropped {} frames "
                "total. The guest is being credited anyway so it keeps running.",
                timeout_ms, ring.dropped);
        }
        return false;
    }

    float* slot = ring.storage.data() +
                  static_cast<size_t>(ring.write_at) * kHostAudioFloatsPerFrame;
    std::memcpy(slot, interleaved_stereo, kHostAudioFloatsPerFrame * sizeof(float));

    ring.write_at = (ring.write_at + 1) % kFrameRingCapacity;
    ring.count++;
    ring.pushed++;
    return true;
}

uint32_t PopHostAudioFrames(float* out, uint32_t max_frames) {
    if (out == nullptr || max_frames == 0) {
        return 0;
    }

    auto& ring = Ring();
    HostAudioCreditFn credit_fn = nullptr;
    void* credit_context = nullptr;
    uint32_t taken = 0;

    {
        std::lock_guard<std::mutex> guard(ring.mutex);
        taken = std::min(max_frames, ring.count);
        for (uint32_t i = 0; i < taken; i++) {
            const float* slot = ring.storage.data() +
                                static_cast<size_t>(ring.read_at) * kHostAudioFloatsPerFrame;
            std::memcpy(out + static_cast<size_t>(i) * kHostAudioFloatsPerFrame, slot,
                        kHostAudioFloatsPerFrame * sizeof(float));
            ring.read_at = (ring.read_at + 1) % kFrameRingCapacity;
        }

        if (taken != 0) {
            ring.count -= taken;
            ring.popped += taken;
            credit_fn = ring.credit_fn;
            credit_context = ring.credit_context;
            ring.space_available.notify_all();
        }
    }

    // Credit outside the lock: the driver's release path is the guest's audio worker
    // wake-up, and holding an audio ring lock across it invites a stall on the very
    // thread this ring exists to pace.
    if (credit_fn != nullptr && taken != 0) {
        credit_fn(credit_context, taken);
    }

    return taken;
}

uint32_t HostAudioQueuedFrames() {
    auto& ring = Ring();
    std::lock_guard<std::mutex> guard(ring.mutex);
    return ring.count;
}

void HostAudioCounters(uint64_t* out_pushed, uint64_t* out_popped, uint64_t* out_dropped) {
    auto& ring = Ring();
    std::lock_guard<std::mutex> guard(ring.mutex);
    if (out_pushed != nullptr) {
        *out_pushed = ring.pushed;
    }
    if (out_popped != nullptr) {
        *out_popped = ring.popped;
    }
    if (out_dropped != nullptr) {
        *out_dropped = ring.dropped;
    }
}

bool HostAudioDriverInstalled() {
    return g_host_audio_driver_installed.load(std::memory_order_acquire);
}

void SetHostAudioDriverInstalled(bool installed) {
    g_host_audio_driver_installed.store(installed, std::memory_order_release);
}

// ---------------------------------------------------------------------------------
// Stage 2
// ---------------------------------------------------------------------------------

// C linkage, per core/payload_bridge.h: the callers are hooks inside the payload, which is
// built by the player's compiler. refii::vfs::NoteVoice* still exists as an inline wrapper
// in core/audio_state.h, so call sites are unchanged.
extern "C" void refii_bridge_note_voice_created(uint32_t handle, uint32_t sample_rate,
                                                uint32_t channels) {
    if (handle == 0) {
        return;
    }

    auto& table = Voices();
    std::lock_guard<std::mutex> guard(table.mutex);
    g_voice_capture_active.store(true, std::memory_order_release);

    // A handle can be reused by the guest's allocator, so an existing entry for it is a
    // stale one that never got a Destroy. Reclaim it rather than growing a duplicate.
    if (VoiceSlot* stale = FindSlot(table, handle)) {
        *stale = VoiceSlot{};
    }

    for (auto& slot : table.slots) {
        if (slot.record.handle != 0 && !slot.reported_retired) {
            continue;
        }

        slot = VoiceSlot{};
        slot.record.handle = handle;
        slot.record.generation = table.next_generation++;
        slot.record.sample_rate = sample_rate;
        slot.record.channels = channels;
        slot.record.volume = 1.0f;
        slot.record.pitch_ratio = 1.0f;
        slot.record.pan_left = 1.0f;
        slot.record.pan_right = 1.0f;
        return;
    }

    table.overflow_drops++;
    if (table.overflow_drops == 1 || table.overflow_drops % 256 == 0) {
        REXLOG_WARN(
            "refii audio: more than {} concurrent guest voices; {} were not tracked. "
            "Those sounds will not reach Godot.",
            kMaxTrackedVoices, table.overflow_drops);
    }
}

extern "C" void refii_bridge_note_voice_destroyed(uint32_t handle) {
    auto& table = Voices();
    std::lock_guard<std::mutex> guard(table.mutex);
    if (VoiceSlot* slot = FindSlot(table, handle)) {
        slot->record.retired = 1;
        slot->record.started = 0;
    }
}

extern "C" void refii_bridge_note_voice_started(uint32_t handle) {
    auto& table = Voices();
    std::lock_guard<std::mutex> guard(table.mutex);
    if (VoiceSlot* slot = FindSlot(table, handle)) {
        slot->record.started = 1;
    }
}

extern "C" void refii_bridge_note_voice_stopped(uint32_t handle, int32_t immediate) {
    auto& table = Voices();
    std::lock_guard<std::mutex> guard(table.mutex);
    VoiceSlot* slot = FindSlot(table, handle);
    if (slot == nullptr) {
        return;
    }

    slot->record.started = 0;

    // Stop does NOT flush the queue — nothing in CSourceVoice::Stop, CVoice::Stop or
    // CSourceStream::Stop touches the packet list; they only move flag bits. A stopped
    // voice can be restarted and resumes from what it still holds, so dropping our copy
    // here would lose audio the guest still intends to play. Only the immediate form
    // discards, matching FlushPackets @ 0x82ce29d0, which force-stops first.
    if (immediate) {
        slot->pcm_read_at = 0;
        slot->pcm_write_at = 0;
        slot->pcm_count = 0;
    }
}

extern "C" void refii_bridge_note_voice_volume(uint32_t handle, float linear_volume) {
    auto& table = Voices();
    std::lock_guard<std::mutex> guard(table.mutex);
    if (VoiceSlot* slot = FindSlot(table, handle)) {
        slot->record.volume = linear_volume;
    }
}

extern "C" void refii_bridge_note_voice_pitch_octaves(uint32_t handle, float octaves) {
    auto& table = Voices();
    std::lock_guard<std::mutex> guard(table.mutex);
    if (VoiceSlot* slot = FindSlot(table, handle)) {
        // The guest states octaves; CSourceStream::SetPitch @ 0x82ce00f8 stores
        // powf(2.0f, arg). Godot's PitchScale is the ratio, so convert once here rather
        // than making every consumer remember the unit.
        slot->record.pitch_ratio = std::exp2(octaves);
    }
}

extern "C" void refii_bridge_note_voice_channel_volumes(uint32_t handle, const float* gains,
                                                        uint32_t count) {
    if (gains == nullptr || count == 0) {
        return;
    }

    // XAudio's default 5.1 order: front left, front right, centre, LFE, back left, back
    // right. The fold matches the SDK's own 5.1 → stereo conversion — centre split evenly
    // across both, LFE discarded, scaled by 1/2.5 so a fully-loaded matrix cannot clip.
    const float fl = count > 0 ? gains[0] : 0.0f;
    const float fr = count > 1 ? gains[1] : 0.0f;
    const float fc = count > 2 ? gains[2] : 0.0f;
    const float bl = count > 4 ? gains[4] : 0.0f;
    const float br = count > 5 ? gains[5] : 0.0f;

    const float centre_halved = fc * 0.5f;
    const float left = (fl + bl + centre_halved) * (1.0f / 2.5f);
    const float right = (fr + br + centre_halved) * (1.0f / 2.5f);

    auto& table = Voices();
    std::lock_guard<std::mutex> guard(table.mutex);
    if (VoiceSlot* slot = FindSlot(table, handle)) {
        slot->record.pan_left = left;
        slot->record.pan_right = right;
    }
}

extern "C" void refii_bridge_note_voice_pcm(uint32_t handle, const void* pcm,
                                            uint32_t byte_count) {
    if (pcm == nullptr || byte_count == 0) {
        return;
    }

    // NOBODY IS LISTENING, SO DO NOT COLLECT.
    //
    // The lifecycle notes above are cheap and stay on unconditionally, so the host can see
    // what voices exist even with per-voice playback off. Copying their PCM is not cheap:
    // it is a memcpy per submit, several hundred times a second, into rings that only
    // TakeVoicePcm drains — and that is only called when Godot is actually playing voices.
    //
    // With playback off the rings therefore filled and stayed full, which a run made
    // obvious: "voice 0x4C8C823C PCM ring full, dropped 3584 submits total" against a
    // feature that was switched off. Free work for nobody, reported as a fault.
    if (!g_voice_capture_owns_output.load(std::memory_order_acquire)) {
        return;
    }

    auto& table = Voices();
    std::lock_guard<std::mutex> guard(table.mutex);
    VoiceSlot* slot = FindSlot(table, handle);
    if (slot == nullptr) {
        return;
    }

    if (slot->pcm.empty()) {
        slot->pcm.resize(kVoicePcmCapacity);
    }

    if (slot->pcm_count + byte_count > kVoicePcmCapacity) {
        // The host is not draining this voice. Dropping the NEWEST keeps what is already
        // queued contiguous and in order; dropping the oldest would splice a hole into
        // the middle of a sound that is currently audible.
        table.pcm_drops++;
        if (table.pcm_drops == 1 || table.pcm_drops % 512 == 0) {
            REXLOG_WARN("refii audio: voice 0x{:08X} PCM ring full, dropped {} submits total",
                        handle, table.pcm_drops);
        }
        return;
    }

    const auto* bytes = static_cast<const uint8_t*>(pcm);
    const uint32_t until_end = kVoicePcmCapacity - slot->pcm_write_at;
    const uint32_t first = std::min(byte_count, until_end);
    std::memcpy(slot->pcm.data() + slot->pcm_write_at, bytes, first);
    if (byte_count > first) {
        std::memcpy(slot->pcm.data(), bytes + first, byte_count - first);
    }

    slot->pcm_write_at = (slot->pcm_write_at + byte_count) % kVoicePcmCapacity;
    slot->pcm_count += byte_count;
    slot->record.queued_bytes = slot->pcm_count;
    slot->record.submitted_seq++;
}

uint32_t TakeVoiceRecords(VoiceRecord* out, uint32_t capacity) {
    auto& table = Voices();
    std::lock_guard<std::mutex> guard(table.mutex);

    if (out != nullptr) {
        table.snapshots++;
    }

    uint32_t written = 0;
    for (auto& slot : table.slots) {
        if (slot.record.handle == 0 || slot.reported_retired) {
            continue;
        }

        if (out != nullptr) {
            if (written >= capacity) {
                break;
            }
            slot.record.queued_bytes = slot.pcm_count;
            out[written] = slot.record;

            // Freeing on the read AFTER the retirement is reported means the host always
            // sees the end of a voice it saw the start of. Freeing at Destroy time would
            // lose short sounds that begin and end between two polls.
            if (slot.record.retired != 0 && slot.pcm_count == 0) {
                slot.reported_retired = true;
                slot.pcm.clear();
                slot.pcm.shrink_to_fit();
            }
        }
        written++;
    }

    return written;
}

uint32_t TakeVoicePcm(uint32_t handle, uint32_t generation, void* out, uint32_t capacity) {
    if (out == nullptr || capacity == 0) {
        return 0;
    }

    auto& table = Voices();
    std::lock_guard<std::mutex> guard(table.mutex);
    VoiceSlot* slot = FindSlot(table, handle);
    if (slot == nullptr || slot->record.generation != generation) {
        // A generation mismatch means the guest recycled this handle for a new sound.
        // Handing back the new voice's PCM under the old identity would play the wrong
        // audio, so refuse instead.
        return 0;
    }

    const uint32_t taken = std::min(capacity, slot->pcm_count);
    if (taken == 0) {
        return 0;
    }

    auto* bytes = static_cast<uint8_t*>(out);
    const uint32_t until_end = kVoicePcmCapacity - slot->pcm_read_at;
    const uint32_t first = std::min(taken, until_end);
    std::memcpy(bytes, slot->pcm.data() + slot->pcm_read_at, first);
    if (taken > first) {
        std::memcpy(bytes + first, slot->pcm.data(), taken - first);
    }

    slot->pcm_read_at = (slot->pcm_read_at + taken) % kVoicePcmCapacity;
    slot->pcm_count -= taken;
    slot->record.queued_bytes = slot->pcm_count;
    table.bytes_taken += taken;
    return taken;
}

void VoiceCaptureStats(uint32_t* out_live, uint64_t* out_bytes_held,
                       uint64_t* out_bytes_taken, uint64_t* out_snapshots) {
    auto& table = Voices();
    std::lock_guard<std::mutex> guard(table.mutex);

    uint32_t live = 0;
    uint64_t held = 0;
    for (const auto& slot : table.slots) {
        if (slot.record.handle == 0 || slot.reported_retired) {
            continue;
        }
        live++;
        held += slot.pcm_count;
    }

    if (out_live != nullptr) {
        *out_live = live;
    }
    if (out_bytes_held != nullptr) {
        *out_bytes_held = held;
    }
    if (out_bytes_taken != nullptr) {
        *out_bytes_taken = table.bytes_taken;
    }
    if (out_snapshots != nullptr) {
        *out_snapshots = table.snapshots;
    }
}

bool VoiceCaptureActive() {
    return g_voice_capture_active.load(std::memory_order_acquire);
}

bool VoiceCaptureOwnsOutput() {
    return g_voice_capture_owns_output.load(std::memory_order_acquire);
}

void SetVoiceCaptureOwnsOutput(bool owns) {
    g_voice_capture_owns_output.store(owns, std::memory_order_release);
}

}  // namespace refii::vfs
