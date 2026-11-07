// THE HEADLESS GAMEFACE CORE: a presentation the guest keeps without its .bgf scene, answered
// from the file. PAYLOAD side — implemented in src/payload/src/core/headless_presentation.cpp and
// compiled into each title's module beside its hooks; a title's hooks are thin shims over it.
//
// Both titles run Anark Gameface 4.0.5 and lay its objects out alike (CPresentation 876 bytes, the
// element service at +492 with the root at +500, the Parameters records at +596 / +600, the scene
// at +24, Mgr:Slides at +620 with its slide records at +628; an element's name hash +0, render node
// +4, parent +12, sibling +16, first child +20, flags +28, attribute count +31, attributes at +68
// when it owns a timeline (flag 0x0001) else +32, slide base +40, slide count +42, current +43).
// What differs is WHERE: every function and global the core calls or reads is the title's, in a
// GamefaceTable its hooks hand in (Fable 2: Fable2/src/vfs/src/hooks/gui_headless_presentation.cpp).
//
// THE FILE ANSWERS WHAT THE TREE WOULD. A presentation's .bgf element table is read through
// Godot's asset reader at the create, and each element is made as the guest's own deserialiser
// would have left it (TU1 `sub_82BC8038`, disc `sub_82BC1968`): its flags (0x2000 the file's
// active bit, 0x0001 a component's own timeline, 0x0002 a start or end time), its NUMERIC
// attribute records in the deserialiser's own word form, and a component's slide base and count
// with the presentation's slide records (name, start, end; no actions — Godot plays the slides).
// So every reader of the guest's own attribute store (the lookup TU1 `sub_822848E0`, `sub_82284948`
// and everything through them: GetPosition, the show/hide helper's opacity, the compounded
// opacity), of the active bit and of the slide state answers the file's authored value until the
// guest writes, and the guest's own writers (the store, the flags writer, GotoSlide) land in that
// memory — the last write the guest told is what it reads back. String attributes are answered at
// the attribute-string read (AnswerAttributeString), never stored.
//
// THE FILE'S SLIDES AND TRACKS (a title that names Mgr:Animations, GamefaceTable::pres_animations).
// The deserialiser also fills Mgr:Animations with the file's animation tracks (16-byte records
// {element, attribute id, first key, count | active << 31 | dynamic << 30} over 24-byte keys {time,
// value, two Bezier controls}) and gives each slide record its entries (+16 the play flags and
// entry count, +28 a stream of {element, commands << 1, track refs << 1 | active} headers, the
// commands' attribute words and the refs). The core lays both out as the file has them, every
// element they name made at the create, so the guest's own GoTo (its slide enter processes the
// entries: the active bit, the attribute commands, each track switched on or off) and its own
// per-frame track pass (Mgr:Animations vf +4: every active track on a visible element evaluated
// at the element's time and stored) leave each attribute where the loaded scene would — a meter's
// SetTime moves the scale its track drives, a slide's commands place its elements. Logic refs
// (behaviours) and string commands are not laid: no behaviour runs, and a headless element holds
// no string attribute for a command to write.
//
// An element the file does not carry (no table, or a name the file has not got there) owns its
// timeline and has no authored attribute; RememberWrite gives it the attribute a setter is about
// to write, so its reads echo the guest's last write all the same.
//
// The statements each element is told reach the host through the shared queues
// (core/told_statements.h), keyed (presentation, serial, kHeadlessOrdinal | id).
#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

#include <rex/ppc.h>

namespace refii::headless {

/// A title's Gameface as the core reaches it. Addresses are the RUNNING build's (the title
/// converts its own); offsets are bytes; a 0 address is a step the title has not got.
struct GamefaceTable {
    const char* title = "";

    // The title's own services.
    PPCFunc* (*find)(uint32_t address) = nullptr;                    // a recompiled entry, or null
    uint32_t (*alloc)(PPCContext& ctx, uint8_t* base, uint32_t size) = nullptr;  // the guest heap
    void (*free)(PPCContext& ctx, uint8_t* base, uint32_t address) = nullptr;
    /// The create's engine steps after the ctor and the name (the presentation's Lua engine, its
    /// data ports and its gameplan word): they differ per title.
    void (*give_engine)(PPCContext& ctx, uint8_t* base, uint32_t gameface, uint32_t presentation) = nullptr;

