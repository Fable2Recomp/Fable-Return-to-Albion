// luadec: a Lua 5.1 bytecode decompiler whose output recompiles to the same
// bytes. Structure comes from the control-flow of the chunk; names, scopes and
// line layout come from the debug sections when the chunk carries them.
#pragma once

#include "luadec_chunk.h"

#include <string>

namespace refii::luadec {

struct DecompileOptions {
    // Emit each token on the line the original compiler recorded for its
    // instruction, so the recompiled lineinfo matches. Off gives a plain layout.
    bool line_layout = true;
    // Indentation string.
    std::string indent = "\t";
};

// Decompiles a chunk. On failure returns false with `error` naming the
// function and pc where the structure could not be recovered.
bool Decompile(const Chunk& chunk, const DecompileOptions& options, std::string* out,
               std::string* error);

// Human-readable listing with resolved jump targets, lines, locals and constants.
void Disassemble(const Chunk& chunk, std::string* out);

}  // namespace refii::luadec
