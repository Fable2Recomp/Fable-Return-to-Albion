// refii - Fable 3's file system, redirected where the host states an override: the file the
// host mount serves (a mod's, or one the host composes, such as a patched database) replaces the
// bank entry the guest would have opened. The decision is Fable 3's VFS library's
// (Fable3/src/vfs, fable3_vfs.h); these hooks are the guest's side.
//
// Every address is the Fable 3 TU3 IDB ("disc" marks one TU3 lacks); each offset names its base.
//
// THE ONE SEAM. Every resource the guest opens goes through the ROOT file system dword_8334B604,
// a CCompositeFileSystem (72 bytes, sub_82DBA680 from sub_83254BC8 at 0x83254BDC; its ctor
// sub_82DBA6F8 stores vtable 0x820FDC04 at 0x82DBA724). Slot 4 is Open(out, fs, name, mode) =
// sub_82DBA828, slot 11 Exists(fs, name) = sub_82DBAB48.
//   by NAME  the file stream sub_8316A3C0 strips `game:\`, `cache:\` and the data root, maps the
//            stream mode (1 -> 1, or 4 under flag 8; 2 and 3 -> 5; 0x8316A530..0x8316A56C) and
//            calls the root's slot 4 at 0x8316A5F4 — game init sub_822D6BD0 installs
//            dword_8334B604 as that root at 0x822D71A8. The databases (sub_822D7FA0 ->
//            sub_82D334E0 -> sub_8316AAD0), the scripts and the text come this way.
//   by HASH  sub_83165AF0 passes an EMPTY name with the hash at name+0x1C to the root's slot 4
//            (0x83165B4C): the animation loader sub_82D6D260 (0x82D6D83C), the model and texture
//            streamer sub_82CD17F8, the .gmd reader sub_821CEE98.
//   BANKS    a vfsconfig Bank node opens its bank on its parent's slot 4 (sub_831656D0,
//            0x83165730), and a separate-data bank its `.dat` by the bank's hash continued over
//            ".dat" (sub_825B3A58 at 0x83164F08).
// THE NAME OBJECT: a wide string (+0 the inline buffer, or a pointer once the capacity at +0x14 is
// 8 or more; +0x10 the length) and at +0x1C its hash, cached by sub_822D5CA0: FNV-1 over each
// unit's low byte (sub_822D5DE8; a 40-character final component parses its last eight as the
// hash). The table is keyed by the bank names hashed as stored — lower case, backslashes,
// relative to `data\` (every record of the disc's gamescripts_r, guiscripts and levels banks) —
// the spelling the host's hash index keys its mount by.
// THE OPEN: modes 0, 2 and 5 go to the default disk file system sub_82DB8B08 (disc 0x82D881F8..
// disc 0x82D88208), as does a name holding a ':' (wcscspn against L":" at unk_820DB150, 0x82DBA888).
// Otherwise h = sub_822D5CA0(name) (disc 0x82D8829C), a binary search of the sorted
// {hash, provider << 20 | entry} table at fs+52..fs+56, and provider->vt[3](out, provider, entry,
// mode) (0x82DBA998); a later mount at equal or higher priority takes a hash over
// (sub_82DBB210, 0x82DBB3B8). The hook asks the host first, under the same two tests.
//
// THE OVERRIDE IS A GUEST CMemoryFile, the class Mode="memory" banks hand out: sub_82DBFE90(out,
// size, id) allocates the 80-byte file and its own buffer of `size` bytes (sub_82DBFB68: buffer at
// +12 and +20, size +28/+32, pending load +36 = 0, id +72) and returns it referenced. Its reads
// copy from +12 (slot 3 sub_82DBFF88 at 0x82DC00A4; slot 4 sub_82DC00C8), its size is +32
// (slot 7), its id +72 (slot 10) — for a bank entry the record's hash (sub_83163438 passes it at
// 0x8316354C), so the override carries the requested hash. Its destructor frees the buffer
// (sub_82DBFC58, 0x82DBFD18). A composite of a header and a data bank opens as the two parts
// concatenated (sub_82DC2520), which is the whole file the host's mount serves for a .mdl or .tex.
//
// WHAT STAYS THE GUEST'S: a name the guest itself resolves to a DISK file — a CWin32File, vtable
// 0x820FE1E4, stored by the disk file system's Open sub_82DB90D0 at 0x82DB9474 over CreateFileA
// sub_83131BF0 -> NtCreateFile. The host's query does not tell a mod's file from the game's own
// loose files (a loose file is an override to it), and the loose files are what the game streams
// from disk — its videos run to 520 MB, which no memory file may hold. Replacing a loose file
// (language\<culture>\text\book.babel) waits on the host stating the owning mod.
//
// EXISTS is the same table search (disc 0x82D88598): a file only the host adds has no row there, so a
// "no" is asked of the host too.