    // The maker's own allocation and ctor.
    uint32_t kernel_allocator = 0;  // () -> allocator
    uint32_t kernel_alloc = 0;      // (allocator, size, class, file, line)
    uint32_t kernel_free = 0;       // (allocator, pointer, size)
    uint32_t presentation_ctor = 0; // (this, name, gameface)
    uint32_t presentation_class_name = 0;
    uint32_t kernel_source_name = 0;
    uint32_t kernel_source_line = 0;
    uint32_t presentation_size = 0;

    // CPresentation fields and vtable slots (byte offsets).
    uint32_t pres_scene = 0;
    uint32_t pres_engine = 0;
    uint32_t pres_gameplan = 0;
    uint32_t pres_service = 0;
    uint32_t pres_root = 0;
    uint32_t pres_param_records = 0;
    uint32_t pres_param_count = 0;
    uint32_t pres_param_size_a = 0;
    uint32_t pres_param_size_b = 0;
    uint32_t pres_slide_records = 0;  // Mgr:Slides +8: the 32-byte slide records
    /// Mgr:Animations (its +8 records, +12 count, +16 keys, +20 key count): where the file's
    /// animation tracks go, and with them each slide's entries (record +16 flags and count, +28 the
    /// entry stream) — THE FILE'S SLIDES AND TRACKS below. 0: the title's slides carry no entries
    /// and no track runs (the records are names, start and end only).
    uint32_t pres_animations = 0;
    uint32_t pres_slot_dtor = 0;      // (this, flags)
    uint32_t pres_slot_update = 0;    // (this, clock)
    uint32_t pres_slot_nullsub = 0;   // a `blr`, the stand-in scene's refresh
    uint32_t pres_slot_set_name = 0;  // (this, char*)
    uint32_t gameplan_slot_release = 0;

    // CGameface fields: the clock object and its ms slot, the presentation callback.
    uint32_t gameface_clock = 0;
    uint32_t clock_slot_ms = 0;
    uint32_t gameface_callback = 0;     // (code, pres, 0, ctx): 2 made, 0 loaded, 1 destroyed
    uint32_t gameface_callback_ctx = 0;

    // The stand-in scene: a copy of CScene's vtable with the refresh a `blr`, and an empty list.
    uint32_t scene_vtable = 0;
    uint32_t scene_slots = 0;
    uint32_t scene_slot_refresh = 0;
    uint32_t scene_slot_list = 0;
    uint32_t scene_render_list = 0;

    // Names: the hash an element or attribute name takes (`(char*) -> hash`), the 27-bit id mask,
    // the "Parameters" CCharString global, the CCharString ctor (this, char*, length or -1) / dtor.
    uint32_t name_hash = 0;
    uint32_t attribute_id_mask = 0;
    uint32_t parameters_name = 0;
    uint32_t char_string_from_cstr = 0;
    uint32_t char_string_dtor = 0;

    // The globals holding the start / end time attribute ids (an element with either carries
    // flag 0x0002, as the deserialiser sets it).
    uint32_t attr_start_time = 0;
    uint32_t attr_end_time = 0;

