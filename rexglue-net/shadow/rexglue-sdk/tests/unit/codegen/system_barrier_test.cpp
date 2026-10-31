/**
 * @file codegen/system_barrier_test.cpp
 * @brief Guest barrier publication and generated-output lifecycle regression.
 * @license BSD 3-Clause License; see the SDK LICENSE.
 */

#include <catch2/catch_test_macros.hpp>
#include <array>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <optional>
#include <string>
#include <vector>

#include <rex/codegen/codegen_context.h>
#include <rex/codegen/codegen_writer.h>
#include <rex/codegen/test_support.h>

namespace {
namespace fs = std::filesystem;
using namespace rex::codegen;
constexpr uint32_t kAddress = 0x82000000;
constexpr std::string_view kGuard =
    "#if defined(__aarch64__) || defined(__arm64__) || defined(_M_ARM64) || defined(_M_ARM64EC)";
constexpr std::string_view kFence = "std::atomic_thread_fence(std::memory_order_seq_cst);";

/// Count exact emitted fragments; no guest binary or runtime is required.
size_t Count(const std::string& text, std::string_view needle) {
  size_t count = 0;
  size_t offset = 0;
  while ((offset = text.find(needle, offset)) != std::string::npos) {
    ++count;
    offset += needle.size();
  }
  return count;
}

/// Exercise real decoding, emission and content-addressed output publication.
struct BarrierFixture {
  fs::path root;
  std::vector<uint8_t> bytes;
  TestModule module;
  std::optional<CodegenContext> context;

  explicit BarrierFixture(std::string_view name, std::initializer_list<uint32_t> opcodes)
      : root(fs::temp_directory_path() / "rexglue_system_barrier_test" / name) {
    fs::remove_all(root);
    fs::create_directories(root);
    for (uint32_t opcode : opcodes) {
      for (int shift : {24, 16, 8, 0})
        bytes.push_back(static_cast<uint8_t>(opcode >> shift));
    }
    module.Load(kAddress, bytes.data(), bytes.size());
    RecompilerConfig config;
    config.projectName = "barrier_test";
    config.outDirectoryPath = "generated";
    context.emplace(CodegenContext::Create(BinaryView::fromModule(module), std::move(config)));
    context->setConfigDir(root);
    context->analysisState().format = "xex";
    context->analysisState().loadAddress = kAddress;
    context->analysisState().entryPoint = kAddress;
    context->analysisState().imageSize = static_cast<uint32_t>(bytes.size());
    auto* function = context->graph.addFunction(kAddress, static_cast<uint32_t>(bytes.size()),
                                               FunctionAuthority::DISCOVERED, true);
    REQUIRE(function != nullptr);
    function->discover({{kAddress, static_cast<uint32_t>(bytes.size())}}, {}, {});
    function->seal();
  }

  ~BarrierFixture() {
    std::error_code error;
    fs::remove_all(root, error);
  }

  /// Read only emitted recompilation bodies, excluding registration/header text.
  std::string RecompiledText() const {
    std::string result;
    for (const auto& entry : fs::directory_iterator(root / "generated")) {
      if (entry.path().filename().string().starts_with("barrier_test_recomp.") &&
          entry.path().extension() == ".cpp") {
        std::ifstream stream(entry.path(), std::ios::binary);
        result.append(std::istreambuf_iterator<char>(stream), std::istreambuf_iterator<char>());
      }
    }
    return result;
  }
};
}  // namespace

TEST_CASE("Guest barriers emit guarded host fences and unchanged writes are idempotent",
          "[codegen][system_barrier]") {
  // Real big-endian sync, lwsync, eieio and blr instructions.
  BarrierFixture fixture("all_barriers", {0x7C0004AC, 0x7C2004AC, 0x7C0006AC, 0x4E800020});
  CodegenWriter first(*fixture.context);
  REQUIRE(first.write(false));
  const auto text = fixture.RecompiledText();
  REQUIRE_FALSE(text.empty());
  CHECK(Count(text, kGuard) == 3);
  CHECK(Count(text, kFence) == 3);
  CHECK(Count(text, "#endif") == 3);
  CHECK(text.find("// sync") < text.find(kGuard));
  CHECK(text.find("// lwsync") != std::string::npos);
  CHECK(text.find("// eieio") != std::string::npos);
  CodegenWriter second(*fixture.context);
  REQUIRE(second.write(false));
  CHECK(second.writtenFiles().empty());
  CHECK(second.deletedFiles().empty());
  CHECK(fixture.RecompiledText() == text);
}

TEST_CASE("Unrelated guest no-ops do not gain host barriers", "[codegen][system_barrier]") {
  BarrierFixture fixture("no_barriers", {0x60000000, 0x4E800020});
  CodegenWriter writer(*fixture.context);
  REQUIRE(writer.write(false));
  const auto text = fixture.RecompiledText();
  REQUIRE_FALSE(text.empty());
  CHECK(Count(text, kFence) == 0);
  CHECK(Count(text, kGuard) == 0);
}
