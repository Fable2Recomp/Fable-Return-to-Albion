// refii-luadec: decompile one Lua 5.1 chunk to source.
//   refii-luadec <chunk.luac> [-o <out.lua>] [--plain]
#include "luadec.h"

#include <cstdio>
#include <fstream>
#include <iostream>
#include <sstream>
#include <string>

int main(int argc, char** argv) {
    std::string input;
    std::string output;
    bool dis = false;
    refii::luadec::DecompileOptions options;
    for (int i = 1; i < argc; ++i) {
        const std::string arg = argv[i];
        if (arg == "-o" && i + 1 < argc) {
            output = argv[++i];
        } else if (arg == "--dis") {
            dis = true;
        } else if (arg == "--plain") {
            options.line_layout = false;
        } else if (arg == "--spaces") {
            options.indent = "    ";
        } else if (input.empty()) {
            input = arg;
        } else {
            std::cerr << "usage: refii-luadec <chunk> [-o <out.lua>] [--plain]\n";
            return 3;
        }
    }
    if (input.empty()) {
        std::cerr << "usage: refii-luadec <chunk> [-o <out.lua>] [--plain]\n";
        return 3;
    }
    std::ifstream in(input, std::ios::binary);
    if (!in) {
        std::cerr << "cannot read " << input << "\n";
        return 3;
    }
    std::stringstream buffer;
    buffer << in.rdbuf();
    const std::string bytes = buffer.str();

    refii::luadec::Chunk chunk;
    std::string error;
    if (!refii::luadec::LoadChunk(bytes, &chunk, &error)) {
        std::cerr << "load failed: " << error << "\n";
        return 1;
    }
    std::string source;
    if (dis) {
        refii::luadec::Disassemble(chunk, &source);
    } else if (!refii::luadec::Decompile(chunk, options, &source, &error)) {
        std::cerr << "decompile failed: " << error << "\n";
        return 1;
    }
    if (output.empty()) {
        std::cout << source;
        return 0;
    }
    std::ofstream out(output, std::ios::binary);
    if (!out) {
        std::cerr << "cannot write " << output << "\n";
        return 3;
    }
    out << source;
    return 0;
}
