refii::lua::internal::NativeCandidateFinalizeResult ClassifyExistingNativeCandidateOutput(
    const std::filesystem::path& relative_input,
    const std::filesystem::path& output_path,
    const std::vector<char>& input_bytes,
    const std::filesystem::path* input_path,
    const refii::lua::LuaWorkspaceDecompileOptions* options,
    std::string* note,
    std::string_view note_prefix) {
    std::string source;
    if (!ReadTextFile(output_path, source)) {
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note, std::string(note_prefix) + "_output_read_failed");
        }
        return {};
    }

    StripLegacyDecompilerBanner(&source, note);
    return ClassifyNativeCandidateSource(
        relative_input, source, input_bytes, input_path, options, note, note_prefix);
}

std::string BuildDisassemblyPlaceholder(const std::filesystem::path& input_relative,
                                        const std::filesystem::path& disassembly_relative,
                                        std::string_view source_name) {
    std::string placeholder;
    placeholder.reserve(512);
    placeholder += "-- refii-disassembly-placeholder\n";
    placeholder += "-- This script could not be decompiled into valid Lua yet.\n";
    placeholder += "-- Path: " + input_relative.generic_string() + "\n";
    if (!source_name.empty()) {
        placeholder += "-- Source: " + std::string(source_name) + "\n";
    }
    placeholder += "-- Readable disassembly: " + disassembly_relative.generic_string() + "\n";
    placeholder += "-- Replace this file with valid Lua to activate a loose-script override.\n";
    return placeholder;
}
