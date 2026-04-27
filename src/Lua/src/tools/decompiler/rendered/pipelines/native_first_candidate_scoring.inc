std::string DescribeLuaReadability(const LuaReadabilitySummary& summary) {
    return "lines=" + std::to_string(summary.nonempty_line_count) +
           ",temp_ids=" + std::to_string(summary.temporary_identifier_count) +
           ",temp_names=" + std::to_string(summary.unique_temporary_identifier_count) +
           ",temp_leading=" + std::to_string(summary.temporary_leading_line_count) +
           ",temp_locals=" + std::to_string(summary.temporary_local_declaration_count) +
           ",temp_assignments=" + std::to_string(summary.temporary_assignment_count) +
           ",temp_table_fields=" +
           std::to_string(summary.temporary_table_field_assignment_count) +
           ",temp_self_assignments=" +
           std::to_string(summary.temporary_self_reference_assignment_count) +
           ",temp_rhs_assignments=" +
           std::to_string(summary.suspicious_temporary_rhs_assignment_count) +
           ",nil_calls=" + std::to_string(summary.obvious_nil_call_count) +
           ",temp_dense_lines=" + std::to_string(summary.multi_temporary_token_line_count) +
           ",risk=" + std::to_string(ScoreLuaReadabilityRisk(summary));
}

void RecordReadabilitySummary(std::string_view source, NativeMainChunkCandidate* candidate) {
    if (candidate == nullptr) {
        return;
    }

    const LuaReadabilitySummary summary = SummarizeLuaReadability(source);
    candidate->readability = summary;
    candidate->readability.risk_score = ScoreLuaReadabilityRisk(summary);
}

void RecordCandidateParity(const refii::lua::audit::LuaBytecodeParityReference* parity_reference,
                           std::string_view chunk_name,
                           NativeMainChunkCandidate* candidate) {
    if (candidate == nullptr || parity_reference == nullptr || !parity_reference->available ||
        !candidate->syntax_validated) {
        return;
    }

    refii::lua::audit::LuaBytecodeParityResult parity_result;
    if (!refii::lua::audit::EvaluateLuaSourceBytecodeParity(*parity_reference,
                                                            candidate->source,
                                                            chunk_name,
                                                            &parity_result)) {
        candidate->parity_checked = true;
        candidate->compiled_candidate = parity_result.compiled_candidate;
        candidate->first_mismatch = parity_result.first_mismatch;
        return;
    }

    candidate->parity_checked = true;
    candidate->main_stats_exact = parity_result.main_stats_exact;
    candidate->full_stats_exact = parity_result.full_stats_exact;
    candidate->exact_parity = parity_result.exact_bytecode;
    candidate->compiled_candidate = parity_result.compiled_candidate;
    candidate->first_mismatch = parity_result.first_mismatch;
}

bool IsPreferredValidatedCandidate(const NativeMainChunkCandidate& candidate,
                                   const NativeMainChunkCandidate& current_best) {
    if (candidate.exact_parity != current_best.exact_parity) {
        return candidate.exact_parity;
    }
    if (candidate.full_stats_exact != current_best.full_stats_exact) {
        return candidate.full_stats_exact;
    }
    if (candidate.main_stats_exact != current_best.main_stats_exact) {
        return candidate.main_stats_exact;
    }

    const LuaReadabilitySummary candidate_summary = candidate.readability;
    const LuaReadabilitySummary best_summary = current_best.readability;
    const bool candidate_registerized = LooksLikeRegisterizedLua(candidate_summary);
    const bool best_registerized = LooksLikeRegisterizedLua(best_summary);
    if (candidate_summary.obvious_nil_call_count != best_summary.obvious_nil_call_count) {
        return candidate_summary.obvious_nil_call_count < best_summary.obvious_nil_call_count;
    }
    if (candidate_registerized != best_registerized) {
        return !candidate_registerized;
    }

    if (candidate_summary.suspicious_temporary_rhs_assignment_count !=
        best_summary.suspicious_temporary_rhs_assignment_count) {
        return candidate_summary.suspicious_temporary_rhs_assignment_count <
               best_summary.suspicious_temporary_rhs_assignment_count;
    }

    if (candidate_summary.suspicious_self_call_assignment_count !=
        best_summary.suspicious_self_call_assignment_count) {
        return candidate_summary.suspicious_self_call_assignment_count <
               best_summary.suspicious_self_call_assignment_count;
    }

    if (candidate.used_raw_renderer != current_best.used_raw_renderer) {
        return !candidate.used_raw_renderer;
    }

    const std::size_t candidate_risk = ScoreLuaReadabilityRisk(candidate_summary);
    const std::size_t best_risk = ScoreLuaReadabilityRisk(best_summary);
    if (candidate_risk != best_risk) {
        return candidate_risk < best_risk;
    }

    return candidate.source.size() < current_best.source.size();
}