    // The inert quad record the title's quad readers' joins answer for a headless element: the
    // guest's own record ctor and size; 0 when the title's readers need none.
    uint32_t quad_record_ctor = 0;
    uint32_t quad_record_size = 0;
};

/// CGameface's maker: a headless presentation for the path at `path`, its two notifications (2
/// made, 0 loaded) included; 0 when there was no room.
uint32_t Create(const GamefaceTable& table, PPCContext& ctx, uint8_t* base, uint32_t gameface, uint32_t path);

/// CGameface's destroy of `presentation`: true when it was headless and is gone; false when it is
/// not one of the core's (the caller runs the original).
bool Destroy(const GamefaceTable& table, PPCContext& ctx, uint8_t* base, uint32_t gameface, uint32_t presentation);

/// After child-by-name (service, hash, parent or 0 the root level) answered 0: the element a
/// headless presentation makes for that name, or 0.
uint32_t MakeNamed(const GamefaceTable& table, PPCContext& ctx, uint8_t* base, uint32_t service,
                   uint32_t hash, uint32_t parent);

/// Before the child walk (service, parent): a headless parent's file children are made, in the
/// file's order.
void ExpandBeforeWalk(const GamefaceTable& table, PPCContext& ctx, uint8_t* base, uint32_t service,
                      uint32_t parent);

/// After the attribute-string read (out CCharString, element, presentation, the name's
/// CCharString) answered "": the file's string attribute constructed into `out`; true when it
/// answered.
bool AnswerAttributeString(const GamefaceTable& table, PPCContext& ctx, uint8_t* base, uint32_t out,
                           uint32_t element, uint32_t presentation, uint32_t name);

/// After CGameface's per-frame pass: Update(pres, clock) on each of this gameface's headless
/// presentations, as the pass's own loop. Returns how many it updated; `clock_out` the clock.
size_t UpdateAll(const GamefaceTable& table, PPCContext& ctx, uint8_t* base, uint32_t gameface,
                 int32_t* clock_out);

/// After GetRootElement returned `root`: a headless root leaves with its timeline time (clock
/// minus the presentation's creation clock) at +48.
void NoteRootRead(const GamefaceTable& table, PPCContext& ctx, uint8_t* base, uint32_t root);

/// A headless element's key, or false.
bool ElementKey(uint32_t element, uint32_t* presentation, uint32_t* serial, uint32_t* id);

/// Whether `presentation` is a live headless one.
bool IsHeadless(uint32_t presentation);

/// A headless element's stored value of attribute `id` (the record's value word), or false: not
/// headless, or the element carries no such attribute.
bool AttributeBits(const uint8_t* base, uint32_t element, uint32_t id, uint32_t* bits);

/// THE STORED VALUES' CHANGES: every watched element whose stored value of attribute `id` changed
/// since the last call, with the value now — read after the guest's own pass (Mgr:Animations vf +4).
/// Watched: every element a file track drives `id` on (on or off — a slide's command overwrites
/// what its track left), and every element stated through WatchStoredValue.
void ChangedStoredValues(const uint8_t* base, uint32_t id, std::vector<std::pair<uint32_t, uint32_t>>* out);

/// Adds a headless element to the stored-value watch at the value just reported for it: a writer
/// hook stated it, and a later slide command or track over it is reported by ChangedStoredValues.
void WatchStoredValue(uint32_t element, uint32_t bits);

/// A headless element's ordinal in its presentation's file (the .bgf element table's index, the
/// root 0), or -1: not headless, or made for a name the file has not got there.
int32_t FileOrdinal(uint32_t element);

/// The inert quad record (0 before the first create, or when the title has none).
uint32_t InertQuadRecord();

/// What RememberWrite found.
enum class Memory {
    kNotHeadless,  ///< not a headless element: the guest's own store decides
    kFileAnswers,  ///< a file element: it holds what the file authored, as a loaded tree would
    kRemembered,   ///< a fileless element: it now holds every attribute named, the write lands
    kNoRoom,       ///< a fileless element with no room left: the caller states the write itself
};

/// At an existence-checked setter's entry (it writes through the store only when the element
/// carries the attribute): makes a FILELESS headless element carry each attribute `ids` names
/// (float records holding `values`), so the setter's own write lands and its readers echo it.
/// A file element is left as the file made it.
Memory RememberWrite(uint8_t* base, uint32_t element, const uint32_t* ids, const float* values, size_t count);

// The statements told to a headless element, through the shared queues; each is false (nothing
// posted) when `element` is not one.
bool TellAttribute(uint32_t element, uint32_t attribute, uint32_t value_bits);
bool TellActive(uint32_t element, bool on);
bool TellTexture(uint32_t element, uint32_t hash);
bool TellText(uint32_t element, const std::vector<uint16_t>& units);

}  // namespace refii::headless
