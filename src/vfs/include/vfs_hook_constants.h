#pragma once

#include <cstdint>

namespace refii::vfs {
inline namespace detail {

constexpr uint32_t kRefiiSyntheticBankVTable = 0x8200DEA0;
constexpr uint32_t kRefiiSyntheticBankSize = 128;
// rex_lhStream_DiskStream_Ctor builds this as 0x82010000 - 0x1FD4.
constexpr uint32_t kRefiiDiskStreamVTable = 0x8200E02C;
constexpr uint32_t kNativeBufferedStreamVTable = 0x820F9AE0;
constexpr uint32_t kStreamDestinationCopyTable = 0x83317164;
constexpr uint32_t kRefiiHostDiskStreamSize = 32;
constexpr uint32_t kNativeReadRequestVTable = 0x820FABBC;
constexpr uint32_t kNativeReadRequestInterfaceVTable = 0x820FABEC;
constexpr uint32_t kNativeReadRequestSize = 344;
constexpr uint32_t kResourceStreamWrapperVTable = 0x820F9DA8;
constexpr uint32_t kResourceStreamWrapperInterfaceVTable = 0x820F9DD8;
constexpr uint32_t kResourceStreamWrapperSize = 0x40;
constexpr uint32_t kResourceWrapperVTableCopySize = 0x40;
constexpr uint32_t kNativeStartupReadyRequestVTable = 0x820A8FA8;
constexpr uint32_t kNativeFrontendRenderRequestVTable = 0x820A48F0;
constexpr bool kDirectResourceManagerWrapperOverride = true;
constexpr bool kTraceNonVfsBootFrameProbes = false;
// Gated at runtime by vfs_trace_vfs.
constexpr bool kTraceFrontendHandoffDiagnostics = true;
constexpr bool kTraceBikCallbackDiagnostics = false;
constexpr bool kTraceBikConsumerHandoffDiagnostics = true;
constexpr bool kTraceMeshModelAudioDiagnostics = false;
}  // namespace detail
}  // namespace refii::vfs
