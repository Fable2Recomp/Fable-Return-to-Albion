/**
 ******************************************************************************
 * Xenia : Xbox 360 Emulator Research Project                                 *
 ******************************************************************************
 * Copyright 2020 Ben Vanik. All rights reserved.                             *
 * Released under the BSD license - see LICENSE in the root for more details. *
 ******************************************************************************
 *
 * @modified    Tom Clay, 2026 - Adapted for ReXGlue runtime
 */

#include <chrono>
#include <algorithm>
#include <array>
#include <cstring>

#include <rex/assert.h>
#include <rex/audio/conversion.h>
#include <rex/audio/downmix.h>
#include <rex/audio/flags.h>
#include <rex/audio/sdl/sdl_audio_driver.h>
#include <rex/cvar.h>
#include <rex/dbg.h>
#include <rex/logging.h>
#include <rex/perf/counter.h>
#include <SDL3/SDL.h>

REXCVAR_DEFINE_BOOL(audio_mute, false, "Audio", "Mute audio output");

namespace rex::audio::sdl {

namespace {

void ConfigureHostSafeAudioBackend() {
#if defined(__linux__)
  if (const char* requested_driver = SDL_GetHint(SDL_HINT_AUDIO_DRIVER);
      requested_driver && *requested_driver) {
    REXAPU_INFO("SDL audio driver requested by environment/hint: {}", requested_driver);
    return;
  }

  // Avoid ALSA fallback by default. If the process is stopped in gdb or hangs,
  // a direct ALSA device can block the rest of the desktop audio stack.
  SDL_SetHintWithPriority(SDL_HINT_AUDIO_DRIVER, "pipewire,pulseaudio", SDL_HINT_DEFAULT);
  SDL_SetHintWithPriority(SDL_HINT_AUDIO_ALSA_DEFAULT_PLAYBACK_DEVICE, "pulse",
                          SDL_HINT_DEFAULT);
  REXAPU_INFO("SDL audio driver preference: pipewire,pulseaudio");
#endif
}

}  // namespace

SDLAudioDriver::SDLAudioDriver(memory::Memory* memory, rex::thread::Semaphore* semaphore)
    : AudioDriver(memory), semaphore_(semaphore) {}

SDLAudioDriver::~SDLAudioDriver() {
  assert_true(frames_queued_.empty());
  assert_true(frames_unused_.empty());
}

bool SDLAudioDriver::Initialize() {
  // Set audio category for proper OS audio handling
  SDL_SetHint(SDL_HINT_AUDIO_CATEGORY, "playback");

  // Set app name for audio device identification
  SDL_SetAppMetadataProperty(SDL_PROP_APP_METADATA_NAME_STRING, "rexglue");

  ConfigureHostSafeAudioBackend();

  if (!SDL_InitSubSystem(SDL_INIT_AUDIO)) {
    REXAPU_ERROR("SDL_InitSubSystem(SDL_INIT_AUDIO) failed: {}", SDL_GetError());
    return false;
  }
  sdl_initialized_ = true;
  if (const char* current_driver = SDL_GetCurrentAudioDriver()) {
    REXAPU_INFO("SDL audio driver selected: {}", current_driver);
  }

  SDL_AudioSpec desired_spec = {};
  SDL_AudioSpec obtained_spec = {};
  desired_spec.freq = frame_frequency_;
  desired_spec.format = SDL_AUDIO_F32LE;
  desired_spec.channels = frame_channels_;
  sdl_device_channels_ = frame_channels_;
  sdl_stream_ = SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &desired_spec,
                                          SDLCallback, this);
  if (!sdl_stream_) {
    REXAPU_ERROR("SDL_OpenAudioDeviceStream() failed: {}", SDL_GetError());
    return false;
  }

  sdl_device_ = SDL_GetAudioStreamDevice(sdl_stream_);
  if (!sdl_device_) {
    REXAPU_ERROR("SDL_GetAudioStreamDevice() failed: {}", SDL_GetError());
    return false;
  }

  if (!SDL_GetAudioDeviceFormat(sdl_device_, &obtained_spec, NULL)) {
    REXAPU_WARN("SDL_GetAudioDeviceFormat() failed: {}", SDL_GetError());
    obtained_spec = desired_spec;
  }

