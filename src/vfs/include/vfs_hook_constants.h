#pragma once

#include <cstdint>

namespace refii::vfs {
inline namespace detail {

constexpr uint32_t kRefiiSyntheticBankVTable = 0x8200DEA0;
constexpr uint32_t kRefiiSyntheticBankSize = 128;
// rex_lhStream_DiskStream_Ctor builds this as 0x82010000 - 0x1FD4.
constexpr uint32_t kRefiiDiskStreamVTable = 0x8200E02C;
constexpr uint32_t kNativeBufferedStreamVTable = 0x820F9AE0;
constexpr uint32_t kStartupBikObjectVTable = 0x8200DF48;
// sub_82374728 constructs localized resource aliases as 0x820B0000 - 28832.
constexpr uint32_t kResourceAliasVTable = 0x820A8F60;
constexpr uint32_t kStreamDestinationCopyTable = 0x83317164;
constexpr uint32_t kRefiiHostDiskStreamSize = 32;
constexpr uint32_t kNativeReadRequestVTable = 0x820FABBC;
constexpr uint32_t kNativeReadRequestInterfaceVTable = 0x820FABEC;
constexpr uint32_t kNativeReadRequestSize = 344;
constexpr uint32_t kResourceStreamWrapperVTable = 0x820F9DA8;
constexpr uint32_t kResourceStreamWrapperInterfaceVTable = 0x820F9DD8;
constexpr uint32_t kResourceStreamWrapperSize = 0x40;
constexpr uint32_t kResourceWrapperDtorFunc = 0x82B54D28;
constexpr uint32_t kResourceWrapperStatusFunc = 0x82B542D8;
constexpr uint32_t kResourceWrapperWaitFunc = 0x82B54250;
constexpr uint32_t kResourceWrapperGetBytesFunc = 0x82B54268;
constexpr uint32_t kResourceWrapperGetSizeFunc = 0x82C0EE10;
constexpr uint32_t kResourceWrapperSlot36Func = 0x82B542F0;
constexpr uint32_t kResourceWrapperVTableCopySize = 0x40;
constexpr uint32_t kRefiiRequestManagerProviderIndex = 0x7FEE0001;
constexpr uint32_t kRefiiRequestManagerEntryKey = 0x52464949;
constexpr uint32_t kRefiiPrivateHandleBase = 0xF9000000;
constexpr uint32_t kRefiiPrivateHandleInvalid = 0xFFFFFFFF;
constexpr uint32_t kTextureParserSingletonObjectGlobal = 0x83497138;
constexpr uint32_t kNativeStartupReadyRequestVTable = 0x820A8FA8;
constexpr uint32_t kNativeFrontendRenderRequestVTable = 0x820A48F0;
constexpr uint32_t kNativeFrontendRenderRequestSlotVTable = 0x820A4910;
constexpr bool kDirectResourceManagerOpenOverride = true;
constexpr bool kDirectResourceManagerWrapperOverride = true;
constexpr bool kDirectTextureCallerOverride = true;
constexpr bool kAutoAugmentRequestManagerRuntimeHashes = false;
constexpr bool kTraceNonVfsBootFrameProbes = false;
// Gated at runtime by vfs_trace_vfs; kept compiled in so post-video VFS
// stalls expose the ready gate, frontend consumer, and texture commit path.
constexpr bool kTraceFrontendHandoffDiagnostics = true;
constexpr bool kTraceBikCallbackDiagnostics = false;
constexpr bool kTraceBikConsumerHandoffDiagnostics = true;
constexpr bool kTraceTextureParserInternals = false;
constexpr bool kTraceMeshModelAudioDiagnostics = false;

}  // namespace detail
}  // namespace refii::vfs
