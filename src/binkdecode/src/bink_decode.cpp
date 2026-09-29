// refii - Bink video decoding for Godot
//
// Plays the game's .bik files WITHOUT the guest. Until now the only way to see a startup
// video was to read the planes the recompiled guest had decoded into its own memory,
// which coupled video to the guest reaching its render path, to its double-buffer timing,
// and to a render thread that is currently parked. Decoding here removes all of that:
// Godot hands over the bytes it already has from the bank and gets frames back.
//
// FFmpeg's Bink decoder does the work. rexglue ships FFmpeg but builds it audio-only for
// XMA/WMA Pro, so the decoder was compiled out; cmake/ffmpeg-bink/config.h turns it on
// and the top-level CMakeLists adds bink.c and the DSP helpers it needs.
//
// The CONTAINER is parsed here rather than by libavformat. libavformat is not built at
// all in this tree and is a large dependency to add for a format that is a fixed header
// plus a table of frame offsets — the layout below is taken from the SDK's own
// libavformat/bink.c so it stays honest about what it is reproducing.

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/imgutils.h>
}

// Reached directly rather than through avcodec_find_decoder: the SDK's generated
// codec_list.c does not list Bink, and avcodec_open2 takes an AVCodec outright, so there
// is no reason to edit a pristine generated file.
extern "C" const AVCodec ff_bink_decoder;
extern "C" const AVCodec ff_binkaudio_dct_decoder;
extern "C" const AVCodec ff_binkaudio_rdft_decoder;

namespace {

uint32_t ReadLe32(const uint8_t* p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) |
           ((uint32_t)p[3] << 24);
}

// Track flags, from the SDK's libavformat/bink.c.
constexpr uint16_t kAudioStereo = 0x2000;
constexpr uint16_t kAudioUseDct = 0x1000;

/// One audio track as the container describes it, plus its decoder once selected.
struct BinkAudioTrack {
    uint32_t id = 0;
    uint32_t sample_rate = 0;
    uint16_t flags = 0;
    bool selected = false;
    float volume = 1.0f;

    AVCodecContext* codec = nullptr;
    std::vector<float> pending;  // interleaved stereo, decoded and not yet taken

    int channels() const { return (flags & kAudioStereo) != 0 ? 2 : 1; }
};

/// One open movie: the container index plus a live decoder.
struct BinkMovie {
    std::vector<uint8_t> data;
    std::vector<uint32_t> frame_offsets;  // one per frame, plus a final end offset
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t frame_count = 0;
    uint32_t fps_num = 0;
    uint32_t fps_den = 0;
    uint32_t audio_tracks = 0;
    uint32_t next_frame = 0;
    std::vector<BinkAudioTrack> tracks;  // in FILE order, which is the packet order
    uint32_t audio_rate = 0;             // of the selected tracks; 0 when none
    AVFrame* audio_frame = nullptr;

    AVCodecContext* codec = nullptr;
    AVFrame* frame = nullptr;
    AVPacket* packet = nullptr;
    std::string error;

    ~BinkMovie() {
        for (BinkAudioTrack& track : tracks) {
            if (track.codec != nullptr) {
                avcodec_free_context(&track.codec);
            }
        }
        if (audio_frame != nullptr) {
            av_frame_free(&audio_frame);
        }
        if (packet != nullptr) {
            av_packet_free(&packet);
        }
        if (frame != nullptr) {
            av_frame_free(&frame);
        }
        if (codec != nullptr) {
            avcodec_free_context(&codec);
        }
    }
};

