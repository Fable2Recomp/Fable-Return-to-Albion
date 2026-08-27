// The ~120 `GUI.*` natives, captured with their arguments at the game VM's `luaD_precall`
// (recognised by implementation pointer). See docs/ui_native_cut.md. Taking a call does not
// suppress it — the guest's implementation still runs; suppression is opt-in per native.

#ifndef REFII_VFS_GUI_GUI_NATIVE_CALLS_H
#define REFII_VFS_GUI_GUI_NATIVE_CALLS_H

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

namespace refii::vfs {

/// One argument as the Lua stack held it. `lua_Number` is a 32-bit float in this build. A
/// string is a Babel tag (`TEXT_QUEST_COMPLETE`), not display text; the host resolves it.
struct GuiNativeArgument {
    enum class Kind : uint8_t {
        kNone = 0,
        kNumber = 1,
        kBoolean = 2,
        kString = 3,
        kOther = 4,
        kTable = 5,  ///< walked into `fields`
    };

    /// One named entry of a table argument. Named only, not nested; values are string,
    /// number or boolean.
    struct Field {
        std::string name;
        Kind kind = Kind::kNone;
        float number = 0.0f;
        bool boolean = false;
        std::string text;
    };

    Kind kind = Kind::kNone;
    float number = 0.0f;
    bool boolean = false;
    std::string text;
    std::vector<Field> fields;
};

/// One intercepted call.
struct GuiNativeCall {
    std::string name;
    std::vector<GuiNativeArgument> arguments;
    uint64_t sequence = 0;
};

/// Names the implementation a `GUI` registration is binding. Call for the `GUI` namespace
/// only: `sub_82458C88` runs once per binding over ~3,000 natives and a string read on each
/// hangs `Game_InitDatabases`.
void NoteGuiNativeRegistration(uint32_t implementation, std::string name);

/// The registered name for an implementation, or empty when it is not a `GUI` native. Hot:
/// runs for every native the game VM calls.
const std::string* GuiNativeNameFor(uint32_t implementation);

/// True once any `GUI` registration has been seen, so the call hook can skip until then.
bool GuiNativesRegistered();

/// What the host answers a predicate with, in place of the guest's own implementation.
enum class GuiNativeAnswerKind : uint8_t {
    kNone = 0,     ///< no answer held; the guest's implementation runs
    kBoolean = 1,  ///< `bits` is 0 or 1
    kFloat = 2,    ///< `bits` is the float's raw bits
    kSuppress = 3, ///< run nothing and return NO results — for a native that pushes no value
};

/// Holds the host's answer for one native, by name. Only answer a native whose setters the
/// host owns. An empty name or `kNone` hands it back to the guest.
void SetGuiNativeAnswer(const std::string& name, GuiNativeAnswerKind kind, uint32_t bits);

/// The host's answer for an implementation, or `kNone` when it should run as shipped.
GuiNativeAnswerKind GuiNativeAnswerFor(uint32_t implementation, uint32_t* out_bits);

/// Names the implementation whose thunk is about to run, on this thread. Thread-local
/// because two Lua VMs run.
void NoteGuiNativeEntry(uint32_t implementation);

/// The implementation `NoteGuiNativeEntry` last named on this thread, or 0.
uint32_t CurrentGuiNativeEntry();

void PostGuiNativeCall(GuiNativeCall call);

/// Takes every pending call. Empties the queue.
std::vector<GuiNativeCall> TakeGuiNativeCalls();

/// How many calls have been intercepted and how many are waiting, for the host's report.
void GuiNativeCounters(uint64_t* out_intercepted, uint32_t* out_pending, uint32_t* out_named);

}  // namespace refii::vfs

#endif  // REFII_VFS_GUI_GUI_NATIVE_CALLS_H
