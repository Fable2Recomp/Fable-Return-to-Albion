// refii - the titles' additions to the shader bank pipeline (see shader_sbk_titles.h).
#include "shader_sbk_titles.h"

#include <array>
#include <cctype>

namespace refii::shader {
namespace {

// ---------------------------------------------------------------- Fable 2

// PixRenderVideoFrame's pixel shader: a 36-byte stub the Fable 2 executable holds at 0x82093C7C
// and the engine recognises by content. The translator writes its hand-made Bink combine for it
// by name (refii_shader_xenos_translator_main.cpp, DetectFableBinkInlineShader).
constexpr std::array<std::uint8_t, 36> kFable2PixRenderVideoFrameBlob = {
    0x00, 0x00, 0x00, 0x00, 0x10, 0x01, 0xc4, 0x00, 0x22, 0x00, 0x00, 0x00,
    0xc8, 0x0f, 0x80, 0x00, 0x02, 0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
};

constexpr ShaderTitleInlineBlob kFable2InlineBlobs[] = {
    {
        "Shaders.sbk",
        9000,
        "PSHADER_FABLE_PIXRENDER_VIDEO",
        ShaderStage::kPixel,
        0x82093C7Cu,
        kFable2PixRenderVideoFrameBlob.data(),
        kFable2PixRenderVideoFrameBlob.size(),
    },
};

// ---------------------------------------------------------------- the table

constexpr ShaderTitle kTitles[] = {
    {"Fable2", kFable2InlineBlobs, std::size(kFable2InlineBlobs)},
    // Fable 3's banks hold every shader it draws; its executable carries none inline.
    {"Fable3", nullptr, 0},
};

bool EqualsIgnoringAsciiCase(std::string_view left, std::string_view right) {
    if (left.size() != right.size()) {
        return false;
    }
    for (std::size_t i = 0; i < left.size(); ++i) {
        if (std::tolower(static_cast<unsigned char>(left[i])) !=
            std::tolower(static_cast<unsigned char>(right[i]))) {
            return false;
        }
    }
    return true;
}

}  // namespace

const ShaderTitle* FindShaderTitle(std::string_view folder) {
    for (const ShaderTitle& title : kTitles) {
        if (EqualsIgnoringAsciiCase(title.folder, folder)) {
            return &title;
        }
    }
    return nullptr;
}

std::string DescribeShaderTitles() {
    std::string text;
    for (const ShaderTitle& title : kTitles) {
        if (!text.empty()) {
            text += ", ";
        }
        text += title.folder;
    }
    return text;
}

}  // namespace refii::shader
