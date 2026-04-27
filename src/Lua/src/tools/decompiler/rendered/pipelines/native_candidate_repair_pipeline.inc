bool TryRepairNativeCandidateSource(std::string& candidate_source,
                                    const std::vector<char>* input_bytes,
                                    std::string* note,
                                    std::string_view note_prefix,
                                    std::string* validation_error_out) {
    const auto deadline_exceeded = [&]() {
        return ShouldAbortCurrentDecompileRepair(
            note,
            note_prefix.empty() ? std::string_view("native_candidate")
                                : note_prefix);
    };
    refii::lua::internal::LuaMutableTextContext candidate_text_context(candidate_source);
    const auto validate_candidate_source = [&](std::string_view chunk_name) {
        candidate_text_context.SetSourceView(candidate_source);
        return candidate_text_context.ValidateCached(chunk_name);
    };
    std::string validation_error =
        validation_error_out != nullptr ? *validation_error_out
                                        : validate_candidate_source("native_candidate");
    const std::string original_source = candidate_source;
    const auto has_inline_anonymous_function_assignment =
        [](std::string_view source_text) {
            bool had_trailing_newline = false;
            const std::vector<std::string> lines =
                SplitLines(std::string(source_text), &had_trailing_newline);
            for (const std::string& line : lines) {
                const std::string_view trimmed =
                    TrimWhitespace(StripLineComment(line));
                if (trimmed.empty() || StartsWith(trimmed, "function(") ||
                    StartsWith(trimmed, "local function ") ||
                    StartsWith(trimmed, "function ")) {
                    continue;
                }
                if (trimmed.find("= function(") != std::string_view::npos) {
                    return true;
                }
            }
            return false;
        };
    const auto should_skip_legacy_ordered_repairs =
        [&](std::string_view current_validation_error, std::string_view current_source) {
            if (current_validation_error.empty() ||
                !IsUnclosedConstructValidationFamily(std::string(current_validation_error))) {
                return false;
            }
            if (current_validation_error.find("near 'else'") == std::string_view::npos &&
                current_validation_error.find("near 'elseif'") == std::string_view::npos) {
                return false;
            }
            return has_inline_anonymous_function_assignment(current_source);
        };

    {
        std::vector<std::string>* lines = candidate_text_context.EnsureMutableLines();
        const bool changed = ::ApplySharedRenderedLuaCleanupProfile(
            lines,
            refii::lua::internal::RenderedLuaCleanupProfile::kAggressiveCandidateRefinement);
        if (changed) {
            candidate_text_context.MarkLinesMutated();
            candidate_source = std::string(candidate_text_context.EnsureSource());
            validation_error = validate_candidate_source("native_candidate");
        }
    }

    if (validation_error.empty()) {
        if (input_bytes != nullptr && candidate_source != original_source &&
            !deadline_exceeded()) {
            RunBoundedReferenceRepairLoop(
                GetCurrentReferenceRepairPassBudget(),
                false,
                &candidate_source,
                &validation_error,
                note,
                std::string(note_prefix) + "_candidate_refined_no_progress",
                [&]() { return deadline_exceeded(); },
                [&]() {
                    return TryRepairCandidateMismatchedFunctionsFromReference(
                        *input_bytes,
                        original_source,
                        &candidate_source,
                        note,
                        std::string(note_prefix) + "_candidate_refined");
                },
                [&]() {
                    return validate_candidate_source("native_candidate");
                });
            validation_error = validate_candidate_source("native_candidate");
        }
        if (validation_error_out != nullptr) {
            *validation_error_out = validation_error;
        }
        if (candidate_source == original_source) {
            return false;
        }
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note, std::string(note_prefix) + "_candidate_refined");
            *note = JoinNotes(*note, std::string(note_prefix) + "_candidate_validated");
        }
        return true;
    }

    TryApplyTargetedRawInvalidStructuralPreRepair(
        candidate_source, &validation_error, "native_candidate", note);
    if (validation_error.empty()) {
        if (validation_error_out != nullptr) {
            *validation_error_out = validation_error;
        }
        if (candidate_source == original_source) {
            return false;
        }
        if (note != nullptr && !note_prefix.empty()) {
            *note = JoinNotes(*note, std::string(note_prefix) + "_candidate_repaired");
            *note = JoinNotes(*note, std::string(note_prefix) + "_candidate_validated");
        }
        return true;
    }

    if (input_bytes != nullptr && !deadline_exceeded() &&
        TryRepairReportedFunctionsFromBytecodeIteratively(*input_bytes,
                                                          candidate_source,
                                                          &validation_error,
                                                          note,
                                                          std::string(note_prefix) +
                                                              "_candidate")) {
        if (!validation_error.empty() &&
            !should_skip_legacy_ordered_repairs(validation_error, candidate_source) &&
            TryNormalizeQuestOwnerContinuationCleanup(candidate_source, note)) {
            validation_error = validate_candidate_source("native_candidate");
        }
        if (validation_error.empty()) {
            if (validation_error_out != nullptr) {
                *validation_error_out = validation_error;
            }
            if (note != nullptr && !note_prefix.empty()) {
                *note = JoinNotes(*note,
                                  std::string(note_prefix) +
                                      "_candidate_validated");
            }
            return true;
        }
    }

    if (!validation_error.empty()) {
        TryApplyTargetedRawInvalidStructuralPreRepair(
            candidate_source, &validation_error, "native_candidate", note);
    }

    bool tried_insert_before_reported_line = false;
    const OrderedSyntaxRepairPassOptions native_candidate_repair_options = {
        .include_top_level_leakage_cleanup = true,
        .include_member_reference_repairs = true,
        .include_for_generator_repairs = true,
        .include_generator_comment_repairs = false,
        .include_while_true_to_repeat = true,
        .include_insert_end_before_return = true,
        .include_insert_end_before_until = true,
        .include_insert_until_before_trailing_end = true,
        .include_insert_ends_before_top_level_boundaries = true,
    };
    const bool skip_legacy_ordered_repairs =
        should_skip_legacy_ordered_repairs(validation_error, candidate_source);
    const auto run_ordered_candidate_repairs =
        [&](std::string* source,
            std::string* error,
            std::string* repair_note,
            std::string_view source_name,
            std::string_view repair_note_prefix) {
            if (source == nullptr || error == nullptr) {
                return;
            }
            if (deadline_exceeded()) {
                return;
            }

            bool local_tried_insert_before_reported_line = false;
            const SyntaxRepairAttemptResult repair_result = RunOrderedSyntaxRepairPassAttempts(
                *source,
                *error,
                source_name,
                repair_note,
                &local_tried_insert_before_reported_line,
                native_candidate_repair_options,
                GetCurrentRepairAttemptCap(64, 12),
                false);
            if (repair_result == SyntaxRepairAttemptResult::kValidated) {
                error->clear();
            }
            const auto validate_source = [&](std::string_view label) {
                if (source == &candidate_source) {
                    return validate_candidate_source(label);
                }
                return ValidateLuaSource(*source, std::string(label));
            };
            if (TryNormalizeQuestOwnerContinuationCleanup(*source, repair_note)) {
                *error = validate_source(source_name);
            }
            if (!error->empty() &&
                TryAppendMissingEnds(*source, *error, repair_note)) {
                *error = validate_source(source_name);
                if (TryNormalizeQuestOwnerContinuationCleanup(*source, repair_note)) {
                    *error = validate_source(source_name);
                }
            }

            if (error->empty() && input_bytes != nullptr && *source != original_source &&
                !deadline_exceeded()) {
                RunBoundedReferenceRepairLoop(
                    GetCurrentReferenceRepairPassBudget(),
                    false,
                    source,
                    error,
                    repair_note,
                    std::string(repair_note_prefix) + "_reference_no_progress",
                    [&]() { return deadline_exceeded(); },
                    [&]() {
                        return TryRepairCandidateMismatchedFunctionsFromReference(
                            *input_bytes,
                            original_source,
                            source,
                            repair_note,
                            std::string(repair_note_prefix));
                    },
                    [&]() {
                        return validate_source("native_candidate");
                    });
                *error = validate_source("native_candidate");
                if (TryNormalizeQuestOwnerContinuationCleanup(*source, repair_note)) {
                    *error = validate_source(source_name);
                }
            }
            if (!error->empty() && input_bytes != nullptr && !deadline_exceeded()) {
                bool retry_insert_before_reported_line = false;
                const SyntaxRepairAttemptResult post_reference_repair_result =
                    RunOrderedSyntaxRepairPassAttempts(
                        *source,
                        *error,
                        source_name,
                        repair_note,
                        &retry_insert_before_reported_line,
                        native_candidate_repair_options,
                        GetCurrentRepairAttemptCap(24, 8),
                        false);
                if (post_reference_repair_result == SyntaxRepairAttemptResult::kValidated) {
                    error->clear();
                }
                if (TryNormalizeQuestOwnerContinuationCleanup(*source, repair_note)) {
                    *error = validate_source(source_name);
                }
            }
        };

    if (!skip_legacy_ordered_repairs) {
        run_ordered_candidate_repairs(&candidate_source,
                                      &validation_error,
                                      note,
                                      "native_candidate",
                                      std::string(note_prefix) + "_candidate_repaired");

        if (!validation_error.empty() && input_bytes != nullptr && !deadline_exceeded() &&
            TryRepairReportedFunctionsFromBytecodeIteratively(*input_bytes,
                                                              candidate_source,
                                                              &validation_error,
                                                              note,
                                                              std::string(note_prefix) +
                                                                  "_candidate_repaired")) {
            if (TryNormalizeQuestOwnerContinuationCleanup(candidate_source, note)) {
                validation_error = validate_candidate_source("native_candidate");
            }
        }

        if (!validation_error.empty() && candidate_source != original_source &&
            !deadline_exceeded()) {
            std::string raw_candidate_source = original_source;
            std::string raw_validation_error =
                ValidateLuaSource(raw_candidate_source, "native_candidate");
            std::string raw_repair_note;
            run_ordered_candidate_repairs(&raw_candidate_source,
                                          &raw_validation_error,
                                          &raw_repair_note,
                                          "native_candidate_raw",
                                          std::string(note_prefix) + "_raw_candidate_repaired");

            if (raw_candidate_source != original_source &&
                (raw_validation_error.empty() ||
                 IsValidationOutcomeBetter(validation_error, raw_validation_error))) {
                candidate_source = std::move(raw_candidate_source);
                validation_error = std::move(raw_validation_error);
                if (note != nullptr && !raw_repair_note.empty()) {
                    *note = JoinNotes(*note, raw_repair_note);
                    if (!note_prefix.empty()) {
                        *note = JoinNotes(*note,
                                          std::string(note_prefix) +
                                              "_raw_candidate_selected");
                    }
                }
            }
        }

        if (!validation_error.empty() && candidate_source != original_source &&
            !deadline_exceeded() &&
            TryAppendMissingEnds(candidate_source, validation_error, note)) {
            validation_error = validate_candidate_source("native_candidate");
            if (TryNormalizeQuestOwnerContinuationCleanup(candidate_source, note)) {
                validation_error = validate_candidate_source("native_candidate");
            }
        }
    } else if (note != nullptr && !note_prefix.empty()) {
        *note = JoinNotes(*note,
                          std::string(note_prefix) +
                              "_legacy_ordered_repairs_skipped_inline_function_else_boundary");
    }

    if (validation_error_out != nullptr) {
        *validation_error_out = validation_error;
    }

    if (candidate_source == original_source) {
        return false;
    }

    if (note != nullptr && !note_prefix.empty()) {
        *note = JoinNotes(*note, std::string(note_prefix) + "_candidate_repaired");
        if (validation_error.empty()) {
            *note = JoinNotes(*note, std::string(note_prefix) + "_candidate_validated");
        } else {
            *note = JoinNotes(*note,
                              std::string(note_prefix) + "_candidate_progress=" +
                                  SummarizeSyntaxError(validation_error));
        }
    }
    return true;
}
