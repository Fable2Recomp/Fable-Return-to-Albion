#include <achievement/achievement_imgui.h>

#include <achievement/achievement_fable_texture_codec.h>
#include <achievement/achievement_runtime.h>
#include <imgui.h>
#include <rex/ui/imgui_dialog.h>
#include <rex/ui/immediate_drawer.h>
#include <rex/ui/keybinds.h>
#include <stb_image.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cinttypes>
#include <cstdint>
#include <cctype>
#include <cstring>
#include <deque>
#include <filesystem>
#include <fstream>
#include <functional>
#include <limits>
#include <memory>
#include <optional>
#include <span>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#if defined(__GNUC__) || defined(__clang__)
namespace refii::renderer {
std::uint64_t RegisterPlumeImGuiRgbaTexture(const void *key,
                                            std::uint32_t width,
                                            std::uint32_t height,
                                            const std::uint8_t *rgba_pixels)
    __attribute__((weak));
} // namespace refii::renderer
#endif

namespace refii::achievement {
namespace {

class AchievementDebugDialog final : public rex::ui::ImGuiDialog {
public:
  explicit AchievementDebugDialog(rex::ui::ImGuiDrawer *imgui_drawer)
      : ImGuiDialog(imgui_drawer) {}

protected:
  void OnDraw(ImGuiIO &io) override;

private:
  int selected_user_index_ = 0;
  int test_achievement_id_ = 1;
  std::uint32_t selected_achievement_id_ = 0;
};

class AchievementToastDialog final : public rex::ui::ImGuiDialog {
public:
  explicit AchievementToastDialog(rex::ui::ImGuiDrawer *imgui_drawer)
      : ImGuiDialog(imgui_drawer) {
    const std::vector<AchievementUnlock> unlocks =
        GetUnlockedAchievementsSnapshot();
    for (const AchievementUnlock &unlock : unlocks) {
      last_seen_sequence_ = std::max(last_seen_sequence_, unlock.sequence);
    }
  }

  [[nodiscard]] std::size_t PendingToastCount() const {
    return pending_unlocks_.size();
  }

protected:
  void OnDraw(ImGuiIO &io) override;

private:
  void QueueNewUnlocks();
  void DrawCurrentToast(ImGuiIO &io);