bool ParseHeader(BinkMovie& movie) {
    const std::vector<uint8_t>& d = movie.data;
    if (d.size() < 48) {
        movie.error = "shorter than a Bink header";
        return false;
    }

    // 'BIK' plus a revision byte. Bink 2 ("KB2") is a different codec FFmpeg does not
    // implement, so it is refused here rather than failing obscurely inside the decoder.
    if (!(d[0] == 'B' && d[1] == 'I' && d[2] == 'K')) {
        movie.error = "not a Bink 1 file";
        return false;
    }

    movie.frame_count = ReadLe32(&d[8]);
    movie.width = ReadLe32(&d[20]);
    movie.height = ReadLe32(&d[24]);
    movie.fps_num = ReadLe32(&d[28]);
    movie.fps_den = ReadLe32(&d[32]);

    if (movie.frame_count == 0 || movie.frame_count > 1000000 || movie.width == 0 ||
        movie.height == 0 || movie.width > 7680 || movie.height > 4800 ||
        movie.fps_num == 0 || movie.fps_den == 0) {
        movie.error = "implausible Bink header";
        return false;
    }

    // Header is 44 bytes, then audio track tables, then the frame offset table. Each
    // track contributes 4 bytes of max-decoded-size, 4 bytes of rate/flags and 4 bytes of
    // id — matching the demuxer's three passes over num_audio_tracks.
    const uint32_t audio_tracks = ReadLe32(&d[40]);
    movie.audio_tracks = audio_tracks;
    if (audio_tracks > 256) {
        movie.error = "implausible audio track count";
        return false;
    }

    if (d.size() < 44 + ((std::size_t)audio_tracks * 12)) {
        movie.error = "truncated audio track table";
        return false;
    }

    // Three passes, as the demuxer reads them: max decoded size, then rate + flags, then id.
    movie.tracks.resize(audio_tracks);
    for (uint32_t i = 0; i < audio_tracks; ++i) {
        const uint8_t* rate = &d[44 + ((std::size_t)audio_tracks * 4) + ((std::size_t)i * 4)];
        BinkAudioTrack& track = movie.tracks[i];
        track.sample_rate = (uint32_t)rate[0] | ((uint32_t)rate[1] << 8);
        track.flags = (uint16_t)((uint32_t)rate[2] | ((uint32_t)rate[3] << 8));
        track.id = ReadLe32(&d[44 + ((std::size_t)audio_tracks * 8) + ((std::size_t)i * 4)]);
    }

    std::size_t cursor = 44 + ((std::size_t)audio_tracks * 12);

    // frame_count + 1 entries: the extra one bounds the last frame.
    const std::size_t table_bytes = ((std::size_t)movie.frame_count + 1) * 4;
    if (cursor + table_bytes > d.size()) {
        movie.error = "frame table runs past the end of the file";
        return false;
    }

    movie.frame_offsets.reserve(movie.frame_count + 1);
    for (std::size_t i = 0; i <= movie.frame_count; ++i) {
        // The low bit is a keyframe flag, not part of the offset.
        movie.frame_offsets.push_back(ReadLe32(&d[cursor + (i * 4)]) & ~1u);
    }

    if (std::getenv("REFII_BINK_DEBUG") != nullptr) {
        std::fprintf(stderr,
                     "[bink] tracks=%u table_at=%zu table_end=%zu first=%u second=%u "
                     "last=%u size=%zu\n",
                     audio_tracks, cursor, cursor + table_bytes,
                     movie.frame_offsets[0], movie.frame_offsets[1],
                     movie.frame_offsets[movie.frame_count], d.size());
    }

    return true;
}

