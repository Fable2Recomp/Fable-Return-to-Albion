std::string BuildRepairProgressStateKey(std::string_view source,
                                        std::string_view validation_error) {
    const DecompiledMarkerSummary marker_summary = SummarizeDecompilerMarkers(source);
    const refii::lua::internal::SyntaxErrorDescriptor descriptor =
        refii::lua::internal::DescribeSyntaxError(validation_error);
    std::ostringstream stream;
    stream << ComputeStableBytecodeIdentity(source) << "|" << descriptor.summary << "@"
           << descriptor.line << "|"
           << marker_summary.decompiler_error_count << ":"
           << marker_summary.repeated_control_flow_count << ":"
           << marker_summary.suspicious_while_true_count << ":"
           << marker_summary.suspicious_until_true_count << ":"
           << marker_summary.for_generator_count << ":"
           << marker_summary.suppressed_control_count;
    return stream.str();
}

std::string BuildRepairQualityStateKey(std::string_view source,
                                       std::string_view validation_error) {
    const LuaOutputQualitySummary quality = SummarizeLuaOutputQuality(source);
    const refii::lua::internal::SyntaxErrorDescriptor descriptor =
        refii::lua::internal::DescribeSyntaxError(validation_error);
    std::ostringstream stream;
    stream << descriptor.summary << "@" << descriptor.line << "|"
           << quality.marker_summary.decompiler_error_count << ":"
           << quality.marker_summary.repeated_control_flow_count << ":"
           << quality.marker_summary.suspicious_while_true_count << ":"
           << quality.marker_summary.suspicious_until_true_count << ":"
           << quality.marker_summary.for_generator_count << ":"
           << quality.marker_summary.suppressed_control_count << ":"
           << quality.undefined_local_warning_count << ":"
           << quality.missing_end_warning_count << ":"
           << quality.do_return_end_count;
    return stream.str();
}

std::string NormalizeRepairFamilySummary(std::string_view summary) {
    std::string normalized;
    normalized.reserve(summary.size());
    bool in_digit_run = false;
    for (const char ch : summary) {
        if (std::isdigit(static_cast<unsigned char>(ch)) != 0) {
            if (!in_digit_run) {
                normalized.push_back('#');
                in_digit_run = true;
            }
            continue;
        }
        in_digit_run = false;
        normalized.push_back(ch);
    }
    return normalized;
}

std::string BuildRepairFamilyStateKey(std::string_view source,
                                      std::string_view validation_error) {
    const LuaOutputQualitySummary quality = SummarizeLuaOutputQuality(source);
    const refii::lua::internal::SyntaxErrorDescriptor descriptor =
        refii::lua::internal::DescribeSyntaxError(validation_error);
    std::ostringstream stream;
    stream << NormalizeRepairFamilySummary(descriptor.summary) << "|"
           << quality.marker_summary.decompiler_error_count << ":"
           << quality.marker_summary.repeated_control_flow_count << ":"
           << quality.marker_summary.suppressed_control_count << ":"
           << quality.undefined_local_warning_count << ":"
           << quality.missing_end_warning_count << ":"
           << quality.do_return_end_count;
    return stream.str();
}

bool HasBlockingRepairDonorSource(std::string_view source) {
    return HasBlockingLuaOutputQualityIssues(SummarizeLuaOutputQuality(source)) ||
           ContainsObviouslyInvalidNilUsage(source);
}
