#pragma once

#include <cstdint>
#include "core/guest_build.h"

namespace refii::vfs {
inline namespace detail {

constexpr refii::guest::Addr kRefiiSyntheticBankVTable{0x8200DEA0u};
constexpr uint32_t kRefiiSyntheticBankSize = 128;
// rex_lhStream_DiskStream_Ctor builds this as 0x82010000 - 0x1FD4.
constexpr refii::guest::Addr kRefiiDiskStreamVTable{0x8200E02Cu};
constexpr refii::guest::Addr kNativeBufferedStreamVTable{0x820F9AE0u};
constexpr refii::guest::Addr kStreamDestinationCopyTable{0x83317164u};
constexpr uint32_t kRefiiHostDiskStreamSize = 32;
constexpr refii::guest::Addr kNativeReadRequestVTable{0x820FABBCu};
constexpr refii::guest::Addr kNativeReadRequestInterfaceVTable{0x820FABECu};
constexpr uint32_t kNativeReadRequestSize = 344;
constexpr refii::guest::Addr kResourceStreamWrapperVTable{0x820F9DA8u};
constexpr refii::guest::Addr kResourceStreamWrapperInterfaceVTable{0x820F9DD8u};
constexpr uint32_t kResourceStreamWrapperSize = 0x40;
constexpr uint32_t kResourceWrapperVTableCopySize = 0x40;
constexpr refii::guest::Addr kNativeStartupReadyRequestVTable{0x820A8FA8u};
constexpr refii::guest::Addr kNativeFrontendRenderRequestVTable{0x820A48F0u};
constexpr bool kDirectResourceManagerWrapperOverride = true;
constexpr bool kTraceNonVfsBootFrameProbes = false;
// Gated at runtime by vfs_trace_vfs.
constexpr bool kTraceFrontendHandoffDiagnostics = true;
constexpr bool kTraceBikCallbackDiagnostics = false;
constexpr bool kTraceBikConsumerHandoffDiagnostics = true;
constexpr bool kTraceMeshModelAudioDiagnostics = false;
}  // namespace detail
}  // namespace refii::vfs
