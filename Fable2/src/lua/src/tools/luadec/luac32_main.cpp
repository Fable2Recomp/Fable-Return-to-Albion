// refii-luac32: command-line front end of the decompiler oracle.
//
//   refii-luac32 <in.lua> -o <out.luac> [-s] [-n <chunkname>]
//   refii-luac32 --verify <orig.luac> <in.lua> [-o <out.luac>]
//
// --verify prints EXACT, or MISMATCH <where>, followed by CODE_EXACT yes|no.
// Exit codes: 0 EXACT / compiled, 1 MISMATCH, 2 compile error, 3 usage or IO.
#include "luadec_oracle.h"

#include <cstdio>
#include <fstream>
#include <iterator>
#include <optional>
#include <string>
#include <vector>

namespace {

bool ReadFileBytes(const std::string& path, std::vector<unsigned char>& out) {
    std::ifstream stream(path, std::ios::binary);
    if (!stream) return false;
    out.assign(std::istreambuf_iterator<char>(stream), std::istreambuf_iterator<char>());
    return true;
}

bool WriteFileBytes(const std::string& path, const std::vector<unsigned char>& bytes) {
    std::ofstream stream(path, std::ios::binary);
    if (!stream) return false;
    stream.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    return static_cast<bool>(stream);
}

int Usage() {
    std::fprintf(stderr,
                 "usage: refii-luac32 <in.lua> -o <out.luac> [-s] [-n <chunkname>]\n"
                 "       refii-luac32 --verify <orig.luac> <in.lua> [-o <out.luac>]\n");
    return 3;
}

int RunVerify(const std::string& original_path, const std::string& input, const std::optional<std::string>& output) {
    std::vector<unsigned char> original;
    std::vector<unsigned char> text;
    if (!ReadFileBytes(original_path, original)) {
        std::fprintf(stderr, "cannot read %s\n", original_path.c_str());
        return 3;
    }
    if (!ReadFileBytes(input, text)) {
        std::fprintf(stderr, "cannot read %s\n", input.c_str());
        return 3;
    }
    const refii::luadec::OracleResult result = refii::luadec::VerifySourceAgainstChunk(
        original, std::string_view(reinterpret_cast<const char*>(text.data()), text.size()));
    if (result.status == refii::luadec::OracleStatus::kInvalidOriginal) {
        std::fprintf(stderr, "cannot load %s: %s\n", original_path.c_str(), result.detail.c_str());
        return 3;
    }
    if (result.status == refii::luadec::OracleStatus::kCompileError) {
        std::fprintf(stderr, "COMPILE_ERROR %s\n", result.detail.c_str());
        return 2;
    }
    if (output && !WriteFileBytes(*output, result.candidate)) {
        std::fprintf(stderr, "cannot write %s\n", output->c_str());
        return 3;
    }
    if (result.status == refii::luadec::OracleStatus::kExact) {
        std::printf("EXACT\nCODE_EXACT yes\n");
        return 0;
    }
    std::printf("MISMATCH %s\nCODE_EXACT %s\n", result.detail.c_str(),
                result.status == refii::luadec::OracleStatus::kCodeExact ? "yes" : "no");
    return 1;
}

}  // namespace

int main(int argc, char** argv) {
    std::vector<std::string> args(argv + 1, argv + argc);
    if (args.empty()) return Usage();

    if (args[0] == "--verify") {
        std::optional<std::string> output;
        std::vector<std::string> positional;
        for (size_t i = 1; i < args.size(); ++i) {
            if (args[i] == "-o") {
                if (i + 1 >= args.size()) return Usage();
                output = args[++i];
            } else {
                positional.push_back(args[i]);
            }
        }
        if (positional.size() != 2) return Usage();
        return RunVerify(positional[0], positional[1], output);
    }

    bool strip = false;
    std::optional<std::string> chunkname;
    std::string output;
    std::string input;
    for (size_t i = 0; i < args.size(); ++i) {
        if (args[i] == "-s") {
            strip = true;
        } else if (args[i] == "-n") {
            if (i + 1 >= args.size()) return Usage();
            chunkname = args[++i];
        } else if (args[i] == "-o") {
            if (i + 1 >= args.size()) return Usage();
            output = args[++i];
        } else if (!args[i].empty() && args[i][0] == '-') {
            return Usage();
        } else if (input.empty()) {
            input = args[i];
        } else {
            return Usage();
        }
    }
    if (input.empty() || output.empty()) return Usage();

    std::vector<unsigned char> text;
    if (!ReadFileBytes(input, text)) {
        std::fprintf(stderr, "cannot read %s\n", input.c_str());
        return 3;
    }
    std::vector<unsigned char> chunk;
    std::string error;
    if (!refii::luadec::CompileSourceToGuestChunk(
            std::string_view(reinterpret_cast<const char*>(text.data()), text.size()),
            chunkname.value_or("@" + input), strip, /*compat_vararg=*/false, &chunk, &error)) {
        std::fprintf(stderr, "COMPILE_ERROR %s\n", error.c_str());
        return 2;
    }
    if (!WriteFileBytes(output, chunk)) {
        std::fprintf(stderr, "cannot write %s\n", output.c_str());
        return 3;
    }
    return 0;
}