  // A 1-channel device gets the stereo fold too, then SDL collapses to mono.
  // Handing it a 6ch stream instead would use SDL's own downmix.
  if (obtained_spec.channels <= 2) {
    SDL_DestroyAudioStream(sdl_stream_);
    sdl_stream_ = nullptr;
    sdl_device_ = 0;
    desired_spec.channels = 2;
    sdl_device_channels_ = 2;
    sdl_stream_ = SDL_OpenAudioDeviceStream(SDL_AUDIO_DEVICE_DEFAULT_PLAYBACK, &desired_spec,
                                            SDLCallback, this);
    if (!sdl_stream_) {
      REXAPU_ERROR("SDL_OpenAudioDeviceStream() stereo fallback failed: {}", SDL_GetError());
      return false;
    }
    sdl_device_ = SDL_GetAudioStreamDevice(sdl_stream_);
    if (!sdl_device_) {
      REXAPU_ERROR("SDL_GetAudioStreamDevice() failed after stereo fallback: {}", SDL_GetError());
      return false;
    }
  }

  // The endpoint layout decides which mix the callback runs, and it is the
  // first thing worth knowing when a report says the balance is wrong on one
  // speaker setup and right on another.
  const char* device_name = SDL_GetAudioDeviceName(sdl_device_);
  REXAPU_INFO("audio endpoint '{}': {} ch, {} Hz, format 0x{:04X}; submitting {} ch",
              device_name ? device_name : "?", obtained_spec.channels, obtained_spec.freq,
              static_cast<uint32_t>(obtained_spec.format), static_cast<int>(sdl_device_channels_));

  if (!SDL_ResumeAudioDevice(sdl_device_)) {
    REXAPU_ERROR("SDL_ResumeAudioDevice() failed: {}", SDL_GetError());
    return false;
  }
  output_paused_.store(false, std::memory_order_release);

  return true;
}

void SDLAudioDriver::SubmitFrame(uint32_t frame_ptr) {
  const auto input_frame = memory_->TranslateVirtual<float*>(frame_ptr);
  float* output_frame;
  {
    std::unique_lock<std::mutex> guard(frames_mutex_);
    if (frames_unused_.empty()) {
      output_frame = new float[frame_samples_];
    } else {
      output_frame = frames_unused_.top();
      frames_unused_.pop();
    }
  }

  std::memcpy(output_frame, input_frame, frame_samples_ * sizeof(float));

  size_t queued_count = 0;
  {
    std::unique_lock<std::mutex> guard(frames_mutex_);
    frames_queued_.push(output_frame);
    queued_count = frames_queued_.size();
    PROFILE_BUFFER_QUEUE_DEPTH(static_cast<int64_t>(queued_count));
  }

  static uint32_t sdl_submit_count = 0;
  if (sdl_submit_count < 10) {
    REXAPU_DEBUG("SDLAudioDriver::SubmitFrame: frame_ptr={:08X} queued_count={}", frame_ptr,
                 queued_count);
    sdl_submit_count++;
  }

  ResumeOutputIfNeeded();
}

void SDLAudioDriver::Shutdown() {
  if (sdl_device_ && !output_paused_.exchange(true, std::memory_order_acq_rel)) {
    if (!SDL_PauseAudioDevice(sdl_device_)) {
      REXAPU_WARN("SDL_PauseAudioDevice() failed during audio shutdown: {}", SDL_GetError());
    }
  }
  if (sdl_stream_) {
    SDL_DestroyAudioStream(sdl_stream_);
    sdl_stream_ = nullptr;
    sdl_device_ = 0;
  }
  if (sdl_initialized_) {
    SDL_QuitSubSystem(SDL_INIT_AUDIO);
    sdl_initialized_ = false;
  }
  output_paused_.store(false, std::memory_order_release);
  std::unique_lock<std::mutex> guard(frames_mutex_);
  while (!frames_unused_.empty()) {
    delete[] frames_unused_.top();
    frames_unused_.pop();
  }
  while (!frames_queued_.empty()) {
    delete[] frames_queued_.front();
    frames_queued_.pop();
  }
}

/// How long the output must be continuously starved before the host device is
/// parked. Long enough that ordinary buffer-to-buffer gaps never trigger it.
static constexpr uint64_t kIdlePauseDelayMs = 2000;

void SDLAudioDriver::PauseOutputIfIdle() {
  if (!sdl_device_) {
    return;
  }
  if (HasQueuedFrames()) {
    starved_since_ms_.store(0, std::memory_order_release);
    return;
  }

  // Only pause after *sustained* silence. The queue drains to empty between
  // buffers as a matter of course, so pausing on the first starved callback
  // meant pause/resume on essentially every buffer -- 5,580 cycles in one
  // session -- and each transition is a discontinuity on a live device, which
  // is audible as crackling. Genuine idle (menus, loading) still parks the
  // device; normal playback no longer touches it.
  const auto now_ms = static_cast<uint64_t>(
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now().time_since_epoch())
          .count());
  uint64_t since = starved_since_ms_.load(std::memory_order_acquire);
  if (since == 0) {
    starved_since_ms_.store(now_ms, std::memory_order_release);
    return;
  }
  if (now_ms - since < kIdlePauseDelayMs) {
    return;
  }

  bool expected = false;
  if (!output_paused_.compare_exchange_strong(expected, true, std::memory_order_acq_rel)) {
    return;
  }

  if (!SDL_PauseAudioDevice(sdl_device_)) {
    output_paused_.store(false, std::memory_order_release);
    REXAPU_WARN("SDL_PauseAudioDevice() failed while idling audio output: {}", SDL_GetError());
    return;
  }

  REXAPU_DEBUG("SDLAudioDriver: paused host audio output while idle");
}

