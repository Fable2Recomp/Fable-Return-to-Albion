// XMA1/XMA2 audio decoding for Godot, via FFmpeg — the same shape as src/binkdecode.
//
// This replaces the hand-written decoder that used to reach into the Fable2AssetBrowser
// submodule (src/assetbrowser/shadow/.../refii_audio_abi.cpp -> Audio/XmaDecoder.*). That
// decoder was, in effect, a reimplementation of FFmpeg's own wmaprodec XMA path: same
// 34-byte extradata, same 2048 block_align, same FLTP planes converted to interleaved
// s16. Using ff_xma*_decoder directly deletes that duplication, and it also removes the
// only real reason refii_assetbrowser had to compile — which matters, because that
// submodule does not build with clang on ANY architecture (its wmaprodata.h is included
// inside an anonymous namespace and drags <stddef.h>'s `namespace std` in with it).
//
// The exported ABI is byte-for-byte the one Godot already calls, so
// Godot/src/Engine/audio/Engine_XmaDecoder.cs only changes its `Library` constant.

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

extern "C" {
#include <libavcodec/avcodec.h>
}

// Reached directly rather than through avcodec_find_decoder, exactly as bink_decode.cpp
// does: the SDK's generated codec_list.c does not list XMA, and avcodec_open2 takes an
// AVCodec outright, so there is no reason to edit a pristine generated file.
extern "C" const AVCodec ff_xma1_decoder;
extern "C" const AVCodec ff_xma2_decoder;

