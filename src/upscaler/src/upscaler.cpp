// Real-ESRGAN through ncnn, behind the C ABI in include/refii/upscaler.h.
//
// The managed side hands over a tightly packed RGBA8 buffer and expects one back. Everything
// between is here: the network, the Vulkan device, the tiling that bounds memory, and the alpha
// channel the model cannot see.

#include "refii/upscaler.h"

#include <algorithm>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "cpu.h"
#include "gpu.h"
#include "mat.h"
#include "net.h"

namespace {

/// Bytes the model is fed and returns per texel row. RGBA in, RGB through the net.
constexpr int kChannels = 3;

/// Tile edge, in SOURCE pixels.
///
/// Tiling is not an optimisation, it is what stops a 2048 albedo needing the whole image resident
/// on the GPU at four times the size. 200 is the size the reference ncnn implementations settled
/// on and it fits comfortably inside the 8 GB cards the July 2026 Steam survey puts second.
constexpr int kTile = 200;

/// Pixels of context fed around each tile and thrown away afterwards.
///
/// Without it every tile boundary is an edge the network has never seen past, and the seams show
/// as a visible grid — the one artefact that makes tiled inference obvious.
constexpr int kOverlap = 8;

/// FNV-1a over a file, for the cache identity.
///
/// The id has to change when the WEIGHTS change, and only the bytes can say that: a name is what
/// somebody typed and a version is what somebody remembered to bump. The managed cache keys every
/// enhanced texture on this string, so a stale one serves the previous model's output forever.
uint64_t HashFile(const std::string& path) {
  FILE* file = std::fopen(path.c_str(), "rb");
  if (file == nullptr) {
    return 0;
  }

  uint64_t hash = 1469598103934665603ULL;
  unsigned char buffer[64 * 1024];
  size_t read = 0;
  while ((read = std::fread(buffer, 1, sizeof(buffer), file)) > 0) {
    for (size_t i = 0; i < read; ++i) {
      hash ^= buffer[i];
      hash *= 1099511628211ULL;
    }
  }

  std::fclose(file);
  return hash;
}

/// The scale the MODEL was trained at, read from its name.
///
/// ncnn cannot be asked, and the answer matters: asking a 4x network for a 2x result and believing
/// it produces an image at the wrong size. Everything Real-ESRGAN ships carries it in the file
/// name, so that is where it is read from, and 4 is the fallback because the general models are 4x.
int ModelScale(const std::string& name) {
  for (size_t i = 0; i + 1 < name.size(); ++i) {
    if ((name[i] == 'x' || name[i] == 'X') && name[i + 1] >= '2' && name[i + 1] <= '8') {
      return name[i + 1] - '0';
    }
  }

  return 4;
}

}  // namespace

struct refii_upscaler {
  ncnn::Net net;
  std::string id;
  std::string input_name;
  std::string output_name;
  int model_scale = 4;
  bool gpu = false;
};

extern "C" refii_upscaler* refii_upscaler_create(const char* model_directory,
                                                 const char* model_name) {
  if (model_directory == nullptr || model_name == nullptr) {
    return nullptr;
  }

  const std::string directory(model_directory);
  const std::string name(model_name);
  const std::string param = directory + "/" + name + ".param";
  const std::string bin = directory + "/" + name + ".bin";

  auto* upscaler = new refii_upscaler();
  upscaler->model_scale = ModelScale(name);

  // THE GPU IS PREFERRED, NOT REQUIRED. §8 wants embedded Vulkan inference and that is what runs
  // when a device is there, but a machine without one still gets a correct result — slowly. The
  // caller can always fall back to the classical resampler, and it decides that from throughput
  // rather than from us refusing to start.
  const int gpus = ncnn::get_gpu_count();
  upscaler->gpu = gpus > 0;
  upscaler->net.opt.use_vulkan_compute = upscaler->gpu;
  upscaler->net.opt.use_fp16_packed = true;
  upscaler->net.opt.use_fp16_storage = true;
  upscaler->net.opt.use_int8_storage = false;

  if (upscaler->gpu) {
    upscaler->net.set_vulkan_device(ncnn::get_default_gpu_index());
  }

  if (upscaler->net.load_param(param.c_str()) != 0 ||
      upscaler->net.load_model(bin.c_str()) != 0) {
    // Missing or unreadable weights. A SUPPORTED outcome, not an error to shout about: they are
    // not in the repository by design, so a build without them is the normal first state.
    delete upscaler;
    return nullptr;
  }

  const std::vector<const char*>& inputs = upscaler->net.input_names();
  const std::vector<const char*>& outputs = upscaler->net.output_names();
  if (inputs.empty() || outputs.empty()) {
    delete upscaler;
    return nullptr;
  }

  upscaler->input_name = inputs.front();
  upscaler->output_name = outputs.back();

  char buffer[256];
  std::snprintf(buffer, sizeof(buffer), "%s-x%d-%016llx", name.c_str(), upscaler->model_scale,
                static_cast<unsigned long long>(HashFile(bin) ^ HashFile(param)));
  upscaler->id = buffer;

  return upscaler;
}

