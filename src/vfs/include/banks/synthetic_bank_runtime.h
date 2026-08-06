#pragma once

#include "loose_asset_hooks.h"
#include "vfs_hook_types.h"

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

struct PPCContext;

namespace refii::vfs {
inline namespace detail {

std::shared_ptr<RefiiSyntheticBankRecord> FindSyntheticBankRecord(
    uint32_t bank_guest);
void StoreSyntheticBankRecord(
    uint32_t bank_guest,
    std::shared_ptr<RefiiSyntheticBankRecord> record);
void EraseSyntheticBankRecord(uint32_t bank_guest);
size_t SyntheticBankRecordCount();

std::shared_ptr<RefiiSyntheticBankIteratorRecord>
FindSyntheticBankIteratorRecord(uint32_t iterator_guest);
void StoreSyntheticBankIteratorRecord(
    uint32_t iterator_guest,
    std::shared_ptr<RefiiSyntheticBankIteratorRecord> record);
void EraseSyntheticBankIteratorRecord(uint32_t iterator_guest);
size_t SyntheticBankIteratorRecordCount();

std::unordered_map<std::string, std::shared_ptr<RefiiSyntheticBankTemplate>>&
SyntheticBankTemplates();
std::mutex& SyntheticBankTemplatesMutex();
std::vector<std::shared_ptr<const RefiiSyntheticBankTemplate>>
GetSyntheticBankTemplateSnapshot();
std::mutex& SyntheticVfsEntryIndexMutex();
std::shared_ptr<const SyntheticVfsEntryIndex>& CachedSyntheticVfsEntryIndex();
void IndexSyntheticManifestEntries(SyntheticVfsEntryIndex& index,
                                   const std::filesystem::path& manifest_root,
                                   const std::filesystem::path& manifest_path);
uint32_t ComputeResourceNameHash(std::string_view value);
bool ParseHexUint32(std::string_view text, uint32_t* out_value);
std::string_view GetLanguageAssetTail(std::string_view normalized_alias);
std::string CanonicalizeRefiiVfsDatabaseAliasKey(std::string_view normalized_key);
bool IsLanguageVideoAssetTail(std::string_view asset_tail);
bool IsLanguageVideoAssetKey(std::string_view normalized_alias);
std::string MakeSyntheticVfsLookupCacheKey(std::string_view asset_path,
                                           size_t template_count);
std::vector<std::string> BuildSyntheticVfsEntryCandidates(
    std::string_view asset_path);
std::vector<std::string> BuildRefiiVfsDatabasePrimeAliases(
    const std::vector<std::string>& aliases);
int ScoreVfsDatabaseAliasCandidate(
    std::string_view candidate,
    const std::vector<std::string>& aliases);
std::string FormatCandidateSample(const std::vector<std::string>& candidates,
                                  size_t max_count);
inline std::string FormatCandidateSample(
    const std::vector<std::string>& candidates) {
  return FormatCandidateSample(candidates, 6);
}
std::shared_ptr<const RefiiSyntheticBankTemplate>
FindOrCreateSyntheticBankTemplate(PPCContext& ctx,
                                  uint8_t* base,
                                  std::string_view bank_name,
                                  std::string_view api);
std::string ReadBankNameForLookup(uint8_t* base, uint32_t bank_guest);
std::string InferSyntheticBankNameFromStream(uint8_t* base,
                                             uint32_t stream_guest);
bool ReadSyntheticBankEntryByIndex(
    const std::shared_ptr<const RefiiSyntheticBankTemplate>& bank_template,
    uint32_t entry_index,
    BankManifestEntry* out_entry);
bool ShouldTraceSyntheticBankEntry(std::string_view bank_name,
                                   const BankManifestEntry* entry);
std::string DescribeSyntheticBankEntry(const BankManifestEntry& entry);
std::string DescribeSyntheticBankEntryWindow(
    const std::shared_ptr<const RefiiSyntheticBankTemplate>& bank_template,
    uint32_t center_index);
bool TryCreateSyntheticBank(PPCContext& ctx,
                            uint8_t* base,
                            uint32_t out_guest,
                            std::string_view bank_name,
                            std::string_view api);
void EnsureRefiiBootBankTemplates(PPCContext& ctx,
                                  uint8_t* base,
                                  std::string_view api);
bool ReadBankIndexEntryOffsetSize(uint8_t* base,
                                  uint32_t bank_guest,
                                  uint32_t entry_index,
                                  uint32_t* out_offset,
                                  uint32_t* out_size);
std::string LookupBankIndexEntryName(uint8_t* base,
                                     uint32_t bank_guest,
                                     uint32_t entry_index);
bool SyntheticBankEntryMatchesFilter(const BankManifestEntry& entry,
                                     std::string_view entry_filter);
std::vector<std::string> MakeBankLooseCandidates(std::string_view bank_name,
                                                 std::string_view entry_name);
std::string MakeBankLooseResolutionCacheKey(std::string_view bank_name,
                                            std::string_view entry_name);
bool ResolveManifestEntryLooseAsset(const std::filesystem::path& manifest_root,
                                    std::string_view entry_name,
                                    LooseAssetResolution* out_resolution,
                                    std::string* out_candidate,
                                    std::string_view host_entry_name);
bool ResolveBankLooseAssetOverride(std::string_view bank_name,
                                   std::string_view entry_name,
                                   LooseAssetResolution* out_resolution,
                                   std::string* out_candidate);
bool ShouldIndexRefiiResourceHashCandidate(std::string_view candidate);

}  // namespace detail
}  // namespace refii::vfs