#include <cstdint>
#include <cstring>

#include <rex/ppc.h>

#include "../../vfs/include/fable3_vfs.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_VfsComposite_Open);
REX_EXTERN(__imp__rex_VfsComposite_Exists);
REX_EXTERN(rex_VfsName_Hash);
REX_EXTERN(rex_VfsMemoryFile_Create);
REX_EXTERN(rex_VfsRef_Reset);

namespace {

/// CWin32File, the disk file system's file (sub_82DB90D0, 0x82DB9474).
const refii::guest::Addr kDiskFileVtable{0x820FE1E4u};

/// The name object's fields (sub_822D5CA0, sub_82DBA828 at disc 0x82D8820C).
constexpr uint32_t kNameLength = 0x10u;
constexpr uint32_t kNameCapacity = 0x14u;
constexpr uint32_t kInlineCapacity = 8u;
/// A bound on the walk, as the guest's own hash bounds its (`li r4, 0x800` at 0x822D5E00).
constexpr uint32_t kMaxNameUnits = 0x800u;

/// CMemoryFile's buffer (sub_82DBFB68 at 0x82DBFC18).
constexpr uint32_t kMemoryFileBuffer = 12u;

uint32_t LoadGuestU32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

uint16_t LoadGuestU16(const uint8_t* base, uint32_t address) {
  uint16_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap16(value);
}

void StoreGuestU32(uint8_t* base, uint32_t address, uint32_t value) {
  const uint32_t swapped = __builtin_bswap32(value);
  std::memcpy(base + address, &swapped, sizeof(swapped));
}

/// Whether the composite resolves `name` through its own table rather than handing it to the
/// disk file system: no ':' in it (0x82DBA888). A name with no characters is a hash-only name.
bool NameReadsThroughTable(const uint8_t* base, uint32_t name) {
  if (name == 0u) {
    return false;
  }

  const uint32_t length = LoadGuestU32(base, name + kNameLength);
  const uint32_t chars = LoadGuestU32(base, name + kNameCapacity) < kInlineCapacity
                             ? name
                             : LoadGuestU32(base, name);
  if (chars == 0u) {
    return true;
  }

  const uint32_t units = length < kMaxNameUnits ? length : kMaxNameUnits;
  for (uint32_t i = 0; i < units; ++i) {
    const uint16_t unit = LoadGuestU16(base, chars + i * 2u);
    if (unit == 0u) {
      break;
    }
    if (unit == u':') {
      return false;
    }
  }
  return true;
}

/// The modes the composite serves itself (disc 0x82D881F8..disc 0x82D88208).
bool ModeReadsThroughTable(uint32_t mode) { return mode != 0u && mode != 2u && mode != 5u; }

/// The name's hash, from the guest's own sub_822D5CA0 — which caches it at +0x1C, as the
/// composite's own call at disc 0x82D8829C would.
uint32_t NameHash(const PPCContext& from, uint8_t* base, uint32_t name) {
  PPCContext call = from;
  call.r3.u32 = name;
  rex_VfsName_Hash(call, base);
  return call.r3.u32;
}

/// Drops the reference `slot` holds and clears it, through the guest's own reset
/// sub_822EE640(T** slot): `(*slot)->vt[2](*slot)` (0x822EE664..0x822EE66C), then `*slot = 0`
/// (0x822EE674) — the call CMemoryFile itself makes on its pending load (0x82DC0008).
void ResetGuestSlot(const PPCContext& from, uint8_t* base, uint32_t slot) {
  PPCContext call = from;
  call.r3.u32 = slot;
  rex_VfsRef_Reset(call, base);
}

/// A referenced CMemoryFile holding the host's bytes for `path`, written to `out` in place of
/// the reference `out` held, which is dropped; 0, with `out` left as it was, when the guest could
/// not allocate the file or the host could not fill it. `out` is the one guest slot the hook
/// owns, so each reference passes through it to be dropped.
uint32_t CreateOverrideFile(const PPCContext& from, uint8_t* base, uint32_t out, uint32_t hash,
                            const char* path, uint32_t size) {
  const uint32_t previous = LoadGuestU32(base, out);

  PPCContext call = from;
  call.r3.u32 = out;
  call.r4.u32 = size;
  call.r5.u32 = hash;
  rex_VfsMemoryFile_Create(call, base);

  const uint32_t file = LoadGuestU32(base, out);
  const uint32_t buffer = file != 0u ? LoadGuestU32(base, file + kMemoryFileBuffer) : 0u;
  if (buffer != 0u && refiii_vfs_read_override(path, base + buffer, size) != 0) {
    StoreGuestU32(base, out, previous);
    ResetGuestSlot(from, base, out);
    StoreGuestU32(base, out, file);
    return file;
  }

  ResetGuestSlot(from, base, out);
  StoreGuestU32(base, out, previous);
  return 0u;
}

}  // namespace

