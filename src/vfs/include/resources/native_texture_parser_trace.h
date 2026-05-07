#pragma once

#include "core/guest_state.h"

#include <cstdint>
#include <string>
#include <string_view>

namespace refii::vfs {
inline namespace detail {

void RecordRefiiPostLoadingAfterHolderRenderOwner(uint32_t owner_guest);
uint32_t SelectRefiiPostLoadingAfterHolderRenderOwner();
std::string FormatNativeVfsAsyncQueueState(uint8_t* base,
                                           uint32_t queue_guest);
std::string FormatNativeVfsRetryInsertState(uint8_t* base,
                                            uint32_t list_guest,
                                            uint32_t pair_high_guest,
                                            uint32_t pair_low_guest,
                                            uint32_t value_holder_guest);
std::string FormatNativeVfsSchedulerState(uint8_t* base,
                                          uint32_t scheduler_guest);
std::string FormatNativeVfsSchedulerVectorState(uint8_t* base,
                                                uint32_t vector_guest);
std::string FormatGeneratedEventWaitState(uint8_t* base,
                                          uint32_t wait_object_guest);
std::string FormatGeneratedEventSetState(uint8_t* base,
                                         uint32_t event_guest);
std::string FormatPostVideoFrameTeardownState(uint8_t* base,
                                              uint32_t object_guest);
std::string ReadStartupBikRequestName(uint8_t* base, uint32_t name_guest);
bool ShouldTraceBikObjectVirtualProbe(uint8_t* base,
                                      uint32_t object_guest,
                                      std::string_view api,
                                      uint32_t limit);
bool ShouldTraceBikConsumerHandoffProbe(uint8_t* base,
                                        uint32_t object_guest,
                                        uint32_t stream_guest,
                                        std::string_view requested_name,
                                        std::string_view api,
                                        uint32_t limit);
std::string FormatTextureParserCdf8DispatchSummary(uint8_t* base);
void RecordTextureParserCdf8ParserObject(uint8_t* base,
                                         uint32_t object_guest);
std::string FormatTextureParserCdf8Context(uint8_t* base);
bool IsParserBoundaryTextureWrapperCaller(uint32_t lr);
std::string GetTextureParserTraceProvider(uint8_t* base,
                                          uint32_t descriptor_guest,
                                          uint32_t* out_wrapper_guest);
bool ShouldTraceTextureParserCall(uint8_t* base,
                                  uint32_t lr,
                                  uint32_t descriptor_guest);
uint32_t ActiveTextureParserTraceDescriptor();
class ScopedTextureParserTraceDescriptor {
 public:
  ScopedTextureParserTraceDescriptor(bool enabled, uint32_t descriptor_guest);
  ~ScopedTextureParserTraceDescriptor();

 private:
  uint32_t previous_descriptor_ = 0;
};

class ScopedTextureParserCdf8Trace {
 public:
  ScopedTextureParserCdf8Trace(bool enabled,
                               uint32_t target_guest,
                               uint32_t descriptor_guest,
                               uint32_t parsed_header_guest,
                               uint32_t mip_index,
                               uint32_t cache_guest,
                               uint32_t r8_guest);
  ~ScopedTextureParserCdf8Trace();

 private:
  ActiveTextureParserCdf8TraceContext previous_context_;
};

bool HasTextureParserDescriptorTraceOwner(uint32_t descriptor_guest);
bool ShouldTreatAsTextureParserDescriptor(uint8_t* base,
                                          uint32_t descriptor_guest);
bool ShouldTraceTextureParserProbeCall(uint8_t* base, const PPCContext& ctx);
bool ShouldTraceTextureParserDirectChainCall(uint8_t* base,
                                             const PPCContext& ctx);
uint32_t SelectTextureParserDescriptorArgument(uint8_t* base,
                                               const PPCContext& ctx);
std::string FormatTextureParserPrimaryWords(uint8_t* base,
                                            uint32_t descriptor_guest);
std::string FormatTextureParserSecondaryWords(uint8_t* base,
                                              uint32_t descriptor_guest);
std::string FormatTextureParserDescriptorSummary(uint8_t* base,
                                                 uint32_t descriptor_guest);
std::string FormatTextureParserDescriptorOwnerWords(uint8_t* base,
                                                    uint32_t descriptor_guest);
void LogTextureParserProbeEnter(uint8_t* base,
                                const PPCContext& original_ctx,
                                std::string_view label);
void LogTextureParserProbeExit(uint8_t* base,
                               const PPCContext& original_ctx,
                               const PPCContext& ctx,
                               std::string_view label);

}  // namespace detail
}  // namespace refii::vfs
