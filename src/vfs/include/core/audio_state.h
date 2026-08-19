// refii — audio state shared by the guest side and the host bridge
//
// THIS MUST LIVE IN librefii_vfs. The Stage 2 voice hooks compile into
// librefii_generated.so, the Stage 1 driver into refii_host, and the bridge that reads
// both is librefii_hostbridge.so. All three link refii_vfs, so defining the state here
// gives them one copy. bink_frame_state.cpp records what happens otherwise: the hook
// wrote one global and the bridge read a different one, forever zero.
//
// Two independent channels live here:
//
//   Stage 1  the guest's MIXED output, so Godot owns the audio device and — by
//            consuming frames — owns the guest's audio clock.
//   Stage 2  the guest's INDIVIDUAL voices, so Godot can place each sound itself.
//
// Stage 1 stays live under Stage 2: the guest's mixer keeps running because its own
// bookkeeping (the packet-retire callback, voice state, the tail countdown) depends on
// it, and that clock is what keeps Godot and the simulation in step. What changes is
// that the mixed frame is discarded instead of played.

#pragma once

#include <cstdint>

#include "core/payload_bridge.h"

namespace refii::vfs {

// ---------------------------------------------------------------------------------
// Stage 1 — the mixed output frame
// ---------------------------------------------------------------------------------

/// The guest's render-driver frame, verified against the binary:
/// CMasteringVoice::Initialize @ 0x82ceae30 sets 6 channels / 48000 Hz, and
/// CXenonRenderer::Process @ 0x82cdeb18 submits 256 samples per channel as planar
/// big-endian float32 (6144 bytes).
inline constexpr uint32_t kGuestAudioSampleRate = 48000;
inline constexpr uint32_t kGuestAudioChannels = 6;
inline constexpr uint32_t kGuestAudioSamplesPerFrame = 256;

/// What we hand Godot. Godot's AudioStreamGenerator is stereo, so the 5.1 frame is
/// folded on the way in — the same fold the SDK's SDL driver already applies to a
/// stereo device, so this is not a regression against the current build. Stage 2
/// retires the question entirely by feeding Godot individual voices instead.
inline constexpr uint32_t kHostAudioChannels = 2;
inline constexpr uint32_t kHostAudioFloatsPerFrame =
    kGuestAudioSamplesPerFrame * kHostAudioChannels;

/// How the guest is credited for a frame the consumer has taken.
///
/// The guest's audio clock IS the consumption rate: one semaphore credit per frame
/// consumed wakes the audio worker, which re-enters the guest's render callback, which
/// mixes the next frame. Crediting on QUEUE rather than on CONSUMPTION would let the
/// guest free-run against a buffer, which is the drift this whole path exists to remove.
using HostAudioCreditFn = void (*)(void* context, uint32_t frames);

/// Installed by the host audio driver. Passing nullptr detaches — any frames still
/// queued are dropped, because nothing can credit them any more.
///
/// Returns false when a sink is ALREADY installed. There is one ring and one clock, so a
/// second render-driver client cannot share them: silently replacing the first would send
/// its credits to the wrong semaphore and stall whichever client lost. The guest registers
/// exactly one client, but "cannot happen" is not the same as "is not handled".
bool SetHostAudioCreditSink(HostAudioCreditFn fn, void* context);

/// Queues one mixed frame of `kHostAudioFloatsPerFrame` interleaved stereo floats.
///
/// BLOCKS while the ring is full, which is the back-pressure that paces the guest. If
/// no consumer takes a frame within `timeout_ms` the frame is DROPPED and credited
/// immediately: a Godot that never polls — no GameAudio node in the scene, a shell that
/// exited — must leave the guest running silently rather than wedged. Returns true when
/// the frame was queued, false when it was dropped.
bool PushHostAudioFrame(const float* interleaved_stereo, uint32_t timeout_ms);

/// Takes up to `max_frames` frames into `out`, which must hold
/// `max_frames * kHostAudioFloatsPerFrame` floats. Credits the guest for exactly what it
/// took. Returns the number of frames written.
uint32_t PopHostAudioFrames(float* out, uint32_t max_frames);

/// Frames waiting to be taken.
uint32_t HostAudioQueuedFrames();

/// Counters since process start. `dropped` climbing means the consumer is absent or too
/// slow; `pushed` should sit at 187.5/s (48000/256) once the guest is producing.
void HostAudioCounters(uint64_t* out_pushed, uint64_t* out_popped, uint64_t* out_dropped);

/// True once the host driver is installed, i.e. the guest is not talking to SDL.
bool HostAudioDriverInstalled();
void SetHostAudioDriverInstalled(bool installed);

// ---------------------------------------------------------------------------------
// Stage 2 — individual voices
// ---------------------------------------------------------------------------------

/// Every voice this title creates is PCM. Verified at the game's own create site
/// sub_82C58748: `HIBYTE(v25[0]) = 1` — format tag 1, never 4. The dispatch at
/// sub_82CDFE90 sends tag <= 3 to XAudioCreatePCMSourceEffect and only tag 4 to XMA, so
/// CXMASourceEffect is linked but dead here and no XMA decode is needed at this seam.
inline constexpr uint32_t kMaxTrackedVoices = 256;

/// What the host needs to play one voice. Volume is LINEAR amplitude (1.0 unity) and
/// `pitch_ratio` is a frequency ratio, already converted from the octaves the guest
/// passes — CSourceStream::SetPitch @ 0x82ce00f8 stores powf(2.0f, arg).
struct VoiceRecord {
    uint32_t handle;         ///< The guest's voice pointer. 0 means the slot is free.
    uint32_t generation;     ///< Bumped on create, so a reused handle is a new voice.
    uint32_t sample_rate;
    uint32_t channels;
    uint32_t started;        ///< Non-zero between Start and Stop.
    uint32_t retired;        ///< Set by DestroyVoice; the slot frees once the host reads it.
    float volume;
    float pitch_ratio;
    uint32_t queued_bytes;   ///< PCM waiting in this voice's ring.
    uint32_t submitted_seq;  ///< Bumped per SubmitSourceBuffer, so the host can spot loss.

