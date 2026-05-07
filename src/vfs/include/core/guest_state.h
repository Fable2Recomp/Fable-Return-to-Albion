#pragma once

#include "vfs_hook_types.h"

#include <array>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include <rex/ppc.h>

namespace refii::vfs {
inline namespace detail {

struct ActiveTextureParserCdf8TraceContext {
  bool enabled = false;
  uint32_t target_guest = 0;
  uint32_t descriptor_guest = 0;
  uint32_t parsed_header_guest = 0;
  uint32_t mip_index = 0;
  uint32_t cache_guest = 0;
  uint32_t r8_guest = 0;
  uint32_t owner_texture_guest = 0;
  uint32_t wrapper_guest = 0;
  uint32_t parser_object_guest = 0;
  uint32_t parser_object_vtable_guest = 0;
  uint32_t parser_object_slot0_guest = 0;
  std::string provider;
};

struct ActiveFrontendTextureConsumerTraceContext {
  bool enabled = false;
  bool verbose = false;
  uint32_t owner_guest = 0;
  uint32_t root_lr = 0;
  uint32_t descriptor_calls = 0;
  uint32_t resolver_calls = 0;
  uint32_t store_pair_calls = 0;
  uint32_t slot_store_calls = 0;
  uint32_t last_descriptor_lr = 0;
  uint32_t last_descriptor_hash = 0;
  uint32_t last_descriptor_guest = 0;
  uint32_t last_descriptor_vtable = 0;
  uint32_t last_descriptor_slot12 = 0;
  uint32_t last_descriptor_slot16 = 0;
  uint32_t last_resolved_object = 0;
  uint32_t last_resolved_hash = 0;
  uint32_t last_resolver_table_index = 0;
  uint32_t last_resolver_table_object = 0;
  uint32_t last_store_destination = 0;
  uint32_t last_store_value = 0;
  bool any_refii_owned = false;
  bool resource_manager_seen = false;
  bool request_manager_seen = false;
  bool texture_parser_seen = false;
  bool resolver_empty_refii_owned = false;
  bool descriptor_slot16_rejected = false;
  bool static_table_list_committed = false;
  bool final_direct_slot_valid = false;
  bool followon_frame_list_committed = false;
  bool synthetic_descriptor_phase_attempted = false;
  std::string descriptor_summary;
  std::string resolver_summary;
  std::string store_summary;
  std::string owner_slot_summary;
};

struct ResourceAliasValidationResult {
  bool ran = false;
  bool attach = false;
  uint32_t result_container = 0;
  uint32_t result_node = 0;
  uint32_t expected_container = 0;
  uint32_t expected_node = 0;
};

extern thread_local int g_suppress_request_manager_hash_entry_loose_streams;
extern uint32_t g_refii_resource_wrapper_vtable;
extern uint32_t g_refii_resource_wrapper_interface_vtable;
extern thread_local uint32_t g_active_texture_parser_trace_descriptor;
extern thread_local ActiveTextureParserCdf8TraceContext
    g_active_texture_parser_cdf8_trace;
extern thread_local ActiveFrontendTextureConsumerTraceContext
    g_active_frontend_texture_consumer_trace;

const ActiveTextureParserCdf8TraceContext* ActiveTextureParserCdf8Trace();
bool TrySilentLargePageReserve(PPCContext& ctx, uint8_t* base);
PPCFunc* FindGuestFunction(uint32_t guest_address);

void ConfigureRefiiVfsTracyPlots();
LooseAssetRuntimeStats& LooseStats();
bool ShouldLogLooseStatsMilestone(uint64_t value);
void LogLooseStatsMilestone(std::string_view label,
                            uint64_t value,
                            size_t stream_record_count);
inline void LogLooseStatsMilestone(std::string_view label, uint64_t value) {
  LogLooseStatsMilestone(label, value, 0);
}
void RecordRefiiOwnedStreamLifecycleOpen(std::string_view provider_name);

uint32_t CallVirtualAlloc(PPCContext& source_ctx,
                          uint8_t* base,
                          uint32_t address,
                          uint32_t size,
                          uint32_t allocation_type,
                          uint32_t protect);
uint32_t CallLhMalloc(PPCContext& source_ctx, uint8_t* base, uint32_t size);
void CallLhFree(PPCContext& source_ctx, uint8_t* base, uint32_t guest_address);
void AddRefGuestObject(PPCContext& source_ctx,
                       uint8_t* base,
                       uint32_t guest_object);
void ReleaseGuestObject(PPCContext& source_ctx,
                        uint8_t* base,
                        uint32_t guest_object);

uint8_t LoadGuestU8(uint8_t* base, uint32_t guest_address);
uint16_t LoadGuestU16(uint8_t* base, uint32_t guest_address);
uint32_t LoadGuestU32(uint8_t* base, uint32_t guest_address);
uint64_t LoadGuestU64(uint8_t* base, uint32_t guest_address);
void StoreGuestU8(uint8_t* base, uint32_t guest_address, uint8_t value);
void StoreGuestU16(uint8_t* base, uint32_t guest_address, uint16_t value);
void StoreGuestU32(uint8_t* base, uint32_t guest_address, uint32_t value);
void StoreGuestU64(uint8_t* base, uint32_t guest_address, uint64_t value);

bool IsRefiiResourceWrapperPrimaryVTable(uint32_t vtable_guest);
bool IsRefiiResourceWrapperInterfaceVTable(uint32_t vtable_guest);
std::pair<uint32_t, uint32_t> EnsureRefiiResourceWrapperVTables(
    PPCContext& ctx,
    uint8_t* base);
bool CallGuestVTableSlot(PPCContext& source_ctx,
                         uint8_t* base,
                         uint32_t object_guest,
                         uint32_t slot_index,
                         std::string_view api);
bool CopyToStreamDestination(PPCContext& source_ctx,
                             uint8_t* base,
                             uint32_t destination_guest,
                             uint32_t destination_kind,
                             uint32_t source_guest,
                             uint32_t byte_count);

std::string ReadGuestWideStringObject(uint8_t* base,
                                      uint32_t string_guest,
                                      size_t max_chars);
std::string ReadGuestAsciiStringObject(uint8_t* base,
                                       uint32_t string_guest,
                                       size_t max_chars);
std::string ReadGuestWideCString(uint8_t* base,
                                 uint32_t string_guest,
                                 size_t max_chars);
bool WriteGuestWideStringObject(uint8_t* base,
                                uint32_t string_guest,
                                std::string_view value);
bool IsOutputStreamSet(uint8_t* base, uint32_t out_guest);
std::string FormatGuestWords(uint8_t* base,
                             uint32_t guest,
                             uint32_t word_count);
std::string JoinStrings(const std::vector<std::string>& values,
                        std::string_view separator);
std::string FormatGuestAsciiBytes(uint8_t* base,
                                  uint32_t guest,
                                  uint32_t byte_count);
std::string ReadBestEffortGuestString(uint8_t* base,
                                      uint32_t string_guest,
                                      uint32_t max_chars);
std::string FormatGuestObjectVTable(uint8_t* base,
                                    uint32_t object_guest,
                                    uint32_t slot_count);
bool ShouldLogVfsTraceOperation(std::string_view api);
bool ShouldLogRepeatedVfsTraceOperation(std::string_view api,
                                        uint32_t limit);
inline bool ShouldLogRepeatedVfsTraceOperation(std::string_view api) {
  return ShouldLogRepeatedVfsTraceOperation(api, 8);
}
std::string DescribeGuestObject(uint8_t* base,
                                uint32_t object_guest,
                                uint32_t word_count,
                                uint32_t slot_count);
inline std::string DescribeGuestObject(uint8_t* base, uint32_t object_guest) {
  return DescribeGuestObject(base, object_guest, 12, 12);
}
std::string ReadBankDebugName(uint8_t* base, uint32_t bank_guest);
std::string ReadMaybeGuestString(uint8_t* base,
                                 uint32_t string_guest,
                                 uint32_t max_chars);
inline std::string ReadMaybeGuestString(uint8_t* base,
                                        uint32_t string_guest) {
  return ReadMaybeGuestString(base, string_guest, 256);
}
std::string KnownGuiCommandForCallerLr(uint32_t lr);

bool IsTraceInterestingAsset(std::string_view asset_path);
bool IsMainMenuTextureTraceAsset(std::string_view asset_path);
bool IsMainMenuTextureHashTraceAsset(std::string_view asset_path);
bool ShouldAllowOwnerlessSplitTextureWrapperCombine(std::string_view api,
                                                    std::string_view provider);
bool IsMainMenuTextureResourceHash(uint32_t resource_hash);
std::string_view MainMenuTextureNameForResourceHash(uint32_t resource_hash);
bool IsBootFontResourceHash(uint32_t resource_hash);
bool ShouldTracePostVideoFontCaller(uint32_t lr);
bool IsResourceWrapperTextureTraceAsset(std::string_view asset_path);
bool IsTextureResourceWrapperCaller(uint32_t lr);
bool ShouldTraceNativeResourceWrapperApi(std::string_view api, uint32_t lr);
bool IsGameFaceBoundaryPath(std::string_view asset_path);
bool ShouldLogFileApiPath(std::string_view api, std::string_view path);
bool LooksLikeConcreteVfsFilePath(std::string_view asset_path);
bool ShouldTraceMeshResourceValidate(uint32_t link_register);
bool IsSupportedDispatchHeaderType(uint8_t type);
void SanitizeCriticalSectionBeforeEnter(uint8_t* base,
                                        uint32_t critical_section_guest,
                                        uint32_t link_register);
bool ApplyResourceAliasNameSideEffects(PPCContext& source_ctx,
                                       uint8_t* base,
                                       uint32_t owner_guest,
                                       uint32_t resolved_name_guest,
                                       uint32_t requested_hash);
bool ApplySyntheticResourceAliasAttach(PPCContext& source_ctx,
                                       uint8_t* base,
                                       uint32_t owner_guest,
                                       uint32_t requested_name_guest);
ResourceAliasValidationResult ValidateSyntheticResourceAliasAttach(
    PPCContext& source_ctx,
    uint8_t* base,
    uint32_t owner_guest,
    uint32_t requested_hash);

float RefiiGuestF32FromWord(uint32_t word);
uint32_t RefiiGuestF32ToWord(float value);
double RefiiGuestF64FromWord(uint64_t word);
uint64_t RefiiGuestF64ToWord(double value);
void LogRefiiVfsChainVerdict(std::string_view chain_id,
                             uint32_t root_lr,
                             std::string_view asset,
                             std::string_view candidate,
                             std::string_view provider_relative,
                             std::string_view shape_owner,
                             std::string_view live_boundary,
                             std::string_view collapse_status,
                             std::string_view next_boundary);

}  // namespace detail
}  // namespace refii::vfs
