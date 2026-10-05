// refii — mirroring the guest's XAudio voices so Godot can play them itself.
//
// These are the guest's own XAudio API shims (the 0x82CD9xxx block), which is the STATED
// audio surface rather than anything decoded or inferred. Each one takes the single global
// XAudio critical section (unk_832F0464) before dispatching through a vtable, so the guest
// never calls two of them at once — which is why nothing here needs guest-side locking of
// its own. The host reads on another thread, so the state itself is still synchronised.
//
// EVERY HOOK CALLS `__imp__` UNCONDITIONALLY. We mirror the engine; we do not replace it.
// The guest's XAudio keeps running and keeps its own bookkeeping, which is what makes this
// tractable: a real replacement would have to answer the packet-retire callback
// (CSourceEffect::CompletePacket @ 0x82ce07b8 — starve it and NLHAudio wedges at its
// ten-packet cap), GetVoiceState, and the tail countdown, all truthfully. Mirroring gets
// those right for free, and costs only the guest's mixing CPU.
//
// See docs/audio_xaudio_cut.md.

#include "vfs_hooks_private.h"

#include <cstdint>
#include <cstring>

#include "core/audio_state.h"
#include "core/guest_state.h"
#include "hooks/ppc_externs.h"

namespace {

/// A guest pointer that could plausibly be an object. The guest passes these straight from
/// its own allocator, so anything outside its address space means we have misread the
/// argument shape and should record nothing rather than invent a voice.
bool PlausibleGuestPointer(uint32_t address) {
    return address >= 0x10000 && address < 0x90000000;
}

/// The source-voice init struct, as the game fills it in sub_82C58748.
///
/// Its first 56 bytes are the format. Two independent readings agree on the layout:
/// XAUDIO::CSourceVoice::ConvertFormat reads the tag at +0, the channel count at +4 and
/// the rate at +8; and the game writes `HIBYTE(v25[0]) = 1`, `HIBYTE(v25[1]) = channels`,
/// `v25[2] = rate` — which on this big-endian target are those same three offsets, since
/// the most significant byte of a word is the one at the lowest address.
struct SourceVoiceFormat {
    uint8_t tag;
    uint8_t channels;
    uint32_t sample_rate;
};

SourceVoiceFormat ReadSourceVoiceFormat(uint8_t* base, uint32_t init_guest) {
    SourceVoiceFormat format{};
    if (base == nullptr || !PlausibleGuestPointer(init_guest)) {
        return format;
    }

    format.tag = refii::vfs::LoadGuestU8(base, init_guest + 0);
    format.channels = refii::vfs::LoadGuestU8(base, init_guest + 4);
    format.sample_rate = refii::vfs::LoadGuestU32(base, init_guest + 8);
    return format;
}

/// Reads the six speaker gains out of the nested descriptor XAudioSetChannelVolumes takes.
///
/// The shape is read straight from the guest's own construction site, sub_82C58328:
///
///   outputVolumes  +0 u8 outputCount, +4 ptr entries
///   entry (8 B)    +0 u8 outputIndex, +4 ptr channelVolumes
///   channelVolumes +0 u8 channelCount, +4 ptr channels
///   channel (8 B)  +0 u8 channelIndex, +4 f32 gain
///
/// The gains themselves are the X3DAudio matrix coefficients the guest computed into
/// CLVoice+500, so this is the game's own spatialisation rather than anything we infer.
/// Only the first output is read: the game builds exactly one.
uint32_t ReadChannelGains(uint8_t* base, uint32_t volumes_guest, float* out, uint32_t capacity) {
    if (base == nullptr || out == nullptr || !PlausibleGuestPointer(volumes_guest)) {
        return 0;
    }

    const uint8_t output_count = refii::vfs::LoadGuestU8(base, volumes_guest + 0);
    const uint32_t entries = refii::vfs::LoadGuestU32(base, volumes_guest + 4);
    if (output_count == 0 || !PlausibleGuestPointer(entries)) {
        return 0;
    }

    const uint32_t channel_block = refii::vfs::LoadGuestU32(base, entries + 4);
    if (!PlausibleGuestPointer(channel_block)) {
        return 0;
    }

    const uint8_t channel_count = refii::vfs::LoadGuestU8(base, channel_block + 0);
    const uint32_t channels = refii::vfs::LoadGuestU32(base, channel_block + 4);
    if (channel_count == 0 || !PlausibleGuestPointer(channels)) {
        return 0;
    }

    uint32_t written = 0;
    for (uint32_t i = 0; i < channel_count && written < capacity; i++) {
        const uint32_t entry = channels + (i * 8);
        const uint8_t channel = refii::vfs::LoadGuestU8(base, entry + 0);
        const uint32_t raw = refii::vfs::LoadGuestU32(base, entry + 4);

        // Indexed by the channel the entry NAMES, not by its position in the array. The
        // game happens to write them in order, but the descriptor does not promise that
        // and a mis-ordered fold would put sounds on the wrong side.
        if (channel < capacity) {
            float gain = 0.0f;
            std::memcpy(&gain, &raw, sizeof(gain));
            out[channel] = gain;
            written = channel + 1 > written ? channel + 1 : written;
        }
    }

    return written;
}

}  // namespace