bool OpenCodec(BinkMovie& movie) {
    movie.codec = avcodec_alloc_context3(&ff_bink_decoder);
    if (movie.codec == nullptr) {
        movie.error = "could not allocate a Bink decoder";
        return false;
    }

    movie.codec->width = (int)movie.width;
    movie.codec->height = (int)movie.height;

    // The decoder takes its bitstream VERSION from the codec tag — `c->version =
    // avctx->codec_tag >> 24` in decode_init — so the file's 'BIKi' magic has to be
    // handed over as the tag. Without it version is 0, every frame parses as the wrong
    // variant, and send_packet returns AVERROR_INVALIDDATA with no further explanation.
    // It also decides swap_planes (version >= 'h'), so getting it wrong would swap U and
    // V even if frames did parse.
    movie.codec->codec_tag = ReadLe32(movie.data.data());

    // Extradata is the VIDEO FLAGS dword at offset 36, not the file tag at offset 0.
    // The demuxer reads it with ff_get_extradata after fps_den and before the audio
    // track count, and the decoder uses those flags to pick its bitstream features —
    // handing it the 'BIKi' tag instead parses as the wrong feature set and every frame
    // fails.
    movie.codec->extradata = (uint8_t*)av_mallocz(4 + AV_INPUT_BUFFER_PADDING_SIZE);
    if (movie.codec->extradata == nullptr) {
        movie.error = "out of memory for extradata";
        return false;
    }
    std::memcpy(movie.codec->extradata, movie.data.data() + 36, 4);
    movie.codec->extradata_size = 4;

    if (avcodec_open2(movie.codec, &ff_bink_decoder, nullptr) < 0) {
        movie.error = "avcodec_open2 refused the Bink stream";
        return false;
    }

    movie.frame = av_frame_alloc();
    movie.packet = av_packet_alloc();
    if (movie.frame == nullptr || movie.packet == nullptr) {
        movie.error = "out of memory for decode buffers";
        return false;
    }

    return true;
}

bool OpenAudioCodec(const BinkMovie& movie, BinkAudioTrack& track) {
    const AVCodec* codec = (track.flags & kAudioUseDct) != 0 ? &ff_binkaudio_dct_decoder
                                                              : &ff_binkaudio_rdft_decoder;
    track.codec = avcodec_alloc_context3(codec);
    if (track.codec == nullptr) {
        return false;
    }

    track.codec->sample_rate = (int)track.sample_rate;
    track.codec->channels = track.channels();

    // The file tag, as the demuxer hands it over: the decoder reads revision 'b' off byte 3.
    track.codec->extradata = (uint8_t*)av_mallocz(4 + AV_INPUT_BUFFER_PADDING_SIZE);
    if (track.codec->extradata == nullptr) {
        avcodec_free_context(&track.codec);
        return false;
    }
    std::memcpy(track.codec->extradata, movie.data.data(), 4);
    track.codec->extradata_size = 4;

    if (avcodec_open2(track.codec, codec, nullptr) < 0) {
        avcodec_free_context(&track.codec);
        return false;
    }

    return true;
}

/// Decodes one track's packet for the current frame onto its pending queue, as stereo.
void DecodeAudioPacket(BinkMovie& movie, BinkAudioTrack& track, const uint8_t* data,
                       uint32_t size) {
    // Four bytes are the packet's own sample count; anything shorter carries no audio.
    if (track.codec == nullptr || size <= 4 || av_new_packet(movie.packet, (int)size) < 0) {
        return;
    }

    std::memcpy(movie.packet->data, data, size);
    const int sent = avcodec_send_packet(track.codec, movie.packet);
    av_packet_unref(movie.packet);
    if (sent < 0) {
        return;
    }

    AVFrame* frame = movie.audio_frame;
    while (avcodec_receive_frame(track.codec, frame) >= 0) {
        const int channels = track.codec->channels;
        const bool planar = track.codec->sample_fmt == AV_SAMPLE_FMT_FLTP;
        const std::size_t at = track.pending.size();
        track.pending.resize(at + ((std::size_t)frame->nb_samples * 2));
        float* out = track.pending.data() + at;

        for (int i = 0; i < frame->nb_samples; ++i) {
            float left = 0.0f;
            float right = 0.0f;
            if (planar) {
                left = ((const float*)frame->extended_data[0])[i];
                right = channels > 1 ? ((const float*)frame->extended_data[1])[i] : left;
            } else {
                const float* samples = (const float*)frame->extended_data[0];
                left = samples[(std::size_t)i * channels];
                right = channels > 1 ? samples[((std::size_t)i * channels) + 1] : left;
            }

            out[(std::size_t)i * 2] = left;
            out[((std::size_t)i * 2) + 1] = right;
        }

        av_frame_unref(frame);
    }
}

}  // namespace

