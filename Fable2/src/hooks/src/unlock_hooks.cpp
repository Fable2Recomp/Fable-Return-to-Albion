// Native overrides that make the Collector's Edition and website-unlock
// content permanently available.
//
// Both unlocks are script-visible predicates, so the contract is stated by the
// title rather than inferred. Addresses are from the title-update image, which
// is what refii recompiles (`assets/game/default.xex`); the Xenia-era be32
// patches for these unlocks were written against the pre-TU executable, where
// both functions live somewhere else entirely.
//
//   IsCollectorsEdition -> sub_824E6370.
//       Registered by name at 0x824E77B0 in the script-binding pass
//       sub_824E63C8 ("IsCollectorsEdition" is at 0x820BE354). The whole body
//       is two instructions:
//           824E6370  lbz  r3, 0x3E1(r3)
//           824E6374  blr
//       That byte is written by exactly one thing, the debug native
//       DEBUG_SetAsCollectorsEdition (sub_824E6378, `stb r4, 0x3E1(r3); blr`),
//       and read by nothing else in the image -- a scan for `lbz rX, 0x3E1(rY)`
//       has this stub as its only hit. Answering 1 is what unlocks Hal's items
//       in the Guild chest and enables the Hall of the Dead.
//
//   PlayerWebsiteUnlocks::IsItemUnlocked -> sub_8253A630.
//       Registered by name at 0x826528E4 in sub_82652718, the pass that also
//       registers the component itself (id 0xF0) as "CECPlayerWebsiteUnlocks".
//       The real body hashes the item name with FNV-1, resolves it to a GDB
//       entry through g_GDBManager, then linearly searches the component's
//       vector of unlocked ids at +0x10..+0x14 and returns whether it found
//       one. Answering 1 for every name is what opens the Guild chest holding
//       the items that were obtainable from the Fable 2 website.
//
// Both stubs are referenced only from their own registration site, so neither
// override reaches anything other than the predicate it replaces.

#include <rex/logging.h>
#include <rex/ppc/context.h>

// bool IsCollectorsEdition()
REX_EXTERN(rex_CECStats_IsCollectorsEdition) {
  (void)base;
  static const bool once = [] {
    REXLOG_INFO("unlock: IsCollectorsEdition forced true (sub_824E6370)");
    return true;
  }();
  (void)once;
  ctx.r3.u64 = 1;
}

// bool PlayerWebsiteUnlocks::IsItemUnlocked(const String& itemName)
REX_EXTERN(rex_CECPlayerWebsiteUnlocks_IsItemUnlocked) {
  (void)base;
  static const bool once = [] {
    REXLOG_INFO(
        "unlock: PlayerWebsiteUnlocks::IsItemUnlocked forced true "
        "(sub_8253A630)");
    return true;
  }();
  (void)once;
  ctx.r3.u64 = 1;
}