REX_EXTERN(rex_VfsComposite_Open) {
  const PPCContext entry = ctx;
  const uint32_t out = ctx.r3.u32;
  const uint32_t name = ctx.r5.u32;

  uint32_t hash = 0;
  if (out != 0u && ModeReadsThroughTable(ctx.r6.u32) && NameReadsThroughTable(base, name)) {
    hash = NameHash(entry, base, name);
  }

  char path[REFIII_VFS_PATH_CAPACITY];
  uint32_t size = 0;
  if (hash == 0u || refiii_vfs_find_override(hash, &size, path, sizeof(path)) == 0) {
    __imp__rex_VfsComposite_Open(ctx, base);
    return;
  }

  // The guest's own resolution first: a disk file stands (the file's header says why).
  __imp__rex_VfsComposite_Open(ctx, base);
  const uint32_t own = LoadGuestU32(base, out);
  if (own != 0u && LoadGuestU32(base, own) == kDiskFileVtable) {
    refiii_vfs_note_override(hash, path, size, 0);
    return;
  }

  if (CreateOverrideFile(entry, base, out, hash, path, size) != 0u) {
    refiii_vfs_note_override(hash, path, size, 1);
  }
  ctx.r3.u32 = out;
}

REX_EXTERN(rex_VfsComposite_Exists) {
  const PPCContext entry = ctx;
  const uint32_t name = ctx.r4.u32;

  __imp__rex_VfsComposite_Exists(ctx, base);
  if (ctx.r3.u32 != 0u || !NameReadsThroughTable(base, name)) {
    return;
  }

  const uint32_t hash = NameHash(entry, base, name);
  char path[REFIII_VFS_PATH_CAPACITY];
  uint32_t size = 0;
  if (hash != 0u && refiii_vfs_find_override(hash, &size, path, sizeof(path)) != 0) {
    refiii_vfs_note_added(hash, path);
    ctx.r3.u32 = 1u;
  }
}