namespace {

// XMA is always carried in 2048-byte blocks; the old decoder hard-coded this too, and
// FFmpeg needs it as block_align to size its packets.
constexpr int kXmaBlockAlign = 2048;

constexpr uint16_t kFormatTagXma1 = 0x0165;
constexpr uint16_t kFormatTagXma2 = 0x0166;
constexpr uint16_t kFormatTagExtensible = 0xFFFE;

void WriteError(const std::string& message, char* error, size_t capacity) {
    if (error == nullptr || capacity == 0) {
        return;
    }
    const size_t copied = message.size() < capacity - 1 ? message.size() : capacity - 1;
    std::memcpy(error, message.data(), copied);
    error[copied] = '\0';
}

uint16_t ReadU16Le(const uint8_t* p) { return uint16_t(p[0] | (p[1] << 8)); }

uint32_t ReadU32Le(const uint8_t* p) {
    return uint32_t(p[0]) | (uint32_t(p[1]) << 8) | (uint32_t(p[2]) << 16) |
           (uint32_t(p[3]) << 24);
}

uint32_t ReadU32Be(const uint8_t* p) {
    return (uint32_t(p[0]) << 24) | (uint32_t(p[1]) << 16) | (uint32_t(p[2]) << 8) |
           uint32_t(p[3]);
}

// The bank payloads are not always flush with the start of the buffer, so the RIFF header
// is searched for rather than assumed at offset 0 — same tolerance the previous decoder had.
int LocateRiff(const std::vector<uint8_t>& bytes) {
    for (int offset = 0; offset <= 8; ++offset) {
        if (bytes.size() < size_t(offset) + 12) {
            break;
        }
        if (std::memcmp(&bytes[offset], "RIFF", 4) == 0 &&
            std::memcmp(&bytes[offset + 8], "WAVE", 4) == 0) {
            return offset;
        }
    }
    return -1;
}

struct Chunk {
    uint32_t id = 0;
    size_t data_off = 0;
    size_t data_size = 0;
};

std::vector<Chunk> ParseChunks(const std::vector<uint8_t>& bytes, size_t start) {
    std::vector<Chunk> chunks;
    size_t cursor = start;
    while (cursor + 8 <= bytes.size()) {
        Chunk chunk;
        chunk.id = ReadU32Le(&bytes[cursor]);
        chunk.data_size = ReadU32Le(&bytes[cursor + 4]);
        chunk.data_off = cursor + 8;
        if (chunk.data_off + chunk.data_size > bytes.size()) {
            chunk.data_size = bytes.size() - chunk.data_off;
        }
        chunks.push_back(chunk);
        // RIFF chunks are word-aligned.
        cursor = chunk.data_off + chunk.data_size + (chunk.data_size & 1);
        if (chunk.data_size == 0 && chunk.id == 0) {
            break;
        }
    }
    return chunks;
}

struct ParsedXma {
    const AVCodec* codec = nullptr;
    int channels = 0;
    int sample_rate = 0;
    size_t data_off = 0;
    size_t data_size = 0;
};

bool ParseXmaSource(const std::vector<uint8_t>& bytes, ParsedXma& out, std::string& error) {
    const int riff_off = LocateRiff(bytes);
    if (riff_off < 0) {
        error = "no RIFF/WAVE header";
        return false;
    }

    const std::vector<Chunk> chunks = ParseChunks(bytes, size_t(riff_off) + 12);
    const Chunk* fmt = nullptr;
    const Chunk* data = nullptr;
    for (const Chunk& chunk : chunks) {
        if (chunk.id == 0x20746d66) {  // 'fmt '
            fmt = &chunk;
        } else if (chunk.id == 0x61746164) {  // 'data'
            data = &chunk;
        }
    }
    if (fmt == nullptr || data == nullptr) {
        error = "missing fmt or data chunk";
        return false;
    }
    if (fmt->data_size < 16) {
        error = "fmt chunk too small";
        return false;
    }

    const uint8_t* fmt_p = &bytes[fmt->data_off];
    const uint16_t format_tag = ReadU16Le(fmt_p + 0);
    const uint16_t channels = ReadU16Le(fmt_p + 2);

    // Xbox-authored headers sometimes carry the sample rate big-endian. Decide by which
    // byte order lands in a plausible audio range, as the previous decoder did.
    const uint32_t rate_le = ReadU32Le(fmt_p + 4);
    const uint32_t rate_be = ReadU32Be(fmt_p + 4);
    const bool xbox_endian = (rate_le < 8000u || rate_le > 200000u) &&
                             (rate_be >= 8000u && rate_be <= 200000u);
    const uint32_t sample_rate = xbox_endian ? rate_be : rate_le;

    uint16_t effective_tag = format_tag;
    if (format_tag == kFormatTagExtensible && fmt->data_size >= 40) {
        effective_tag = ReadU16Le(fmt_p + 24);  // SubFormat's first field.
    }

    if (effective_tag == kFormatTagXma2) {
        out.codec = &ff_xma2_decoder;
    } else if (effective_tag == kFormatTagXma1) {
        out.codec = &ff_xma1_decoder;
    } else {
        char buffer[96];
        std::snprintf(buffer, sizeof(buffer), "format tag 0x%04X is not XMA1/XMA2",
                      format_tag);
        error = buffer;
        return false;
    }

    if (channels <= 0 || sample_rate == 0) {
        error = "fmt chunk has no channels or sample rate";
        return false;
    }

    out.channels = int(channels);
    out.sample_rate = int(sample_rate);
    out.data_off = data->data_off;
    out.data_size = data->data_size;
    return true;
}

// XMA streams carry 1-2 channels each, which is how FFmpeg derives its stream count.
int StreamCountFor(int channels) { return (channels + 1) / 2; }

// FFmpeg validates extradata by SIZE, and for the XMA2/34 case it does not read the
// contents at all -- xma_decode_init just does `num_streams = (channels + 1) / 2`. XMA1
// does read one byte: extradata[4] is the stream count, and the size must be
// 8 + 20 * num_streams. Building the smallest header each decoder accepts is therefore
// exactly equivalent to synthesising a full XMA2WAVEFORMATEX, without duplicating it.
bool AttachExtradata(AVCodecContext* ctx, bool is_xma2, std::string& error) {
    const int streams = StreamCountFor(ctx->channels);
    const size_t size = is_xma2 ? 34u : size_t(8 + 20 * streams);

    ctx->extradata =
        static_cast<uint8_t*>(av_mallocz(size + AV_INPUT_BUFFER_PADDING_SIZE));
    if (ctx->extradata == nullptr) {
        error = "out of memory for extradata";
        return false;
    }
    ctx->extradata_size = int(size);
    if (!is_xma2) {
        ctx->extradata[4] = uint8_t(streams);
    }
    return true;
}

void AppendFrameAsS16(const AVFrame* frame, int channels, std::vector<int16_t>& pcm) {
    const int samples = frame->nb_samples;
    if (samples <= 0) {
        return;
    }
    const size_t base = pcm.size();
    pcm.resize(base + size_t(samples) * size_t(channels));

    // The XMA decoders only ever advertise AV_SAMPLE_FMT_FLTP, so the frame is planar
    // float and has to be interleaved and clamped on the way to s16.
    for (int channel = 0; channel < channels; ++channel) {
        const auto* plane = reinterpret_cast<const float*>(frame->extended_data[channel]);
        if (plane == nullptr) {
            continue;
        }
        for (int i = 0; i < samples; ++i) {
            float value = plane[i];
            value = value < -1.0f ? -1.0f : (value > 1.0f ? 1.0f : value);
            pcm[base + size_t(i) * size_t(channels) + size_t(channel)] =
                int16_t(value * 32767.0f);
        }
    }
}

}  // namespace

