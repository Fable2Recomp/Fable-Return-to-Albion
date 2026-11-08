// Fable 3's Gameface for the shared headless core (core/headless_presentation.h): the addresses
// and offsets the core calls and reads, every one read in the Fable 3 360 disc IDB (port 13339),
// each with the instruction that proves it. The hooks that hand it to the core, and the title's
// own steps (find, the guest heap, the create's engine sequence), are headless_presentation_hooks.cpp.
//
// Fable 3's elements: every element +4 reader null-checks it (F3 audit gf §5 i), so no inert quad
// record.
#pragma once

#include <cstdint>

#include "core/headless_presentation.h"
#include "../../vfs/include/fable3_guest_build.h"

namespace refii::fable3 {

/// The table without the title's services (find / alloc / free / give_engine), which the hooks
/// file sets.
inline refii::headless::GamefaceTable HeadlessTableAddresses() {
    refii::headless::GamefaceTable t;
    t.title = "Fable 3";
    // The maker K vf +28 `sub_82C558F8`: allocator `sub_82C680A0` (0x82C55954), kernel alloc
    // `sub_82C68A60`(allocator, 0x36C, "class CPresentation", file, 0x47) at 0x82C55958..0x82C55970
    // (`addi r5,r11,aClassCpresenta@l` 0x820E4C38, `addi r6,r10,aDPulseWorkF3Re_32@l` 0x820E4BC8,
    // `li r4,0x36C`, `li r7,0x47`), ctor `sub_82C68D68`(this, name, K) at 0x82C5597C..0x82C55984
    // (`*(a1+20) = a3` 0x82C68DCC); the kernel free `sub_82C68BB0`(allocator, pres, 0x36C) as K's
    // own destroy frees (`li r5,0x36C` / `bl sub_82C68BB0` 0x82C6AFF4..0x82C6AFF8).
    t.kernel_allocator = refii::guest::Address(0x82C680A0u);
    t.kernel_alloc = refii::guest::Address(0x82C68A60u);
    t.kernel_free = refii::guest::Address(0x82C68BB0u);
    t.presentation_ctor = refii::guest::Address(0x82C68D68u);
    t.presentation_class_name = refii::guest::Address(0x820E4C38u);
    t.kernel_source_name = refii::guest::Address(0x820E4BC8u);
    t.kernel_source_line = 0x47u;
    t.presentation_size = 0x36Cu;
    // CPresentation, vtable 0x82007628 (`stw r10,0(r31)` 0x82C68D90): +0 dtor `sub_82C69140`,
    // +4 Update `sub_8225D9F0`, +16 set engine disc 0x82C35488 `stw r4,0x1C(r3)`, +32 service
    // disc 0x821CA7E0, +68 nullsub_1 0x828173B8 (`blr`), +72 SetName `sub_82C69AC0`. The ctor zeroes
    // +500 (`stw r30,0x1F4(r31)` 0x82C68F20), +564 (0x82C68F74), +596/+600 (0x82C68FC8/bc),
    // +608/+612 (0x82C68FD8/cc), +628 (0x82C68FEC), +648 (0x82C69000). The contracts walk
    // `sub_82877968` takes pres vf +56 disc 0x82C35498 `addi r3,r3,0x250` (+592), its count vf +4
    // 0x82F09930 `lwz r3,8(r3)` (+600) and record i vf +12 `sub_82C6F5A0` from `lwz r11,4(r3)`
    // (+596), answering the record's first word (`lwz r3,0(r22)` 0x82C6F72C): {element, 0}.
    t.pres_scene = 24u;
    t.pres_engine = 28u;
    t.pres_gameplan = 32u;
    t.pres_service = 492u;
    t.pres_root = 500u;  // service +8: child-by-name's root `lwz r5,8(r3)` disc 0x82C3A428
    t.pres_param_records = 596u;
    t.pres_param_count = 600u;
    t.pres_param_size_a = 608u;
    t.pres_param_size_b = 612u;
    t.pres_slide_records = 628u;
    // Mgr:Animations at +732: pres vf +44 disc 0x821CAA38 `addi r3,r3,0x2DC`; the ctor stores its vtable
    // 0x820018B0 at +0x2DC (0x82C69090), the presentation at +0x2E0 (0x82C6906C) and zeroes +0x2E4..
    // +0x2F4 (0x82C69074..0x82C6908C); its pass vf +4 `sub_82273018` evaluates +8 / +12 (`lwz r4,8(r3)`
    // / `lwz r5,0xC(r3)` 0x82273024..0x8227302C -> `sub_821CA0E0`) over the keys at +16. The slide
    // records' entries (+16 count, +28 stream) are what the slide enter `sub_821CA850` walks
    // (0x821CA8D8..0x821CA980 -> `sub_821CA6D8`), and with them the file's tracks run as the loaded
    // scene's: Bar's TopBoxX track (scale.x 0.2 at the start, 1.105 at 1000) is what the TopBox and
    // subtitle calibration `sub_8227AEB0` measures through `sub_82876768` at meter 100.
    t.pres_animations = 732u;
    t.pres_slot_dtor = 0u;
    t.pres_slot_update = 4u;
    t.pres_slot_nullsub = 68u;
    t.pres_slot_set_name = 72u;
    // K's destroy releases +32 through vf +12: `lwz r3,0x20(r31)` / `lwz r10,0xC(r11)` / `bctrl`
    // 0x82C55C7C..0x82C55C94, null-checked at 0x82C55C84.
    t.gameplan_slot_release = 12u;
    // K (CGameface, vtable 0x82002A80): the clock K +8 vf +16 (`addi r3,r31,8` / `lwz r10,0x10(r11)`
    // 0x82279B38..0x82279B44), Update pres vf +4 per presentation (0x82279B70..0x82279B7C); the
    // callback K +588 / ctx +592 (`lwz r11,0x24C(r29)` / `lwz r6,0x250(r29)` 0x82C55A50..0x82C55A70:
    // (2, pres, 0, ctx) made, 0x82C55BB4 (0, ...) loaded; 0x82C55C70 (1, ...) destroyed).
    t.gameface_clock = 8u;
    t.clock_slot_ms = 16u;
    t.gameface_callback = 588u;
    t.gameface_callback_ctx = 592u;
    // CScene, vtable 0x82002A00: +16 `sub_82272A80` the refresh, called unchecked after pres vf +12
    // (`lwz r10,0x10(r11)` / `bctrl` 0x825F2DF0..0x825F2DF8); +28 disc 0x82C31A68 `lwz r3,0x6F0(r3)`.
    t.scene_vtable = refii::guest::Address(0x82002A00u);
    t.scene_slots = 16u;
    t.scene_slot_refresh = 4u;
    t.scene_slot_list = 7u;
    t.scene_render_list = 0x6F0u;
    // Names: the attribute-string read `sub_828760D0` hashes the name with `sub_824CBC50`
    // (0x82876114), keeps 27 bits (`insrwi r11,r3,27,0` / `srwi r4,r11,5` 0x8287611C..0x82876128),
    // looks it up with `sub_821C9F40` and builds its answer with `sub_8221E890`(out, char*, -1)
    // (`li r5,-1` / `bl sub_8221E890` 0x8287619C..0x828761A4); the filer frees it with
    // lhCharString_Dtor `sub_8221E2B8` (0x82BCF338). "Parameters": the contracts walk hashes the
    // CCharString `dword_8359E7EC` (`addi r28,r11,dword_8359E7EC@l` 0x828779EC, `bl sub_82236B30` =
    // `**a1` 0x82877A14, `bl sub_824CBC50` 0x82877A18) against the record's name (0x82877A20).
    t.name_hash = refii::guest::Address(0x824CBC50u);
    t.attribute_id_mask = 0x07FFFFFFu;
    t.parameters_name = refii::guest::Address(0x8359E7ECu);
    t.char_string_from_cstr = refii::guest::Address(0x8221E890u);
    t.char_string_dtor = refii::guest::Address(0x8221E2B8u);
    // The deserialiser's element section `sub_82C644A8`: start / end time `dword_8359FA58` /
    // `dword_8359FA5C` set flag 0x0002 (`v28 == dword_8359FA58` 0x82C64854, `|| v28 ==
    // dword_8359FA5C` 0x82C64888 -> svc vf +80(e, 2, 1) 0x82C648A8). Its attribute word conversion
    // (0x82C64778) and component / plain builders (`sub_82C6B318` 0x82C646A0 on file flag 4,
    // `sub_82C6B220`(…, flags & 1, …) 0x82C646DC) are Fable 2's, so the core's file reader holds.
    t.attr_start_time = refii::guest::Address(0x8359FA58u);
    t.attr_end_time = refii::guest::Address(0x8359FA5Cu);
    t.quad_record_ctor = 0u;
    t.quad_record_size = 0u;
    return t;
}

}  // namespace refii::fable3
