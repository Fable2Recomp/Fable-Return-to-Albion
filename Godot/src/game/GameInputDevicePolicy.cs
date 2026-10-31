using System;
using System.Collections.Generic;

namespace Refii.Game;

/// <summary>Changes-only input ownership identity used to retire taps captured for a previous screen or modal.</summary>
/// <param name="FrontEndFound">Whether a native front-end object exists.</param>
/// <param name="FrontEndScreen">The native screen identity; unavailable is zero.</param>
/// <param name="FrontEndState">The screen's native substate, including title/menu transition gates.</param>
/// <param name="PauseScreen">The active in-game book/pause screen, or its unavailable sentinel.</param>
/// <param name="Modal">The native modal identity.</param>
/// <param name="Mode">The native modal mode.</param>
/// <param name="Tab">The native active pause tab.</param>
/// <param name="HostFlags">Existing host-owned input surfaces; excludes highlight/frame/animation cadence.</param>
internal readonly record struct GameInputButtonRoute(bool FrontEndFound, uint FrontEndScreen,
    uint FrontEndState, uint PauseScreen, uint Modal, uint Mode, uint Tab, uint HostFlags);

/// <summary>Device metadata policy seam for isolated native Godot fixtures, with no button/analog or guest injection.</summary>
/// <remarks>Production leaves this null and reads the real platform, window and connected/known devices.
/// Synthetic Input.ParseInputEvent updates Godot polling but does not enumerate a hardware backend.</remarks>
/// <param name="CapturePhysicalButtons">The fixture's platform feature decision.</param>
/// <param name="WindowFocused">Window focus metadata; the application DevicesEnabled gate still applies.</param>
/// <param name="Connected">Fixture-enumerated identities, bounded by the capture helper.</param>
/// <param name="Known">Whether the metadata says a device has a real mapping, preserving first-press assignment rules.</param>
/// <param name="Route">Fixture-supplied changes-only screen ownership; no action is dispatched.</param>
internal sealed record GameInputDevicePolicy(bool CapturePhysicalButtons, bool WindowFocused,
    IReadOnlyList<int> Connected, Func<int, bool> Known, GameInputButtonRoute Route);
