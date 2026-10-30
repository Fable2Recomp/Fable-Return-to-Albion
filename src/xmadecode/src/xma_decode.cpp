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
extern "C" const AVCodec ff_wmav2_decoder;

namespace {

// XMA is always carried in 2048-byte blocks; the old decoder hard-coded this too, and
// FFmpeg needs it as block_align to size its packets.
constexpr int kXmaBlockAlign = 2048;

constexpr uint16_t kFormatTagXma1 = 0x0165;
constexpr uint16_t kFormatTagXma2 = 0x0166;
constexpr uint16_t kFormatTagExtensible = 0xFFFE;
constexpr uint16_t kFormatTagWmaV2 = 0x0161;

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
// Fable 3's speech is the second form: a 4-byte "xwma" tag, then RIFF/XWMA (the guest's
// xWMA reader requires form 'XWMA', 0x82E1FFC8, after skipping the 4 bytes, 0x82DC1E6C).
int LocateRiff(const std::vector<uint8_t>& bytes, bool& xwma) {
    for (int offset = 0; offset <= 8; ++offset) {
        if (bytes.size() < size_t(offset) + 12) {
            break;
        }
        if (std::memcmp(&bytes[offset], "RIFF", 4) != 0) {
            continue;
        }
        if (std::memcmp(&bytes[offset + 8], "WAVE", 4) == 0) {
            xwma = false;
            return offset;
        }
        if (std::memcmp(&bytes[offset + 8], "XWMA", 4) == 0) {
            xwma = true;
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
    int block_align = kXmaBlockAlign;
    int64_t bit_rate = 0;
    uint16_t wma_flags = 0;
    size_t data_off = 0;
    size_t data_size = 0;
};

// The WMAv2 decoder flags (FFmpeg's flags2, extradata[4..5]) the guest builds for a speech
// stream from its WMA format table (unk_820130F0 via sub_82E671C0 / sub_82E66558): exponent
// VLC, bit reservoir and variable block length always, and the block-size count by bit rate
// per channel. The table's entries give 0x000F at 32 kbps per channel and above (Fable 3's
// 44.1 kHz speech) and 0x0017 below (its 32 and 22.05 kHz speech, 20 kbps). FFmpeg's own
// xWMA default, 0x001F, garbles every 32 kHz file.
uint16_t WmaFlagsFor(int64_t bit_rate, int channels) {
    return bit_rate / channels >= 32000 ? 0x000F : 0x0017;
}

// The xWMA fmt is a little-endian WAVEFORMATEX (sub_82E202E0 swaps every field), its
// extensible form naming the codec in the u16 at +24 (sub_82E503B8).
bool ParseXwmaFmt(const uint8_t* fmt_p, size_t fmt_size, ParsedXma& out, std::string& error) {
    if (fmt_size < 16) {
        error = "xWMA fmt chunk too small";
        return false;
    }

    uint16_t tag = ReadU16Le(fmt_p + 0);
    if (tag == kFormatTagExtensible && fmt_size >= 26) {
        tag = ReadU16Le(fmt_p + 24);
    }
    if (tag != kFormatTagWmaV2) {
        char buffer[96];
        std::snprintf(buffer, sizeof(buffer), "xWMA format tag 0x%04X is not WMAv2", tag);
        error = buffer;
        return false;
    }

    out.codec = &ff_wmav2_decoder;
    out.channels = ReadU16Le(fmt_p + 2);
    out.sample_rate = int(ReadU32Le(fmt_p + 4));
    out.bit_rate = int64_t(ReadU32Le(fmt_p + 8)) * 8;
    out.block_align = ReadU16Le(fmt_p + 12);
    if (out.channels <= 0 || out.sample_rate <= 0 || out.block_align <= 0 || out.bit_rate <= 0) {
        error = "xWMA fmt states no channels, rate, bit rate or block size";
        return false;
    }

    out.wma_flags = WmaFlagsFor(out.bit_rate, out.channels);
    return true;
}

bool ParseXmaSource(const std::vector<uint8_t>& bytes, ParsedXma& out, std::string& error) {
    bool xwma = false;
    const int riff_off = LocateRiff(bytes, xwma);
    if (riff_off < 0) {
        error = "no RIFF/WAVE or RIFF/XWMA header";
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
    if (xwma) {
        if (!ParseXwmaFmt(&bytes[fmt->data_off], fmt->data_size, out, error)) {
            return false;
        }
        out.data_off = data->data_off;
        out.data_size = data->data_size;
        return true;
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
bool AttachExtradata(AVCodecContext* ctx, const ParsedXma& parsed, std::string& error) {
    const bool is_wma = parsed.codec == &ff_wmav2_decoder;
    const bool is_xma2 = parsed.codec == &ff_xma2_decoder;
    const int streams = StreamCountFor(ctx->channels);
    const size_t size = is_wma ? 6u : (is_xma2 ? 34u : size_t(8 + 20 * streams));

    ctx->extradata =
        static_cast<uint8_t*>(av_mallocz(size + AV_INPUT_BUFFER_PADDING_SIZE));
    if (ctx->extradata == nullptr) {
        error = "out of memory for extradata";
        return false;
    }
    ctx->extradata_size = int(size);
    if (is_wma) {
        // WMAv2 reads its flags2 little-endian from extradata[4] (FFmpeg wmadec_init).
        ctx->extradata[4] = uint8_t(parsed.wma_flags & 0xFF);
        ctx->extradata[5] = uint8_t(parsed.wma_flags >> 8);
    } else if (!is_xma2) {
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

    // The XMA and WMAv2 decoders only ever advertise AV_SAMPLE_FMT_FLTP, so the frame is
    // planar float and has to be interleaved and clamped on the way to s16.
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

int RefiiAudioAbiVersion() { return 2; }

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
        WriteError("could not allocate an audio decoder", error, error_capacity);
        return 0;
    }

    ctx->channels = parsed.channels;
    ctx->sample_rate = parsed.sample_rate;
    ctx->block_align = parsed.block_align;
    ctx->bit_rate = parsed.bit_rate;

    if (!AttachExtradata(ctx, parsed, message)) {
        avcodec_free_context(&ctx);
        WriteError(message, error, error_capacity);
        return 0;
    }

    if (avcodec_open2(ctx, parsed.codec, nullptr) < 0) {
        avcodec_free_context(&ctx);
        WriteError("avcodec_open2 refused the stream", error, error_capacity);
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

    // Feed whole blocks of the stated size (2048 for XMA, the fmt's nBlockAlign for WMAv2),
    // then flush. The XMA decoders are AV_CODEC_CAP_DELAY and AV_CODEC_CAP_SUBFRAMES, so a
    // single packet can yield several frames and the final frames only appear after the drain.
    const size_t block_size = size_t(parsed.block_align);
    for (size_t offset = 0; offset < parsed.data_size; offset += block_size) {
        const size_t remaining = parsed.data_size - offset;
        const size_t block = remaining < block_size ? remaining : block_size;
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