    /// The guest's own spatialisation, folded to a stereo balance.
    ///
    /// These are NOT invented on the host. sub_82C58328 takes the six speaker gains
    /// X3DAudio wrote to the voice's DSP settings (CLVoice+500) and hands them to
    /// XAudioSetChannelVolumes; this is that matrix, folded 5.1 → stereo. Until Godot
    /// places voices itself against its own camera, following the guest's matrix is what
    /// keeps a per-voice mix positioned the way the game intended.
    float pan_left;
    float pan_right;
};

/// Called by the voice hooks. All of these run under the guest's own XAudio critical
/// section (unk_832F0464, taken by every 0x82CD9xxx shim), so the guest never calls them
/// concurrently with each other — but the HOST reads on its own thread, so the state
/// itself is still synchronised.
///
/// These forward to the C entries in core/payload_bridge.h: the hooks calling them compile
/// into librefii_generated.so on the player's machine, and a C++ symbol crossing that
/// boundary would pin the payload to our exact compiler and standard library. Inline, so
/// the call site emits the same cross-module call it always did.
inline void NoteVoiceCreated(uint32_t handle, uint32_t sample_rate, uint32_t channels) {
    refii_bridge_note_voice_created(handle, sample_rate, channels);
}
inline void NoteVoiceDestroyed(uint32_t handle) {
    refii_bridge_note_voice_destroyed(handle);
}
inline void NoteVoiceStarted(uint32_t handle) {
    refii_bridge_note_voice_started(handle);
}
inline void NoteVoiceStopped(uint32_t handle, bool immediate) {
    refii_bridge_note_voice_stopped(handle, immediate ? 1 : 0);
}
inline void NoteVoiceVolume(uint32_t handle, float linear_volume) {
    refii_bridge_note_voice_volume(handle, linear_volume);
}
inline void NoteVoicePitchOctaves(uint32_t handle, float octaves) {
    refii_bridge_note_voice_pitch_octaves(handle, octaves);
}

/// The guest's six speaker gains, in XAudio's order (fl, fr, fc, lfe, bl, br). Folded to
/// a stereo balance here so every consumer gets the same answer.
inline void NoteVoiceChannelVolumes(uint32_t handle, const float* gains, uint32_t count) {
    refii_bridge_note_voice_channel_volumes(handle, gains, count);
}

/// Copies submitted PCM into the voice's ring. The guest reuses its buffers as its own
/// retire callback drains them, so holding the pointer would be a use-after-free; this
/// takes a copy at submit time.
///
/// **Does nothing unless VoiceCaptureOwnsOutput()** — only the host playing voices ever
/// drains these rings, so collecting with playback off fills them and then reports the
/// overflow as a fault. The lifecycle calls above stay on regardless; they cost a table
/// entry, not a copy per submit — and with playback off that entry is released by
/// NoteVoiceDestroyed rather than held for a TakeVoiceRecords that will never come.
inline void NoteVoicePcm(uint32_t handle, const void* pcm, uint32_t byte_count) {
    refii_bridge_note_voice_pcm(handle, pcm, byte_count);
}

/// Snapshots every live voice. Returns how many were written; pass a null buffer to
/// count. Slots whose `retired` has been read are freed by this call.
uint32_t TakeVoiceRecords(VoiceRecord* out, uint32_t capacity);

/// Drains PCM for one voice as interleaved 16-bit samples. Returns bytes written.
uint32_t TakeVoicePcm(uint32_t handle, uint32_t generation, void* out, uint32_t capacity);

/// True once the voice hooks have seen anything at all, so the host can tell "Stage 2 is
/// live" from "Stage 2 is built but the guest has not reached audio yet".
bool VoiceCaptureActive();

/// What the capture side believes it is holding, for comparison against what the host
/// reports receiving. The two disagreeing is the whole reason this exists: the rings
/// reported themselves FULL while the host read every voice as empty, and only having both
/// numbers side by side says which end is wrong.
void VoiceCaptureStats(uint32_t* out_live, uint64_t* out_bytes_held,
                       uint64_t* out_bytes_taken, uint64_t* out_snapshots);

/// True when the host is playing individual voices rather than the guest's mix.
///
/// This gates PCM COLLECTION and nothing else. It deliberately does NOT change the mixed
/// frame's path: that frame is still folded, still queued and still paced, because
/// PushHostAudioFrame is the only thing holding the guest to real time. Skipping it here
/// once removed the audio clock entirely and let the guest free-run — the host silences
/// the mix instead, which costs nothing and keeps both modes on the same clock.
bool VoiceCaptureOwnsOutput();
void SetVoiceCaptureOwnsOutput(bool owns);

}  // namespace refii::vfs