extern "C" void refii_upscaler_destroy(refii_upscaler* upscaler) { delete upscaler; }

extern "C" const char* refii_upscaler_id(refii_upscaler* upscaler) {
  return upscaler == nullptr ? nullptr : upscaler->id.c_str();
}

extern "C" int refii_upscaler_run(refii_upscaler* upscaler,
                                  const uint8_t* src,
                                  int32_t src_width,
                                  int32_t src_height,
                                  int32_t scale,
                                  refii_upscaler_filter filter,
                                  uint8_t* dst,
                                  int32_t dst_capacity) {
  if (upscaler == nullptr || src == nullptr || dst == nullptr) {
    return 1;
  }

  if (src_width <= 0 || src_height <= 0 || scale <= 1) {
    return 1;
  }

  // DECLINED, NOT SUBSTITUTED. A normal map is not colour, and a network trained on photographs
  // does not preserve the invariant that nx^2 + ny^2 stays inside the unit disk — the managed
  // side's classical path handles those correctly and will take them back.
  if (filter != REFII_UPSCALER_FILTER_PERCEPTUAL) {
    return 1;
  }

  const int64_t out_width = static_cast<int64_t>(src_width) * scale;
  const int64_t out_height = static_cast<int64_t>(src_height) * scale;
  if (out_width * out_height * 4 > dst_capacity) {
    return 1;
  }

  const int model_scale = upscaler->model_scale;
  const int64_t net_width = static_cast<int64_t>(src_width) * model_scale;
  const int64_t net_height = static_cast<int64_t>(src_height) * model_scale;

  // The network runs at ITS scale and the result is resized to the one asked for. A 4x model asked
  // for 2x has to produce 4x first; resizing after is a downsample, which loses nothing, whereas
  // pretending the network is 2x produces an image of the wrong size.
  ncnn::Mat rgb(static_cast<int>(net_width), static_cast<int>(net_height), kChannels);
  if (rgb.empty()) {
    return 1;
  }

  const float norm[3] = {1.0f / 255.0f, 1.0f / 255.0f, 1.0f / 255.0f};
  const float denorm[3] = {255.0f, 255.0f, 255.0f};

  for (int y = 0; y < src_height; y += kTile) {
    for (int x = 0; x < src_width; x += kTile) {
      const int tile_w = std::min(kTile, src_width - x);
      const int tile_h = std::min(kTile, src_height - y);

      // The padded window, clamped to the image. The padding is CONTEXT: it is fed to the network
      // and then discarded, so the tile's own pixels are never the ones at the edge of what the
      // network saw.
      const int px0 = std::max(0, x - kOverlap);
      const int py0 = std::max(0, y - kOverlap);
      const int px1 = std::min(src_width, x + tile_w + kOverlap);
      const int py1 = std::min(src_height, y + tile_h + kOverlap);
      const int pw = px1 - px0;
      const int ph = py1 - py0;

      ncnn::Mat in = ncnn::Mat::from_pixels_roi(src, ncnn::Mat::PIXEL_RGBA2RGB, src_width,
                                                src_height, px0, py0, pw, ph);
      if (in.empty()) {
        return 1;
      }

      in.substract_mean_normalize(nullptr, norm);

      ncnn::Extractor ex = upscaler->net.create_extractor();
      ex.set_light_mode(true);
      if (ex.input(upscaler->input_name.c_str(), in) != 0) {
        return 1;
      }

      ncnn::Mat out;
      if (ex.extract(upscaler->output_name.c_str(), out) != 0) {
        return 1;
      }

      if (out.w != pw * model_scale || out.h != ph * model_scale) {
        // The model did not scale by what its name claimed. Better to decline than to write a
        // differently sized tile into the middle of the destination.
        return 1;
      }

      out.substract_mean_normalize(nullptr, denorm);

      // Copy back only the tile itself, dropping the context on every side that had any.
      const int keep_x = (x - px0) * model_scale;
      const int keep_y = (y - py0) * model_scale;
      const int keep_w = tile_w * model_scale;
      const int keep_h = tile_h * model_scale;

      for (int c = 0; c < kChannels; ++c) {
        const float* source = out.channel(c);
        float* target = rgb.channel(c);
        for (int row = 0; row < keep_h; ++row) {
          const float* from = source + static_cast<size_t>(keep_y + row) * out.w + keep_x;
          float* to = target +
                      static_cast<size_t>(y * model_scale + row) * rgb.w +
                      static_cast<size_t>(x) * model_scale;
          std::memcpy(to, from, static_cast<size_t>(keep_w) * sizeof(float));
        }
      }
    }
  }

  // ALPHA NEVER WENT THROUGH THE NETWORK. Real-ESRGAN is a three-channel model, and on a Fable 2
  // albedo the alpha is the scissor threshold the material tests against — inventing detail in it
  // would move the cutout edge. It is resampled bilinearly instead, which moves nothing.
  // Plain buffers and resize_bilinear_c1 rather than an ncnn::Mat: Mat(w, h, int c, size_t
  // elemsize) and Mat(w, h, size_t elemsize, int elempack) are genuinely ambiguous for a
  // single-channel byte image, and the raw-pointer overload says what is meant without a cast
  // that only exists to pick an overload.
  std::vector<unsigned char> alpha_in(static_cast<size_t>(src_width) * src_height);
  for (size_t i = 0; i < alpha_in.size(); ++i) {
    alpha_in[i] = src[i * 4 + 3];
  }

  std::vector<unsigned char> alpha_out(static_cast<size_t>(out_width) * out_height);
  ncnn::resize_bilinear_c1(alpha_in.data(), src_width, src_height, alpha_out.data(),
                           static_cast<int>(out_width), static_cast<int>(out_height));

  // The RGB is at the model's scale; the caller asked for `scale`. Resize when they differ.
  //
  // AND RELEASE THE MODEL-SCALE BUFFER THE MOMENT THE SMALLER ONE EXISTS. A 4x model asked for
  // 2x holds both at once — four times the requested area in floats, plus the destination —
  // and the write loop below only ever reads `rgb_final`, so the larger one was staying
  // resident for no reader. `ncnn::Mat` is reference counted and `rgb_final` ALIASES `rgb`
  // when the sizes already agree, so this only releases on the branch that actually allocated
  // a second buffer.
  //
  // The tiles are still accumulated at the model's scale rather than downsampled one at a
  // time. Per-tile downsampling would drop the peak further but each tile would be filtered
  // without its neighbours, which prints a seam at every tile edge — and the enhancement edge
  // cap already bounds a source here to 512, so this buffer is a quarter of what it was.
  ncnn::Mat rgb_final = rgb;
  if (net_width != out_width || net_height != out_height) {
    ncnn::resize_bilinear(rgb, rgb_final, static_cast<int>(out_width),
                          static_cast<int>(out_height));
    rgb.release();
  }

  for (int64_t y = 0; y < out_height; ++y) {
    for (int64_t x = 0; x < out_width; ++x) {
      const size_t index = static_cast<size_t>(y) * out_width + x;
      for (int c = 0; c < kChannels; ++c) {
        const float value = rgb_final.channel(c)[index];
        dst[index * 4 + c] =
            static_cast<uint8_t>(std::clamp(value + 0.5f, 0.0f, 255.0f));
      }

      dst[index * 4 + 3] = alpha_out[index];
    }
  }

  return 0;
}
