#include "tools/decompiler/native_first_readability_policy.h"

namespace refii::lua::decompiler {

std::size_t ScoreLuaReadabilityRisk(const NativeLuaReadabilitySummary& summary) {
    return summary.risk_score;
}

bool LooksLikeRegisterizedLua(const NativeLuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 10) {
        return false;
    }

    if (summary.temporary_self_reference_assignment_count >= 3 &&
        summary.temporary_leading_line_count * 2 >= summary.nonempty_line_count) {
        return true;
    }

    if (summary.temporary_leading_line_count >= 8 &&
        summary.multi_temporary_token_line_count >= 6 &&
        ScoreLuaReadabilityRisk(summary) >= 48) {
        return true;
    }

    return summary.nonempty_line_count >= 24 &&
           summary.temporary_leading_line_count * 3 >= summary.nonempty_line_count &&
           summary.unique_temporary_identifier_count >= 4 &&
           ScoreLuaReadabilityRisk(summary) >= 64;
}

bool IsRunnableLuaDespiteTemporaryNoise(const NativeLuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 200) {
        return false;
    }

    if (summary.temporary_self_reference_assignment_count != 0) {
        return false;
    }

    return summary.temporary_leading_line_count * 5 < summary.nonempty_line_count &&
           summary.multi_temporary_token_line_count * 6 < summary.nonempty_line_count &&
           summary.temporary_local_declaration_count * 16 < summary.nonempty_line_count;
}

bool IsStructuredTableInitializerLuaDespiteTemporaryNoise(
    const NativeLuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 80) {
        return false;
    }

    if (summary.temporary_self_reference_assignment_count != 0 ||
        summary.temporary_table_field_assignment_count < 24 ||
        summary.temporary_local_declaration_count * 24 >= summary.nonempty_line_count ||
        summary.multi_temporary_token_line_count * 4 >= summary.nonempty_line_count) {
        return false;
    }

    if (summary.temporary_table_field_assignment_count * 2 >=
        summary.temporary_leading_line_count) {
        return true;
    }

    return summary.nonempty_line_count >= 300 &&
           summary.temporary_assignment_count * 2 >= summary.temporary_leading_line_count &&
           summary.unique_temporary_identifier_count <= 12;
}

bool IsLargeNativeLuaBodyUsableDespiteTemporaryNoise(
    const NativeLuaReadabilitySummary& summary) {
    if (summary.nonempty_line_count < 240) {
        return false;
    }

    if (summary.temporary_self_reference_assignment_count != 0 ||
        summary.unique_temporary_identifier_count > 24 ||
        summary.temporary_leading_line_count * 8 >= summary.nonempty_line_count * 5) {
        return false;
    }

    const bool table_heavy = summary.temporary_table_field_assignment_count >= 20;
    const bool assignment_heavy =
        summary.temporary_assignment_count * 3 >= summary.temporary_leading_line_count * 2;
    return table_heavy || assignment_heavy;
}

bool ShouldTrySeedRepairForValidatedCandidate(
    const NativeLuaReadabilitySummary& clean_summary) {
    return clean_summary.suspicious_self_call_assignment_count != 0 ||
           clean_summary.temporary_self_reference_assignment_count != 0;
}

bool ShouldTryShadowRepairForValidatedCandidate(
    const NativeLuaReadabilitySummary& clean_summary,
    const NativeLuaReadabilitySummary& raw_summary) {
    return clean_summary.temporary_local_declaration_count != 0 ||
           clean_summary.temporary_identifier_count != 0 ||
           clean_summary.temporary_leading_line_count != 0 ||
           raw_summary.temporary_local_declaration_count >= 8;
}

bool ShouldTryExpandedAssignmentRepairForValidatedCandidate(
    const NativeLuaReadabilitySummary& clean_summary) {
    return clean_summary.temporary_identifier_count != 0 ||
           clean_summary.temporary_leading_line_count != 0 ||
           clean_summary.temporary_local_declaration_count != 0 ||
           clean_summary.temporary_assignment_count != 0 ||
           clean_summary.temporary_table_field_assignment_count != 0 ||
           clean_summary.temporary_self_reference_assignment_count != 0 ||
           clean_summary.suspicious_self_call_assignment_count != 0 ||
           clean_summary.suspicious_temporary_rhs_assignment_count != 0 ||
           clean_summary.obvious_nil_call_count != 0;
}

bool ShouldTryGuardedCarryRepairForValidatedCandidate(
    const NativeLuaReadabilitySummary& raw_summary) {
    return raw_summary.temporary_assignment_count != 0 ||
           raw_summary.temporary_identifier_count != 0;
}

std::string DescribeLuaReadability(const NativeLuaReadabilitySummary& summary) {
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
           ",temp_dense_lines=" +
           std::to_string(summary.multi_temporary_token_line_count) +
           ",risk=" + std::to_string(ScoreLuaReadabilityRisk(summary));
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

    const NativeLuaReadabilitySummary candidate_summary = candidate.readability;
    const NativeLuaReadabilitySummary best_summary = current_best.readability;
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

}  // namespace refii::lua::decompiler
