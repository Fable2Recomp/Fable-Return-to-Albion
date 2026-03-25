#include "tools/native_lua_chunk.h"
#include "tools/native_lua_unluac.h"

#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <string>
#include <vector>

#ifdef __cplusplus
extern "C" {
#endif
#include "lauxlib.h"
#include "lua.h"
#ifdef __cplusplus
}
#endif

extern "C" int luadec_main(int argc, char* argv[]);

namespace {

bool ReadBinaryFile(const std::filesystem::path& path, std::vector<char>* bytes) {
    if (bytes == nullptr) {
        return false;
    }

    std::ifstream stream(path, std::ios::binary);
    if (!stream) {
        return false;
    }

    stream.seekg(0, std::ios::end);
    const std::streampos size = stream.tellg();
    if (size < 0) {
        return false;
    }

    stream.seekg(0, std::ios::beg);
    bytes->resize(static_cast<std::size_t>(size));
    if (!bytes->empty()) {
        stream.read(bytes->data(), static_cast<std::streamsize>(bytes->size()));
    }
    return stream.good() || stream.eof();
}

bool WriteTextFile(const std::filesystem::path& path, const std::string& contents) {
    std::ofstream stream(path, std::ios::binary | std::ios::trunc);
    if (!stream) {
        return false;
    }
    stream.write(contents.data(), static_cast<std::streamsize>(contents.size()));
    return static_cast<bool>(stream);
}

bool ReadTextFile(const std::filesystem::path& path, std::string* text) {
    if (text == nullptr) {
        return false;
    }

    std::ifstream stream(path, std::ios::binary);
    if (!stream) {
        return false;
    }

    text->assign(std::istreambuf_iterator<char>(stream), std::istreambuf_iterator<char>());
    return stream.good() || stream.eof();
}

void AppendChildrenPreorder(const refii::lua::Lua51Function& function,
                            std::vector<const refii::lua::Lua51Function*>* out) {
    if (out == nullptr) {
        return;
    }

    for (const refii::lua::Lua51Function& child : function.child_functions) {
        out->push_back(&child);
        AppendChildrenPreorder(child, out);
    }
}

void AppendFunctionsPreorderWithDepth(const refii::lua::Lua51Function& function,
                                      int depth,
                                      std::vector<const refii::lua::Lua51Function*>* out,
                                      std::vector<int>* depths) {
    if (out == nullptr || depths == nullptr) {
        return;
    }

    for (const refii::lua::Lua51Function& child : function.child_functions) {
        out->push_back(&child);
        depths->push_back(depth);
        AppendFunctionsPreorderWithDepth(child, depth + 1, out, depths);
    }
}

int RunNativeRenderer(int argc, char** argv) {
    std::filesystem::path input_path;
    std::filesystem::path output_path;
    int function_index = 0;
    bool render_body = false;
    bool render_raw = false;

    for (int i = 1; i < argc; ++i) {
        const std::string_view arg = argv[i];
        if (arg == "--native-render") {
            continue;
        }
        if (arg == "--body") {
            render_body = true;
            continue;
        }
        if (arg == "--raw") {
            render_raw = true;
            continue;
        }
        if (arg == "-o" && i + 1 < argc) {
            output_path = argv[++i];
            continue;
        }
        if (arg == "-f" && i + 1 < argc) {
            function_index = std::atoi(argv[++i]);
            continue;
        }
        if (!arg.empty() && arg.front() == '-') {
            std::cerr << "unsupported native-render argument: " << arg << "\n";
            return 2;
        }
        input_path = argv[i];
    }

    if (input_path.empty()) {
        std::cerr << "native-render input path missing\n";
        return 2;
    }

    std::vector<char> bytes;
    if (!ReadBinaryFile(input_path, &bytes)) {
        std::cerr << "failed to read " << input_path << "\n";
        return 3;
    }

    refii::lua::Lua51Chunk chunk;
    std::string parse_error;
    if (!refii::lua::ParseLua51Chunk(bytes, &chunk, &parse_error)) {
        std::cerr << "chunk parse failed";
        if (!parse_error.empty()) {
            std::cerr << ": " << parse_error;
        }
        std::cerr << "\n";
        return 4;
    }

    const refii::lua::Lua51Function* target = &chunk.main;
    if (function_index > 0) {
        std::vector<const refii::lua::Lua51Function*> children;
        AppendChildrenPreorder(chunk.main, &children);
        if (static_cast<std::size_t>(function_index) > children.size()) {
            std::cerr << "function index out of range: " << function_index << "\n";
            return 5;
        }
        target = children[static_cast<std::size_t>(function_index - 1)];
    }

    std::string rendered;
    std::string render_error;
    const bool rendered_ok = render_body
                                 ? (render_raw
                                        ? refii::lua::RenderLua51StraightLineFunctionBodyRaw(
                                              *target, &rendered, &render_error)
                                        : refii::lua::RenderLua51StraightLineFunctionBody(
                                              *target, &rendered, &render_error))
                                 : (render_raw
                                        ? refii::lua::RenderLua51StraightLineFunctionRaw(
                                              *target, &rendered, &render_error)
                                        : refii::lua::RenderLua51StraightLineFunction(
                                              *target, &rendered, &render_error));
    if (!rendered_ok) {
        std::cerr << "native render failed";
        if (!render_error.empty()) {
            std::cerr << ": " << render_error;
        }
        std::cerr << "\n";
        return 6;
    }

    if (!output_path.empty()) {
        if (!WriteTextFile(output_path, rendered)) {
            std::cerr << "failed to write " << output_path << "\n";
            return 7;
        }
    } else {
        std::cout << rendered;
        if (rendered.empty() || rendered.back() != '\n') {
            std::cout << "\n";
        }
    }

    return 0;
}

int RunValidator(int argc, char** argv) {
    std::filesystem::path input_path;
    for (int i = 1; i < argc; ++i) {
        const std::string_view arg = argv[i];
        if (arg == "--validate") {
            continue;
        }
        if (!arg.empty() && arg.front() == '-') {
            std::cerr << "unsupported validate argument: " << arg << "\n";
            return 2;
        }
        input_path = argv[i];
    }

    if (input_path.empty()) {
        std::cerr << "validate input path missing\n";
        return 2;
    }

    std::string source;
    if (!ReadTextFile(input_path, &source)) {
        std::cerr << "failed to read " << input_path << "\n";
        return 3;
    }

    lua_State* state = luaL_newstate();
    if (state == nullptr) {
        std::cerr << "validator_init_failed\n";
        return 4;
    }

    const std::string chunk_name = input_path.string();
    const int status = luaL_loadbuffer(state, source.data(), source.size(), chunk_name.c_str());
    if (status != 0) {
        const char* message = lua_tostring(state, -1);
        std::cerr << (message != nullptr ? message : "syntax_invalid") << "\n";
        lua_close(state);
        return 5;
    }

    lua_close(state);
    std::cout << "ok\n";
    return 0;
}

int RunInstructionDump(int argc, char** argv) {
    std::filesystem::path input_path;
    int function_index = 0;

    for (int i = 1; i < argc; ++i) {
        const std::string_view arg = argv[i];
        if (arg == "--dump-instructions") {
            continue;
        }
        if (arg == "-f" && i + 1 < argc) {
            function_index = std::atoi(argv[++i]);
            continue;
        }
        if (!arg.empty() && arg.front() == '-') {
            std::cerr << "unsupported dump-instructions argument: " << arg << "\n";
            return 2;
        }
        input_path = argv[i];
    }

    if (input_path.empty()) {
        std::cerr << "dump-instructions input path missing\n";
        return 2;
    }

    std::vector<char> bytes;
    if (!ReadBinaryFile(input_path, &bytes)) {
        std::cerr << "failed to read " << input_path << "\n";
        return 3;
    }

    refii::lua::Lua51Chunk chunk;
    std::string parse_error;
    if (!refii::lua::ParseLua51Chunk(bytes, &chunk, &parse_error)) {
        std::cerr << "chunk parse failed";
        if (!parse_error.empty()) {
            std::cerr << ": " << parse_error;
        }
        std::cerr << "\n";
        return 4;
    }

    const refii::lua::Lua51Function* target = &chunk.main;
    if (function_index > 0) {
        std::vector<const refii::lua::Lua51Function*> children;
        AppendChildrenPreorder(chunk.main, &children);
        if (static_cast<std::size_t>(function_index) > children.size()) {
            std::cerr << "function index out of range: " << function_index << "\n";
            return 5;
        }
        target = children[static_cast<std::size_t>(function_index - 1)];
    }

    const std::vector<refii::lua::Lua51Instruction> instructions =
        refii::lua::DecodeLua51Instructions(*target);
    for (const refii::lua::Lua51Instruction& instruction : instructions) {
        std::cout << std::setw(4) << instruction.line << "  "
                  << refii::lua::DescribeLua51Opcode(instruction.opcode) << "  "
                  << "A=" << instruction.a << " B=" << instruction.b << " C=" << instruction.c
                  << " BX=" << instruction.bx << " SBX=" << instruction.sbx << " RAW=0x"
                  << std::hex << std::setw(8) << std::setfill('0') << instruction.raw
                  << std::dec << std::setfill(' ') << "\n";
    }

    return 0;
}

int RunListFunctions(int argc, char** argv) {
    std::filesystem::path input_path;
    int find_line = -1;

    for (int i = 1; i < argc; ++i) {
        const std::string_view arg = argv[i];
        if (arg == "--list-functions") {
            continue;
        }
        if (arg == "--find-line" && i + 1 < argc) {
            find_line = std::atoi(argv[++i]);
            continue;
        }
        if (!arg.empty() && arg.front() == '-') {
            std::cerr << "unsupported list-functions argument: " << arg << "\n";
            return 2;
        }
        input_path = argv[i];
    }

    if (input_path.empty()) {
        std::cerr << "list-functions input path missing\n";
        return 2;
    }

    std::vector<char> bytes;
    if (!ReadBinaryFile(input_path, &bytes)) {
        std::cerr << "failed to read " << input_path << "\n";
        return 3;
    }

    refii::lua::Lua51Chunk chunk;
    std::string parse_error;
    if (!refii::lua::ParseLua51Chunk(bytes, &chunk, &parse_error)) {
        std::cerr << "chunk parse failed";
        if (!parse_error.empty()) {
            std::cerr << ": " << parse_error;
        }
        std::cerr << "\n";
        return 4;
    }

    std::vector<const refii::lua::Lua51Function*> functions;
    std::vector<int> depths;
    AppendFunctionsPreorderWithDepth(chunk.main, 1, &functions, &depths);

    std::cout << "idx\tdepth\tline_defined\tlast_line\tinstructions\tchildren\tparams\tlocals\tsource\n";
    for (std::size_t i = 0; i < functions.size(); ++i) {
        const refii::lua::Lua51Function& function = *functions[i];
        if (find_line >= 0 && static_cast<int>(function.line_defined) != find_line) {
            continue;
        }
        std::cout << (i + 1) << '\t' << depths[i] << '\t' << function.line_defined << '\t'
                  << function.last_line_defined << '\t' << function.code.size() << '\t'
                  << function.child_functions.size() << '\t'
                  << static_cast<int>(function.parameter_count) << '\t' << function.locals.size()
                  << '\t' << function.source_name << '\n';
    }

    return 0;
}

}  // namespace

int main(int argc, char** argv) {
    for (int i = 1; i < argc; ++i) {
        if (std::string_view(argv[i]) == "--validate") {
            return RunValidator(argc, argv);
        }
        if (std::string_view(argv[i]) == "--native-render") {
            return RunNativeRenderer(argc, argv);
        }
        if (std::string_view(argv[i]) == "--dump-instructions") {
            return RunInstructionDump(argc, argv);
        }
        if (std::string_view(argv[i]) == "--list-functions") {
            return RunListFunctions(argc, argv);
        }
    }

    return luadec_main(argc, argv);
}
