#pragma once

#include <cstdint>
#include <filesystem>
#include <string>

namespace refii::shader {

// Xenos memory export (memexport / GPU stream-out) facts recovered while
// translating one blob. Reported per row in compile_report.tsv so that a
// dropped export is visible instead of silent.
struct XenosMemoryExportStats {
    // False when the translator could not report (e.g. the inline Bink shader
    // path, which never runs XenosRecomp).
    bool reported = false;
    bool enabled = false;
    std::uint32_t streams = 0;
    std::uint32_t data_register_mask = 0;
    std::uint32_t unresolved_address_writes = 0;
};

bool TranslateXenosShaderBlob(
    const std::filesystem::path& input,
    const std::string& target,
    const std::string& stage,
    const std::string& name,
    const std::filesystem::path& output,
    const std::filesystem::path& log_path = std::filesystem::path{},
    std::string* error = nullptr,
    XenosMemoryExportStats* memexport_stats = nullptr,
    // Vertex stage only: when non-empty, the same generated HLSL is compiled a
    // second time with -D REFII_TESSELLATION_VARIANT=1 into this path — the
    // hardware-tessellation variant artifact (`<blob>.tess.<target>.bin`).
    // The variant seeds r0 from SV_VertexID/SV_InstanceID per the Xenos
    // tessellation input contract; see XenosRecomp/shader_common.h.
    const std::filesystem::path& tess_output = std::filesystem::path{},
    // The title folder the blob came from ("Fable2", "Fable3"). The godot target classifies
    // the title's own per-draw constants by it; empty states none.
    const std::string& title = std::string{});

// "none" when nothing was reported, otherwise a compact
// streams=N,eM=0xNN[,unresolved_eA=N] descriptor for the report column.
std::string DescribeXenosMemoryExportStats(const XenosMemoryExportStats& stats);

}  // namespace refii::shader