/// XAudioCreateSourceVoice(pInit, pOutVoice) — sub_82CD9B00.
REX_EXTERN(rex_XAudioCreateSourceVoice) {
  REX_FUNC_PROLOGUE();

  const uint32_t init_guest = ctx.r3.u32;
  const uint32_t out_guest = ctx.r4.u32;

  __imp__rex_XAudioCreateSourceVoice(ctx, base);

  // The handle only exists after the call, and only if it succeeded. XAudio returns an
  // HRESULT, so the sign bit is the failure test.
  if (static_cast<int32_t>(ctx.r3.u32) < 0 || !PlausibleGuestPointer(out_guest)) {
    return;
  }

  const uint32_t voice = refii::vfs::LoadGuestU32(base, out_guest);
  if (!PlausibleGuestPointer(voice)) {
    return;
  }

  const SourceVoiceFormat format = ReadSourceVoiceFormat(base, init_guest);

  // Tag 4 is the XMA path, and this title never takes it — every create site writes 0 or
  // 1. If that assumption ever breaks, the PCM this mirror collects would be compressed
  // frames rather than samples, so it is worth saying so rather than playing noise.
  if (format.tag == 4) {
    REXLOG_WARN(
        "refii audio: voice 0x{:08X} was created with format tag 4 (XMA). This title was "
        "only ever seen to build PCM voices; its PCM mirror will be wrong.",
        voice);
  }

  refii::vfs::NoteVoiceCreated(voice, format.sample_rate, format.channels);
}

/// XAudioDestroyVoice(hVoice) — sub_82CD92B0.
REX_EXTERN(rex_XAudioDestroyVoice) {
  REX_FUNC_PROLOGUE();

  const uint32_t voice = ctx.r3.u32;

  // Recorded BEFORE the call: the object is freed inside it, and the host's snapshot keys
  // off a handle that is about to become someone else's memory.
  refii::vfs::NoteVoiceDestroyed(voice);

  __imp__rex_XAudioDestroyVoice(ctx, base);
}

/// XAudioSubmitSourceBuffer(hVoice, pBuffer, flags) — sub_82CD9490.
REX_EXTERN(rex_XAudioSubmitSourceBuffer) {
  REX_FUNC_PROLOGUE();

  const uint32_t voice = ctx.r3.u32;
  const uint32_t descriptor = ctx.r4.u32;

  if (PlausibleGuestPointer(voice) && PlausibleGuestPointer(descriptor)) {
    // XAUDIOSOURCEBUFFER: +0 data pointer, +4 byte count. The remaining fields (loop
    // counts, play begin/end, the guest's retire context) belong to the guest's own
    // engine, which is still running and still honouring them.
    const uint32_t data = refii::vfs::LoadGuestU32(base, descriptor + 0);
    const uint32_t bytes = refii::vfs::LoadGuestU32(base, descriptor + 4);

    // Copied now, not referenced. The guest reuses these buffers as its own retire
    // callback drains them, so a pointer held past this call is a use-after-free.
    if (PlausibleGuestPointer(data) && bytes != 0 && bytes <= (1u << 22)) {
      const uint8_t* pcm = rex::memory::GuestPtr<uint8_t*>(base, data);
      if (pcm != nullptr) {
        refii::vfs::NoteVoicePcm(voice, pcm, bytes);
      }
    }
  }

  __imp__rex_XAudioSubmitSourceBuffer(ctx, base);
}