extern "C" {

/// Opens a .bik from memory. Returns an opaque handle, or null with `error` filled in.
///
/// The bytes are COPIED: Godot reads them out of a bank archive and has no reason to keep
/// the buffer alive for the life of playback.
void* RefiiBinkOpen(const unsigned char* data, int size, char* error, int error_capacity) {
    const auto fail = [&](const char* message) -> void* {
        if (error != nullptr && error_capacity > 0) {
            std::snprintf(error, (std::size_t)error_capacity, "%s", message);
        }
        return nullptr;
    };

    if (data == nullptr || size <= 0) {
        return fail("no data");
    }

    if (std::getenv("REFII_BINK_DEBUG") != nullptr) {
        av_log_set_level(AV_LOG_VERBOSE);
    }

    auto* movie = new BinkMovie();
    movie->data.assign(data, data + size);

    if (!ParseHeader(*movie) || !OpenCodec(*movie)) {
        const std::string message = movie->error;
        delete movie;
        return fail(message.c_str());
    }

    return movie;
}

/// Geometry and length. Any out pointer may be null.
void RefiiBinkInfo(void* handle, int* out_width, int* out_height, int* out_frames,
                   int* out_fps_num, int* out_fps_den) {
    auto* movie = static_cast<BinkMovie*>(handle);
    if (movie == nullptr) {
        return;
    }

    if (out_width != nullptr) *out_width = (int)movie->width;
    if (out_height != nullptr) *out_height = (int)movie->height;
    if (out_frames != nullptr) *out_frames = (int)movie->frame_count;
    if (out_fps_num != nullptr) *out_fps_num = (int)movie->fps_num;
    if (out_fps_den != nullptr) *out_fps_den = (int)movie->fps_den;
}

/// Decodes the next frame into three tightly packed planes.
///
/// Planes rather than RGB: the caller uploads them as three 8-bit textures and converts
/// on the GPU, which is both cheaper and gives chroma real filtering instead of
/// nearest-neighbour integer scaling. `out_desc` receives luma w/h then chroma w/h.
///
/// Returns 1 on success, 0 at the end of the movie or on a decode error.
int RefiiBinkNextFrame(void* handle, unsigned char* out_y, int y_capacity,
                       unsigned char* out_u, int u_capacity, unsigned char* out_v,
                       int v_capacity, int* out_desc) {
    auto* movie = static_cast<BinkMovie*>(handle);
    if (movie == nullptr || movie->next_frame >= movie->frame_count) {
        return 0;
    }

    const uint32_t begin = movie->frame_offsets[movie->next_frame];
    const uint32_t end = movie->frame_offsets[movie->next_frame + 1];
    if (end <= begin || end > movie->data.size()) {
        return 0;
    }

    ++movie->next_frame;

    // A frame starts with its AUDIO packets, one per track: a 4-byte length followed by
    // that many bytes. The video bitstream is whatever is left. Handing the whole frame
    // to the video decoder feeds it audio and it returns AVERROR_INVALIDDATA with no
    // further explanation — which is exactly what happened, and looked like a container
    // parse error even though the frame table was correct.
    uint32_t video_begin = begin;
    for (uint32_t track = 0; track < movie->audio_tracks; ++track) {
        if (video_begin + 4 > end) {
            return 0;
        }

        const uint32_t audio_size = ReadLe32(movie->data.data() + video_begin);
        video_begin += 4;
        if (audio_size > end - video_begin) {
            return 0;
        }

        if (movie->tracks[track].selected) {
            DecodeAudioPacket(*movie, movie->tracks[track],
                              movie->data.data() + video_begin, audio_size);
        }

        video_begin += audio_size;
    }

    if (video_begin >= end) {
        return 0;
    }

    // Padded because FFmpeg's bitstream readers over-read the end of a packet by design.
    const int packet_size = (int)(end - video_begin);
    if (av_new_packet(movie->packet, packet_size) < 0) {
        return 0;
    }
    std::memcpy(movie->packet->data, movie->data.data() + video_begin,
                (std::size_t)packet_size);

    const int sent = avcodec_send_packet(movie->codec, movie->packet);
    av_packet_unref(movie->packet);
    if (sent < 0) {
        if (std::getenv("REFII_BINK_DEBUG") != nullptr) {
            std::fprintf(stderr, "[bink] send_packet -> %d\n", sent);
        }
        return 0;
    }

    const int got = avcodec_receive_frame(movie->codec, movie->frame);
    if (got < 0) {
        if (std::getenv("REFII_BINK_DEBUG") != nullptr) {
            std::fprintf(stderr, "[bink] receive_frame -> %d\n", got);
        }
        return 0;
    }

    AVFrame* frame = movie->frame;
    const int luma_w = frame->width;
    const int luma_h = frame->height;

    // Chroma size comes from the frame's own format rather than an assumed 2:1 ratio.
    const int chroma_w = AV_CEIL_RSHIFT(luma_w, 1);
    const int chroma_h = AV_CEIL_RSHIFT(luma_h, 1);

    if (out_desc != nullptr) {
        out_desc[0] = luma_w;
        out_desc[1] = luma_h;
        out_desc[2] = chroma_w;
        out_desc[3] = chroma_h;
    }

    if (out_y == nullptr || out_u == nullptr || out_v == nullptr) {
        av_frame_unref(frame);
        return 1;  // size probe
    }

    if (y_capacity < luma_w * luma_h || u_capacity < chroma_w * chroma_h ||
        v_capacity < chroma_w * chroma_h) {
        av_frame_unref(frame);
        return 0;
    }

    // Row by row: FFmpeg's linesize is not the width.
    const auto copy = [](unsigned char* dst, const uint8_t* src, int stride, int width,
                         int height) {
        for (int row = 0; row < height; ++row) {
            std::memcpy(dst + ((std::size_t)row * width),
                        src + ((std::size_t)row * stride), (std::size_t)width);
        }
    };

    copy(out_y, frame->data[0], frame->linesize[0], luma_w, luma_h);
    copy(out_u, frame->data[1], frame->linesize[1], chroma_w, chroma_h);
    copy(out_v, frame->data[2], frame->linesize[2], chroma_w, chroma_h);

    av_frame_unref(frame);
    return 1;
}

/// Rewinds to the first frame. The decoder is flushed so the next frame decodes as a
/// keyframe rather than against stale reference data.
void RefiiBinkRewind(void* handle) {
    auto* movie = static_cast<BinkMovie*>(handle);
    if (movie == nullptr) {
        return;
    }

    movie->next_frame = 0;
    if (movie->codec != nullptr) {
        avcodec_flush_buffers(movie->codec);
    }

    for (BinkAudioTrack& track : movie->tracks) {
        track.pending.clear();
        if (track.codec != nullptr) {
            avcodec_flush_buffers(track.codec);
        }
    }
}

/// How many audio tracks the file carries.
int RefiiBinkAudioTrackCount(void* handle) {
    auto* movie = static_cast<BinkMovie*>(handle);
    return movie == nullptr ? 0 : (int)movie->tracks.size();
}

/// One track by FILE index: its id, rate and channel count. Returns 0 out of range.
int RefiiBinkAudioTrackInfo(void* handle, int index, int* out_id, int* out_rate,
                            int* out_channels) {
    auto* movie = static_cast<BinkMovie*>(handle);
    if (movie == nullptr || index < 0 || (std::size_t)index >= movie->tracks.size()) {
        return 0;
    }

    const BinkAudioTrack& track = movie->tracks[(std::size_t)index];
    if (out_id != nullptr) *out_id = (int)track.id;
    if (out_rate != nullptr) *out_rate = (int)track.sample_rate;
    if (out_channels != nullptr) *out_channels = track.channels();
    return 1;
}

/// Selects the tracks to decode, by track ID — BinkSetSoundTrack's contract, including its
/// treatment of an id the file does not carry: dropped, not an error (BinkOpen's matching
/// loop, 0x832BBF48). Replaces any earlier selection. Returns how many ids matched.
///
/// Every selected track must share one sample rate, since they are mixed into one stream;
/// a track at a different rate from the first match is left out.
int RefiiBinkSelectAudio(void* handle, const int* ids, int count) {
    auto* movie = static_cast<BinkMovie*>(handle);
    if (movie == nullptr) {
        return 0;
    }

    for (BinkAudioTrack& track : movie->tracks) {
        track.selected = false;
        track.pending.clear();
    }
    movie->audio_rate = 0;

    if (movie->audio_frame == nullptr) {
        movie->audio_frame = av_frame_alloc();
        if (movie->audio_frame == nullptr) {
            return 0;
        }
    }

    int matched = 0;
    for (int i = 0; ids != nullptr && i < count; ++i) {
        for (BinkAudioTrack& track : movie->tracks) {
            if ((int)track.id != ids[i] || track.selected) {
                continue;
            }
            if (movie->audio_rate != 0 && track.sample_rate != movie->audio_rate) {
                continue;
            }
            if (track.codec == nullptr && !OpenAudioCodec(*movie, track)) {
                continue;
            }

            track.selected = true;
            movie->audio_rate = track.sample_rate;
            ++matched;
            break;
        }
    }

    return matched;
}

/// Sample rate of the selected tracks, 0 when none is selected.
int RefiiBinkAudioRate(void* handle) {
    auto* movie = static_cast<BinkMovie*>(handle);
    return movie == nullptr ? 0 : (int)movie->audio_rate;
}

/// Linear gain for one track by ID, applied when its samples are taken.
void RefiiBinkSetAudioVolume(void* handle, int id, float volume) {
    auto* movie = static_cast<BinkMovie*>(handle);
    if (movie == nullptr) {
        return;
    }

    for (BinkAudioTrack& track : movie->tracks) {
        if ((int)track.id == id) {
            track.volume = volume;
        }
    }
}

/// Takes the audio decoded so far: the selected tracks summed, as interleaved stereo float.
/// Returns the number of stereo FRAMES written, at most `capacity_frames`; the rest stays
/// queued. Audio is produced by RefiiBinkNextFrame, one video frame's worth per call.
int RefiiBinkTakeAudio(void* handle, float* out, int capacity_frames) {
    auto* movie = static_cast<BinkMovie*>(handle);
    if (movie == nullptr || out == nullptr || capacity_frames <= 0) {
        return 0;
    }

    // Only what EVERY selected track has: they decode in lockstep, so this is all of it.
    std::size_t frames = (std::size_t)capacity_frames;
    bool any = false;
    for (const BinkAudioTrack& track : movie->tracks) {
        if (track.selected) {
            frames = std::min(frames, track.pending.size() / 2);
            any = true;
        }
    }
    if (!any || frames == 0) {
        return 0;
    }

    std::memset(out, 0, frames * 2 * sizeof(float));
    for (BinkAudioTrack& track : movie->tracks) {
        if (!track.selected) {
            continue;
        }

        for (std::size_t i = 0; i < frames * 2; ++i) {
            out[i] += track.pending[i] * track.volume;
        }
        track.pending.erase(track.pending.begin(),
                            track.pending.begin() + (std::ptrdiff_t)(frames * 2));
    }

    return (int)frames;
}

void RefiiBinkClose(void* handle) { delete static_cast<BinkMovie*>(handle); }

}  // extern "C"
