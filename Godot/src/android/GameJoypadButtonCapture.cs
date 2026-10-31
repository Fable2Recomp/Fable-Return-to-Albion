using System;
using System.Collections.Generic;

namespace Refii.Game;

/// <summary>Bounded Android physical joypad edge capture before frame polling; native Godot is not required.</summary>
/// <remarks>Capture runs before GUI consumption; sampling still feeds the existing UI and ownership masks.
/// No analog values, masks, animation gates, native calls or debug inputs are changed by this helper.</remarks>
internal sealed class GameJoypadButtonCapture
{
    /// <summary>The existing fourteen XInput button bits; unknown Godot buttons are ignored.</summary>
    public const uint SupportedButtons = 0xF3FF;
    /// <summary>At most four outstanding presses per button, preventing an unbounded stale-menu backlog.</summary>
    public const int MaxPendingPresses = 4;
    /// <summary>The hardware enumeration bound; no event can create an unlimited device map.</summary>
    public const int MaxDevices = 16;

    /// <summary>One connected identity's bounded button credits and sampled/event frontiers.</summary>
    private sealed class Device
    {
        internal uint EventDown, Reported, Blocked;
        internal readonly int[] Pending = new int[16];
    }
    private readonly Dictionary<int, Device> _devices = new();
    private bool _focused = true;
    private GameInputButtonRoute _route;

    /// <summary>Number of over-capacity presses discarded; overflow also retires that button's old backlog.</summary>
    public long Overflows { get; private set; }
    /// <summary>The currently retained device count.</summary>
    public int DeviceCount => _devices.Count;
    /// <summary>Changes-only lifetime generation for retiring already-published taps from old devices, focus or routes.</summary>
    public long ContextGeneration { get; private set; }

    /// <summary>Refreshes actual connected IDs, discarding all identity state for disconnected controllers.</summary>
    /// <param name="connected">The current Godot hardware enumeration, never event-created identities.</param>
    public void RefreshDevices(IEnumerable<int> connected)
    {
        _refreshIds.Clear();
        foreach (int id in connected) AddConnected(id);
        ApplyConnected();
    }

    /// <summary>The same refresh over a list, without an enumerator box: Android calls it every frame.</summary>
    /// <param name="connected">The current hardware enumeration.</param>
    public void RefreshDevices(List<int> connected)
    {
        _refreshIds.Clear();
        for (int i = 0; i < connected.Count; ++i) AddConnected(connected[i]);
        ApplyConnected();
    }

    // Kept between refreshes so an unchanged enumeration allocates nothing.
    private readonly HashSet<int> _refreshIds = new();
    private readonly List<int> _refreshGone = new();

    private void AddConnected(int id)
    {
        if (id < 0 || !_refreshIds.Add(id)) throw new ArgumentException("Invalid/duplicate connected device");
        if (_refreshIds.Count > MaxDevices) throw new ArgumentException("Too many connected devices");
    }

    private void ApplyConnected()
    {
        bool changed = false;
        _refreshGone.Clear();
        foreach (int id in _devices.Keys) if (!_refreshIds.Contains(id)) _refreshGone.Add(id);
        foreach (int id in _refreshGone) { _devices.Remove(id); changed = true; }
        foreach (int id in _refreshIds) if (!_devices.ContainsKey(id)) { _devices[id] = new Device(); changed = true; }
        if (changed) ++ContextGeneration;
    }

    /// <summary>Discards stale tap credits on focus or menu ownership change without changing normal held values.</summary>
    /// <param name="focused">The same external/window focus predicate used by production GameInput.</param>
    /// <param name="route">Actual screen/modal/host ownership, excluding continuously changing frame/selection values.</param>
    public void SetContext(bool focused, GameInputButtonRoute route)
    {
        if (_focused != focused || _route != route)
        {
            ++ContextGeneration;
            foreach (Device device in _devices.Values)
            {
                Array.Clear(device.Pending);
                device.Blocked = 0;
                if (!focused) device.Reported = 0;
            }
        }
        _focused = focused;
        _route = route;
    }

    /// <summary>Records one actual mapped joypad press/release; duplicate downs never manufacture new taps.</summary>
    /// <param name="id">An actually connected Godot device ID.</param>
    /// <param name="button">One existing XInput bit, already translated from JoyButton.</param>
    /// <param name="pressed">The event's actual down/up state.</param>
    public void Capture(int id, uint button, bool pressed)
    {
        if (button == 0 || (button & (button - 1)) != 0 || (button & ~SupportedButtons) != 0)
            return;
        if (!_devices.TryGetValue(id, out Device? device)) return;
        bool wasDown = (device.EventDown & button) != 0;
        device.EventDown = pressed ? device.EventDown | button : device.EventDown & ~button;
        if (!pressed) { device.Blocked &= ~button; return; }
        if (!_focused || wasDown || (device.Blocked & button) != 0) return;
        int index = Index(button);
        if (device.Pending[index] == MaxPendingPresses)
        {
            device.Pending[index] = 0;
            device.Blocked |= button;
            ++Overflows;
            return;
        }
        ++device.Pending[index];
    }

    /// <summary>Returns only unsampled physical presses, allowing the existing first-press pad claim to see a short tap.</summary>
    /// <param name="id">The connected device whose assignment is being considered.</param>
    /// <returns>Pending button bits; zero when unfocused or disconnected.</returns>
    public uint PendingButtons(int id)
    {
        if (!_focused || !_devices.TryGetValue(id, out Device? device)) return 0;
        uint buttons = 0;
        for (int i = 0; i < 16; ++i) if (device.Pending[i] != 0) buttons |= 1u << i;
        return buttons & ~device.Blocked;
    }

    /// <summary>Augments the ordinary physical poll with one tap edge, with a released frame between distinct taps.</summary>
    /// <param name="id">The connected device sampled by its existing guest-pad assignment.</param>
    /// <param name="polledButtons">The ordinary current IsJoyButtonPressed result; holds remain authoritative.</param>
    /// <returns>Raw physical buttons to merge before existing UI/mask/held-across-unmask logic.</returns>
    public uint Sample(int id, uint polledButtons)
    {
        if (!_focused || !_devices.TryGetValue(id, out Device? device)) return 0;
        // A sampler can be attached while a button is already physically held. Seed the
        // event frontier from that ordinary poll so a duplicate down cannot invent a release.
        device.EventDown = polledButtons & SupportedButtons;
        device.Blocked &= polledButtons;
        uint result = polledButtons & SupportedButtons & ~device.Blocked;
        for (int i = 0; i < 16; ++i)
        {
            uint bit = 1u << i;
            if (device.Pending[i] == 0) continue;
            if ((device.Reported & bit) != 0) result &= ~bit;
            else { result |= bit; --device.Pending[i]; }
        }
        device.Reported = result;
        return result;
    }

    private static int Index(uint button)
    {
        int index = 0;
        while ((button >>= 1) != 0) ++index;
        return index;
    }
}
