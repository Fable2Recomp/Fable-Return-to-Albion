// refii — GDB hot reload: the guest's in-memory databases take a remounted .gdb / .gdb.tu
// without a relaunch (mod_commands.cpp `gdb_reload PATH` / `gdb_revert PATH HASH...`).
//
// WHAT THE GUEST DOES (default.xex TU1; the same bytes on the disc build):
//   GDBManager_LoadFile 0x829FFFD0 (manager, path, tag) allocates a 64-byte GDBFile and has
//   GDBFile_Parse 0x82A016C0 read the container into ONE allocation (file+32) that it keeps:
//   each record's schema word is fixed up to an absolute pointer in place
//   (0x82A01858..0x82A0189C `*(v19 + file+8) += file+28`), the name tail goes into the
//   manager's name registry (GDBNames_RegisterTable 0x82A01338, hashes it knows are skipped),
//   then GDBManager_RegisterFileEntries 0x82A00280 inserts every {id, record pointer} into the
//   manager's open-addressed map (slots at manager+16, capacity +20, count +24) through
//   sub_829FFA80, which OVERWRITES a present id (`cmplw cr6,r10,r7 / beq loc_829FFAD8 /
//   stdx r4,r11,r9`), and the file node {next, prev, handle, tag} goes at the FRONT of the
//   manager's list (sub_82683BA0 at 0x82A000CC; manager+4 is the sentinel). That is how a
//   title update replaces a record: GDB_LoadDatabaseWithTU 0x829FFD68 loads X.gdb then
//   X.gdb.tu with the same tag.
//
//   Every lookup by id goes through that map (GDBManager_FindEntryByHash 0x821E4250) and every
//   value read is a pointer into the record (GDBEntry_FindPropWalkParents 0x821E5A08 returns
//   &record[1 + slot]; sub_82A008F8 and GDBEntry_GetParent 0x821E58D8 index the record directly).
//   Entities cache record POINTERS (sub_821DA348 keeps FindEntryByHash's answer at +4), so a
//   loaded file is never freed here: the guest's own unload (0x82A000E0) is per tag and the
//   level's, not ours.
//
// So a reload IS the guest's title-update mechanism run again: LoadFile on the remounted path
// (the VFS now serves the new bytes) maps every id the file states to its new record — a lookup
// made from now on sees added rows, added properties and changed values — and then every OLDER
// generation of each record (the base file's, an earlier patch's) takes the new values in place,
// property by property through GDBSchema_FindPropHash 0x821E5970, so a holder of an old pointer
// sees them too. A property an older copy's schema lacks cannot be written into it: a holder
// of that copy keeps the old shape until it looks the record up again.
//
// A revert (a row a mod stated no more) re-points the map at the base file's record
// (sub_829FFA80 on the id) and writes the base values into every other generation; a row the
// base never had stays mapped to its last patch — the map has no tombstones.

#include "vfs_hooks_private.h"

#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#include <rex/logging.h>

#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/refii_mod.h"
#include "hooks/ppc_externs.h"

PPC_EXTERN_FUNC(rex_GDBManager_LoadFile);
PPC_EXTERN_FUNC(rex_GDBFile_FindEntry);
PPC_EXTERN_FUNC(rex_GDBSchema_FindPropHash);
PPC_EXTERN_FUNC(rex_GDBManager_MapInsert);

