#include "extractor/default_xex_icon_extractor.h"

#include <array>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace {

constexpr std::array<std::string_view, 67> kDefaultXexIconFilenames = {
    "067_X_IMAGEID_GAME.png",
    "066_icon_colourist.png",
    "065_icon_fowl_player.png",
    "064_icon_combatant.png",
    "063_icon_multiplicator.png",
    "062_icon_gladiator.png",
    "061_icon_repugnant.png",
    "060_icon_ghastly_jester.png",
    "059_icon_howler.png",
    "058_icon_collector2.png",
    "057_icon_visionary.png",
    "056_icon_concierge.png",
    "055_icon_paramour.png",
    "054_icon_nutcracker.png",
    "053_Icon_bibliophile.png",
    "052_Icon_meteorologist.png",
    "051_Icon_bandit_therapist.png",
    "050_Icon_completionist.png",
    "049_Icon_workhorse.png",
    "048_Icon_miser.png",
    "047_Icon_swinger.png",
    "046_Icon_sweetheart.png",
    "045_Icon_spouse.png",
    "044_Icon_hunter.png",
    "043_Icon_sacrifice.png",
    "042_Icon_ruler_albion.png",
    "041_Icon_rogue.png",
    "040_Icon_property_magnate.png",
    "039_Icon_gargoyle.png",
    "038_Icon_pooch_pamperer.png",
    "037_Icon_philanthropist.png",
    "036_Icon_persuader.png",
    "035_Icon_party_animal.png",
    "034_Icon_parent.png",
    "033_Icon_painted_man.png",
    "032_Icon_whippersnapper.png",
    "031_Icon_collector.png",
    "030_Icon_lord_dance.png",
    "029_Icon_muse.png",
    "028_Icon_horder.png",
    "027_Icon_highwayman.png",
    "026_Icon_hero_will.png",
    "025_Icon_hero_strength.png",
    "024_Icon_hero_skill.png",
    "023_Icon_many_faces.png",
    "022_Icon_goth.png",
    "021_Icon_hero_inside.png",
    "020_Icon_gambler.png",
    "019_Icon_family.png",
    "018_Icon_executioner.png",
    "017_Icon_doll_catcher.png",
    "016_Icon_dog_trainer.png",
    "015_Icon_divergent.png",
    "014_Icon_dervish.png",
    "013_Icon_danger_society.png",
    "012_Icon_companions.png",
    "011_Icon_cliff_jumper.png",
    "010_Icon_chicken_kicker.png",
    "009_Icon_charmer.png",
    "008_Icon_celebrity.png",
    "007_Icon_bloodbrothers.png",
    "006_Icon_bigamist.png",
    "005_Icon_artisan.png",
    "004_Icon_archmage.png",
    "003_Icon_archiologist.png",
    "002_Icon_abnormal.png",
    "001_Icon_black_knight.png",
};

std::uint32_t ReadBigEndianU32(const std::vector<char>& bytes, const std::size_t offset) {
    return (static_cast<std::uint32_t>(static_cast<unsigned char>(bytes[offset])) << 24) |
           (static_cast<std::uint32_t>(static_cast<unsigned char>(bytes[offset + 1])) << 16) |
           (static_cast<std::uint32_t>(static_cast<unsigned char>(bytes[offset + 2])) << 8) |
           static_cast<std::uint32_t>(static_cast<unsigned char>(bytes[offset + 3]));
}

bool HasPngSignatureAt(const std::vector<char>& bytes, const std::size_t offset) {
    static constexpr unsigned char kPngSignature[] = {
        0x89, 'P', 'N', 'G', '\r', '\n', 0x1A, '\n',
    };
    if (offset + std::size(kPngSignature) > bytes.size()) {
        return false;
    }
    for (std::size_t index = 0; index < std::size(kPngSignature); ++index) {
        if (static_cast<unsigned char>(bytes[offset + index]) != kPngSignature[index]) {
            return false;
        }
    }
    return true;
}

