// refii — the guest's audio output, as a Godot audio effect.
//
// Godot's buses carry speaker PAIRS (front, centre/LFE, rear, side), and the only code that
// can write a given pair is an AudioEffectInstance's `_process`, which Godot exposes to
// GDExtension and nothing else. So this library — the one .NET already holds as the host
// bridge — is also a GDExtension: Godot opens the same file, gets the same module, and the
// effect below reads the frame ring in-process. No second library, no hand-off.
//
// One effect on one bus. Godot creates one instance per speaker pair and calls them in pair
// order every mix step. Pair 0 pulls the guest frames the step needs and converts them once
// — the SDK's ITU fold for a stereo device, a pass-through for 5.1 — each pair copies its two
// channels out, and the last pair releases what the step consumed. Pulling IS the guest's
// clock: every frame taken is a credit, so the guest runs at the device's rate. Nothing here
// blocks; an empty ring is written as silence and counted.
//
// Written against the raw C interface rather than godot-cpp: godot-cpp has no 4.7 line while
// this project runs 4.7.1, and the surface needed is one class pair with three virtuals.

#include "host_abi.h"

#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <vector>

#include <godot/gdextension_interface.h>
#include <rex/audio/conversion.h>
#include <rex/audio/downmix.h>
#include <rex/logging.h>

#include "core/audio_state.h"

namespace {

// ---------------------------------------------------------------------------------
// The output stage
// ---------------------------------------------------------------------------------

/// Speaker pairs to channels. A device with a centre and rears (5.1, 7.1: three or more
/// pairs) takes the guest's six channels straight; stereo and 3.1 take the fold. 3.1 could
/// carry the centre discretely, but that is a third mix; it gets the fold on its front pair.
uint32_t ChannelsForPairs(uint32_t pairs) { return pairs >= 3 ? 6u : 2u; }

struct Output {
    std::atomic<uint32_t> pairs{1};
    std::atomic<uint32_t> mix_rate{0};
    std::atomic<uint32_t> channels{2};
    std::atomic<uint64_t> frames_pulled{0};
    std::atomic<uint64_t> underrun_steps{0};

