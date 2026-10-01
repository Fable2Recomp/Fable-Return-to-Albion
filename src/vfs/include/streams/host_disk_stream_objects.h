#pragma once

#include "loose_asset_hooks.h"
#include "streams/types.h"

#include <cstdint>
#include <filesystem>
#include <memory>
#include <string_view>

struct PPCContext;

namespace refii::vfs {
inline namespace detail {

void StoreRefiiHostDiskStreamObject(uint8_t* base,
                                    uint32_t stream_guest,
                                    uint64_t size);
uint32_t CreateRefiiHostDiskStream(
    PPCContext& ctx,
    uint8_t* base,
    uint32_t mode,
    const std::filesystem::path& loose_path,
    std::string_view provider_relative_path);
uint32_t CreateRefiiHostMemoryStream(
    PPCContext& ctx,
    uint8_t* base,
    uint32_t mode,
    std::string_view asset_key,
    std::string_view provider_relative_path);
bool TryOpenLooseAssetResourceWrapper(
    PPCContext& ctx,
    uint8_t* base,
    uint32_t out_guest,
    uint32_t mode,
    const LooseAssetResolution& resolution,
    std::string_view provider_relative_path,
    uint8_t resource_flags,
    uint64_t read_offset,
    uint32_t requested_size);
bool TryOpenLooseAsset(PPCContext& ctx,
                       uint8_t* base,
                       uint32_t out_guest,
                       uint32_t manager_guest,
                       uint32_t mode,
                       const LooseAssetResolution& resolution,
                       std::string_view provider_relative_path);
uint32_t ReadRefiiHostStreamBytes(uint8_t* base,
                                  uint32_t stream_guest,
                                  uint32_t buffer_guest,
                                  uint64_t offset,
                                  uint32_t requested_bytes);
uint32_t ReadRefiiHostStreamToDestination(PPCContext& ctx,
                                          uint8_t* base,
                                          uint32_t stream_guest,
                                          uint32_t destination_guest,
                                          uint64_t offset,
                                          uint32_t requested_bytes);
uint32_t ReadRefiiHostStreamForReadRequest(
    PPCContext& ctx,
    uint8_t* base,
    const std::shared_ptr<RefiiReadRequestRecord>& record);

bool IsRefiiHostDiskStream(uint8_t* base, uint32_t stream_guest);
bool IsRefiiReadRequestObject(uint32_t request_guest);
bool IsSupportedRefiiLooseOpenMode(uint32_t mode);
bool IsCompositeStreamWithRefiiHostSegment(uint8_t* base,
                                           uint32_t stream_guest);
bool IsRefiiStreamReader(uint8_t* base, uint32_t reader_guest);
uint32_t RefiiReaderPageEntry(uint8_t* base,
                              uint32_t reader_guest,
                              uint32_t page_index);
}  // namespace detail
}  // namespace refii::vfs