extern "C" {

int RefiiAudioAbiVersion() { return 1; }

int RefiiXmaDecode(const unsigned char* data,
                   size_t length,
                   short** out_pcm,
                   size_t* out_sample_count,
                   int* out_sample_rate,
                   int* out_channels,
                   char* error,
                   size_t error_capacity) {
    if (out_pcm != nullptr) {
        *out_pcm = nullptr;
    }
    if (out_sample_count != nullptr) {
        *out_sample_count = 0;
    }
    if (out_sample_rate != nullptr) {
        *out_sample_rate = 0;
    }
    if (out_channels != nullptr) {
        *out_channels = 0;
    }

    if (data == nullptr || out_pcm == nullptr || out_sample_count == nullptr) {
        WriteError("null argument", error, error_capacity);
        return 0;
    }

    const std::vector<uint8_t> bytes(data, data + length);
    ParsedXma parsed;
    std::string message;
    if (!ParseXmaSource(bytes, parsed, message)) {
        WriteError(message, error, error_capacity);
        return 0;
    }

    AVCodecContext* ctx = avcodec_alloc_context3(parsed.codec);
    if (ctx == nullptr) {
        WriteError("could not allocate an XMA decoder", error, error_capacity);
        return 0;
    }

    ctx->channels = parsed.channels;
    ctx->sample_rate = parsed.sample_rate;
    ctx->block_align = kXmaBlockAlign;

    if (!AttachExtradata(ctx, parsed.codec == &ff_xma2_decoder, message)) {
        avcodec_free_context(&ctx);
        WriteError(message, error, error_capacity);
        return 0;
    }

    if (avcodec_open2(ctx, parsed.codec, nullptr) < 0) {
        avcodec_free_context(&ctx);
        WriteError("avcodec_open2 refused the XMA stream", error, error_capacity);
        return 0;
    }

    AVFrame* frame = av_frame_alloc();
    AVPacket* packet = av_packet_alloc();
    if (frame == nullptr || packet == nullptr) {
        av_frame_free(&frame);
        av_packet_free(&packet);
        avcodec_free_context(&ctx);
        WriteError("out of memory for decode buffers", error, error_capacity);
        return 0;
    }

    std::vector<int16_t> pcm;

    // Feed whole 2048-byte blocks, then flush. The XMA decoders are AV_CODEC_CAP_DELAY
    // and AV_CODEC_CAP_SUBFRAMES, so a single packet can yield several frames and the
    // final frames only appear after the drain packet.
    for (size_t offset = 0; offset < parsed.data_size; offset += size_t(kXmaBlockAlign)) {
        const size_t remaining = parsed.data_size - offset;
        const size_t block = remaining < size_t(kXmaBlockAlign) ? remaining
                                                                : size_t(kXmaBlockAlign);
        packet->data = const_cast<uint8_t*>(&bytes[parsed.data_off + offset]);
        packet->size = int(block);

        if (avcodec_send_packet(ctx, packet) < 0) {
            continue;  // A bad block should not throw away the audio around it.
        }
        while (avcodec_receive_frame(ctx, frame) == 0) {
            AppendFrameAsS16(frame, parsed.channels, pcm);
        }
    }

    avcodec_send_packet(ctx, nullptr);
    while (avcodec_receive_frame(ctx, frame) == 0) {
        AppendFrameAsS16(frame, parsed.channels, pcm);
    }

    av_frame_free(&frame);
    av_packet_free(&packet);
    avcodec_free_context(&ctx);

    if (pcm.empty()) {
        WriteError("decoder produced no audio", error, error_capacity);
        return 0;
    }

    auto* buffer = new (std::nothrow) short[pcm.size()];
    if (buffer == nullptr) {
        WriteError("out of memory holding decoded audio", error, error_capacity);
        return 0;
    }
    std::memcpy(buffer, pcm.data(), pcm.size() * sizeof(short));

    *out_pcm = buffer;
    *out_sample_count = pcm.size();
    if (out_sample_rate != nullptr) {
        *out_sample_rate = parsed.sample_rate;
    }
    if (out_channels != nullptr) {
        *out_channels = parsed.channels;
    }
    return 1;
}

void RefiiXmaFree(short* pcm) { delete[] pcm; }

}  // extern "C"
