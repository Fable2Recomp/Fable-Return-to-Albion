
#include "Audio/XmaDecoder.h"

#include <cstring>
#include <exception>
#include <string>
#include <vector>

namespace {

void WriteError(const std::string& message, char* error, size_t capacity) {
    if (error == nullptr || capacity == 0) {
        return;
    }
    const size_t copied = message.size() < capacity - 1 ? message.size() : capacity - 1;
    std::memcpy(error, message.data(), copied);
    error[copied] = '\0';
}

}

extern "C" {

int RefiiAudioAbiVersion() {
    return 1;
}

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

    try {
        std::vector<unsigned char> bytes(data, data + length);
        std::vector<int16_t> pcm;
        int sample_rate = 0;
        int channels = 0;
        std::string message;

        if (!XmaDecoder::decode_xma_to_pcm(bytes, pcm, sample_rate, channels, &message)) {
            WriteError(message.empty() ? "decode failed" : message, error, error_capacity);
            return 0;
        }

        if (pcm.empty() || sample_rate <= 0 || channels <= 0) {
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
            *out_sample_rate = sample_rate;
        }
        if (out_channels != nullptr) {
            *out_channels = channels;
        }
        return 1;
    } catch (const std::exception& ex) {
        WriteError(ex.what(), error, error_capacity);
        return 0;
    } catch (...) {
        WriteError("unknown failure decoding audio", error, error_capacity);
        return 0;
    }
}

void RefiiXmaFree(short* pcm) {
    delete[] pcm;
}

}