namespace {

using refii::mods::LoadU32;
using refii::mods::StoreU32;

/// g_GDBManager: a cell holding the manager (the game's callers pass the cell and dereference).
constexpr uint32_t kGdbManagerCell = 0x83496DA0u;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

/// One loaded database, newest first in the manager's list.
struct LoadedFile {
  uint32_t file = 0;  // the GDBFile: +0 ids, +4 count, +8 records, +12 stride, +16 aliases, +20 alias count
  uint32_t tag = 0;   // the list node's tag (0 globals, 1 level, 2 environment themes)
  std::string path;   // the path the guest opened it by (file+36: an MSVC std::string)
  std::string key;    // `path` lower-cased with '/' separators
};

uint32_t Manager(uint8_t* base) {
  const uint32_t cell = refii::vfs::LoadGuestU32(base, refii::guest::Address(kGdbManagerCell));
  if (!Plausible(cell)) {
    return 0;
  }
  const uint32_t manager = LoadU32(base, cell);
  return Plausible(manager) ? manager : 0;
}

std::string Normalise(std::string path) {
  for (char& c : path) {
    if (c == '\\') {
      c = '/';
    } else if (c >= 'A' && c <= 'Z') {
      c = static_cast<char>(c - 'A' + 'a');
    }
  }
  return path;
}

/// The guest's path string at file+36: 16 bytes in place under a capacity of 15, else a pointer.
std::string FilePath(uint8_t* base, uint32_t file) {
  const uint32_t size = LoadU32(base, file + 56u);
  const uint32_t capacity = LoadU32(base, file + 60u);
  if (size == 0 || size > 1024u) {
    return std::string();
  }
  const uint32_t at = capacity >= 16u ? LoadU32(base, file + 40u) : file + 40u;
  if (!Plausible(at)) {
    return std::string();
  }
  return std::string(reinterpret_cast<const char*>(base + at), size);
}

std::vector<LoadedFile> Files(uint8_t* base, uint32_t manager) {
  std::vector<LoadedFile> files;
  const uint32_t sentinel = LoadU32(base, manager + 4u);
  if (!Plausible(sentinel)) {
    return files;
  }
  uint32_t node = LoadU32(base, sentinel);
  for (int guard = 0; guard < 256 && Plausible(node) && node != sentinel; ++guard) {
    const uint32_t handle = LoadU32(base, node + 8u);
    const uint32_t file = Plausible(handle) ? LoadU32(base, handle) : 0u;
    if (Plausible(file)) {
      LoadedFile entry;
      entry.file = file;
      entry.tag = LoadU32(base, node + 12u);
      entry.path = FilePath(base, file);
      entry.key = Normalise(entry.path);
      files.push_back(std::move(entry));
    }
    node = LoadU32(base, node);
  }
  return files;
}

/// `key` names `want` when it is the same path or ends with it at a separator.
bool Matches(const std::string& key, const std::string& want) {
  if (want.empty() || key.size() < want.size()) {
    return false;
  }
  if (key.size() == want.size()) {
    return key == want;
  }
  return key.compare(key.size() - want.size(), want.size(), want) == 0 &&
         key[key.size() - want.size() - 1] == '/';
}

/// The database a path belongs to: `x.gdb.tu` and `x.gdb` are one family.
std::string FamilyOf(const std::string& key) {
  static const char kTu[] = ".tu";
  if (key.size() > 3 && key.compare(key.size() - 3, 3, kTu) == 0) {
    return key.substr(0, key.size() - 3);
  }
  return key;
}

const LoadedFile* Find(const std::vector<LoadedFile>& files, const std::string& want) {
  for (const LoadedFile& file : files) {
    if (Matches(file.key, want)) {
      return &file;
    }
  }
  return nullptr;
}

/// sub_82A015F8: the file's own record for `id` (binary search of its sorted ids, then the
/// interpolation delta), 0 when the file does not state it.
uint32_t FindEntry(const PPCContext& ctx, uint8_t* base, uint32_t file, uint32_t id) {
  PPCContext work = refii::mods::Frame(ctx);
  work.r3.u32 = file;
  work.r4.u32 = id;
  rex_GDBFile_FindEntry(work, base);
  return Plausible(work.r3.u32) ? work.r3.u32 : 0u;
}

/// GDBSchema_FindPropHash: the FIRST sorted slot of `hash` in `schema` (a repeated hash is an
/// array property; the walk back at 0x821E59D0 lands on its first element).
bool FindSlot(const PPCContext& ctx, uint8_t* base, uint32_t schema, uint32_t hash, uint32_t scratch,
              uint32_t& slot) {
  StoreU32(base, scratch, 0xFFFFFFFFu);
  PPCContext work = refii::mods::Frame(ctx);
  work.r3.u32 = schema;
  work.r4.u32 = hash;
  work.r5.u32 = scratch;
  rex_GDBSchema_FindPropHash(work, base);
  if ((work.r3.u32 & 0xFFu) == 0u) {
    return false;
  }
  slot = LoadU32(base, scratch);
  return true;
}

/// Every value `from` states written into `to` where `to`'s schema declares the same property
/// with the same type (array elements by position). Records are [schema pointer][u32 per sorted
/// property]; a schema is [count << 8 | flags][sorted hashes][descriptors: type << 24 | ...].
/// Returns how many values were written.
int SyncRecord(const PPCContext& ctx, uint8_t* base, uint32_t from, uint32_t to, uint32_t scratch) {
  const uint32_t schema_from = LoadU32(base, from);
  const uint32_t schema_to = LoadU32(base, to);
  if (!Plausible(schema_from) || !Plausible(schema_to)) {
    return 0;
  }
  const uint32_t n_from = LoadU32(base, schema_from) >> 8;
  const uint32_t n_to = LoadU32(base, schema_to) >> 8;
  if (n_from == 0 || n_from > 4096u || n_to == 0 || n_to > 4096u) {
    return 0;
  }
  int written = 0;
  for (uint32_t k = 0; k < n_from; ++k) {
    const uint32_t hash = LoadU32(base, schema_from + 4u + 4u * k);
    const uint32_t type = LoadU32(base, schema_from + 4u + 4u * n_from + 4u * k) >> 24;
    // The element index within a repeated hash: the sorted table keeps the copies adjacent.
    uint32_t element = 0;
    while (k - element > 0 && LoadU32(base, schema_from + 4u + 4u * (k - element - 1u)) == hash) {
      ++element;
    }
    uint32_t slot = 0;
    if (!FindSlot(ctx, base, schema_to, hash, scratch, slot)) {
      continue;
    }
    slot += element;
    if (slot >= n_to || LoadU32(base, schema_to + 4u + 4u * slot) != hash) {
      continue;
    }
    if ((LoadU32(base, schema_to + 4u + 4u * n_to + 4u * slot) >> 24) != type) {
      continue;
    }
    const uint32_t value = LoadU32(base, from + 4u + 4u * k);
    if (LoadU32(base, to + 4u + 4u * slot) != value) {
      StoreU32(base, to + 4u + 4u * slot, value);
      ++written;
    }
  }
  return written;
}

/// `source`'s values for `id` written into every other generation of the family that states
/// the record (the base file, earlier patches, earlier reloads).
int SyncOthers(const PPCContext& ctx, uint8_t* base, const std::vector<LoadedFile>& files,
               const LoadedFile& source, uint32_t id, uint32_t record, uint32_t scratch, int& copies) {
  const std::string family = FamilyOf(source.key);
  int written = 0;
  for (const LoadedFile& other : files) {
    if (other.file == source.file || FamilyOf(other.key) != family) {
      continue;
    }
    const uint32_t old = FindEntry(ctx, base, other.file, id);
    if (old == 0 || old == record) {
      continue;
    }
    ++copies;
    written += SyncRecord(ctx, base, record, old, scratch);
  }
  return written;
}

uint32_t ParseHash(const std::string& token) {
  return static_cast<uint32_t>(std::strtoull(token.c_str(), nullptr, 0));
}

}  // namespace

