// refii — the app context of the guest Godot hosts and steps.
#pragma once

#include <rex/ui/windowed_app_context.h>

namespace refii::hostcore {

/// The UI-thread work queue of the stepped guest, and nothing else.
///
/// The SDK's context is its platform's UI loop: a window system's event queue that other threads
/// wake so their queued functions run on the UI thread. The hosted guest has no such loop. It
/// opens no window and presents nothing (REX_EMBED_HEADLESS), its input is the host's
/// (HostInputDriver) and its sound goes to Godot (HostAudioSystem); what is left of the context
/// is the queue itself, which the base class holds, and `RefiiHostStep` drains it once per host
/// frame on the thread that made this object.
///
/// So neither platform hook has anything to do. There is no loop to wake — the next step is
/// already coming — and none to stop: the host stops stepping when `HasQuitFromUIThread()`.
///
/// NOT the SDK's SDL context: that starts SDL's video subsystem for its event queue, so the
/// guest's start depended on SDL finding a display of its own, and a build whose SDL was
/// compiled without X11 or Wayland stopped at "No available video device".
class HostedAppContext final : public rex::ui::WindowedAppContext {
public:
    HostedAppContext() = default;

    ~HostedAppContext() override {
        // Leftover queued functions run before the queue goes, as the SDK's contexts do.
        ExecutePendingFunctionsFromUIThread();
    }

protected:
    void NotifyUILoopOfPendingFunctions() override {}
    void PlatformQuitFromUIThread() override {}
};

}  // namespace refii::hostcore