void SDLAudioDriver::ResumeOutputIfNeeded() {
  if (!sdl_device_) {
    return;
  }
  // Fresh audio: the starvation window restarts from here.
  starved_since_ms_.store(0, std::memory_order_release);

  if (!output_paused_.exchange(false, std::memory_order_acq_rel)) {
    return;
  }

  if (!SDL_ResumeAudioDevice(sdl_device_)) {
    output_paused_.store(true, std::memory_order_release);
    REXAPU_ERROR("SDL_ResumeAudioDevice() failed while resuming audio output: {}", SDL_GetError());
    return;
  }

  REXAPU_DEBUG("SDLAudioDriver: resumed host audio output");
}

bool SDLAudioDriver::HasQueuedFrames() {
  std::unique_lock<std::mutex> guard(frames_mutex_);
  return !frames_queued_.empty();
}

void SDLAudioDriver::SDLCallback(void* userdata, SDL_AudioStream* stream, int additional_amount,
                                 [[maybe_unused]] int total_amount) {
  SCOPE_profile_cpu_f("apu");
  if (!userdata || !stream) {
    REXAPU_ERROR("SDLAudioDriver::SDLCallback called with nullptr.");
    return;
  }
  const auto driver = static_cast<SDLAudioDriver*>(userdata);
  const int sample_count =
      static_cast<int>(channel_samples_ * std::max<uint8_t>(driver->sdl_device_channels_, 1));
  const int len = static_cast<int>(sizeof(float) * sample_count);
  float* data = SDL_stack_alloc(float, sample_count);
  if (!data) {
    REXAPU_ERROR("SDLAudioDriver::SDLCallback failed to allocate {} samples", sample_count);
    return;
  }
  // Snapshot once. A change mid-callback would split the frame across two mixes.
  const StereoFold fold = GetStereoFold();
  const SurroundMix mix = GetSurroundMix();
  const float gain = GetOutputGain();

  bool output_starved = false;
  while (additional_amount > 0) {
    static uint32_t sdl_callback_count = 0;
    float* buffer = nullptr;
    {
      std::unique_lock<std::mutex> guard(driver->frames_mutex_);
      if (!driver->frames_queued_.empty()) {
        buffer = driver->frames_queued_.front();
        driver->frames_queued_.pop();
      }
    }

    if (!buffer) {
      if (sdl_callback_count < 10) {
        REXAPU_DEBUG("SDLCallback: no frames queued (silence)");
        sdl_callback_count++;
      }
      output_starved = true;
      std::memset(data, 0, len);
      if (!SDL_PutAudioStreamData(stream, data, len)) {
        REXAPU_ERROR("SDL_PutAudioStreamData() failed while filling silence: {}", SDL_GetError());
        break;
      }
      additional_amount -= len;
      continue;
    }

    if (REXCVAR_GET(audio_mute)) {
      std::memset(data, 0, len);
    } else {
      switch (driver->sdl_device_channels_) {
        case 2:
          conversion::sequential_6_BE_to_interleaved_2_LE(data, buffer, channel_samples_, fold,
                                                          gain);
          break;
        case 6:
          conversion::sequential_6_BE_to_interleaved_6_LE(data, buffer, channel_samples_, mix,
                                                          gain);
          break;
        default:
          assert_unhandled_case(driver->sdl_device_channels_);
          break;
      }
    }
    if (!SDL_PutAudioStreamData(stream, data, len)) {
      REXAPU_ERROR("SDL_PutAudioStreamData() failed: {}", SDL_GetError());
      std::unique_lock<std::mutex> guard(driver->frames_mutex_);
      driver->frames_unused_.push(buffer);
      break;
    }

    {
      std::unique_lock<std::mutex> guard(driver->frames_mutex_);
      driver->frames_unused_.push(buffer);
    }

    auto ret = driver->semaphore_->Release(1, nullptr);
    assert_true(ret);
    additional_amount -= len;
  }

  if (output_starved) {
    driver->PauseOutputIfIdle();
  }

  SDL_stack_free(data);
}

}  // namespace rex::audio::sdl