namespace refii::vfs {

/// `gdb_reload PATH`: PATH a database the guest has loaded (any suffix of its own path, '/' or
/// '\\', any case: `globals/globals.gdb.tu`), or the `.tu` of one it has — loaded by the guest's
/// own loader with the base's tag when the guest never saw the patch (a mod enabled live).
void GdbReload(PPCContext& ctx, uint8_t* base, const std::string& path) {
  const uint32_t manager = Manager(base);
  if (manager == 0) {
    REXLOG_WARN("Refii gdb: reload of {}: no database manager", path);
    return;
  }
  std::vector<LoadedFile> files = Files(base, manager);
  const std::string want = Normalise(path);
  std::string guest_path;
  uint32_t tag = 0;
  if (const LoadedFile* loaded = Find(files, want)) {
    guest_path = loaded->path;
    tag = loaded->tag;
  } else if (FamilyOf(want) != want) {
    if (const LoadedFile* parent = Find(files, FamilyOf(want))) {
      guest_path = parent->path + ".tu";
      tag = parent->tag;
    }
  }
  if (guest_path.empty()) {
    REXLOG_INFO("Refii gdb: reload of {}: the guest has not loaded that database; it is served from now on",
                path);
    return;
  }

  const uint32_t heap = CallLhMalloc(ctx, base, static_cast<uint32_t>(guest_path.size()) + 1u);
  if (!Plausible(heap)) {
    REXLOG_WARN("Refii gdb: reload of {}: no guest memory for the path", path);
    return;
  }
  std::memcpy(base + heap, guest_path.c_str(), guest_path.size() + 1u);
  PPCContext work = refii::mods::Frame(ctx);
  work.r3.u32 = manager;
  work.r4.u32 = heap;
  work.r5.u32 = tag;
  rex_GDBManager_LoadFile(work, base);
  const bool ok = (work.r3.u32 & 0xFFu) != 0u;
  CallLhFree(ctx, base, heap);
  if (!ok) {
    REXLOG_WARN("Refii gdb: reload of {} (tag {}): the guest's loader refused it; the databases are as they were",
                   guest_path, tag);
    return;
  }

  // The loader pushed the new file at the front; sync its records into every older copy.
  files = Files(base, manager);
  const LoadedFile* fresh = files.empty() ? nullptr : &files.front();
  if (fresh == nullptr || fresh->key != Normalise(guest_path)) {
    fresh = Find(files, Normalise(guest_path));
  }
  if (fresh == nullptr) {
    REXLOG_WARN("Refii gdb: reload of {}: loaded, but the manager's list does not show it", guest_path);
    return;
  }
  const uint32_t scratch = CallLhMalloc(ctx, base, 16u);
  const uint32_t count = LoadU32(base, fresh->file + 4u);
  const uint32_t ids = LoadU32(base, fresh->file);
  int copies = 0;
  int written = 0;
  if (Plausible(scratch) && Plausible(ids) && count <= 0x100000u) {
    for (uint32_t i = 0; i < count; ++i) {
      const uint32_t id = LoadU32(base, ids + 4u * i);
      const uint32_t record = FindEntry(ctx, base, fresh->file, id);
      if (record == 0) {
        continue;
      }
      written += SyncOthers(ctx, base, files, *fresh, id, record, scratch, copies);
    }
  }
  if (Plausible(scratch)) {
    CallLhFree(ctx, base, scratch);
  }
  REXLOG_INFO("Refii gdb: reloaded {} (tag {}): {} record(s) mapped, {} older copies updated in place ({} value(s)); {} file(s) loaded",
              guest_path, tag, count, copies, written, files.size());
}

/// `gdb_revert PATH HASH...`: each id mapped back to PATH's base file's record (a patch no
/// longer states it), the base values written into every other generation. An id the base
/// never had is left as it is and counted.
void GdbRevert(PPCContext& ctx, uint8_t* base, const std::vector<std::string>& w) {
  if (w.size() < 3) {
    return;
  }
  const uint32_t manager = Manager(base);
  if (manager == 0) {
    return;
  }
  const std::vector<LoadedFile> files = Files(base, manager);
  const std::string family = FamilyOf(Normalise(w[1]));
  const LoadedFile* root = Find(files, family);
  if (root == nullptr) {
    REXLOG_INFO("Refii gdb: revert in {}: the guest has not loaded that database", w[1]);
    return;
  }
  const uint32_t scratch = CallLhMalloc(ctx, base, 16u);
  if (!Plausible(scratch)) {
    return;
  }
  int reverted = 0;
  int kept = 0;
  int copies = 0;
  int written = 0;
  for (size_t i = 2; i < w.size(); ++i) {
    const uint32_t id = ParseHash(w[i]);
    const uint32_t record = id == 0 ? 0u : FindEntry(ctx, base, root->file, id);
    if (record == 0) {
      ++kept;
      continue;
    }
    PPCContext work = refii::mods::Frame(ctx);
    work.r3.u32 = manager + 16u;
    work.r4.u64 = (static_cast<uint64_t>(id) << 32) | record;
    rex_GDBManager_MapInsert(work, base);
    ++reverted;
    written += SyncOthers(ctx, base, files, *root, id, record, scratch, copies);
  }
  CallLhFree(ctx, base, scratch);
  REXLOG_INFO("Refii gdb: reverted {} record(s) of {} to {} ({} kept: the base never had them); {} patch copies updated ({} value(s))",
              reverted, w[1], root->path, kept, copies, written);
}

}  // namespace refii::vfs
