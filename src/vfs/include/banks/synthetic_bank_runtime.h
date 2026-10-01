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
void StoreSyntheticBankRecord(
    uint32_t bank_guest,
    std::shared_ptr<RefiiSyntheticBankRecord> record);
void EraseSyntheticBankRecord(uint32_t bank_guest);
size_t SyntheticBankRecordCount();

void EraseSyntheticBankIteratorRecord(uint32_t iterator_guest);

std::unordered_map<std::string, std::shared_ptr<RefiiSyntheticBankTemplate>>&
SyntheticBankTemplates();
std::mutex& SyntheticBankTemplatesMutex();
uint32_t ComputeResourceNameHash(std::string_view value);
std::string_view GetLanguageAssetTail(std::string_view normalized_alias);
std::string CanonicalizeRefiiVfsDatabaseAliasKey(std::string_view normalized_key);
bool IsLanguageVideoAssetTail(std::string_view asset_tail);
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
bool ReadSyntheticBankEntryByIndex(
    const std::shared_ptr<const RefiiSyntheticBankTemplate>& bank_template,
    uint32_t entry_index,
    BankManifestEntry* out_entry);
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
bool ShouldIndexRefiiResourceHashCandidate(std::string_view candidate);
}  // namespace detail
}  // namespace refii::vfs