    // Audio thread only from here down.
    std::vector<float> staging;  ///< Interleaved, `stride` floats per sample.
    uint32_t stride = 0;
    size_t staged = 0;           ///< Samples converted and not yet released.
    uint32_t step_calls = 0;     ///< Which pair this process call is, within the step.
    bool heard_guest = false;    ///< A pop has succeeded, so an empty ring is an underrun.
    alignas(16) float raw[refii::vfs::kGuestAudioFloatsPerFrame];
};

Output& Out() {
    static Output output;
    return output;
}

/// Makes `count` converted samples available at the front of the staging buffer.
void Fill(Output& o, size_t count) {
    const uint32_t channels = ChannelsForPairs(o.pairs.load(std::memory_order_relaxed));
    if (channels != o.stride) {
        o.stride = channels;
        o.staged = 0;
        o.channels.store(channels, std::memory_order_relaxed);
    }

    const size_t need = (count + refii::vfs::kGuestAudioSamplesPerFrame) * o.stride;
    if (o.staging.size() < need) {
        // Grows on the first steps after a device change and then never again.
        o.staging.resize(need, 0.0f);
    }

    while (o.staged < count) {
        if (refii::vfs::PopHostAudioFrames(o.raw, 1) == 0) {
            std::fill(o.staging.begin() + static_cast<std::ptrdiff_t>(o.staged * o.stride),
                      o.staging.begin() + static_cast<std::ptrdiff_t>(count * o.stride), 0.0f);
            o.staged = count;
            if (o.heard_guest) {
                o.underrun_steps.fetch_add(1, std::memory_order_relaxed);
            }
            return;
        }

        o.heard_guest = true;
        float* at = o.staging.data() + o.staged * o.stride;
        if (o.stride == 2) {
            rex::audio::conversion::sequential_6_BE_to_interleaved_2_LE(
                at, o.raw, refii::vfs::kGuestAudioSamplesPerFrame, rex::audio::GetStereoFold(),
                rex::audio::GetOutputGain());
        } else {
            rex::audio::conversion::sequential_6_BE_to_interleaved_6_LE(
                at, o.raw, refii::vfs::kGuestAudioSamplesPerFrame, rex::audio::GetSurroundMix(),
                rex::audio::GetOutputGain());
        }
        o.staged += refii::vfs::kGuestAudioSamplesPerFrame;
        o.frames_pulled.fetch_add(1, std::memory_order_relaxed);
    }
}

/// Drops the `count` samples every pair has now copied.
void Release(Output& o, size_t count) {
    if (o.staged <= count) {
        o.staged = 0;
        return;
    }
    std::memmove(o.staging.data(), o.staging.data() + count * o.stride,
                 (o.staged - count) * o.stride * sizeof(float));
    o.staged -= count;
}

/// One pair's share of one mix step. `src` and `dst` are Godot AudioFrames: two floats each.
void Process(const float* src, float* dst, int32_t frame_count) {
    Output& o = Out();
    const uint32_t pairs = std::max(1u, o.pairs.load(std::memory_order_relaxed));
    if (o.step_calls >= pairs) {
        o.step_calls = 0;
    }
    const uint32_t pair = o.step_calls++;
    if (frame_count <= 0 || src == nullptr || dst == nullptr) {
        return;
    }
    const size_t count = static_cast<size_t>(frame_count);

    if (pair == 0) {
        Fill(o, count);
    }

    // Stage 2 (per-voice playback) owns the sound: the mix is still pulled, so the clock
    // runs, and written as silence.
    const bool muted = refii::vfs::VoiceCaptureOwnsOutput();
    const uint32_t stride = o.stride;
    const bool carries = (2u * pair + 1u) < stride && !muted && o.staged >= count;

    if (carries) {
        const float* s = o.staging.data() + 2u * pair;
        for (size_t i = 0; i < count; i++, s += stride) {
            dst[2 * i] = src[2 * i] + s[0];
            dst[(2 * i) + 1] = src[(2 * i) + 1] + s[1];
        }
    } else if (dst != src) {
        std::memcpy(dst, src, count * 2 * sizeof(float));
    }

    if (pair + 1 == pairs) {
        Release(o, count);
    }
}

// ---------------------------------------------------------------------------------
// GDExtension plumbing
// ---------------------------------------------------------------------------------

GDExtensionClassLibraryPtr g_library = nullptr;
GDExtensionInterfaceClassdbRegisterExtensionClass6 g_register_class = nullptr;
GDExtensionInterfaceClassdbUnregisterExtensionClass g_unregister_class = nullptr;
GDExtensionInterfaceClassdbConstructObject3 g_construct_object = nullptr;
GDExtensionInterfaceObjectSetInstance g_object_set_instance = nullptr;
GDExtensionInterfaceRefSetObject g_ref_set_object = nullptr;
GDExtensionInterfaceStringNameNewWithLatin1Chars g_string_name_new = nullptr;
GDExtensionInterfaceVariantGetPtrDestructor g_variant_get_ptr_destructor = nullptr;
GDExtensionPtrDestructor g_string_name_destroy = nullptr;

/// A StringName is one pointer wide on every platform Godot ships.
struct Name {
    alignas(8) uint8_t storage[8];
};

Name g_effect_class_name;    // "RefiiGuestAudioEffect"
Name g_instance_class_name;  // "RefiiGuestAudioEffectInstance"
Name g_audio_effect_name;    // "AudioEffect"
Name g_audio_instance_name;  // "AudioEffectInstance"
bool g_registered = false;

/// Virtual method hashes, from the 4.7.1 extension API (`--dump-extension-api`). Godot asks
/// for each virtual by name AND hash; dispatching on the hash is what godot-cpp does too.
constexpr uint32_t kHashInstantiate = 1659796816u;    // AudioEffect::_instantiate
constexpr uint32_t kHashProcess = 1649997291u;        // AudioEffectInstance::_process
constexpr uint32_t kHashProcessSilence = 36873697u;   // AudioEffectInstance::_process_silence

// Addresses only; Godot hands them back as the class and virtual-call user data.
int g_effect_class_tag;
int g_instance_class_tag;
int g_instantiate_tag;
int g_process_tag;
int g_process_silence_tag;

/// What `object_set_instance` attaches. Nothing per object is needed — the output is one —
/// but Godot requires an instance pointer and frees it through `free_instance_func`.
struct Instance {
    int kind;
};

GDExtensionObjectPtr Create(const Name& parent, const Name& self, int kind) {
    GDExtensionObjectPtr object = g_construct_object(&parent);
    if (object == nullptr) {
        return nullptr;
    }
    g_object_set_instance(object, &self, new Instance{kind});
    return object;
}

GDExtensionObjectPtr CreateEffect(void*, GDExtensionBool) {
    return Create(g_audio_effect_name, g_effect_class_name, 0);
}

GDExtensionObjectPtr CreateEffectInstance(void*, GDExtensionBool) {
    return Create(g_audio_instance_name, g_instance_class_name, 1);
}

void FreeInstance(void*, GDExtensionClassInstancePtr instance) {
    delete static_cast<Instance*>(instance);
}

void* GetVirtualCallData(void* class_userdata, GDExtensionConstStringNamePtr, uint32_t hash) {
    if (class_userdata == &g_effect_class_tag && hash == kHashInstantiate) {
        return &g_instantiate_tag;
    }
    if (class_userdata == &g_instance_class_tag) {
        if (hash == kHashProcess) {
            return &g_process_tag;
        }
        if (hash == kHashProcessSilence) {
            return &g_process_silence_tag;
        }
    }
    return nullptr;
}

void CallVirtual(GDExtensionClassInstancePtr, GDExtensionConstStringNamePtr, void* tag,
                 const GDExtensionConstTypePtr* args, GDExtensionTypePtr ret) {
    if (tag == &g_instantiate_tag) {
        // Constructing our own class by name routes through CreateEffectInstance above.
        g_ref_set_object(ret, g_construct_object(&g_instance_class_name));
    } else if (tag == &g_process_tag) {
        // Pointer arguments arrive as pointers to the pointer; integers are 64-bit.
        const float* src = *static_cast<const float* const*>(args[0]);
        float* dst = *static_cast<float* const*>(args[1]);
        const auto frames = static_cast<int32_t>(*static_cast<const int64_t*>(args[2]));
        Process(src, dst, frames);
    } else if (tag == &g_process_silence_tag) {
        // Always run: the pull is the guest's clock whether or not the bus is audible.
        *static_cast<GDExtensionBool*>(ret) = 1;
    }
}

void RegisterClass(const Name& self, const Name& parent, int* tag,
                   GDExtensionClassCreateInstance3 create) {
    GDExtensionClassCreationInfo6 info{};
    info.is_exposed = 1;
    info.create_instance_func = create;
    info.free_instance_func = &FreeInstance;
    info.get_virtual_call_data_func = &GetVirtualCallData;
    info.call_virtual_with_data_func = &CallVirtual;
    info.class_userdata = tag;
    g_register_class(g_library, &self, &parent, &info);
}

void Initialize(void*, GDExtensionInitializationLevel level) {
    if (level != GDEXTENSION_INITIALIZATION_SCENE || g_registered) {
        return;
    }
    g_string_name_new(&g_effect_class_name, "RefiiGuestAudioEffect", 1);
    g_string_name_new(&g_instance_class_name, "RefiiGuestAudioEffectInstance", 1);
    g_string_name_new(&g_audio_effect_name, "AudioEffect", 1);
    g_string_name_new(&g_audio_instance_name, "AudioEffectInstance", 1);

    RegisterClass(g_effect_class_name, g_audio_effect_name, &g_effect_class_tag, &CreateEffect);
    RegisterClass(g_instance_class_name, g_audio_instance_name, &g_instance_class_tag,
                  &CreateEffectInstance);
    g_registered = true;
    REXLOG_INFO("refii audio: RefiiGuestAudioEffect registered with Godot");
}

void Deinitialize(void*, GDExtensionInitializationLevel level) {
    if (level != GDEXTENSION_INITIALIZATION_SCENE || !g_registered) {
        return;
    }
    refii::vfs::SetHostAudioConsumerAttached(false);
    g_unregister_class(g_library, &g_instance_class_name);
    g_unregister_class(g_library, &g_effect_class_name);
    for (Name* name : {&g_effect_class_name, &g_instance_class_name, &g_audio_effect_name,
                       &g_audio_instance_name}) {
        g_string_name_destroy(name);
    }
    g_registered = false;
}

template <typename Fn>
bool Load(GDExtensionInterfaceGetProcAddress get_proc, const char* name, Fn& out) {
    out = reinterpret_cast<Fn>(get_proc(name));
    if (out == nullptr) {
        REXLOG_ERROR("refii audio: Godot does not provide `{}`; the audio effect cannot load",
                     name);
        return false;
    }
    return true;
}

}  // namespace

