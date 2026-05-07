#include "tools/decompiler/rendered/bytecode_parity_repair_context.h"

#include "tools/decompiler/lua_decompiler_internal.h"

#include <cstdlib>

namespace refii::lua::internal {
namespace {

bool Lua51ConstantsEquivalent(const refii::lua::Lua51Constant& lhs,
                              const refii::lua::Lua51Constant& rhs) {
    if (lhs.type != rhs.type) {
        return false;
    }

    switch (lhs.type) {
        case refii::lua::Lua51ConstantType::kNil:
            return true;
        case refii::lua::Lua51ConstantType::kBoolean:
            return lhs.boolean_value == rhs.boolean_value;
        case refii::lua::Lua51ConstantType::kNumber:
            return lhs.number_value == rhs.number_value;
        case refii::lua::Lua51ConstantType::kString:
            return lhs.string_value == rhs.string_value;
    }

    return false;
}

void AppendLuaFunctionsPreorderIncludingMain(
    const refii::lua::Lua51Function& function,
    std::vector<const refii::lua::Lua51Function*>* out) {
    if (out == nullptr) {
        return;
    }
    out->push_back(&function);
    for (const refii::lua::Lua51Function& child : function.child_functions) {
        AppendLuaFunctionsPreorderIncludingMain(child, out);
    }
}

}  // namespace

const refii::lua::Lua51Function* GetLuaFunctionByPreorderIndex(
    const refii::lua::Lua51Chunk& chunk,
    std::size_t function_index) {
    std::vector<const refii::lua::Lua51Function*> functions;
    AppendLuaFunctionsPreorderIncludingMain(chunk.main, &functions);
    if (function_index >= functions.size()) {
        return nullptr;
    }
    return functions[function_index];
}

bool Lua51FunctionBytecodeExact(const refii::lua::Lua51Function& original,
                                const refii::lua::Lua51Function& candidate) {
    if (original.upvalue_count != candidate.upvalue_count ||
        original.parameter_count != candidate.parameter_count ||
        original.vararg_flags != candidate.vararg_flags ||
        original.max_stack_size != candidate.max_stack_size ||
        original.code != candidate.code ||
        original.constants.size() != candidate.constants.size() ||
        original.child_functions.size() != candidate.child_functions.size()) {
        return false;
    }

    for (std::size_t index = 0; index < original.constants.size(); ++index) {
        if (!Lua51ConstantsEquivalent(original.constants[index], candidate.constants[index])) {
            return false;
        }
    }

    for (std::size_t index = 0; index < original.child_functions.size(); ++index) {
        if (!Lua51FunctionBytecodeExact(original.child_functions[index],
                                        candidate.child_functions[index])) {
            return false;
        }
    }

    return true;
}

bool Lua51FunctionBytecodeExactInChunk(const refii::lua::Lua51Chunk& original_chunk,
                                       const refii::lua::Lua51Chunk& candidate_chunk,
                                       std::size_t function_index) {
    const refii::lua::Lua51Function* original =
        GetLuaFunctionByPreorderIndex(original_chunk, function_index);
    const refii::lua::Lua51Function* candidate =
        GetLuaFunctionByPreorderIndex(candidate_chunk, function_index);
    if (original == nullptr || candidate == nullptr) {
        return false;
    }

    return Lua51FunctionBytecodeExact(*original, *candidate);
}

bool IsFirstFunctionMismatchNotWorse(
    const refii::lua::audit::FunctionMismatchInfo& candidate,
    const refii::lua::audit::FunctionMismatchInfo& baseline) {
    if (!baseline.available) {
        return !candidate.available;
    }
    if (!candidate.available) {
        return true;
    }
    if (candidate.function_index != baseline.function_index) {
        return candidate.function_index >= baseline.function_index;
    }

    const long long candidate_delta_sum =
        std::llabs(candidate.instruction_delta) +
        std::llabs(candidate.constant_delta) +
        std::llabs(candidate.local_delta);
    const long long baseline_delta_sum =
        std::llabs(baseline.instruction_delta) +
        std::llabs(baseline.constant_delta) +
        std::llabs(baseline.local_delta);
    return candidate_delta_sum <= baseline_delta_sum;
}

bool BuildBytecodeParityRepairContext(const std::vector<char>& input_bytes,
                                      std::string_view candidate_source,
                                      BytecodeParityRepairContext* context_out) {
    if (context_out == nullptr) {
        return false;
    }

    *context_out = {};
    std::string parse_error;
    if (!refii::lua::ParseLua51Chunk(input_bytes, &context_out->original_chunk, &parse_error)) {
        return false;
    }
    if (!CompileLuaSourceToChunkCached(candidate_source,
                                       "bytecode_parity_candidate",
                                       &context_out->compiled_candidate) ||
        !context_out->compiled_candidate.chunk_parsed) {
        return false;
    }
    context_out->mismatch = refii::lua::audit::FindFirstFunctionMismatch(
        context_out->original_chunk, context_out->compiled_candidate.chunk);
    return context_out->mismatch.available;
}

long long BytecodeParityMismatchDeltaSum(
    const refii::lua::audit::FunctionMismatchInfo& mismatch) {
    if (!mismatch.available) {
        return 0LL;
    }
    return std::llabs(mismatch.instruction_delta) +
           std::llabs(mismatch.constant_delta) +
           std::llabs(mismatch.local_delta);
}

bool IsBytecodeParityStrictMismatchImprovement(
    const BytecodeParityRepairContext& context,
    const refii::lua::audit::FunctionMismatchInfo& transformed_mismatch) {
    return !transformed_mismatch.available ||
           transformed_mismatch.function_index > context.mismatch.function_index ||
           (transformed_mismatch.function_index == context.mismatch.function_index &&
            BytecodeParityMismatchDeltaSum(transformed_mismatch) <
                BytecodeParityMismatchDeltaSum(context.mismatch));
}

bool TryCommitBytecodeParityImprovingSource(
    const BytecodeParityRepairContext& context,
    std::string transformed_source,
    std::string_view compile_label,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }

    refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
    if (!CompileLuaSourceToChunkCached(transformed_source,
                                       std::string(compile_label).c_str(),
                                       &transformed_chunk) ||
        !transformed_chunk.chunk_parsed) {
        return false;
    }

    const refii::lua::audit::FunctionMismatchInfo transformed_mismatch =
        refii::lua::audit::FindFirstFunctionMismatch(context.original_chunk,
                                                     transformed_chunk.chunk);
    if (!IsFirstFunctionMismatchNotWorse(transformed_mismatch, context.mismatch) ||
        !IsBytecodeParityStrictMismatchImprovement(context, transformed_mismatch)) {
        return false;
    }

    *candidate_source = std::move(transformed_source);
    return true;
}

bool TryCommitBytecodeParityImprovingLines(
    const BytecodeParityRepairContext& context,
    const std::vector<std::string>& transformed_lines,
    bool had_trailing_newline,
    std::string_view compile_label,
    std::string* candidate_source) {
    return TryCommitBytecodeParityImprovingSource(
        context,
        JoinLines(transformed_lines, had_trailing_newline),
        compile_label,
        candidate_source);
}

bool TryCommitBytecodeParityExactSource(
    const BytecodeParityRepairContext& context,
    std::string transformed_source,
    std::string_view compile_label,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }

    refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
    if (!CompileLuaSourceToChunkCached(transformed_source,
                                       std::string(compile_label).c_str(),
                                       &transformed_chunk) ||
        !transformed_chunk.chunk_parsed ||
        !Lua51FunctionBytecodeExact(context.original_chunk.main,
                                    transformed_chunk.chunk.main)) {
        return false;
    }

    *candidate_source = std::move(transformed_source);
    return true;
}

bool TryCommitBytecodeParityExactLines(
    const BytecodeParityRepairContext& context,
    const std::vector<std::string>& transformed_lines,
    bool had_trailing_newline,
    std::string_view compile_label,
    std::string* candidate_source) {
    return TryCommitBytecodeParityExactSource(
        context,
        JoinLines(transformed_lines, had_trailing_newline),
        compile_label,
        candidate_source);
}

bool TryCommitBytecodeParityExactCurrentFunctionLines(
    const BytecodeParityRepairContext& context,
    const std::vector<std::string>& transformed_lines,
    bool had_trailing_newline,
    std::string_view compile_label,
    std::string* candidate_source) {
    if (candidate_source == nullptr) {
        return false;
    }

    std::string transformed_source = JoinLines(transformed_lines, had_trailing_newline);
    refii::lua::audit::CompiledLuaChunkInfo transformed_chunk;
    if (!CompileLuaSourceToChunkCached(transformed_source,
                                       std::string(compile_label).c_str(),
                                       &transformed_chunk) ||
        !transformed_chunk.chunk_parsed) {
        return false;
    }

    const bool target_exact =
        context.mismatch.function_index == 0
            ? Lua51FunctionBytecodeExact(context.original_chunk.main,
                                         transformed_chunk.chunk.main)
            : Lua51FunctionBytecodeExactInChunk(context.original_chunk,
                                                transformed_chunk.chunk,
                                                context.mismatch.function_index);
    if (!target_exact) {
        return false;
    }

    *candidate_source = std::move(transformed_source);
    return true;
}

}  // namespace refii::lua::internal
