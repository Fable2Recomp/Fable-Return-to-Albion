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

namespace refii::fable3 {

/// The table without the title's services (find / alloc / free / give_engine), which the hooks
/// file sets.
inline refii::headless::GamefaceTable HeadlessTableAddresses() {
    refii::headless::GamefaceTable t;
    t.title = "Fable 3";
    // The maker K vf +28 `sub_82C211F8`: allocator `sub_82C33990` (0x82c21254), kernel alloc
    // `sub_82C34350`(allocator, 0x36C, "class CPresentation", file, 0x47) at 0x82c21258..0x82c21270
    // (`addi r5,r11,aClassCpresenta@l` 0x820E22AC, `addi r6,r10,aDPulseWorkF3Re_32@l` 0x820E2240,
    // `li r4,0x36C`, `li r7,0x47`), ctor `sub_82C34658`(this, name, K) at 0x82c2127c..0x82c21284
    // (`*(a1+20) = a3` 0x82c346bc); the kernel free `sub_82C344A0`(allocator, pres, 0x36C) as K's
    // own destroy frees (`li r5,0x36C` / `bl sub_82C344A0` 0x82c368b4..0x82c368b8).
    t.kernel_allocator = 0x82C33990u;
    t.kernel_alloc = 0x82C34350u;
    t.kernel_free = 0x82C344A0u;
    t.presentation_ctor = 0x82C34658u;
    t.presentation_class_name = 0x820E22ACu;
    t.kernel_source_name = 0x820E2240u;
    t.kernel_source_line = 0x47u;
    t.presentation_size = 0x36Cu;
    // CPresentation, vtable 0x82007700 (`stw r10,0(r31)` 0x82c34680): +0 dtor `sub_82C34A30`,
    // +4 Update `sub_8225E868`, +16 set engine 0x82C35488 `stw r4,0x1C(r3)`, +32 service
    // 0x821CA7E0, +68 nullsub_1 0x82C1BA40 (`blr`), +72 SetName `sub_82C353B0`. The ctor zeroes
    // +500 (`stw r30,0x1F4(r31)` 0x82c34810), +564 (0x82c34864), +596/+600 (0x82c348b8/bc),
    // +608/+612 (0x82c348c8/cc), +628 (0x82c348dc), +648 (0x82c348f0). The contracts walk
    // `sub_82819C60` takes pres vf +56 0x82C35498 `addi r3,r3,0x250` (+592), its count vf +4
    // 0x8310E910 `lwz r3,8(r3)` (+600) and record i vf +12 `sub_82C3AE20` from `lwz r11,4(r3)`
    // (+596), answering the record's first word (`lwz r3,0(r22)` 0x82c3afac): {element, 0}.
    t.pres_scene = 24u;
    t.pres_engine = 28u;
    t.pres_gameplan = 32u;
    t.pres_service = 492u;
    t.pres_root = 500u;  // service +8: child-by-name's root `lwz r5,8(r3)` 0x82c3a428
    t.pres_param_records = 596u;
    t.pres_param_count = 600u;
    t.pres_param_size_a = 608u;
    t.pres_param_size_b = 612u;
    t.pres_slide_records = 628u;
    // Mgr:Animations at +732: pres vf +44 0x821CAA38 `addi r3,r3,0x2DC`; the ctor stores its vtable
    // 0x82001914 at +0x2DC (0x82c34980), the presentation at +0x2E0 (0x82c3495c) and zeroes +0x2E4..
    // +0x2F4 (0x82c34964..0x82c3497c); its pass vf +4 `sub_82274B08` evaluates +8 / +12 (`lwz r4,8(r3)`
    // / `lwz r5,0xC(r3)` 0x82274b14..0x82274b1c -> `sub_821CA070`) over the keys at +16. The slide
    // records' entries (+16 count, +28 stream) are what the slide enter `sub_821CA7E8` walks
    // (0x821ca870..0x821ca918 -> `sub_821CA668`), and with them the file's tracks run as the loaded
    // scene's: Bar's TopBoxX track (scale.x 0.2 at the start, 1.105 at 1000) is what the TopBox and
    // subtitle calibration `sub_8227CC70` measures through `sub_82818A60` at meter 100.
    t.pres_animations = 732u;
    t.pres_slot_dtor = 0u;
    t.pres_slot_update = 4u;
    t.pres_slot_nullsub = 68u;
    t.pres_slot_set_name = 72u;
    // K's destroy releases +32 through vf +12: `lwz r3,0x20(r31)` / `lwz r10,0xC(r11)` / `bctrl`
    // 0x82c2157c..0x82c21594, null-checked at 0x82c21584.
    t.gameplan_slot_release = 12u;
    // K (CGameface, vtable 0x82002B00): the clock K +8 vf +16 (`addi r3,r31,8` / `lwz r10,0x10(r11)`
    // 0x8227b830..0x8227b83c), Update pres vf +4 per presentation (0x8227b868..0x8227b874); the
    // callback K +588 / ctx +592 (`lwz r11,0x24C(r29)` / `lwz r6,0x250(r29)` 0x82c21350..0x82c21370:
    // (2, pres, 0, ctx) made, 0x82c214b4 (0, ...) loaded; 0x82c21570 (1, ...) destroyed).
    t.gameface_clock = 8u;
    t.clock_slot_ms = 16u;
    t.gameface_callback = 588u;
    t.gameface_callback_ctx = 592u;
    // CScene, vtable 0x82002A80: +16 `sub_822744E0` the refresh, called unchecked after pres vf +12
    // (`lwz r10,0x10(r11)` / `bctrl` 0x825c80a8..0x825c80b0); +28 0x82C31A68 `lwz r3,0x6F0(r3)`.
    t.scene_vtable = 0x82002A80u;
    t.scene_slots = 16u;
    t.scene_slot_refresh = 4u;
    t.scene_slot_list = 7u;
    t.scene_render_list = 0x6F0u;
    // Names: the attribute-string read `sub_828183C8` hashes the name with `sub_825C01B8`
    // (0x8281840c), keeps 27 bits (`insrwi r11,r3,27,0` / `srwi r4,r11,5` 0x82818414..0x82818420),
    // looks it up with `sub_821C9ED0` and builds its answer with `sub_8221F788`(out, char*, -1)
    // (`li r5,-1` / `bl sub_8221F788` 0x82818494..0x8281849c); the filer frees it with
    // lhCharString_Dtor `sub_8221F208` (0x82b9a1f0). "Parameters": the contracts walk hashes the
    // CCharString `dword_8355DB84` (`addi r28,r11,dword_8355DB84@l` 0x82819ce4, `bl sub_822DE350` =
    // `**a1` 0x82819d0c, `bl sub_825C01B8` 0x82819d10) against the record's name (0x82819d18).
    t.name_hash = 0x825C01B8u;
    t.attribute_id_mask = 0x07FFFFFFu;
    t.parameters_name = 0x8355DB84u;
    t.char_string_from_cstr = 0x8221F788u;
    t.char_string_dtor = 0x8221F208u;
    // The deserialiser's element section `sub_82C2FC58`: start / end time `dword_8355EE40` /
    // `dword_8355EE44` set flag 0x0002 (`v28 == dword_8355EE40` 0x82c30004, `|| v28 ==
    // dword_8355EE44` 0x82c30038 -> svc vf +80(e, 2, 1) 0x82c30058). Its attribute word conversion
    // (0x82c2ff28) and component / plain builders (`sub_82C36BD8` 0x82c2fe50 on file flag 4,
    // `sub_82C36AE0`(…, flags & 1, …) 0x82c2fe8c) are Fable 2's, so the core's file reader holds.
    t.attr_start_time = 0x8355EE40u;
    t.attr_end_time = 0x8355EE44u;
    t.quad_record_ctor = 0u;
    t.quad_record_size = 0u;
    return t;
}

}  // namespace refii::fable3
