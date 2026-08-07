#include <filesystem>
#include <memory>
#include <string>

#include "abi/refii_job_impl.h"
#include "core/platform.h"
#include "core/utils/inimanager.h"
#include "installer/installer.h"
#include "tools/native/native_lua_decompiler.h"

#if defined(_WIN32)
#define REFII_ABI_EXPORT extern "C" __declspec(dllexport)
#else
#define REFII_ABI_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {

struct OpsProgress {
    std::shared_ptr<IsoExtractionProgress> progress = std::make_shared<IsoExtractionProgress>();
    refii::lua::LuaWorkspaceVerifySummary verifySummary;
};

OpsProgress* AsOps(void* handle) { return static_cast<OpsProgress*>(handle); }

const OpsProgress* AsOps(const void* handle) { return static_cast<const OpsProgress*>(handle); }

std::string ReadError(const OpsProgress* ops) {
    if (ops == nullptr) {
        return std::string();
    }
    std::lock_guard<std::mutex> lock(ops->progress->outputMutex);
    return ops->progress->errorMessage;
}

}  // namespace

REFII_ABI_EXPORT void refii_ops_initialize(const char* launcher_directory) {
    if (launcher_directory != nullptr && launcher_directory[0] != '\0') {
        Platform::SetExecutableDirectoryOverride(std::filesystem::path(launcher_directory));
    }
    INI::EnsureINIFileExists();
}

REFII_ABI_EXPORT void* refii_ops_progress_create(void) { return new OpsProgress(); }

REFII_ABI_EXPORT void refii_ops_progress_destroy(void* handle) { delete AsOps(handle); }

REFII_ABI_EXPORT int refii_ops_is_extracting(const void* handle) {
    const OpsProgress* ops = AsOps(handle);
    return ops != nullptr && ops->progress->isExtracting.load() ? 1 : 0;
}

REFII_ABI_EXPORT int refii_ops_is_complete(const void* handle) {
    const OpsProgress* ops = AsOps(handle);
    return ops != nullptr && ops->progress->isComplete.load() ? 1 : 0;
}

REFII_ABI_EXPORT int refii_ops_has_error(const void* handle) {
    const OpsProgress* ops = AsOps(handle);
    return ops != nullptr && ops->progress->hasError.load() ? 1 : 0;
}

REFII_ABI_EXPORT int refii_ops_operation_label(const void* handle, char* buffer, int capacity) {
    const OpsProgress* ops = AsOps(handle);
    return refii::abi::CopyString(
        ops == nullptr ? std::string() : ops->progress->GetOperationLabel(), buffer, capacity);
}

REFII_ABI_EXPORT int refii_ops_status_message(const void* handle, char* buffer, int capacity) {
    const OpsProgress* ops = AsOps(handle);
    return refii::abi::CopyString(
        ops == nullptr ? std::string() : ops->progress->GetStatusMessage(), buffer, capacity);
}

REFII_ABI_EXPORT int refii_ops_status_history(const void* handle, char* buffer, int capacity) {
    const OpsProgress* ops = AsOps(handle);
    return refii::abi::CopyString(
        ops == nullptr ? std::string() : ops->progress->GetStatusHistory(), buffer, capacity);
}

REFII_ABI_EXPORT int refii_ops_error_message(const void* handle, char* buffer, int capacity) {
    return refii::abi::CopyString(ReadError(AsOps(handle)), buffer, capacity);
}

REFII_ABI_EXPORT void refii_ops_rebuild_lua_workspace(void* handle) {
    OpsProgress* ops = AsOps(handle);
    if (ops != nullptr) {
        Installer::RebuildLuaWorkspaceAsync(ops->progress);
    }
}

REFII_ABI_EXPORT void refii_ops_extract_general_game_banks(void* handle) {
    OpsProgress* ops = AsOps(handle);
    if (ops != nullptr) {
        Installer::ExtractGeneralGameBanksAsync(ops->progress);
    }
}

REFII_ABI_EXPORT void refii_ops_extract_game_shaders(void* handle) {
    OpsProgress* ops = AsOps(handle);
    if (ops != nullptr) {
        Installer::ExtractGameShadersAsync(ops->progress);
    }
}

REFII_ABI_EXPORT void refii_ops_verify_lua_workspace(void* handle) {
    OpsProgress* ops = AsOps(handle);
    if (ops != nullptr) {
        Installer::VerifyLuaWorkspaceAsync(ops->progress);
    }
}

REFII_ABI_EXPORT void refii_ops_reset_lua_workspace(void* handle) {
    OpsProgress* ops = AsOps(handle);
    if (ops != nullptr) {
        Installer::ResetLuaWorkspaceAsync(ops->progress);
    }
}

REFII_ABI_EXPORT void refii_ops_reset_lua_workspace_file(void* handle, const char* relative_path) {
    OpsProgress* ops = AsOps(handle);
    if (ops != nullptr) {
        Installer::ResetLuaWorkspaceFileAsync(
            std::filesystem::path(relative_path ? relative_path : ""), ops->progress);
    }
}

REFII_ABI_EXPORT int refii_ops_verify_lua_workspace_sync(void* handle, char* error, int capacity) {
    OpsProgress* ops = AsOps(handle);
    if (ops == nullptr) {
        return 0;
    }

    ops->verifySummary = {};
    std::string verifyError;
    const bool ok = Installer::VerifyLuaWorkspace(&ops->verifySummary, &verifyError);
    refii::abi::CopyString(verifyError, error, capacity);
    return ok ? 1 : 0;
}

REFII_ABI_EXPORT int refii_ops_verify_entry_count(const void* handle) {
    const OpsProgress* ops = AsOps(handle);
    return ops == nullptr ? 0 : static_cast<int>(ops->verifySummary.entries.size());
}

REFII_ABI_EXPORT int refii_ops_verify_entry_status(const void* handle, int index) {
    const OpsProgress* ops = AsOps(handle);
    if (ops == nullptr || index < 0 ||
        index >= static_cast<int>(ops->verifySummary.entries.size())) {
        return -1;
    }
    return static_cast<int>(ops->verifySummary.entries[static_cast<std::size_t>(index)].status);
}

REFII_ABI_EXPORT int refii_ops_verify_entry_path(const void* handle,
                                                 int index,
                                                 char* buffer,
                                                 int capacity) {
    const OpsProgress* ops = AsOps(handle);
    if (ops == nullptr || index < 0 ||
        index >= static_cast<int>(ops->verifySummary.entries.size())) {
        return refii::abi::CopyString(std::string(), buffer, capacity);
    }
    return refii::abi::CopyString(
        ops->verifySummary.entries[static_cast<std::size_t>(index)].relative_path.generic_string(),
        buffer,
        capacity);
}

REFII_ABI_EXPORT int refii_ops_verify_baseline_missing(const void* handle) {
    const OpsProgress* ops = AsOps(handle);
    return ops == nullptr ? 0 : static_cast<int>(ops->verifySummary.baseline_missing);
}

REFII_ABI_EXPORT int refii_ops_verify_baseline_corrupt(const void* handle) {
    const OpsProgress* ops = AsOps(handle);
    return ops == nullptr ? 0 : static_cast<int>(ops->verifySummary.baseline_corrupt);
}

REFII_ABI_EXPORT int refii_ops_describe_verify_status(int status, char* buffer, int capacity) {
    const char* text = refii::lua::DescribeLuaWorkspaceVerifyStatus(
        static_cast<refii::lua::LuaWorkspaceVerifyStatus>(status));
    return refii::abi::CopyString(text ? text : "", buffer, capacity);
}
