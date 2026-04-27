namespace refii::lua::internal {

std::string NormalizePathString(std::string value) {
    return ::NormalizePathString(std::move(value));
}

bool StartsWith(std::string_view value, std::string_view prefix) {
    return ::StartsWith(value, prefix);
}

bool EndsWith(std::string_view value, std::string_view suffix) {
    return ::EndsWith(value, suffix);
}

int GetInputPriority(const std::filesystem::path& path) {
    return ::GetInputPriority(path);
}

bool IsLuaBytecode(const std::vector<char>& bytes) {
    return ::IsLuaBytecode(bytes);
}

std::string DecodeSourceName(const std::vector<char>& bytes) {
    return ::DecodeSourceName(bytes);
}

std::string DescribeBytecodeChunk(const std::vector<char>& bytes) {
    return ::DescribeBytecodeChunk(bytes);
}

ResolvedInput ResolveBestInputPath(const std::filesystem::path& workspace,
                                   const std::filesystem::path& relative_path) {
    return ::ResolveBestInputPath(workspace, relative_path);
}

bool ReadFile(const std::filesystem::path& path, std::vector<char>& bytes) {
    return ::ReadFile(path, bytes);
}

int RunDecompiler(const LuaWorkspaceDecompileOptions& options,
                  const std::filesystem::path& input_path,
                  const std::filesystem::path& output_path,
                  const std::vector<std::string>& extra_args,
                  bool force_external_tool,
                  unsigned int timeout_seconds) {
    return ::RunDecompiler(
        options, input_path, output_path, extra_args, force_external_tool, timeout_seconds);
}

std::string BuildIgnoredOutputPrefix(const std::filesystem::path& workspace_root,
                                     const std::filesystem::path& output_root) {
    return ::BuildIgnoredOutputPrefix(workspace_root, output_root);
}

std::string DescribeRunResult(std::string_view label, int result) {
    return ::DescribeRunResult(label, result);
}

std::string JoinNotes(std::string lhs, std::string rhs) {
    return ::JoinNotes(std::move(lhs), std::move(rhs));
}

std::string_view StripLineComment(std::string_view line) {
    return ::StripLineComment(line);
}

std::string_view TrimWhitespace(std::string_view value) {
    return ::TrimWhitespace(value);
}

std::vector<std::string> SplitLines(std::string_view text, bool* had_trailing_newline) {
    return ::SplitLines(text, had_trailing_newline);
}

std::string JoinLines(const std::vector<std::string>& lines, bool had_trailing_newline) {
    return ::JoinLines(lines, had_trailing_newline);
}

bool ReadTextFile(const std::filesystem::path& path, std::string& text) {
    return ::ReadTextFile(path, text);
}

bool WriteTextFile(const std::filesystem::path& path, std::string_view text) {
    return ::WriteTextFile(path, text);
}

bool HasUsableDecompiledLuaOutput(const std::filesystem::path& path, std::string_view note) {
    return ::HasUsableDecompiledLuaOutput(path, note);
}

bool CopyNormalizedLuaTextFile(const std::filesystem::path& from,
                               const std::filesystem::path& to,
                               std::string* note) {
    return ::CopyNormalizedLuaTextFile(from, to, note);
}

std::string BuildDisassemblyPlaceholder(const std::filesystem::path& input_relative,
                                        const std::filesystem::path& disassembly_relative,
                                        std::string_view source_name) {
    return ::BuildDisassemblyPlaceholder(input_relative, disassembly_relative, source_name);
}


void ResetTransientDecompileCaches() {
    ResetTransientSourceDecompileCaches();
    ::ResetTransientRenderedDecompileCachesImpl();
}

void SetCurrentDecompileRepairContext(bool prefer_exhaustive_rescue,
                                      bool outlier_candidate,
                                      bool previous_failed_file,
                                      bool unfinished_active,
                                      std::chrono::steady_clock::time_point deadline) {
    g_decompile_repair_context.prefer_exhaustive_rescue = prefer_exhaustive_rescue;
    g_decompile_repair_context.outlier_candidate = outlier_candidate;
    g_decompile_repair_context.previous_failed_file = previous_failed_file;
    g_decompile_repair_context.unfinished_active = unfinished_active;
    g_decompile_repair_context.deadline_active =
        deadline != std::chrono::steady_clock::time_point{};
    g_decompile_repair_context.deadline = deadline;
    g_decompile_repair_context.deadline_noted = false;
}

void ResetCurrentDecompileRepairContext() {
    g_decompile_repair_context = DecompileRepairContext{};
}

bool IsCurrentDecompileRepairDeadlineExceeded() {
    return ::IsCurrentDecompileRepairDeadlineExceeded();
}

bool ShouldAbortCurrentDecompileRepair(std::string* note, std::string_view note_prefix) {
    return ::ShouldAbortCurrentDecompileRepair(note, note_prefix);
}

bool ApplySharedRenderedLuaCleanup(std::vector<std::string>* lines,
                                   RenderedLuaCleanupProfile profile) {
    return ::ApplySharedRenderedLuaCleanupProfile(lines, profile);
}

bool DebugApplyHeaderComparisonParitySwap(std::string* source) {
    if (source == nullptr || source->empty()) {
        return false;
    }

    refii::lua::internal::LuaMutableTextContext text_context(*source);
    std::vector<std::string>* lines = text_context.EnsureMutableLines();
    if (!refii::lua::TrySwapRenderedCallRightHandComparisonsForParity(lines)) {
        return false;
    }
    text_context.MarkLinesMutated();
    *source = std::string(text_context.EnsureSource());
    return true;
}

bool TryRepairReportedFunctionsFromBytecodeIteratively(const std::vector<char>& input_bytes,
                                                       std::string& decompiled_source,
                                                       std::string* validation_error,
                                                       std::string* note,
                                                       std::string_view note_prefix) {
    return ::TryRepairReportedFunctionsFromBytecodeIteratively(
        input_bytes, decompiled_source, validation_error, note, note_prefix);
}

bool TryReplaceDecompilerErrorFunctions(const std::vector<char>& input_bytes,
                                        std::string& decompiled_source,
                                        std::string* note,
                                        bool allow_full_main_chunk_replacement,
                                        const LuaWorkspaceDecompileOptions* options,
                                        const std::filesystem::path* input_path) {
    return ::TryReplaceDecompilerErrorFunctions(input_bytes,
                                                decompiled_source,
                                                note,
                                                allow_full_main_chunk_replacement,
                                                options,
                                                input_path);
}

bool TryReplaceWithValidatedNativeMainChunk(const std::vector<char>& input_bytes,
                                            std::string& decompiled_source,
                                            std::string* note,
                                            const LuaWorkspaceDecompileOptions* options,
                                            const std::filesystem::path* input_path,
                                            std::string_view preferred_render_mode) {
    return ::TryReplaceWithValidatedNativeMainChunk(input_bytes,
                                                    decompiled_source,
                                                    note,
                                                    options,
                                                    input_path,
                                                    preferred_render_mode);
}

bool ShouldRequireExactParityForInput(const std::filesystem::path& relative_input,
                                      const std::filesystem::path* input_path,
                                      const std::vector<char>& input_bytes,
                                      const LuaWorkspaceDecompileOptions* options) {
    return ::ShouldRequireExactParityForInputPolicy(
        relative_input, input_path, input_bytes, options);
}

NativeCandidateFinalizeResult TryFinalizeNativeCandidateOutput(
    const std::filesystem::path& relative_input,
    std::string final_source,
    const std::vector<char>& input_bytes,
    const std::filesystem::path& output_path,
    const std::filesystem::path& failed_output_path,
    const LuaWorkspaceDecompileOptions* options,
    const std::filesystem::path* input_path,
    std::string* note,
    const char* note_prefix) {
    return ::TryFinalizeNativeCandidateOutput(relative_input,
                                              std::move(final_source),
                                              input_bytes,
                                              output_path,
                                              failed_output_path,
                                              options,
                                              input_path,
                                              note,
                                              note_prefix);
}

NativeCandidateFinalizeResult ClassifyExistingNativeCandidateOutput(
    const std::filesystem::path& relative_input,
    const std::filesystem::path& output_path,
    const std::vector<char>& input_bytes,
    const std::filesystem::path* input_path,
    const LuaWorkspaceDecompileOptions* options,
    std::string* note,
    const char* note_prefix) {
    return ::ClassifyExistingNativeCandidateOutput(
        relative_input, output_path, input_bytes, input_path, options, note, note_prefix);
}

bool ValidateAndRepairDecompiledOutput(const std::filesystem::path& relative_input,
                                       std::string_view source_name,
                                       const std::vector<char>& input_bytes,
                                       const LuaWorkspaceDecompileOptions* options,
                                       const std::filesystem::path* input_path,
                                       const std::filesystem::path& output_path,
                                       const std::filesystem::path& failed_output_path,
                                       std::string* note) {
    return ::ValidateAndRepairDecompiledOutput(relative_input,
                                               source_name,
                                               input_bytes,
                                               options,
                                               input_path,
                                               output_path,
                                               failed_output_path,
                                               note);
}

}  // namespace refii::lua::internal