extern "C" {

/// The GDExtension entry point, named in the manifest the host writes (Engine_AudioOutput).
REFII_HOST_ABI_EXPORT GDExtensionBool refii_godot_audio_init(
    GDExtensionInterfaceGetProcAddress get_proc, GDExtensionClassLibraryPtr library,
    GDExtensionInitialization* initialization) {
    g_library = library;
    const bool loaded =
        Load(get_proc, "classdb_register_extension_class6", g_register_class) &&
        Load(get_proc, "classdb_unregister_extension_class", g_unregister_class) &&
        Load(get_proc, "classdb_construct_object3", g_construct_object) &&
        Load(get_proc, "object_set_instance", g_object_set_instance) &&
        Load(get_proc, "ref_set_object", g_ref_set_object) &&
        Load(get_proc, "string_name_new_with_latin1_chars", g_string_name_new) &&
        Load(get_proc, "variant_get_ptr_destructor", g_variant_get_ptr_destructor);
    if (!loaded) {
        return 0;
    }
    g_string_name_destroy = g_variant_get_ptr_destructor(GDEXTENSION_VARIANT_TYPE_STRING_NAME);

    initialization->minimum_initialization_level = GDEXTENSION_INITIALIZATION_SCENE;
    initialization->userdata = nullptr;
    initialization->initialize = &Initialize;
    initialization->deinitialize = &Deinitialize;
    return 1;
}

/// The bus is up: how many speaker pairs it has (Godot's bus channel count — 1 stereo, 2 for
/// 3.1, 3 for 5.1, 4 for 7.1) and the mix rate. Attaches the output to the ring.
REFII_HOST_ABI_EXPORT void RefiiHostAudioOutputConfigure(uint32_t speaker_pairs,
                                                         uint32_t mix_rate) {
    Output& o = Out();
    const uint32_t pairs = std::max(1u, speaker_pairs);
    o.pairs.store(pairs, std::memory_order_relaxed);
    o.mix_rate.store(mix_rate, std::memory_order_relaxed);
    o.channels.store(ChannelsForPairs(pairs), std::memory_order_relaxed);
    refii::vfs::SetHostAudioConsumerAttached(true);

    REXLOG_INFO("refii audio: output configured for {} speaker pair(s) at {} Hz: the guest's 5.1 "
                "is {}",
                pairs, mix_rate, ChannelsForPairs(pairs) == 6 ? "passed through" : "folded to stereo");
    if (mix_rate != refii::vfs::kGuestAudioSampleRate) {
        // The effect copies samples one to one. A bus at another rate plays the guest at the
        // wrong speed, which is a project setting (audio/driver/mix_rate), not a runtime fix.
        REXLOG_ERROR("refii audio: Godot mixes at {} Hz but the guest renders at {} Hz; set "
                     "audio/driver/mix_rate to {}",
                     mix_rate, refii::vfs::kGuestAudioSampleRate, refii::vfs::kGuestAudioSampleRate);
    }
}

/// The bus is going away; the guest's frames are dropped rather than waited for.
REFII_HOST_ABI_EXPORT void RefiiHostAudioOutputDetach(void) {
    refii::vfs::SetHostAudioConsumerAttached(false);
}

REFII_HOST_ABI_EXPORT void RefiiHostAudioOutputStats(uint64_t* out_frames_pulled,
                                                     uint64_t* out_underrun_steps,
                                                     uint32_t* out_channels) {
    Output& o = Out();
    if (out_frames_pulled != nullptr) {
        *out_frames_pulled = o.frames_pulled.load(std::memory_order_relaxed);
    }
    if (out_underrun_steps != nullptr) {
        *out_underrun_steps = o.underrun_steps.load(std::memory_order_relaxed);
    }
    if (out_channels != nullptr) {
        *out_channels = o.channels.load(std::memory_order_relaxed);
    }
}

/// The linear weight the output stage gives a front channel: the fold's `scale` times the
/// output gain for a stereo device, the gain alone for a pass-through (the SurroundMix's front
/// weight is an implicit 1.0). Sound the host decodes itself — movies — is scaled by this so
/// it sits where the guest's mix sits; on the console both went through the same mastering
/// voice.
REFII_HOST_ABI_EXPORT float RefiiHostAudioFrontGain(uint32_t channels) {
    const float gain = rex::audio::GetOutputGain();
    return channels >= 6 ? gain : rex::audio::GetStereoFold().scale * gain;
}

}  // extern "C"