std::optional<std::size_t> FindPngEnd(const std::vector<char>& bytes, const std::size_t start) {
    if (!HasPngSignatureAt(bytes, start)) {
        return std::nullopt;
    }

    std::size_t cursor = start + 8;
    while (cursor + 12 <= bytes.size()) {
        const std::uint32_t payload_size = ReadBigEndianU32(bytes, cursor);
        const std::size_t chunk_end = cursor + 12 + static_cast<std::size_t>(payload_size);
        if (chunk_end < cursor || chunk_end > bytes.size()) {
            return std::nullopt;
        }

        const char* chunk_type = bytes.data() + cursor + 4;
        if (std::memcmp(chunk_type, "IEND", 4) == 0) {
            return chunk_end;
        }
        cursor = chunk_end;
    }

    return std::nullopt;
}

std::vector<std::size_t> FindPngStarts(const std::vector<char>& bytes) {
    std::vector<std::size_t> starts;
    for (std::size_t offset = 0; offset + 8 <= bytes.size(); ++offset) {
        if (HasPngSignatureAt(bytes, offset)) {
            starts.push_back(offset);
            offset += 7;
        }
    }
    return starts;
}

} // namespace

namespace refii::extractor {

bool ExtractDefaultXexIcons(
    const std::filesystem::path& xex_path,
    DefaultXexIconExtractionSummary* summary,
    std::string* error) {
    std::ifstream stream(xex_path, std::ios::binary);
    if (!stream.is_open()) {
        if (error != nullptr) {
            *error = "failed to open XEX for icon extraction: " + xex_path.string();
        }
        return false;
    }

    std::vector<char> bytes((std::istreambuf_iterator<char>(stream)),
                            std::istreambuf_iterator<char>());
    const std::vector<std::size_t> png_starts = FindPngStarts(bytes);
    if (png_starts.size() < kDefaultXexIconFilenames.size()) {
        if (error != nullptr) {
            *error = "default.xex did not contain the expected icon PNG table";
        }
        return false;
    }

    const std::size_t first_icon_index = png_starts.size() - kDefaultXexIconFilenames.size();
    const std::filesystem::path icon_root =
        xex_path.parent_path() / (xex_path.filename().generic_string() + ".icons");

    try {
        if (std::filesystem::exists(icon_root)) {
            std::filesystem::remove_all(icon_root);
        }
        std::filesystem::create_directories(icon_root);
    } catch (const std::exception& exception) {
        if (error != nullptr) {
            *error = exception.what();
        }
        return false;
    }

    std::ofstream manifest(icon_root / "manifest.tsv", std::ios::trunc);
    if (!manifest.is_open()) {
        if (error != nullptr) {
            *error = "failed to create XEX icon manifest";
        }
        return false;
    }
    manifest << "index\tfile\tname\tstart\tend\tsize\n";

    for (std::size_t icon_index = 0; icon_index < kDefaultXexIconFilenames.size();
         ++icon_index) {
        const std::size_t start = png_starts[first_icon_index + icon_index];
        const std::optional<std::size_t> end = FindPngEnd(bytes, start);
        if (!end.has_value()) {
            if (error != nullptr) {
                *error = "failed to parse PNG icon in default.xex";
            }
            return false;
        }

        const std::string filename{kDefaultXexIconFilenames[icon_index]};
        const std::filesystem::path output_path = icon_root / filename;
        std::ofstream output(output_path, std::ios::binary | std::ios::trunc);
        if (!output.is_open()) {
            if (error != nullptr) {
                *error = "failed to create " + output_path.string();
            }
            return false;
        }
        output.write(bytes.data() + start, static_cast<std::streamsize>(*end - start));
        if (!output) {
            if (error != nullptr) {
                *error = "failed to write " + output_path.string();
            }
            return false;
        }

        const std::string display_name = filename.size() > 4 ? filename.substr(4) : filename;
        manifest << (icon_index + 1) << '\t' << filename << '\t' << display_name << '\t'
                 << start << '\t' << *end << '\t' << (*end - start) << '\n';
    }

    if (summary != nullptr) {
        summary->output_directory = icon_root;
        summary->extracted_icons = kDefaultXexIconFilenames.size();
    }
    return true;
}

} // namespace refii::extractor