  std::uint64_t last_seen_sequence_ = 0;
  float current_age_seconds_ = 0.0f;
  std::deque<AchievementUnlock> pending_unlocks_;
};

std::unique_ptr<AchievementDebugDialog> g_dialog;
std::unique_ptr<AchievementToastDialog> g_toast_dialog;
rex::ui::ImGuiDrawer *g_drawer = nullptr;
rex::ui::ImmediateDrawer *g_immediate_drawer = nullptr;
ImFont *g_fable_toast_title_font = nullptr;
ImFont *g_fable_toast_body_font = nullptr;
std::string g_fable_font_status = "not configured";

constexpr const char *kFable2ToastFrameTexture = "frames/messagebox.tex";
constexpr const char *kFable2ToastIconRingTexture =
    "frames/received_item_circle.tex";
constexpr const char *kFable2ToastIconRingTextureAlt =
    "frames/received_item_circle_v2.tex";

struct FableGuiTexture {
  std::string key;
  std::string source_path;
  std::uint32_t width = 0;
  std::uint32_t height = 0;
  std::uint32_t format = 0;
  std::uint32_t header_payload_size = 0;
  std::uint32_t header_payload_offset = 0;
  std::uint32_t header_tail = 0;
  std::uint64_t payload_size = 0;
  std::string status;
  bool load_attempted = false;
  bool decode_failed = false;
  std::vector<std::uint8_t> rgba_pixels;
  std::unique_ptr<rex::ui::ImmediateTexture> texture;
};

std::unordered_map<std::string, FableGuiTexture> g_gui_textures;

struct AchievementIconTexture {
  std::uint32_t achievement_id = 0;
  std::uint32_t image_id = 0;
  std::uint32_t width = 0;
  std::uint32_t height = 0;
  std::size_t png_byte_count = 0;
  bool decode_failed = false;
  std::vector<std::uint8_t> rgba_pixels;
  std::unique_ptr<rex::ui::ImmediateTexture> texture;
};

std::unordered_map<std::uint32_t, AchievementIconTexture> g_icon_textures;

std::optional<std::filesystem::path> FindFable2FontPath() {
  const std::array<std::filesystem::path, 6> candidates = {
      std::filesystem::path("assets/launcher/assets/fable2.ttf"),
      std::filesystem::current_path() / "assets/launcher/assets/fable2.ttf",
      std::filesystem::path("out/build/linux-amd64-debug/bin/assets/launcher/"
                            "assets/fable2.ttf"),
      std::filesystem::path("src/launcher/assets/fable2.ttf"),
      std::filesystem::current_path() / "src/launcher/assets/fable2.ttf",
      std::filesystem::current_path().parent_path() / "assets/launcher/assets/"
          "fable2.ttf",
  };

  for (const std::filesystem::path &candidate : candidates) {
    std::error_code error;
    if (std::filesystem::is_regular_file(candidate, error)) {
      return candidate;
    }
  }
  return std::nullopt;
}

std::uint64_t AchievementUnlockKey(const std::uint32_t user_index,
                                   const std::uint32_t achievement_id) {
  return (static_cast<std::uint64_t>(user_index) << 32) | achievement_id;
}

std::uint32_t ReadBigEndianU32(const std::uint8_t *bytes) {
  return (static_cast<std::uint32_t>(bytes[0]) << 24) |
         (static_cast<std::uint32_t>(bytes[1]) << 16) |
         (static_cast<std::uint32_t>(bytes[2]) << 8) |
         static_cast<std::uint32_t>(bytes[3]);
}

void LoadFableToastFonts(ImFontAtlas *atlas) {
  g_fable_toast_title_font = nullptr;
  g_fable_toast_body_font = nullptr;
  if (atlas == nullptr) {
    g_fable_font_status = "font atlas unavailable";
    return;
  }

  const std::optional<std::filesystem::path> font_path = FindFable2FontPath();
  if (!font_path.has_value()) {
    g_fable_font_status = "fable2.ttf not found";
    return;
  }

  const std::string path = font_path->string();
  ImFontConfig title_config;
  title_config.OversampleH = 2;
  title_config.OversampleV = 2;
  title_config.RasterizerMultiply = 1.08f;
  title_config.GlyphOffset = ImVec2(0.0f, 1.0f);

  ImFontConfig body_config = title_config;
  body_config.RasterizerMultiply = 1.04f;

  g_fable_toast_title_font =
      atlas->AddFontFromFileTTF(path.c_str(), 13.5f, &title_config);
  g_fable_toast_body_font =
      atlas->AddFontFromFileTTF(path.c_str(), 15.0f, &body_config);

  if (g_fable_toast_title_font == nullptr ||
      g_fable_toast_body_font == nullptr) {
    g_fable_toast_title_font = nullptr;
    g_fable_toast_body_font = nullptr;
    g_fable_font_status = "failed to load " + path;
    return;
  }

  g_fable_font_status = "loaded " + path;
}

std::optional<std::filesystem::path> FindUnpackedGuiRoot() {
  const std::array<std::filesystem::path, 4> candidates = {
      std::filesystem::path("assets/unpacked/art/gui"),
      std::filesystem::path("out/build/linux-amd64-debug/bin/assets/unpacked/"
                            "art/gui"),
      std::filesystem::current_path() / "out/build/linux-amd64-debug/bin/"
                                         "assets/unpacked/art/gui",
      std::filesystem::current_path() / "assets/unpacked/art/gui",
  };

  for (const std::filesystem::path &candidate : candidates) {
    std::error_code error;
    if (std::filesystem::is_directory(candidate, error)) {
      return candidate;
    }
  }
  return std::nullopt;
}

bool ReadFileBytes(const std::filesystem::path &path,
                   std::vector<std::uint8_t> &bytes) {
  std::ifstream input(path, std::ios::binary);
  if (!input) {
    return false;
  }

  input.seekg(0, std::ios::end);
  const std::streamoff size = input.tellg();
  if (size <= 0 ||
      size > static_cast<std::streamoff>(std::numeric_limits<int>::max())) {
    return false;
  }
  input.seekg(0, std::ios::beg);

  bytes.assign(static_cast<std::size_t>(size), 0);
  return static_cast<bool>(input.read(
      reinterpret_cast<char *>(bytes.data()),
      static_cast<std::streamsize>(bytes.size())));
}

bool DecodeSingleChannelTexture(const std::vector<std::uint8_t> &payload,
                                const std::uint32_t width,
                                const std::uint32_t height,
                                FableGuiTexture &texture) {
  const std::size_t pixel_count =
      static_cast<std::size_t>(width) * static_cast<std::size_t>(height);
  if (width == 0 || height == 0 || pixel_count == 0) {
    return false;
  }

  std::size_t offset = 0;
  std::size_t stride = 1;
  if (payload.size() == pixel_count + 4) {
    offset = 4;
  } else if (payload.size() == pixel_count * 4 + 4) {
    offset = 4;
    stride = 4;
  } else if (payload.size() == pixel_count * 4) {
    stride = 4;
  } else if (payload.size() != pixel_count) {
    return false;
  }

  texture.rgba_pixels.assign(pixel_count * 4, 0);
  for (std::size_t index = 0; index < pixel_count; ++index) {
    const std::uint8_t value = payload[offset + index * stride];
    const std::size_t rgba = index * 4;
    texture.rgba_pixels[rgba + 0] = 0;
    texture.rgba_pixels[rgba + 1] = 0;
    texture.rgba_pixels[rgba + 2] = 0;
    texture.rgba_pixels[rgba + 3] = value;
  }
  return true;
}

std::uint8_t Expand5To8(const std::uint16_t value) {
  return static_cast<std::uint8_t>((value << 3u) | (value >> 2u));
}

std::uint8_t Expand6To8(const std::uint16_t value) {
  return static_cast<std::uint8_t>((value << 2u) | (value >> 4u));
}

void DecodeBc1Block(const std::uint8_t *block, std::uint32_t *out_rgba) {
  const std::uint16_t c0 =
      static_cast<std::uint16_t>(block[0] | (block[1] << 8));
  const std::uint16_t c1 =
      static_cast<std::uint16_t>(block[2] | (block[3] << 8));
  const std::uint8_t r0 = Expand5To8((c0 >> 11u) & 31u);
  const std::uint8_t g0 = Expand6To8((c0 >> 5u) & 63u);
  const std::uint8_t b0 = Expand5To8(c0 & 31u);
  const std::uint8_t r1 = Expand5To8((c1 >> 11u) & 31u);
  const std::uint8_t g1 = Expand6To8((c1 >> 5u) & 63u);
  const std::uint8_t b1 = Expand5To8(c1 & 31u);
  std::uint32_t colors[4]{};
  colors[0] = (0xFFu << 24u) | (static_cast<std::uint32_t>(b0) << 16u) |
              (static_cast<std::uint32_t>(g0) << 8u) | r0;
  colors[1] = (0xFFu << 24u) | (static_cast<std::uint32_t>(b1) << 16u) |
              (static_cast<std::uint32_t>(g1) << 8u) | r1;
  if (c0 > c1) {
    colors[2] =
        (0xFFu << 24u) |
        (static_cast<std::uint32_t>((2u * b0 + b1 + 1u) / 3u) << 16u) |
        (static_cast<std::uint32_t>((2u * g0 + g1 + 1u) / 3u) << 8u) |
        ((2u * r0 + r1 + 1u) / 3u);
    colors[3] =
        (0xFFu << 24u) |
        (static_cast<std::uint32_t>((b0 + 2u * b1 + 1u) / 3u) << 16u) |
        (static_cast<std::uint32_t>((g0 + 2u * g1 + 1u) / 3u) << 8u) |
        ((r0 + 2u * r1 + 1u) / 3u);
  } else {
    colors[2] = (0xFFu << 24u) |
                (static_cast<std::uint32_t>((b0 + b1) >> 1u) << 16u) |
                (static_cast<std::uint32_t>((g0 + g1) >> 1u) << 8u) |
                ((r0 + r1) >> 1u);
    colors[3] = 0;
  }

  const std::uint32_t indices =
      block[4] | (block[5] << 8u) | (block[6] << 16u) | (block[7] << 24u);
  for (int py = 0; py < 4; ++py) {
    for (int px = 0; px < 4; ++px) {
      const int index = (indices >> (2 * (py * 4 + px))) & 3;
      out_rgba[py * 4 + px] = colors[index];
    }
  }
}

void DecodeBc4Block(const std::uint8_t *block, std::uint8_t *out_alpha) {
  const std::uint8_t a0 = block[0];
  const std::uint8_t a1 = block[1];
  std::uint64_t indices = 0;
  for (int index = 0; index < 6; ++index) {
    indices |= static_cast<std::uint64_t>(block[2 + index]) << (8 * index);
  }

  std::uint8_t alpha[8]{};
  alpha[0] = a0;
  alpha[1] = a1;
  if (a0 > a1) {
    for (int index = 1; index <= 6; ++index) {
      alpha[index + 1] =
          static_cast<std::uint8_t>(((7 - index) * a0 + index * a1 + 3) / 7);
    }
  } else {
    for (int index = 1; index <= 4; ++index) {
      alpha[index + 1] =
          static_cast<std::uint8_t>(((5 - index) * a0 + index * a1 + 2) / 5);
    }
    alpha[6] = 0;
    alpha[7] = 255;
  }

  for (int index = 0; index < 16; ++index) {
    out_alpha[index] = alpha[(indices >> (3 * index)) & 7u];
  }
}

void BlitBc1ToRgba(const std::vector<std::uint8_t> &bc1, const int width,
                   const int height, std::vector<std::uint8_t> &rgba) {
  const std::size_t blocks_x = static_cast<std::size_t>((width + 3) / 4);
  const std::size_t blocks_y = static_cast<std::size_t>((height + 3) / 4);
  rgba.assign(static_cast<std::size_t>(width) * height * 4, 0xFF);
  std::size_t source_offset = 0;
  for (std::size_t block_y = 0; block_y < blocks_y; ++block_y) {
    for (std::size_t block_x = 0; block_x < blocks_x; ++block_x) {
      if (source_offset + 8 > bc1.size()) {
        return;
      }
      std::uint32_t pixels[16]{};
      DecodeBc1Block(bc1.data() + source_offset, pixels);
      source_offset += 8;
      for (int py = 0; py < 4; ++py) {
        const int y = static_cast<int>(block_y) * 4 + py;
        if (y >= height) {
          break;
        }
        for (int px = 0; px < 4; ++px) {
          const int x = static_cast<int>(block_x) * 4 + px;
          if (x >= width) {
            break;
          }
          std::memcpy(rgba.data() + (static_cast<std::size_t>(y) * width + x) * 4,
                      &pixels[py * 4 + px], 4);
        }
      }
    }
  }
}

void ApplyBc4Alpha(const std::vector<std::uint8_t> &alpha_blocks,
                   const int width, const int height,
                   std::vector<std::uint8_t> &rgba) {
  const std::size_t blocks_x = static_cast<std::size_t>((width + 3) / 4);
  const std::size_t blocks_y = static_cast<std::size_t>((height + 3) / 4);
  if (alpha_blocks.size() < blocks_x * blocks_y * 8) {
    return;
  }

  for (std::size_t block_y = 0; block_y < blocks_y; ++block_y) {
    for (std::size_t block_x = 0; block_x < blocks_x; ++block_x) {
      std::uint8_t alpha[16]{};
      DecodeBc4Block(alpha_blocks.data() + (block_y * blocks_x + block_x) * 8,
                     alpha);
      for (int py = 0; py < 4; ++py) {
        const int y = static_cast<int>(block_y) * 4 + py;
        if (y >= height) {
          break;
        }
        for (int px = 0; px < 4; ++px) {
          const int x = static_cast<int>(block_x) * 4 + px;
          if (x >= width) {
            break;
          }
          rgba[(static_cast<std::size_t>(y) * width + x) * 4 + 3] =
              alpha[py * 4 + px];
        }
      }
    }
  }
}

bool DecodePackedFableGuiTexture(const std::vector<std::uint8_t> &payload,
                                 FableGuiTexture &texture) {
  if (payload.size() < 48) {
    return false;
  }

  const std::uint32_t comp_flag = ReadBigEndianU32(payload.data() + 0);
  const std::uint32_t data_offset = ReadBigEndianU32(payload.data() + 4);
  const std::uint32_t data_size = ReadBigEndianU32(payload.data() + 8);
  const std::uint32_t alpha_comp_flag = ReadBigEndianU32(payload.data() + 12);
  const std::uint32_t alpha_offset = ReadBigEndianU32(payload.data() + 16);
  const std::uint32_t alpha_size = ReadBigEndianU32(payload.data() + 20);
  if ((comp_flag != 1u && comp_flag != 11u) || data_offset != 48u ||
      data_offset > payload.size() || data_size > payload.size() - data_offset) {
    return false;
  }

  std::vector<std::uint8_t> bc1;
  int decoded_width = 0;
  int decoded_height = 0;
  std::string error;
  if (!DecodeFableCompressedBc1Mip(payload.data() + data_offset, data_size,
                                   decoded_width, decoded_height, bc1, &error,
                                   comp_flag == 11u)) {
    texture.status = "LH color decode failed: " + error;
    return false;
  }

  BlitBc1ToRgba(bc1, decoded_width, decoded_height, texture.rgba_pixels);
  texture.width = static_cast<std::uint32_t>(decoded_width);
  texture.height = static_cast<std::uint32_t>(decoded_height);

  if (alpha_size != 0 && alpha_offset <= payload.size() &&
      alpha_size <= payload.size() - alpha_offset) {
    std::vector<std::uint8_t> alpha_blocks;
    std::string alpha_error;
    bool alpha_decoded = false;
    if (alpha_comp_flag == 1u || alpha_comp_flag == 11u) {
      int alpha_width = 0;
      int alpha_height = 0;
      alpha_decoded = DecodeFableCompressedBc1Mip(
          payload.data() + alpha_offset, alpha_size, alpha_width, alpha_height,
          alpha_blocks, &alpha_error, alpha_comp_flag == 11u);
    } else if (alpha_comp_flag == 2u || alpha_comp_flag == 3u ||
               alpha_comp_flag == 4u) {
      alpha_decoded = DecodeFableCompressedVariant234(
          payload.data() + alpha_offset, alpha_size, 2, decoded_width,
          decoded_height, alpha_blocks, &alpha_error);
    } else if (alpha_comp_flag == 7u) {
      const std::size_t expected =
          static_cast<std::size_t>((decoded_width + 3) / 4) *
          static_cast<std::size_t>((decoded_height + 3) / 4) * 8;
      if (alpha_size >= expected) {
        alpha_blocks.assign(payload.data() + alpha_offset,
                            payload.data() + alpha_offset + expected);
        alpha_decoded = true;
      }
    }

    if (alpha_decoded) {
      ApplyBc4Alpha(alpha_blocks, decoded_width, decoded_height,
                    texture.rgba_pixels);
    }
  }

  texture.status = "decoded LH packed stream";
  return !texture.rgba_pixels.empty();
}

FableGuiTexture &GetOrCreateGuiTexture(const std::string &key) {
  FableGuiTexture &texture = g_gui_textures[key];
  texture.key = key;
  if (texture.load_attempted) {
    if (!texture.rgba_pixels.empty() && texture.texture == nullptr &&
        g_immediate_drawer != nullptr) {
      texture.texture = g_immediate_drawer->CreateTexture(
          texture.width, texture.height, rex::ui::ImmediateTextureFilter::kLinear,
          false, texture.rgba_pixels.data());
    }
    return texture;
  }

  texture.load_attempted = true;
  const std::optional<std::filesystem::path> gui_root = FindUnpackedGuiRoot();
  if (!gui_root.has_value()) {
    texture.status = "unpacked GUI root missing";
    texture.decode_failed = true;
    return texture;
  }

  const std::filesystem::path flat_payload_path = *gui_root / key;
  std::vector<std::uint8_t> flat_payload;
  if (ReadFileBytes(flat_payload_path, flat_payload)) {
    texture.source_path = flat_payload_path.string();
    texture.payload_size = flat_payload.size();
    if (DecodePackedFableGuiTexture(flat_payload, texture)) {
      texture.decode_failed = false;
    } else {
      texture.status = "unsupported flattened payload layout";
      texture.decode_failed = true;
    }
    if (!texture.rgba_pixels.empty() && g_immediate_drawer != nullptr) {
      texture.texture = g_immediate_drawer->CreateTexture(
          texture.width, texture.height,
          rex::ui::ImmediateTextureFilter::kLinear, false,
          texture.rgba_pixels.data());
      if (texture.texture == nullptr) {
        texture.status = "texture upload failed";
        texture.decode_failed = true;
      }
    }
    return texture;
  }

  const std::filesystem::path header_path =
      *gui_root / "gui_texture_headers/art/gui" / key;
  const std::filesystem::path payload_path =
      *gui_root / "gui_textures/art/gui" / key;
  texture.source_path = payload_path.string();

  std::vector<std::uint8_t> header;
  std::vector<std::uint8_t> payload;
  if (!ReadFileBytes(header_path, header) || header.size() < 84) {
    texture.status = "header missing";
    texture.decode_failed = true;
    return texture;
  }
  if (!ReadFileBytes(payload_path, payload)) {
    texture.status = "payload missing";
    texture.decode_failed = true;
    return texture;
  }

  if (ReadBigEndianU32(header.data()) != 0xFFFFFFFEu) {
    texture.status = "not a split texture header";
    texture.decode_failed = true;
    return texture;
  }

  texture.header_payload_size = ReadBigEndianU32(header.data() + 4);
  texture.header_payload_offset = ReadBigEndianU32(header.data() + 12);
  texture.width = ReadBigEndianU32(header.data() + 16);
  texture.height = ReadBigEndianU32(header.data() + 20);
  texture.format = ReadBigEndianU32(header.data() + 24);
  texture.header_tail = ReadBigEndianU32(header.data() + 80);
  texture.payload_size = payload.size();

  if (texture.header_payload_size != payload.size()) {
    texture.status = "payload size mismatch";
    texture.decode_failed = true;
    return texture;
  }

  if (DecodePackedFableGuiTexture(payload, texture)) {
    texture.decode_failed = false;
  } else if (DecodeSingleChannelTexture(payload, texture.width, texture.height,
                                        texture)) {
    texture.status = "decoded single-channel raw";
  } else if (payload.size() >= 0x30 &&
             (ReadBigEndianU32(payload.data()) == 1u ||
              ReadBigEndianU32(payload.data()) == 11u) &&
             ReadBigEndianU32(payload.data() + 4) == 0x30u) {
    texture.status = "packed Fable stream";
    texture.decode_failed = true;
  } else {
    texture.status = "unsupported payload layout";
    texture.decode_failed = true;
  }

  if (!texture.rgba_pixels.empty() && g_immediate_drawer != nullptr) {
    texture.texture = g_immediate_drawer->CreateTexture(
        texture.width, texture.height, rex::ui::ImmediateTextureFilter::kLinear,
        false, texture.rgba_pixels.data());
    if (texture.texture == nullptr) {
      texture.status = "texture upload failed";
      texture.decode_failed = true;
    }
  }
  return texture;
}

ImTextureID GuiImTextureId(const FableGuiTexture &texture) {
  if (texture.texture == nullptr) {
    return 0;
  }
  return static_cast<ImTextureID>(
      reinterpret_cast<std::uintptr_t>(texture.texture.get()));
}

ImTextureID TryRegisterPlumeTexture(const void *key, const std::uint32_t width,
                                    const std::uint32_t height,
                                    const std::vector<std::uint8_t> &pixels) {
#if defined(__GNUC__) || defined(__clang__)
  if (refii::renderer::RegisterPlumeImGuiRgbaTexture == nullptr ||
      pixels.empty() || width == 0 || height == 0) {
    return 0;
  }

  return static_cast<ImTextureID>(
      refii::renderer::RegisterPlumeImGuiRgbaTexture(key, width, height,
                                                     pixels.data()));
#else
  (void)key;
  (void)width;
  (void)height;
  (void)pixels;
  return 0;
#endif
}

ImTextureID FableGuiImTextureId(const FableGuiTexture &texture) {
  if (!texture.rgba_pixels.empty()) {
    const ImTextureID plume_texture_id =
        TryRegisterPlumeTexture(&texture, texture.width, texture.height,
                                texture.rgba_pixels);
    if (plume_texture_id != 0) {
      return plume_texture_id;
    }
  }
  return GuiImTextureId(texture);
}

ImTextureID AchievementIconImTextureId(const AchievementIconTexture &icon) {
  if (!icon.rgba_pixels.empty()) {
    const ImTextureID plume_texture_id =
        TryRegisterPlumeTexture(&icon, icon.width, icon.height,
                                icon.rgba_pixels);
    if (plume_texture_id != 0) {
      return plume_texture_id;
    }
  }

  if (icon.texture == nullptr) {
    return 0;
  }
  return static_cast<ImTextureID>(
      reinterpret_cast<std::uintptr_t>(icon.texture.get()));
}

ImU32 ModulateImColor(const std::uint8_t r, const std::uint8_t g,
                      const std::uint8_t b, const std::uint8_t a,
                      const ImU32 tint) {
  const std::uint32_t tr = tint & 0xFFu;
  const std::uint32_t tg = (tint >> 8u) & 0xFFu;
  const std::uint32_t tb = (tint >> 16u) & 0xFFu;
  const std::uint32_t ta = (tint >> 24u) & 0xFFu;
  return IM_COL32((static_cast<std::uint32_t>(r) * tr) / 255u,
                  (static_cast<std::uint32_t>(g) * tg) / 255u,
                  (static_cast<std::uint32_t>(b) * tb) / 255u,
                  (static_cast<std::uint32_t>(a) * ta) / 255u);
}

ImU32 SampleFableGuiTextureBilinear(const FableGuiTexture &texture,
                                    const float normalized_x,
                                    const float normalized_y,
                                    const ImU32 tint) {
  if (texture.rgba_pixels.empty() || texture.width == 0 ||
      texture.height == 0) {
    return 0;
  }

  const float source_x =
      std::clamp(normalized_x, 0.0f, 1.0f) *
      static_cast<float>(texture.width > 1 ? texture.width - 1 : 0);
  const float source_y =
      std::clamp(normalized_y, 0.0f, 1.0f) *
      static_cast<float>(texture.height > 1 ? texture.height - 1 : 0);
  const std::uint32_t x0 = static_cast<std::uint32_t>(source_x);
  const std::uint32_t y0 = static_cast<std::uint32_t>(source_y);
  const std::uint32_t x1 = std::min<std::uint32_t>(texture.width - 1, x0 + 1);
  const std::uint32_t y1 = std::min<std::uint32_t>(texture.height - 1, y0 + 1);
  const float tx = source_x - static_cast<float>(x0);
  const float ty = source_y - static_cast<float>(y0);

  auto channel = [&](const std::uint32_t x, const std::uint32_t y,
                     const std::size_t component) -> float {
    const std::size_t offset =
        (static_cast<std::size_t>(y) * texture.width + x) * 4 + component;
    return static_cast<float>(texture.rgba_pixels[offset]);
  };

  auto blend = [&](const std::size_t component) -> std::uint8_t {
    const float top = channel(x0, y0, component) * (1.0f - tx) +
                      channel(x1, y0, component) * tx;
    const float bottom = channel(x0, y1, component) * (1.0f - tx) +
                         channel(x1, y1, component) * tx;
    const float value = top * (1.0f - ty) + bottom * ty;
    return static_cast<std::uint8_t>(std::clamp(value, 0.0f, 255.0f));
  };

  return ModulateImColor(blend(0), blend(1), blend(2), blend(3), tint);
}

bool DrawFableGuiTexturePixels(ImDrawList *draw_list,
                               const FableGuiTexture &texture,
                               const ImVec2 min, const ImVec2 max,
                               const ImVec2 uv_min, const ImVec2 uv_max,
                               const ImU32 tint,
                               const float max_cell_size = 3.0f) {
  if (draw_list == nullptr || texture.rgba_pixels.empty() ||
      texture.width == 0 || texture.height == 0 || min.x >= max.x ||
      min.y >= max.y) {
    return false;
  }

  const float width = max.x - min.x;
  const float height = max.y - min.y;
  const int cells_x =
      std::clamp(static_cast<int>(std::ceil(width / max_cell_size)), 8, 256);
  const int cells_y =
      std::clamp(static_cast<int>(std::ceil(height / max_cell_size)), 8, 128);
  const float cell_width = width / static_cast<float>(cells_x);
  const float cell_height = height / static_cast<float>(cells_y);

  for (int y = 0; y < cells_y; ++y) {
    const float fy0 = static_cast<float>(y) / static_cast<float>(cells_y);
    const float fy1 = static_cast<float>(y + 1) / static_cast<float>(cells_y);
    const float sample_y0 = uv_min.y + (uv_max.y - uv_min.y) * fy0;
    const float sample_y1 = uv_min.y + (uv_max.y - uv_min.y) * fy1;
    for (int x = 0; x < cells_x; ++x) {
      const float fx0 = static_cast<float>(x) / static_cast<float>(cells_x);
      const float fx1 =
          static_cast<float>(x + 1) / static_cast<float>(cells_x);
      const float sample_x0 = uv_min.x + (uv_max.x - uv_min.x) * fx0;
      const float sample_x1 = uv_min.x + (uv_max.x - uv_min.x) * fx1;
      const ImU32 color00 =
          SampleFableGuiTextureBilinear(texture, sample_x0, sample_y0, tint);
      const ImU32 color10 =
          SampleFableGuiTextureBilinear(texture, sample_x1, sample_y0, tint);
      const ImU32 color11 =
          SampleFableGuiTextureBilinear(texture, sample_x1, sample_y1, tint);
      const ImU32 color01 =
          SampleFableGuiTextureBilinear(texture, sample_x0, sample_y1, tint);
      if (((color00 | color10 | color11 | color01) & IM_COL32_A_MASK) == 0) {
        continue;
      }

      const ImVec2 cell_min(min.x + static_cast<float>(x) * cell_width,
                            min.y + static_cast<float>(y) * cell_height);
      const ImVec2 cell_max(
          min.x + static_cast<float>(x + 1) * cell_width + 0.35f,
          min.y + static_cast<float>(y + 1) * cell_height + 0.35f);
      draw_list->AddRectFilledMultiColor(cell_min, cell_max, color00, color10,
                                         color11, color01);
    }
  }
  return true;
}

bool DrawFableGuiImage(ImDrawList *draw_list, const FableGuiTexture &texture,
                       const ImVec2 min, const ImVec2 max, const ImU32 tint) {
  const ImTextureID texture_id = FableGuiImTextureId(texture);
  if (texture_id != 0) {
    draw_list->AddImage(texture_id, min, max, ImVec2(0.0f, 0.0f),
                        ImVec2(1.0f, 1.0f), tint);
    return true;
  }
  return DrawFableGuiTexturePixels(draw_list, texture, min, max,
                                   ImVec2(0.0f, 0.0f), ImVec2(1.0f, 1.0f),
                                   tint);
}

bool DrawFableGuiImageUv(ImDrawList *draw_list, const FableGuiTexture &texture,
                         const ImVec2 min, const ImVec2 max,
                         const ImVec2 uv_min, const ImVec2 uv_max,
                         const ImU32 tint) {
  const ImTextureID texture_id = FableGuiImTextureId(texture);
  if (texture_id != 0) {
    draw_list->AddImage(texture_id, min, max, uv_min, uv_max, tint);
    return true;
  }
  return DrawFableGuiTexturePixels(draw_list, texture, min, max, uv_min, uv_max,
                                   tint);
}

bool DrawFableGuiNineSlice(ImDrawList *draw_list, const FableGuiTexture &texture,
                           ImVec2 min, ImVec2 max, ImVec4 source_border,
                           ImVec4 dest_border, ImU32 tint);

bool DrawFableGuiNineSlice(ImDrawList *draw_list, const FableGuiTexture &texture,
                           const ImVec2 min, const ImVec2 max,
                           const ImVec2 source_border,
                           const ImVec2 dest_border,
                           const ImU32 tint) {
  const ImVec4 source_borders(source_border.x, source_border.y,
                              source_border.x, source_border.y);
  const ImVec4 dest_borders(dest_border.x, dest_border.y, dest_border.x,
                            dest_border.y);
  return DrawFableGuiNineSlice(draw_list, texture, min, max, source_borders,
                               dest_borders, tint);
}

bool DrawFableGuiNineSlice(ImDrawList *draw_list, const FableGuiTexture &texture,
                           const ImVec2 min, const ImVec2 max,
                           const ImVec4 source_border,
                           const ImVec4 dest_border,
                           const ImU32 tint) {
  const ImTextureID texture_id = FableGuiImTextureId(texture);
  const bool has_gpu_texture = texture_id != 0;
  const bool has_cpu_texture = !texture.rgba_pixels.empty() &&
                               texture.width != 0 && texture.height != 0;
  if (draw_list == nullptr || (!has_gpu_texture && !has_cpu_texture) ||
      min.x >= max.x || min.y >= max.y) {
    return false;
  }

  const float texture_width = static_cast<float>(texture.width);
  const float texture_height = static_cast<float>(texture.height);
  const float dst_width = max.x - min.x;
  const float dst_height = max.y - min.y;
  const float src_left =
      std::clamp(source_border.x, 0.0f, texture_width * 0.5f);
  const float src_top =
      std::clamp(source_border.y, 0.0f, texture_height * 0.5f);
  const float src_right =
      std::clamp(source_border.z, 0.0f, texture_width * 0.5f);
  const float src_bottom =
      std::clamp(source_border.w, 0.0f, texture_height * 0.5f);
  const float dst_left = std::clamp(dest_border.x, 0.0f, dst_width * 0.5f);
  const float dst_top = std::clamp(dest_border.y, 0.0f, dst_height * 0.5f);
  const float dst_right = std::clamp(dest_border.z, 0.0f, dst_width * 0.5f);
  const float dst_bottom = std::clamp(dest_border.w, 0.0f, dst_height * 0.5f);

  const std::array<float, 4> x = {min.x, min.x + dst_left, max.x - dst_right,
                                 max.x};
  const std::array<float, 4> y = {min.y, min.y + dst_top, max.y - dst_bottom,
                                 max.y};
  const std::array<float, 4> u = {0.0f, src_left / texture_width,
                                 1.0f - src_right / texture_width, 1.0f};
  const std::array<float, 4> v = {0.0f, src_top / texture_height,
                                 1.0f - src_bottom / texture_height, 1.0f};

  for (int row = 0; row < 3; ++row) {
    if (y[row] >= y[row + 1]) {
      continue;
    }
    for (int column = 0; column < 3; ++column) {
      if (x[column] >= x[column + 1]) {
        continue;
      }
      if (has_gpu_texture) {
        draw_list->AddImage(texture_id, ImVec2(x[column], y[row]),
                            ImVec2(x[column + 1], y[row + 1]),
                            ImVec2(u[column], v[row]),
                            ImVec2(u[column + 1], v[row + 1]), tint);
      } else {
        DrawFableGuiTexturePixels(draw_list, texture,
                                  ImVec2(x[column], y[row]),
                                  ImVec2(x[column + 1], y[row + 1]),
                                  ImVec2(u[column], v[row]),
                                  ImVec2(u[column + 1], v[row + 1]), tint,
                                  2.5f);
      }
    }
  }
  return true;
}

ImFont *ToastTitleFont() {
  return g_fable_toast_title_font != nullptr ? g_fable_toast_title_font
                                             : ImGui::GetFont();
}

ImFont *ToastBodyFont() {
  return g_fable_toast_body_font != nullptr ? g_fable_toast_body_font
                                            : ImGui::GetFont();
}

float ToastFontSize(const ImFont *font, const float fallback_size) {
  return font != nullptr && font->LegacySize > 0.0f ? font->LegacySize
                                                    : fallback_size;
}

void DrawToastText(ImDrawList *draw_list, ImFont *font, const float font_size,
                   const ImVec2 pos, const ImU32 color, const char *text,
                   const float wrap_width = 0.0f) {
  if (draw_list == nullptr || text == nullptr || text[0] == '\0') {
    return;
  }

  const char *text_end = text + std::strlen(text);
  const std::uint32_t color_alpha = (color >> 24u) & 0xFFu;
  const std::uint32_t shadow_alpha =
      std::min<std::uint32_t>(color_alpha, 155u);
  const float shadow_offset = std::clamp(font_size / 12.0f, 1.2f, 2.8f);
  draw_list->AddText(font, font_size,
                     ImVec2(pos.x + shadow_offset, pos.y + shadow_offset),
                     IM_COL32(32, 18, 8, shadow_alpha), text, text_end,
                     wrap_width);
  draw_list->AddText(font, font_size, pos, color, text, text_end, wrap_width);
}

void DrawToastTextCentered(ImDrawList *draw_list, ImFont *font,
                           const float font_size, const ImVec2 min,
                           const ImVec2 max, const ImU32 color,
                           const char *text) {
  if (draw_list == nullptr || font == nullptr || text == nullptr ||
      text[0] == '\0' || min.x >= max.x || min.y >= max.y) {
    return;
  }

  const char *text_end = text + std::strlen(text);
  const float available_width = max.x - min.x;
  float draw_size = font_size;
  ImVec2 text_size = font->CalcTextSizeA(
      draw_size, std::numeric_limits<float>::max(), 0.0f, text, text_end);
  if (text_size.x > available_width && text_size.x > 0.0f) {
    draw_size *= std::clamp(available_width / text_size.x, 0.72f, 1.0f);
    text_size = font->CalcTextSizeA(draw_size, std::numeric_limits<float>::max(),
                                    0.0f, text, text_end);
  }

  DrawToastText(
      draw_list, font, draw_size,
      ImVec2(min.x + std::max(0.0f, available_width - text_size.x) * 0.5f,
             min.y + std::max(0.0f, (max.y - min.y) - text_size.y) * 0.5f),
      color, text);
}

float ToastTextWidth(ImFont *font, const float font_size,
                     const std::string &text) {
  if (font == nullptr || text.empty()) {
    return 0.0f;
  }
  return font
      ->CalcTextSizeA(font_size, std::numeric_limits<float>::max(), 0.0f,
                      text.c_str(), text.c_str() + text.size())
      .x;
}

std::vector<std::string> WrapToastText(ImFont *font, const float font_size,
                                       const char *text,
                                       const float available_width,
                                       const int max_lines) {
  std::vector<std::string> lines;
  if (font == nullptr || text == nullptr || text[0] == '\0' ||
      available_width <= 0.0f) {
    return lines;
  }

  const bool limit_lines = max_lines > 0;
  std::string current;
  const char *cursor = text;
  while (*cursor != '\0') {
    while (*cursor == ' ' || *cursor == '\t' || *cursor == '\n' ||
           *cursor == '\r') {
      ++cursor;
    }
    if (*cursor == '\0') {
      break;
    }

    const char *word_begin = cursor;
    while (*cursor != '\0' && *cursor != ' ' && *cursor != '\t' &&
           *cursor != '\n' && *cursor != '\r') {
      ++cursor;
    }

    std::string word(word_begin, cursor);
    std::string candidate = current.empty() ? word : current + " " + word;
    if (!current.empty() &&
        ToastTextWidth(font, font_size, candidate) > available_width) {
      lines.push_back(current);
      current = std::move(word);
      if (limit_lines && static_cast<int>(lines.size()) >= max_lines) {
        break;
      }
    } else {
      current = std::move(candidate);
    }
  }

  if (!current.empty() &&
      (!limit_lines || static_cast<int>(lines.size()) < max_lines)) {
    lines.push_back(std::move(current));
  }
  return lines;
}

std::string TrimToastWhitespace(std::string text) {
  const auto is_space = [](const unsigned char value) {
    return std::isspace(value) != 0;
  };
  text.erase(text.begin(),
             std::find_if(text.begin(), text.end(),
                          [&](const char value) {
                            return !is_space(static_cast<unsigned char>(value));
                          }));
  text.erase(std::find_if(text.rbegin(), text.rend(),
                          [&](const char value) {
                            return !is_space(static_cast<unsigned char>(value));
                          })
                 .base(),
             text.end());
  return text;
}

std::vector<std::string> BuildToastSentenceLines(ImFont *font,
                                                 const float font_size,
                                                 const char *text,
                                                 const float available_width) {
  std::vector<std::string> lines;
  if (font == nullptr || text == nullptr || text[0] == '\0') {
    return lines;
  }

  const char *sentence_begin = text;
  for (const char *cursor = text;; ++cursor) {
    const bool is_line_break_punctuation =
        *cursor == '.' || *cursor == ',' || *cursor == '?' || *cursor == '!';
    if (!is_line_break_punctuation && *cursor != '\0') {
      continue;
    }

    std::string sentence(sentence_begin, cursor);
    sentence = TrimToastWhitespace(std::move(sentence));
    if (!sentence.empty()) {
      if (is_line_break_punctuation) {
        sentence.push_back(*cursor);
      }
      std::vector<std::string> wrapped =
          WrapToastText(font, font_size, sentence.c_str(), available_width, 0);
      lines.insert(lines.end(), wrapped.begin(), wrapped.end());
    }

    if (*cursor == '\0') {
      break;
    }
    sentence_begin = cursor + 1;
  }

  return lines;
}

void TrimToastTextToWidth(ImFont *font, const float font_size,
                          const float available_width, std::string &text) {
  if (font == nullptr || text.empty()) {
    return;
  }

  constexpr const char *kSuffix = "...";
  while (!text.empty() &&
         ToastTextWidth(font, font_size, text + kSuffix) > available_width) {
    text.pop_back();
  }
  if (!text.empty()) {
    text += kSuffix;
  }
}

void DrawToastTextWrappedCentered(ImDrawList *draw_list, ImFont *font,
                                  const float font_size, const ImVec2 min,
                                  const ImVec2 max, const ImU32 color,
                                  const char *text,
                                  const int max_lines = 2,
                                  const float scroll_progress = 0.0f) {
  if (draw_list == nullptr || font == nullptr || text == nullptr ||
      text[0] == '\0' || min.x >= max.x || min.y >= max.y) {
    return;
  }

  const float available_width = max.x - min.x;
  const float available_height = max.y - min.y;
  (void)max_lines;
  float draw_size = font_size;
  std::vector<std::string> lines;
  for (int step = 0; step < 8; ++step) {
    lines = BuildToastSentenceLines(font, draw_size, text, available_width);
    const float line_height =
        font->CalcTextSizeA(draw_size, std::numeric_limits<float>::max(), 0.0f,
                            "Mg")
            .y;
    const float total_height =
        static_cast<float>(lines.size()) * line_height +
        std::max(0, static_cast<int>(lines.size()) - 1) * draw_size * 0.1f;
    bool fits_width = !lines.empty();
    for (const std::string &line : lines) {
      fits_width =
          fits_width && ToastTextWidth(font, draw_size, line) <= available_width;
    }
    if (fits_width &&
        (total_height <= available_height || draw_size <= font_size * 0.86f)) {
      break;
    }
    draw_size *= 0.94f;
  }

  if (lines.empty()) {
    return;
  }
  for (std::string &line : lines) {
    if (ToastTextWidth(font, draw_size, line) > available_width) {
      TrimToastTextToWidth(font, draw_size, available_width, line);
    }
  }

  const float line_height =
      font->CalcTextSizeA(draw_size, std::numeric_limits<float>::max(), 0.0f,
                          "Mg")
          .y;
  const float line_spacing = draw_size * 0.1f;
  const float total_height =
      static_cast<float>(lines.size()) * line_height +
      std::max(0, static_cast<int>(lines.size()) - 1) * line_spacing;
  float y = min.y + std::max(0.0f, available_height - total_height) * 0.5f;
  if (total_height > available_height) {
    const float hold_fraction = 0.16f;
    const float scroll_t =
        std::clamp((std::clamp(scroll_progress, 0.0f, 1.0f) - hold_fraction) /
                       (1.0f - hold_fraction * 2.0f),
                   0.0f, 1.0f);
    const float eased_scroll_t = scroll_t * scroll_t * (3.0f - 2.0f * scroll_t);
    y = min.y - (total_height - available_height) * eased_scroll_t;
  }

  draw_list->PushClipRect(min, max, true);
  for (const std::string &line : lines) {
    const float line_width = ToastTextWidth(font, draw_size, line);
    DrawToastText(draw_list, font, draw_size,
                  ImVec2(min.x +
                             std::max(0.0f, available_width - line_width) *
                                 0.5f,
                         y),
                  color, line.c_str());
    y += line_height + line_spacing;
  }
  draw_list->PopClipRect();
}

void DrawToastOuterFrame(ImDrawList *draw_list, const ImVec2 min,
                         const ImVec2 max,
                         const std::function<ImU32(int, int, int, int)>
                             &faded_color,
                         const float scale) {
  if (draw_list == nullptr) {
    return;
  }
  const auto s = [scale](const float value) { return value * scale; };

  const ImVec2 outer_min(min.x + s(4.0f), min.y + s(4.0f));
  const ImVec2 outer_max(max.x - s(4.0f), max.y - s(4.0f));
  draw_list->AddRect(outer_min, outer_max, faded_color(42, 21, 8, 228), 4.0f,
                     0, s(2.4f));
  draw_list->AddRect(ImVec2(outer_min.x + s(3.5f), outer_min.y + s(3.5f)),
                     ImVec2(outer_max.x - s(3.5f), outer_max.y - s(3.5f)),
                     faded_color(210, 160, 78, 172), 3.0f, 0, s(1.1f));
}

AchievementIconTexture &GetOrCreateIconTexture(
    const AchievementDefinition &definition) {
  AchievementIconTexture &icon =
      g_icon_textures[definition.achievement_id];
  icon.achievement_id = definition.achievement_id;
  icon.image_id = definition.image_id;

  if (icon.texture || icon.decode_failed) {
    return icon;
  }

  if (!icon.rgba_pixels.empty() && g_immediate_drawer != nullptr) {
    icon.texture = g_immediate_drawer->CreateTexture(
        icon.width, icon.height, rex::ui::ImmediateTextureFilter::kLinear,
        false, icon.rgba_pixels.data());
    icon.decode_failed = icon.texture == nullptr;
    return icon;
  }

  const std::vector<std::uint8_t> icon_bytes =
      GetAchievementIconPngSnapshot(definition.achievement_id);
  icon.png_byte_count = icon_bytes.size();
  if (icon_bytes.empty()) {
    return icon;
  }

  if (icon_bytes.size() >
      static_cast<std::size_t>(std::numeric_limits<int>::max())) {
    icon.decode_failed = true;
    return icon;
  }

  int width = 0;
  int height = 0;
  int channel_count = 0;
  stbi_uc *rgba_pixels = stbi_load_from_memory(
      icon_bytes.data(), static_cast<int>(icon_bytes.size()), &width, &height,
      &channel_count, 4);
  (void)channel_count;

  if (rgba_pixels == nullptr || width <= 0 || height <= 0) {
    if (rgba_pixels != nullptr) {
      stbi_image_free(rgba_pixels);
    }
    icon.decode_failed = true;
    return icon;
  }

  icon.width = static_cast<std::uint32_t>(width);
  icon.height = static_cast<std::uint32_t>(height);
  icon.rgba_pixels.assign(rgba_pixels,
                          rgba_pixels +
                              static_cast<std::size_t>(width) *
                                  static_cast<std::size_t>(height) * 4);
  if (g_immediate_drawer != nullptr) {
    icon.texture = g_immediate_drawer->CreateTexture(
        icon.width, icon.height, rex::ui::ImmediateTextureFilter::kLinear,
        false, rgba_pixels);
  }
  stbi_image_free(rgba_pixels);
  icon.decode_failed =
      icon.rgba_pixels.empty() ||
      (g_immediate_drawer != nullptr && icon.texture == nullptr);
  return icon;
}

ImU32 SampleIconBilinear(const AchievementIconTexture &icon,
                         const float normalized_x,
                         const float normalized_y) {
  const float source_x =
      std::clamp(normalized_x, 0.0f, 1.0f) *
      static_cast<float>(icon.width > 1 ? icon.width - 1 : 0);
  const float source_y =
      std::clamp(normalized_y, 0.0f, 1.0f) *
      static_cast<float>(icon.height > 1 ? icon.height - 1 : 0);
  const std::uint32_t x0 = static_cast<std::uint32_t>(source_x);
  const std::uint32_t y0 = static_cast<std::uint32_t>(source_y);
  const std::uint32_t x1 = std::min<std::uint32_t>(icon.width - 1, x0 + 1);
  const std::uint32_t y1 = std::min<std::uint32_t>(icon.height - 1, y0 + 1);
  const float tx = source_x - static_cast<float>(x0);
  const float ty = source_y - static_cast<float>(y0);

  auto channel = [&](const std::uint32_t x, const std::uint32_t y,
                     const std::size_t component) -> float {
    const std::size_t offset =
        (static_cast<std::size_t>(y) * icon.width + x) * 4 + component;
    return static_cast<float>(icon.rgba_pixels[offset]);
  };

  auto blend = [&](const std::size_t component) -> std::uint8_t {
    const float top = channel(x0, y0, component) * (1.0f - tx) +
                      channel(x1, y0, component) * tx;
    const float bottom = channel(x0, y1, component) * (1.0f - tx) +
                         channel(x1, y1, component) * tx;
    const float value = top * (1.0f - ty) + bottom * ty;
    return static_cast<std::uint8_t>(std::clamp(value, 0.0f, 255.0f));
  };

  return IM_COL32(blend(0), blend(1), blend(2), blend(3));
}

void DrawIconPixelPreview(const AchievementIconTexture &icon,
                          const float icon_size) {
  if (icon.rgba_pixels.empty() || icon.width == 0 || icon.height == 0) {
    ImGui::TextDisabled(icon.decode_failed ? "!" : "-");
    return;
  }

  const ImVec2 origin = ImGui::GetCursorScreenPos();
  ImDrawList *draw_list = ImGui::GetWindowDrawList();
  draw_list->AddRectFilled(
      origin, ImVec2(origin.x + icon_size, origin.y + icon_size),
      IM_COL32(12, 20, 14, 180));

  const int cells_per_axis =
      std::clamp(static_cast<int>(icon_size), 24,
                 static_cast<int>(std::min<std::uint32_t>(
                     96, std::max(icon.width, icon.height))));
  const float cell_size = icon_size / static_cast<float>(cells_per_axis);

  for (int y = 0; y < cells_per_axis; ++y) {
    for (int x = 0; x < cells_per_axis; ++x) {
      const ImU32 color = SampleIconBilinear(
          icon, (static_cast<float>(x) + 0.5f) /
                    static_cast<float>(cells_per_axis),
          (static_cast<float>(y) + 0.5f) /
              static_cast<float>(cells_per_axis));
      if ((color & IM_COL32_A_MASK) == 0) {
        continue;
      }

      const ImVec2 min(origin.x + static_cast<float>(x) * cell_size,
                       origin.y + static_cast<float>(y) * cell_size);
      const ImVec2 max(origin.x + static_cast<float>(x + 1) * cell_size + 0.5f,
                       origin.y + static_cast<float>(y + 1) * cell_size + 0.5f);
      draw_list->AddRectFilled(min, max, color);
    }
  }

  draw_list->AddRect(origin, ImVec2(origin.x + icon_size, origin.y + icon_size),
                     IM_COL32(110, 135, 112, 190));

  ImGui::Dummy(ImVec2(icon_size, icon_size));
}

void DrawIconTexture(const AchievementIconTexture &icon,
                     const float max_icon_size = 36.0f) {
  const ImTextureID texture_id = AchievementIconImTextureId(icon);
  if (texture_id == 0) {
    DrawIconPixelPreview(icon, max_icon_size);
    return;
  }

  const float scale =
      std::min(max_icon_size / static_cast<float>(icon.width),
               max_icon_size / static_cast<float>(icon.height));
  const ImVec2 size(static_cast<float>(icon.width) * scale,
                    static_cast<float>(icon.height) * scale);
  ImGui::Image(texture_id, size);
}

bool DrawCircularIconPixels(ImDrawList *draw_list,
                            const AchievementIconTexture &icon,
                            const ImVec2 center,
                            const float radius) {
  if (draw_list == nullptr || icon.rgba_pixels.empty() || icon.width == 0 ||
      icon.height == 0 || radius <= 0.0f) {
    return false;
  }

  const int cells =
      std::clamp(static_cast<int>(std::ceil(radius * 2.0f)), 24, 72);
  const float diameter = radius * 2.0f;
  const float cell_size = diameter / static_cast<float>(cells);
  const ImVec2 min(center.x - radius, center.y - radius);
  const float radius_squared = radius * radius;

  for (int y = 0; y < cells; ++y) {
    const float sample_y = (static_cast<float>(y) + 0.5f) /
                           static_cast<float>(cells);
    const float py = min.y + (static_cast<float>(y) + 0.5f) * cell_size;
    for (int x = 0; x < cells; ++x) {
      const float sample_x = (static_cast<float>(x) + 0.5f) /
                             static_cast<float>(cells);
      const float px = min.x + (static_cast<float>(x) + 0.5f) * cell_size;
      const float dx = px - center.x;
      const float dy = py - center.y;
      if (dx * dx + dy * dy > radius_squared) {
        continue;
      }

      const ImU32 color = SampleIconBilinear(icon, sample_x, sample_y);
      if ((color & IM_COL32_A_MASK) == 0) {
        continue;
      }

      draw_list->AddRectFilled(
          ImVec2(min.x + static_cast<float>(x) * cell_size,
                 min.y + static_cast<float>(y) * cell_size),
          ImVec2(min.x + static_cast<float>(x + 1) * cell_size + 0.45f,
                 min.y + static_cast<float>(y + 1) * cell_size + 0.45f),
          color);
    }
  }

  return true;
}

void DrawGuiTexturePreview(FableGuiTexture &texture,
                           const float max_width,
                           const float max_height) {
  if (texture.texture == nullptr && texture.rgba_pixels.empty()) {
    ImGui::TextDisabled("%s", texture.status.empty() ? "not decoded"
                                                     : texture.status.c_str());
    return;
  }

  const float texture_width = texture.texture != nullptr
                                  ? static_cast<float>(texture.texture->width)
                                  : static_cast<float>(texture.width);
  const float texture_height = texture.texture != nullptr
                                   ? static_cast<float>(texture.texture->height)
                                   : static_cast<float>(texture.height);
  const float scale =
      std::min(max_width / texture_width, max_height / texture_height);
  const ImVec2 size(texture_width * scale, texture_height * scale);
  const ImTextureID texture_id = FableGuiImTextureId(texture);
  if (texture_id != 0) {
    ImGui::Image(texture_id, size);
  } else {
    const ImVec2 origin = ImGui::GetCursorScreenPos();
    DrawFableGuiTexturePixels(ImGui::GetWindowDrawList(), texture, origin,
                              ImVec2(origin.x + size.x, origin.y + size.y),
                              ImVec2(0.0f, 0.0f), ImVec2(1.0f, 1.0f),
                              IM_COL32_WHITE, 3.0f);
    ImGui::Dummy(size);
  }
}

void DrawDefinitionRow(const AchievementDefinition &definition,
                       const std::unordered_set<std::uint64_t> &unlocked_keys,
                       const int selected_user_index,
                       std::uint32_t &selected_achievement_id) {
  const bool unlocked = unlocked_keys.contains(AchievementUnlockKey(
      static_cast<std::uint32_t>(std::max(0, selected_user_index)),
      definition.achievement_id));
  const bool selected = selected_achievement_id == definition.achievement_id;

  ImGui::TableNextRow();
  ImGui::TableSetColumnIndex(0);
  AchievementIconTexture &icon = GetOrCreateIconTexture(definition);
  DrawIconTexture(icon);

  ImGui::TableSetColumnIndex(1);
  ImGui::TextUnformatted(unlocked ? "Yes" : "No");

  ImGui::TableSetColumnIndex(2);
  ImGui::Text("%u", definition.achievement_id);

  ImGui::TableSetColumnIndex(3);
  ImGui::Text("%u", definition.gamerscore);

  ImGui::TableSetColumnIndex(4);
  ImGui::Text("%u", definition.image_id);

  ImGui::TableSetColumnIndex(5);
  ImGui::Text("%zu", icon.png_byte_count);

  ImGui::TableSetColumnIndex(6);
  ImGui::PushID(static_cast<int>(definition.achievement_id));
  const std::string label = definition.name.empty()
                                ? std::string("<unnamed>")
                                : definition.name;
  if (ImGui::Selectable(label.c_str(), selected,
                        ImGuiSelectableFlags_SpanAllColumns)) {
    selected_achievement_id = definition.achievement_id;
  }
  ImGui::PopID();

  if (ImGui::IsItemHovered()) {
    ImGui::BeginTooltip();
    ImGui::Text("ID: %u", definition.achievement_id);
    ImGui::Text("Gamerscore: %u", definition.gamerscore);
    ImGui::Text("Image: %u", definition.image_id);
    ImGui::Text("PNG: %zu bytes", icon.png_byte_count);
    if (icon.width != 0 && icon.height != 0) {
      ImGui::Text("Decoded: %ux%u", icon.width, icon.height);
    }
    ImGui::EndTooltip();
  }
}

void DrawAchievementDetailsPanel(
    const AchievementDefinition &definition,
    const std::unordered_set<std::uint64_t> &unlocked_keys,
    const int selected_user_index) {
  const bool unlocked = unlocked_keys.contains(AchievementUnlockKey(
      static_cast<std::uint32_t>(std::max(0, selected_user_index)),
      definition.achievement_id));
  AchievementIconTexture &icon = GetOrCreateIconTexture(definition);

  ImGui::SeparatorText("Details");
  ImGui::BeginGroup();
  DrawIconTexture(icon, 96.0f);
  ImGui::EndGroup();

  ImGui::SameLine();
  ImGui::BeginGroup();
  if (definition.name.empty()) {
    ImGui::TextDisabled("<unnamed>");
  } else {
    ImGui::TextUnformatted(definition.name.c_str());
  }

  if (ImGui::BeginTable("##achievement_detail_fields", 2,
                        ImGuiTableFlags_SizingFixedFit)) {
    ImGui::TableSetupColumn("Field", ImGuiTableColumnFlags_WidthFixed, 84.0f);
    ImGui::TableSetupColumn("Value");

    ImGui::TableNextRow();
    ImGui::TableSetColumnIndex(0);
    ImGui::TextUnformatted("Unlocked");
    ImGui::TableSetColumnIndex(1);
    ImGui::TextUnformatted(unlocked ? "Yes" : "No");

    ImGui::TableNextRow();
    ImGui::TableSetColumnIndex(0);
    ImGui::TextUnformatted("ID");
    ImGui::TableSetColumnIndex(1);
    ImGui::Text("%u", definition.achievement_id);

    ImGui::TableNextRow();
    ImGui::TableSetColumnIndex(0);
    ImGui::TextUnformatted("Score");
    ImGui::TableSetColumnIndex(1);
    ImGui::Text("%u", definition.gamerscore);

    ImGui::TableNextRow();
    ImGui::TableSetColumnIndex(0);
    ImGui::TextUnformatted("Image");
    ImGui::TableSetColumnIndex(1);
    ImGui::Text("%u", definition.image_id);

    ImGui::TableNextRow();
    ImGui::TableSetColumnIndex(0);
    ImGui::TextUnformatted("PNG");
    ImGui::TableSetColumnIndex(1);
    ImGui::Text("%zu bytes", icon.png_byte_count);

    ImGui::TableNextRow();
    ImGui::TableSetColumnIndex(0);
    ImGui::TextUnformatted("Decoded");
    ImGui::TableSetColumnIndex(1);
    if (icon.width != 0 && icon.height != 0) {
      ImGui::Text("%ux%u", icon.width, icon.height);
    } else {
      ImGui::TextDisabled("-");
    }

    ImGui::TableNextRow();
    ImGui::TableSetColumnIndex(0);
    ImGui::TextUnformatted("Flags");
    ImGui::TableSetColumnIndex(1);
    ImGui::Text("0x%08X", definition.flags);

    ImGui::EndTable();
  }
  ImGui::EndGroup();

  ImGui::Spacing();
  ImGui::TextUnformatted("Unlocked description");
  ImGui::TextWrapped("%s", definition.unlocked_description.empty()
                               ? "<no unlocked description>"
                               : definition.unlocked_description.c_str());
  ImGui::TextUnformatted("Locked description");
  ImGui::TextWrapped("%s", definition.locked_description.empty()
                               ? "<no locked description>"
                               : definition.locked_description.c_str());
}

void DrawUnlockRow(
    const AchievementUnlock &unlock,
    const std::unordered_map<std::uint32_t, AchievementDefinition>
        &definitions_by_id) {
  ImGui::TableNextRow();
  ImGui::TableSetColumnIndex(0);
  ImGui::Text("%" PRIu64, unlock.sequence);

  ImGui::TableSetColumnIndex(1);
  ImGui::Text("%u", unlock.user_index);

  ImGui::TableSetColumnIndex(2);
  ImGui::Text("%u", unlock.achievement_id);

  ImGui::TableSetColumnIndex(3);
  const auto definition_it = definitions_by_id.find(unlock.achievement_id);
  if (definition_it == definitions_by_id.end() ||
      definition_it->second.name.empty()) {
    ImGui::TextDisabled("<metadata pending>");
  } else {
    ImGui::TextUnformatted(definition_it->second.name.c_str());
  }
}

void AchievementToastDialog::QueueNewUnlocks() {
  const std::vector<AchievementUnlock> unlocks =
      GetUnlockedAchievementsSnapshot();
  if (unlocks.empty()) {
    pending_unlocks_.clear();
    last_seen_sequence_ = 0;
    current_age_seconds_ = 0.0f;
    return;
  }

  for (const AchievementUnlock &unlock : unlocks) {
    if (unlock.sequence <= last_seen_sequence_) {
      continue;
    }

    pending_unlocks_.push_back(unlock);
    last_seen_sequence_ = std::max(last_seen_sequence_, unlock.sequence);
  }
}

void AchievementToastDialog::DrawCurrentToast(ImGuiIO &io) {
  constexpr float kUnlockPhaseSeconds = 5.0f;
  constexpr float kDescriptionPhaseSeconds = 5.0f;
  constexpr float kToastDurationSeconds =
      kUnlockPhaseSeconds + kDescriptionPhaseSeconds;
  constexpr float kFadeInSeconds = 0.22f;
  constexpr float kMessageFadeSeconds = 0.32f;
  constexpr float kFadeOutSeconds = 0.55f;
  constexpr float kBaseToastHeight = 106.0f;
  constexpr float kBaseBottomMargin = 78.0f;
  constexpr float kBaseIconRadius = 23.0f;
  constexpr float kBaseIconRingRadius = 31.0f;

  if (pending_unlocks_.empty()) {
    current_age_seconds_ = 0.0f;
    return;
  }

  current_age_seconds_ += std::max(io.DeltaTime, 1.0f / 240.0f);
  if (current_age_seconds_ >= kToastDurationSeconds) {
    pending_unlocks_.pop_front();
    current_age_seconds_ = 0.0f;
    return;
  }

  const AchievementUnlock &unlock = pending_unlocks_.front();
  AchievementDefinition definition =
      GetAchievementDefinitionSnapshot(unlock.achievement_id);
  if (definition.achievement_id == 0) {
    definition.achievement_id = unlock.achievement_id;
    definition.name = "Achievement Unlocked";
  }

  const float fade_in =
      std::clamp(current_age_seconds_ / kFadeInSeconds, 0.0f, 1.0f);
  const float fade_out = std::clamp(
      (kToastDurationSeconds - current_age_seconds_) / kFadeOutSeconds, 0.0f,
      1.0f);
  const float alpha = std::min(fade_in, fade_out);
  const bool showing_description =
      current_age_seconds_ >= kUnlockPhaseSeconds;
  const float description_elapsed =
      std::max(0.0f, current_age_seconds_ - kUnlockPhaseSeconds);
  const float description_progress =
      std::clamp(description_elapsed / kDescriptionPhaseSeconds, 0.0f, 1.0f);
  const float message_alpha =
      showing_description
          ? std::clamp((current_age_seconds_ - kUnlockPhaseSeconds) /
                           kMessageFadeSeconds,
                       0.0f, 1.0f)
          : std::clamp((kUnlockPhaseSeconds - current_age_seconds_) /
                           kMessageFadeSeconds,
                       0.0f, 1.0f);
  const float toast_scale = std::clamp(io.DisplaySize.y / 1080.0f, 0.85f, 2.0f);
  const auto s = [toast_scale](const float value) { return value * toast_scale; };
  const float toast_height = s(kBaseToastHeight);
  const float bottom_margin = s(kBaseBottomMargin);
  const float icon_radius = s(kBaseIconRadius);
  const float icon_ring_radius = s(kBaseIconRingRadius);
  const float slide_y = (1.0f - fade_in) * s(22.0f);
  const float toast_width =
      std::min(s(486.0f), std::max(s(340.0f), io.DisplaySize.x - s(48.0f)));
  const ImVec2 toast_pos((io.DisplaySize.x - toast_width) * 0.5f,
                         io.DisplaySize.y - toast_height - bottom_margin +
                             slide_y);

  ImGui::SetNextWindowPos(toast_pos, ImGuiCond_Always);
  ImGui::SetNextWindowSize(ImVec2(toast_width, toast_height),
                           ImGuiCond_Always);
  ImGui::SetNextWindowBgAlpha(0.0f);

  const ImGuiWindowFlags flags =
      ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoMove |
      ImGuiWindowFlags_NoSavedSettings | ImGuiWindowFlags_NoFocusOnAppearing |
      ImGuiWindowFlags_NoNav | ImGuiWindowFlags_NoInputs |
      ImGuiWindowFlags_NoBackground;
  ImGui::PushStyleVar(ImGuiStyleVar_WindowPadding, ImVec2(0.0f, 0.0f));
  ImGui::PushStyleVar(ImGuiStyleVar_Alpha, alpha);
  if (ImGui::Begin("##refii_achievement_toast", nullptr, flags)) {
    const ImVec2 min = ImGui::GetWindowPos();
    const ImVec2 max(min.x + toast_width, min.y + toast_height);
    const ImVec2 frame_min(min.x + s(8.0f), min.y + s(8.0f));
    const ImVec2 frame_max(max.x - s(8.0f), max.y - s(8.0f));
    const ImVec2 parchment_min(frame_min.x + s(5.0f), frame_min.y + s(5.0f));
    const ImVec2 parchment_max(frame_max.x - s(5.0f), frame_max.y - s(5.0f));
    ImDrawList *draw_list = ImGui::GetWindowDrawList();
    const auto faded_color = [alpha](const int r, const int g, const int b,
                                     const int a) {
      return IM_COL32(r, g, b,
                      static_cast<int>(static_cast<float>(a) * alpha));
    };
    const auto message_color = [alpha, message_alpha](
                                   const int r, const int g, const int b,
                                   const int a) {
      return IM_COL32(r, g, b,
                      static_cast<int>(static_cast<float>(a) * alpha *
                                       message_alpha));
    };
    FableGuiTexture &messagebox_texture =
        GetOrCreateGuiTexture(kFable2ToastFrameTexture);
    draw_list->AddRectFilled(frame_min, frame_max,
                             faded_color(41, 24, 12, 170), s(4.0f));
    if (!DrawFableGuiImageUv(
            draw_list, messagebox_texture,
            ImVec2(frame_min.x + s(5.0f), frame_min.y + s(5.0f)),
            ImVec2(frame_max.x - s(5.0f), frame_max.y - s(5.0f)),
            ImVec2(0.32f, 0.30f), ImVec2(0.68f, 0.72f),
            faded_color(255, 244, 204, 255))) {
      draw_list->AddRectFilled(
          ImVec2(frame_min.x + s(5.0f), frame_min.y + s(5.0f)),
          ImVec2(frame_max.x - s(5.0f), frame_max.y - s(5.0f)),
          faded_color(154, 105, 47, 70), s(3.0f));
    }
    const bool drew_fable_frame = DrawFableGuiNineSlice(
        draw_list, messagebox_texture, parchment_min, parchment_max,
        ImVec4(84.0f, 84.0f, 84.0f, 400.0f),
        ImVec4(s(20.0f), s(20.0f), s(20.0f), s(12.0f)),
        faded_color(255, 244, 204, 255));
    if (!drew_fable_frame) {
      draw_list->AddRectFilled(parchment_min, parchment_max,
                               faded_color(34, 27, 18, 232), s(10.0f));
      draw_list->AddRectFilled(
          ImVec2(parchment_min.x + s(5.0f), parchment_min.y + s(5.0f)),
          ImVec2(parchment_max.x - s(5.0f), parchment_max.y - s(5.0f)),
          faded_color(201, 178, 124, 226), s(7.0f));
      draw_list->AddRectFilled(
          ImVec2(parchment_min.x + s(12.0f), parchment_min.y + s(12.0f)),
          ImVec2(parchment_max.x - s(12.0f), parchment_max.y - s(12.0f)),
          faded_color(41, 49, 35, 222), s(4.0f));
    }
    DrawToastOuterFrame(draw_list, frame_min, frame_max, faded_color,
                        toast_scale);

    const ImVec2 content_min(frame_min.x + s(56.0f), frame_min.y + s(22.0f));
    const ImVec2 content_max(frame_max.x - s(39.0f), frame_max.y - s(23.0f));
    const float content_center_y =
        content_min.y + (content_max.y - content_min.y) * 0.5f;
    const ImVec2 icon_center(content_min.x + icon_ring_radius, content_center_y);
    const std::array<const char *, 2> icon_ring_keys = {
        kFable2ToastIconRingTexture,
        kFable2ToastIconRingTextureAlt,
    };
    FableGuiTexture *icon_ring_texture = nullptr;
    for (const char *key : icon_ring_keys) {
      FableGuiTexture &candidate = GetOrCreateGuiTexture(key);
      if (candidate.texture != nullptr || !candidate.rgba_pixels.empty()) {
        icon_ring_texture = &candidate;
        break;
      }
    }
    if (icon_ring_texture == nullptr) {
      draw_list->AddCircleFilled(icon_center, icon_ring_radius,
                                 faded_color(28, 20, 12, 230), 48);
      draw_list->AddCircleFilled(icon_center, icon_ring_radius - s(4.0f),
                                 faded_color(161, 122, 61, 218), 48);
      draw_list->AddCircleFilled(icon_center, icon_ring_radius - s(8.0f),
                                 faded_color(36, 45, 31, 236), 48);
    }

    AchievementIconTexture &icon = GetOrCreateIconTexture(definition);
    draw_list->AddCircleFilled(icon_center, icon_radius + s(2.0f),
                               faded_color(39, 25, 12, 210), 48);
    if (!DrawCircularIconPixels(draw_list, icon, icon_center, icon_radius)) {
      draw_list->AddCircleFilled(icon_center, icon_radius,
                                 faded_color(100, 54, 34, 226), 48);
    }
    if (icon_ring_texture != nullptr) {
      DrawFableGuiImage(
          draw_list, *icon_ring_texture,
          ImVec2(icon_center.x - icon_ring_radius,
                 icon_center.y - icon_ring_radius),
          ImVec2(icon_center.x + icon_ring_radius,
                 icon_center.y + icon_ring_radius),
          faded_color(255, 239, 192, 235));
    }

    const ImVec2 score_min(content_max.x - s(45.0f),
                           content_center_y - s(17.0f));
    const ImVec2 score_max(content_max.x + s(1.0f),
                           content_center_y + s(17.0f));
    const float text_left = icon_center.x + icon_ring_radius + s(17.0f);
    const float text_right = score_min.x - s(12.0f);

    ImFont *title_font = ToastTitleFont();
    ImFont *body_font = ToastBodyFont();
    const float title_size = ToastFontSize(title_font, 13.5f) * toast_scale * 1.09f;
    const float body_size = ToastFontSize(body_font, 15.0f) * toast_scale * 1.12f;
    const ImVec2 title_min(text_left, content_center_y - s(22.0f));
    const ImVec2 title_max(text_right, content_center_y - s(3.0f));
    const ImVec2 body_min(text_left, content_center_y - s(1.0f));
    const ImVec2 body_max(text_right, content_center_y + s(22.0f));
    const ImVec2 text_back_min(text_left - s(6.0f),
                               content_center_y - s(22.0f));
    const ImVec2 text_back_max(text_right + s(6.0f),
                               content_center_y + s(22.0f));
    draw_list->AddRectFilled(text_back_min, text_back_max,
                             faded_color(28, 24, 14, 112), s(5.0f));
    draw_list->AddRect(text_back_min, text_back_max,
                       faded_color(228, 184, 92, 58), s(5.0f), 0,
                       s(0.8f));
    if (showing_description) {
      const char *description_text =
          !definition.unlocked_description.empty()
              ? definition.unlocked_description.c_str()
              : (!definition.locked_description.empty()
                     ? definition.locked_description.c_str()
                     : (definition.name.empty() ? "Achievement unlocked."
                                                : definition.name.c_str()));
      DrawToastTextWrappedCentered(
          draw_list, body_font, body_size * 0.82f,
          ImVec2(text_left, content_center_y - s(22.0f)),
          ImVec2(text_right, content_center_y + s(22.0f)),
          message_color(255, 244, 205, 255), description_text, 2,
          description_progress);
    } else {
      DrawToastTextCentered(draw_list, title_font, title_size, title_min,
                            title_max, message_color(255, 232, 164, 255),
                            "Achievement Unlocked");
      DrawToastTextCentered(
          draw_list, body_font, body_size, body_min, body_max,
          message_color(255, 244, 205, 255),
          definition.name.empty() ? "Unknown Achievement"
                                  : definition.name.c_str());
    }

    draw_list->AddRectFilled(score_min, score_max, faded_color(26, 25, 15, 166),
                             s(18.0f));
    draw_list->AddRect(score_min, score_max, faded_color(227, 191, 103, 230),
                       s(18.0f), 0, s(1.5f));
    const std::string score_text =
        "+" + std::to_string(definition.gamerscore) + " G";
    const float score_size = ToastFontSize(title_font, 13.5f) * toast_scale * 1.05f;
    const ImVec2 score_text_size = title_font->CalcTextSizeA(
        score_size, std::numeric_limits<float>::max(), 0.0f,
        score_text.c_str());
    DrawToastText(draw_list, title_font, score_size,
                  ImVec2(score_min.x +
                             (score_max.x - score_min.x - score_text_size.x) *
                                 0.5f,
                         score_min.y +
                             (score_max.y - score_min.y - score_text_size.y) *
                                 0.5f),
                  faded_color(250, 224, 138, 255), score_text.c_str());
  }
  ImGui::End();
  ImGui::PopStyleVar(2);
}

void AchievementToastDialog::OnDraw(ImGuiIO &io) {
  QueueNewUnlocks();
  DrawCurrentToast(io);
}

void ToggleAchievementDialog() {
  if (g_dialog) {
    g_dialog.reset();
    return;
  }

  if (g_drawer != nullptr) {
    g_dialog = std::make_unique<AchievementDebugDialog>(g_drawer);
  }
}

void AchievementDebugDialog::OnDraw(ImGuiIO &io) {
  (void)io;

  const std::vector<AchievementDefinition> definitions =
      GetAchievementDefinitionsSnapshot();
  const std::vector<AchievementUnlock> unlocks =
      GetUnlockedAchievementsSnapshot();

  std::unordered_map<std::uint32_t, AchievementDefinition> definitions_by_id;
  definitions_by_id.reserve(definitions.size());
  for (const AchievementDefinition &definition : definitions) {
    definitions_by_id.emplace(definition.achievement_id, definition);
  }
  if (!definitions.empty() &&
      !definitions_by_id.contains(selected_achievement_id_)) {
    selected_achievement_id_ = definitions.front().achievement_id;
  }

  std::unordered_set<std::uint64_t> unlocked_keys;
  unlocked_keys.reserve(unlocks.size());
  std::uint32_t total_gamerscore = 0;
  for (const AchievementUnlock &unlock : unlocks) {
    unlocked_keys.insert(
        AchievementUnlockKey(unlock.user_index, unlock.achievement_id));

    const auto definition_it = definitions_by_id.find(unlock.achievement_id);
    if (definition_it != definitions_by_id.end()) {
      total_gamerscore += definition_it->second.gamerscore;
    }
  }

  ImGui::SetNextWindowPos(ImVec2(48.0f, 48.0f), ImGuiCond_FirstUseEver);
  ImGui::SetNextWindowSize(ImVec2(760.0f, 620.0f), ImGuiCond_FirstUseEver);
  ImGui::SetNextWindowBgAlpha(0.88f);
  if (!ImGui::Begin("Achievements##refii_achievement_debug")) {
    ImGui::End();
    return;
  }

  ImGui::Text("Unlocked: %zu", unlocks.size());
  ImGui::SameLine();
  ImGui::Text("Known: %zu", definitions.size());
  ImGui::SameLine();
  ImGui::Text("Score: %u", total_gamerscore);
  ImGui::SameLine();
  ImGui::Text("Toast queue: %zu",
              g_toast_dialog ? g_toast_dialog->PendingToastCount() : 0);

  ImGui::Separator();
  ImGui::SetNextItemWidth(90.0f);
  ImGui::InputInt("User##achievement_user", &selected_user_index_);
  selected_user_index_ = std::max(0, selected_user_index_);

  ImGui::SameLine();
  ImGui::SetNextItemWidth(120.0f);
  ImGui::InputInt("Achievement##achievement_id", &test_achievement_id_);
  test_achievement_id_ = std::max(1, test_achievement_id_);

  ImGui::SameLine();
  if (ImGui::Button("Record Unlock##achievement_record")) {
    const AchievementWrite write{
        static_cast<std::uint32_t>(selected_user_index_),
        static_cast<std::uint32_t>(test_achievement_id_),
    };
    RecordAchievementWrites(std::span<const AchievementWrite>(&write, 1));
  }

  ImGui::SameLine();
  if (ImGui::Button("Queue 3##achievement_queue_test")) {
    std::array<AchievementWrite, 3> writes{};
    std::size_t write_count = 0;
    const std::uint32_t user_index =
        static_cast<std::uint32_t>(selected_user_index_);
    const std::uint32_t start_id =
        static_cast<std::uint32_t>(test_achievement_id_);
    for (std::uint32_t offset = 0; offset < 8 && write_count < writes.size();
         ++offset) {
      const std::uint32_t achievement_id = start_id + offset;
      if (unlocked_keys.contains(
              AchievementUnlockKey(user_index, achievement_id))) {
        continue;
      }
      writes[write_count++] = AchievementWrite{
          user_index,
          achievement_id,
      };
    }
    if (write_count > 0) {
      RecordAchievementWrites(std::span<const AchievementWrite>(
          writes.data(), write_count));
    }
  }

  ImGui::SameLine();
  if (ImGui::Button("Flush##achievement_flush")) {
    FlushAchievementSave();
  }

  ImGui::SameLine();
  if (ImGui::Button("Clear##achievement_clear")) {
    ClearAchievementSave();
  }

  FableGuiTexture &toast_frame_texture =
      GetOrCreateGuiTexture(kFable2ToastFrameTexture);
  FableGuiTexture &toast_ring_texture =
      GetOrCreateGuiTexture(kFable2ToastIconRingTexture);
  FableGuiTexture &toast_ring_texture_alt =
      GetOrCreateGuiTexture(kFable2ToastIconRingTextureAlt);
  const bool toast_frame_gpu = FableGuiImTextureId(toast_frame_texture) != 0;
  const bool toast_frame_cpu = !toast_frame_texture.rgba_pixels.empty();
  const bool toast_ring_gpu = FableGuiImTextureId(toast_ring_texture) != 0 ||
                              FableGuiImTextureId(toast_ring_texture_alt) != 0;
  const bool toast_ring_cpu = !toast_ring_texture.rgba_pixels.empty() ||
                              !toast_ring_texture_alt.rgba_pixels.empty();
  const bool toast_ring_ready = toast_ring_gpu || toast_ring_cpu;
  ImGui::Text("Toast Fable 2 textures:");
  ImGui::SameLine();
  ImGui::TextColored((toast_frame_gpu || toast_frame_cpu)
                         ? ImVec4(0.58f, 0.95f, 0.45f, 1.0f)
                         : ImVec4(1.0f, 0.45f, 0.35f, 1.0f),
                     "frame %s",
                     toast_frame_gpu
                         ? "gpu"
                         : (toast_frame_cpu ? "cpu"
                                            : toast_frame_texture.status.c_str()));
  ImGui::SameLine();
  ImGui::TextColored(toast_ring_ready ? ImVec4(0.58f, 0.95f, 0.45f, 1.0f)
                                      : ImVec4(1.0f, 0.45f, 0.35f, 1.0f),
                     "ring %s",
                     toast_ring_gpu ? "gpu" : (toast_ring_cpu ? "cpu" : "fallback"));
  if (!toast_frame_texture.source_path.empty()) {
    ImGui::TextDisabled("Frame source: %s",
                        toast_frame_texture.source_path.c_str());
  }
  ImGui::TextDisabled("Fable font: %s", g_fable_font_status.c_str());

  if (definitions.empty()) {
    ImGui::TextDisabled(
        "Achievement metadata will appear after the title XDBF is cached.");
  } else if (ImGui::BeginTable("##achievement_definitions", 7,
                               ImGuiTableFlags_RowBg | ImGuiTableFlags_Borders |
                                   ImGuiTableFlags_Resizable |
                                   ImGuiTableFlags_ScrollX |
                                   ImGuiTableFlags_ScrollY,
                               ImVec2(0.0f, 260.0f))) {
    ImGui::TableSetupColumn("Icon", ImGuiTableColumnFlags_WidthFixed, 46.0f);
    ImGui::TableSetupColumn("Unlocked", ImGuiTableColumnFlags_WidthFixed,
                            68.0f);
    ImGui::TableSetupColumn("ID", ImGuiTableColumnFlags_WidthFixed, 58.0f);
    ImGui::TableSetupColumn("G", ImGuiTableColumnFlags_WidthFixed, 42.0f);
    ImGui::TableSetupColumn("Img", ImGuiTableColumnFlags_WidthFixed, 44.0f);
    ImGui::TableSetupColumn("PNG", ImGuiTableColumnFlags_WidthFixed, 64.0f);
    ImGui::TableSetupColumn("Name");
    ImGui::TableHeadersRow();

    for (const AchievementDefinition &definition : definitions) {
      DrawDefinitionRow(definition, unlocked_keys, selected_user_index_,
                        selected_achievement_id_);
    }
    ImGui::EndTable();
  }

  const auto selected_definition_it =
      definitions_by_id.find(selected_achievement_id_);
  if (selected_definition_it != definitions_by_id.end()) {
    DrawAchievementDetailsPanel(selected_definition_it->second, unlocked_keys,
                                selected_user_index_);
  }

  if (ImGui::TreeNode("Fable GUI textures##achievement_gui_textures")) {
    const std::array<const char *, 9> gui_texture_keys = {
        kFable2ToastFrameTexture,       kFable2ToastIconRingTexture,
        "frames/received_item_circle_v2.tex",
        "frames/infobox.tex",          "frames/papera.tex",
        "frames/paperb.tex",           "frames/paperc.tex",
        "frames/round_soft_bg.tex",
        "persistant/frame_tiled_background.tex",
    };

    if (ImGui::BeginTable("##achievement_gui_texture_table", 7,
                          ImGuiTableFlags_RowBg | ImGuiTableFlags_Borders |
                              ImGuiTableFlags_Resizable |
                              ImGuiTableFlags_ScrollY,
                          ImVec2(0.0f, 156.0f))) {
      ImGui::TableSetupColumn("Texture");
      ImGui::TableSetupColumn("Status", ImGuiTableColumnFlags_WidthFixed,
                              156.0f);
      ImGui::TableSetupColumn("Size", ImGuiTableColumnFlags_WidthFixed,
                              74.0f);
      ImGui::TableSetupColumn("Fmt", ImGuiTableColumnFlags_WidthFixed, 38.0f);
      ImGui::TableSetupColumn("Hdr", ImGuiTableColumnFlags_WidthFixed, 62.0f);
      ImGui::TableSetupColumn("Payload", ImGuiTableColumnFlags_WidthFixed,
                              70.0f);
      ImGui::TableSetupColumn("Tail", ImGuiTableColumnFlags_WidthFixed, 42.0f);
      ImGui::TableHeadersRow();

      for (const char *key : gui_texture_keys) {
        FableGuiTexture &texture = GetOrCreateGuiTexture(key);
        ImGui::TableNextRow();
        ImGui::TableSetColumnIndex(0);
        ImGui::TextUnformatted(key);
        if (ImGui::IsItemHovered() && !texture.source_path.empty()) {
          ImGui::BeginTooltip();
          ImGui::TextUnformatted(texture.source_path.c_str());
          ImGui::EndTooltip();
        }
        ImGui::TableSetColumnIndex(1);
        ImGui::TextUnformatted(texture.status.empty() ? "-"
                                                      : texture.status.c_str());
        ImGui::TableSetColumnIndex(2);
        ImGui::Text("%ux%u", texture.width, texture.height);
        ImGui::TableSetColumnIndex(3);
        ImGui::Text("%u", texture.format);
        ImGui::TableSetColumnIndex(4);
        ImGui::Text("%u", texture.header_payload_size);
        ImGui::TableSetColumnIndex(5);
        ImGui::Text("%" PRIu64, texture.payload_size);
        ImGui::TableSetColumnIndex(6);
        ImGui::Text("%u", texture.header_tail);
      }
      ImGui::EndTable();
    }

    FableGuiTexture &preview = GetOrCreateGuiTexture(kFable2ToastFrameTexture);
    DrawGuiTexturePreview(preview, 320.0f, 160.0f);
    ImGui::TreePop();
  }

  if (ImGui::TreeNode("Unlock log##achievement_unlock_log")) {
    if (unlocks.empty()) {
      ImGui::TextDisabled("No unlocks recorded yet.");
    } else if (ImGui::BeginTable(
                   "##achievement_unlocks", 4,
                   ImGuiTableFlags_RowBg | ImGuiTableFlags_Borders |
                       ImGuiTableFlags_Resizable | ImGuiTableFlags_ScrollY,
                   ImVec2(0.0f, 130.0f))) {
      ImGui::TableSetupColumn("Seq", ImGuiTableColumnFlags_WidthFixed, 54.0f);
      ImGui::TableSetupColumn("User", ImGuiTableColumnFlags_WidthFixed, 48.0f);
      ImGui::TableSetupColumn("ID", ImGuiTableColumnFlags_WidthFixed, 58.0f);
      ImGui::TableSetupColumn("Name");
      ImGui::TableHeadersRow();

      for (const AchievementUnlock &unlock : unlocks) {
        DrawUnlockRow(unlock, definitions_by_id);
      }
      ImGui::EndTable();
    }
    ImGui::TreePop();
  }

  ImGui::End();
}

} // namespace

void ConfigureAchievementFonts(ImFontAtlas *atlas) {
  LoadFableToastFonts(atlas);
}

void RegisterAchievementImGui(rex::ui::ImGuiDrawer *drawer,
                              rex::ui::ImmediateDrawer *immediate_drawer) {
  g_drawer = drawer;
  g_immediate_drawer = immediate_drawer;
  if (g_drawer != nullptr && g_toast_dialog == nullptr) {
    g_toast_dialog = std::make_unique<AchievementToastDialog>(g_drawer);
  }
  rex::ui::RegisterBind("bind_refii_achievements", "F5",
                        "Toggle achievements overlay",
                        [] { ToggleAchievementDialog(); });
}

void UnregisterAchievementImGui() {
  rex::ui::UnregisterBind("bind_refii_achievements");
  g_dialog.reset();
  g_toast_dialog.reset();
  g_icon_textures.clear();
  g_gui_textures.clear();
  g_immediate_drawer = nullptr;
  g_drawer = nullptr;
}

} // namespace refii::achievement