/// XAudioStartVoice(hVoice, flags) — sub_82CD9548.
REX_EXTERN(rex_XAudioStartVoice) {
  REX_FUNC_PROLOGUE();

  const uint32_t voice = ctx.r3.u32;

  __imp__rex_XAudioStartVoice(ctx, base);

  if (static_cast<int32_t>(ctx.r3.u32) >= 0) {
    // Flags bit 0 means "synchronised start" — queue it, do not run it yet — but the
    // guest's own SynchStart still ends in the voice running, and we have no separate
    // signal for when. Treating both as started keeps the host from holding silent
    // streams that the guest considers live.
    refii::vfs::NoteVoiceStarted(voice);
  }
}

/// XAudioStopVoice(hVoice, flags) — sub_82CD9590.
REX_EXTERN(rex_XAudioStopVoice) {
  REX_FUNC_PROLOGUE();

  const uint32_t voice = ctx.r3.u32;
  const uint32_t flags = ctx.r4.u32;

  __imp__rex_XAudioStopVoice(ctx, base);

  if (static_cast<int32_t>(ctx.r3.u32) >= 0) {
    // Bit 0 clear means "play out the tail", and a stop of EITHER kind leaves the packet
    // queue intact — nothing in CSourceVoice::Stop, CVoice::Stop or CSourceStream::Stop
    // touches it. So only the immediate form discards what we hold; the other keeps it,
    // because the guest can restart the voice and resume from exactly those packets.
    refii::vfs::NoteVoiceStopped(voice, (flags & 1u) != 0);
  }
}

/// XAudioSetVolume(hVoice, float) — sub_82CD97B0.
REX_EXTERN(rex_XAudioSetVolume) {
  REX_FUNC_PROLOGUE();

  const uint32_t voice = ctx.r3.u32;
  // LINEAR amplitude, 1.0 unity. No volume path in this engine touches log, exp or pow —
  // the guest never speaks in decibels. PPC passes a float argument in f1 as a double.
  const float volume = static_cast<float>(ctx.f1.f64);

  __imp__rex_XAudioSetVolume(ctx, base);

  refii::vfs::NoteVoiceVolume(voice, volume);
}

/// XAudioSetPitch(hVoice, float) — sub_82CD9828.
REX_EXTERN(rex_XAudioSetPitch) {
  REX_FUNC_PROLOGUE();

  const uint32_t voice = ctx.r3.u32;
  // OCTAVES, not a ratio and not semitones: CSourceStream::SetPitch @ 0x82ce00f8 stores
  // powf(2.0f, arg), and dbl_82000DE8 is 2.0. NLHAudio agrees from the other side — it
  // passes log10(rate) * 3.321928f, which is 1/log10(2), i.e. log2(rate).
  const float octaves = static_cast<float>(ctx.f1.f64);

  __imp__rex_XAudioSetPitch(ctx, base);

  refii::vfs::NoteVoicePitchOctaves(voice, octaves);
}

/// XAudioSetChannelVolumes(hVoice, pOutputVolumes) — sub_82CD93E0.
REX_EXTERN(rex_XAudioSetChannelVolumes) {
  REX_FUNC_PROLOGUE();

  const uint32_t voice = ctx.r3.u32;
  const uint32_t volumes = ctx.r4.u32;

  float gains[6] = {0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f};
  const uint32_t count = ReadChannelGains(base, volumes, gains, 6);

  __imp__rex_XAudioSetChannelVolumes(ctx, base);

  if (count != 0) {
    refii::vfs::NoteVoiceChannelVolumes(voice, gains, count);
  }
}
