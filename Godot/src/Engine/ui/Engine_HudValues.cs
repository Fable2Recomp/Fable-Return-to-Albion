using System.Collections.Generic;

using Refii.Core;

namespace Refii.Ui;

/// <summary>
/// The guest's live HUD values, read by name. Values are published against `FNV1(name)` into a
/// registry (`sub_823FE210(registry, FNV1("PlayerHealth"), value)`); `hud.bgf` tags elements with
/// a matching `MeterName`. A meter is `current / absoluteMax * 100`, the current maximum being a
/// second meter (`PlayerHealthMax`). Callers resolve a <see cref="Handle"/> once at build time.
/// </summary>
public sealed class Engine_HudValues
{
    /// <summary>A resolved meter or text name: the hash plus the string.</summary>
    public readonly record struct Handle(string Name, uint Hash)
    {
        public static Handle For(string name) => new(name, Engine_GameHash.Fnv1(name));
    }

    private Dictionary<uint, float> _meters = new();
    private Dictionary<uint, string> _texts = new();

    /// <summary>
    /// The feed this one overlays, or null for a feed of its own. An overlay is a view's own
    /// statement over the shared feed — player two's half on split screen, which shows player
    /// two's numbers where the guest's one HUD states only the local player's: what it states
    /// (<see cref="Override"/>) wins, everything else reads through.
    /// </summary>
    public Engine_HudValues? Under { get; init; }

    /// <summary>States a text field on this overlay (null withdraws it, reading through again).</summary>
    public void Override(Handle handle, string? text)
    {
        if (text is null)
        {
            _texts.Remove(handle.Hash);
        }
        else
        {
            _texts[handle.Hash] = text;
        }
    }

    /// <summary>
    /// States a meter on this overlay, 0..100 (null withdraws it). On a feed of its own the
    /// statement outlasts each read of the guest's channels until withdrawn — the online joiner's
    /// readout, which shows player two's numbers where the guest states the host's.
    /// </summary>
    public void OverrideMeter(Handle handle, float? percent)
    {
        if (percent is { } value)
        {
            _meters[handle.Hash] = value;
            _meterOverrides[handle.Hash] = value;
        }
        else
        {
            // A feed of its own keeps the guest's value until its next read.
            _meterOverrides.Remove(handle.Hash);
            if (Under is not null)
            {
                _meters.Remove(handle.Hash);
            }
        }
    }

    /// <summary>The meters stated over this feed's reads (<see cref="OverrideMeter"/>).</summary>
    private readonly Dictionary<uint, float> _meterOverrides = new();

    /// <summary>States an element's visibility on this overlay (null withdraws it).</summary>
    public void OverrideVisible(Handle handle, bool? visible)
    {
        if (visible is { } shown)
        {
            _toggled[handle.Hash] = shown;
        }
        else
        {
            _toggled.Remove(handle.Hash);
        }
    }

    /// <summary>How many meters and text fields the guest published on the last read.</summary>
    public int MeterCount => _meters.Count;

    public int TextCount => _texts.Count;

    /// <summary>
    /// Text stated through `GUI.SetGUIText(element, tag)`, keyed by FNV-1 of the raw element
    /// name (no Babel lookup, no `TEXT_` prefix); the value is the resolved Babel tag. Guest
    /// store `*(*(GUI+32)+108)`: `+0` fade-in set, `+16` meter ratios, `+28` text ids, `+40`
    /// visible set (`elemMgr+28` is the world-icon manager, a different base).
    /// </summary>
    private readonly Dictionary<uint, string> _stated = new();

    /// <summary>Elements hidden or shown through `GUI.ToggleElement`; same key rule. The set at `STORE+40` is separate from the fade-in set at `STORE+0`: fading out does not clear toggled visibility.</summary>
    private readonly Dictionary<uint, bool> _toggled = new();

    /// <summary>Drops every statement: they belong to the game that made them, and the view
    /// outlives it across quit-to-title.</summary>
    public void Reset()
    {
        _stated.Clear();
        _toggled.Clear();
        _statedMeters.Clear();
        _faded.Clear();
        _slideSets.Clear();
        DpadCentered = null;
        PlayerIconsDrawn = null;
    }

    /// <summary>`GUI.SetGUIText(element, tag)` — the call, already resolved.</summary>
    public void StateText(uint elementHash, string display) => _stated[elementHash] = display;

    /// <summary>`GUI.ToggleElement(element, visible)`.</summary>
    public void StateVisible(uint elementHash, bool visible) => _toggled[elementHash] = visible;

    /// <summary>
    /// Meters stated through `SetMeterRatioOverTime`, 0..100, keyed by FNV-1 of the raw element
    /// name (`sub_82306928`, basis 0x811C9DC5). The Lua channel; the C++ registry covers ~20 names.
    /// </summary>
    private readonly Dictionary<uint, Seek> _statedMeters = new();

    /// <summary>
    /// One meter binding: `sub_823FE2D0` writes target and rate, `sub_82265D20` moves current
    /// toward target each frame.
    /// </summary>
    private sealed class Seek
    {
        public float Current;
        public float Target;

        /// <summary>Percent per second, signed toward the target; 0 means snap.</summary>
        public float Rate;

        /// <summary>What the C++ registry last published for this name, to detect a change.</summary>
        public float? Polled;
    }

    /// <summary>
    /// `GUI.SetMeterRatioOverTime(element, ratio, seconds)`, units percent — the percent twin
    /// (`SetMeterPercentOverTime`, `sub_823069A8`) is the same call with `percent * 0.01`.
    /// `sub_823FE2D0`: `target = clamp(value, 0, 100)`,
    /// `rate = seconds &lt;= 0 ? 0 : (target - current) / seconds`; `sub_82265D20` seeks `current`
    /// per frame (rate 0 snaps). Widgets scrub <c>current</c>.
    /// </summary>
    public void StateMeter(uint elementHash, float percent, float seconds)
    {
        float target = System.Math.Clamp(percent, 0f, 100f);
        if (!_statedMeters.TryGetValue(elementHash, out Seek? seek))
        {
            // A fresh binding starts at the C++-published value (0 when unwritten) so a
            // screen's first write animates up rather than snapping.
            float current = _meters.TryGetValue(elementHash, out float polled) ? polled : 0f;
            seek = new Seek { Current = current, Polled = current };
            _statedMeters[elementHash] = seek;
        }

        seek.Target = target;
        seek.Rate = seconds <= 0f ? 0f : (target - seek.Current) / seconds;
    }

    /// <summary>`sub_82265D20`, mode 0, for every stated meter.</summary>
    private void AdvanceMeters(float dt)
    {
        foreach (Seek seek in _statedMeters.Values)
        {
            if (seek.Current == seek.Target)
            {
                continue;
            }

            if (seek.Rate == 0f)
            {
                seek.Current = seek.Target;
                continue;
            }

            float next = seek.Current + (seek.Rate * dt);
            if ((seek.Rate > 0f && next > seek.Target) || (seek.Rate < 0f && next < seek.Target))
            {
                next = seek.Target;
                seek.Rate = 0f;
            }

            seek.Current = next;
        }
    }

    /// <summary>
    /// `GUI.SetDPadCenter(bool)`: whether the quick ring is drawn centred. The visible effect is
    /// the `setCenterOn` / `setCenterOff` logic event on the `UIContract` presentation, not the
    /// bool at `dpadObject+0x22`. Null until stated.
    /// </summary>
    public bool? DpadCentered
    {
        get => _dpadCentered ?? Under?.DpadCentered;
        private set => _dpadCentered = value;
    }

    private bool? _dpadCentered;

    /// <summary>`GUI.SetDPadCenter(bool)`.</summary>
    public void StateDpadCentered(bool centered) => DpadCentered = centered;

    /// <summary>
    /// `GUI.SetShouldDrawPlayerIcons(bool)`: the two offscreen player markers only
    /// (`HeroOffscreenIconHandle` type 52, `HenchmanOffscreenIconHandle` type 53). Flag at
    /// `*(elemMgr+100)+16`; does not affect quest markers. Null until stated.
    /// </summary>
    public bool? PlayerIconsDrawn
    {
        get => _playerIconsDrawn ?? Under?.PlayerIconsDrawn;
        private set => _playerIconsDrawn = value;
    }

    private bool? _playerIconsDrawn;

    /// <summary>`GUI.SetShouldDrawPlayerIcons(bool)`.</summary>
    public void StatePlayerIconsDrawn(bool drawn) => PlayerIconsDrawn = drawn;

    /// <summary>
    /// Elements the guest faded, by name hash, carrying the slide asked for. The fade natives are
    /// `GotoSlide` calls: `SlideFaderName` gets 1/2/3 for In/OutFast/OutSlow, `SlideFader2Name`
    /// gets 2/3/4. Duration and curve come from the slide record in the `.bgf`, never a constant.
    /// Separate from the `ToggleElement` set.
    /// </summary>
    private readonly Dictionary<uint, int> _faded = new();

    /// <summary>The slide the guest last faded an element to, or null when it never has.</summary>
    public int? FadeSlide(Handle handle) =>
        _faded.TryGetValue(handle.Hash, out int slide) ? slide : null;

    /// <summary>`GUI.FadeElementIn` / `FadeElementOutFast` / `FadeElementOutSlow`.</summary>
    /// <param name="slide">1 in, 2 out fast, 3 out slow — the `SlideFaderName` index.</param>
    public void StateFade(uint elementHash, int slide) => _faded[elementHash] = slide;

    /// <summary>Every stated toggle, for the view to fold as slides (true → 2, false → 1, as `sub_823FE848`).</summary>
    public IEnumerable<(uint Hash, bool Visible)> Toggles
    {
        get
        {
            foreach ((uint hash, bool visible) in _toggled)
            {
                yield return (hash, visible);
            }
        }
    }

    /// <summary>Every stated fade, as the `SlideFaderName` index (1 in, 2 out fast, 3 out slow).</summary>
    public IEnumerable<(uint Hash, int Slide)> Fades
    {
        get
        {
            foreach ((uint hash, int slide) in _faded)
            {
                yield return (hash, slide);
            }
        }
    }

    /// <summary>
    /// Elements the guest set to a whole slide by INDEX, by name hash — `SlideSetName`,
    /// registry 10. Unlike the fader this is not a fixed 1/2/3: the published integer goes
    /// straight to the slide player, so it is stated verbatim.
    ///
    /// STICKY, like the fader beside it. The guest's lower path `sub_823FD9B0` does not touch
    /// registry 10, so the last index legitimately survives the readout going down.
    /// </summary>
    private readonly Dictionary<uint, int> _slideSets = new();

    /// <summary>The `SlideSetName` publish — the index as stated, never shifted.</summary>
    public void StateSlideSet(uint elementHash, int slide) => _slideSets[elementHash] = slide;

    /// <summary>Every stated slide set, as the verbatim `SlideSetName` index.</summary>
    public IEnumerable<(uint Hash, int Slide)> SlideSets
    {
        get
        {
            foreach ((uint hash, int slide) in _slideSets)
            {
                yield return (hash, slide);
            }
        }
    }

    /// <summary>Whether the guest has hidden an element, or null when it never said.</summary>
    public bool? Visible(Handle handle) =>
        _toggled.TryGetValue(handle.Hash, out bool visible) ? visible : Under?.Visible(handle);

    /// <summary>One snapshot of everything the guest publishes; one walk for the whole HUD.</summary>
    /// <param name="dt">Seconds since the last sample; the seek clock of the stated meters.</param>
    public void Sample(float dt)
    {
        Advance(dt);
        Read();
    }

    /// <summary>
    /// Every value on Godot's clock, every frame: the stated meters' glide, the polled meters'
    /// glide onto what the guest last published, and each pulser's own phase.
    /// </summary>
    public void Advance(float dt)
    {
        AdvanceMeters(dt);
        foreach ((uint hash, Seek seek) in _statedMeters)
        {
            if (_meters.ContainsKey(hash))
            {
                _meters[hash] = seek.Current;
            }
        }

        AdvanceSeeks(_polledMeters.Values, dt);
        foreach ((uint hash, Seek seek) in _polledMeters)
        {
            if (!_statedMeters.ContainsKey(hash) && _meters.ContainsKey(hash))
            {
                _meters[hash] = seek.Current;
            }
        }

        foreach ((uint hash, Pulse pulse) in _pulses)
        {
            pulse.Phase = Wrap01(pulse.Phase + (pulse.Rate * dt));
            if (_pulsers.ContainsKey(hash))
            {
                _pulsers[hash] = pulse.Phase;
            }
        }
    }

    /// <summary>
    /// The C++ registry's meters, glided onto each published value over one guest frame. The
    /// guest seeks them itself (`sub_82265D20`, per HF frame) and publishes the result; drawn as
    /// read, a draining bar stepped at 30 Hz under a display drawing faster.
    /// </summary>
    private readonly Dictionary<uint, Seek> _polledMeters = new();

    /// <summary>The guest's frame interval, which a published change is glided over.</summary>
    private const float GuestFrameSeconds = 1f / 30f;

    /// <summary>
    /// A pulser on Godot's clock: its phase advances at the rate measured between the guest's
    /// statements and is pulled toward each one; a jump of more than a quarter cycle is a
    /// restart and is taken at once.
    /// </summary>
    private sealed class Pulse
    {
        public float Phase;
        public float Rate;
        public float Stated;
        public ulong StatedUsec;
    }

    private readonly Dictionary<uint, Pulse> _pulses = new();

    private static float Wrap01(float phase)
    {
        phase -= System.MathF.Floor(phase);
        return phase;
    }

    /// <summary>The shortest signed distance between two phases on the unit loop.</summary>
    private static float PhaseGap(float gap)
    {
        gap -= System.MathF.Round(gap);
        return gap;
    }

    private static void AdvanceSeeks(IEnumerable<Seek> seeks, float dt)
    {
        foreach (Seek seek in seeks)
        {
            if (seek.Current == seek.Target)
            {
                continue;
            }

            if (seek.Rate == 0f)
            {
                seek.Current = seek.Target;
                continue;
            }

            float next = seek.Current + (seek.Rate * dt);
            if ((seek.Rate > 0f && next > seek.Target) || (seek.Rate < 0f && next < seek.Target))
            {
                next = seek.Target;
                seek.Rate = 0f;
            }

            seek.Current = next;
        }
    }

    /// <summary>Whether anything is still moving on Godot's clock: a gliding meter or a running pulser.</summary>
    public bool Seeking
    {
        get
        {
            foreach (Seek seek in _statedMeters.Values)
            {
                if (seek.Current != seek.Target)
                {
                    return true;
                }
            }

            foreach (Seek seek in _polledMeters.Values)
            {
                if (seek.Current != seek.Target)
                {
                    return true;
                }
            }

            foreach (Pulse pulse in _pulses.Values)
            {
                if (pulse.Rate != 0f)
                {
                    return true;
                }
            }

            return Under?.Seeking == true;
        }
    }

    /// <summary>The guest's channels, read: once per guest frame is the whole of what they change.</summary>
    public void Read()
    {

        // `_meters` is replaced, not merged, so the stated meters are re-applied at the end.
        _meters = Engine_UiSource.Current.GetHudMeters();
        _pulsers = Engine_UiSource.Current.GetHudPulsers();
        _texts = Engine_UiSource.Current.GetHudTexts();
        FollowPolled();
        foreach ((uint hash, float value) in _meterOverrides)
        {
            _meters[hash] = value;
        }

        // The registry wins where both channels hold a name.
        foreach ((uint hash, float value) in Engine_UiSource.Current.GetHudScriptMeters())
        {
            if (!_meters.ContainsKey(hash))
            {
                _meters[hash] = value;
            }
        }

        // The script store holds a tag id, not a string: without the book it is unresolvable
        // and the name is left to the registry.
        if (Strings is null)
        {
            return;
        }

        foreach ((uint hash, uint tag) in Engine_UiSource.Current.GetHudScriptTexts())
        {
            if (!_texts.ContainsKey(hash) && Strings.TryGetString(tag, out string display))
            {
                _texts[hash] = display;
            }
        }

        // The stated value wins over the polled channels, so it is applied last.
        foreach ((uint hash, string display) in _stated)
        {
            _texts[hash] = display;
        }

        // Last writer wins, as in the guest: both channels write the same element records. A
        // registry value that changed since the last sample collapses the seek onto it; an
        // unchanged one loses to the seek's own progress.
        foreach ((uint hash, Seek seek) in _statedMeters)
        {
            if (_meters.TryGetValue(hash, out float polled) && seek.Polled != polled)
            {
                seek.Polled = polled;
                seek.Current = polled;
                seek.Target = polled;
                seek.Rate = 0f;
            }

            _meters[hash] = seek.Current;
        }
    }

    /// <summary>
    /// Takes this read's published meters and pulser phases as targets for the glides that draw
    /// them every frame, and puts the glided values in their place.
    /// </summary>
    private void FollowPolled()
    {
        foreach ((uint hash, float value) in _meters)
        {
            if (!_polledMeters.TryGetValue(hash, out Seek? seek))
            {
                _polledMeters[hash] = new Seek { Current = value, Target = value };
                continue;
            }

            if (value != seek.Target)
            {
                seek.Target = value;
                seek.Rate = (value - seek.Current) / GuestFrameSeconds;
            }
        }

        foreach ((uint hash, Seek seek) in _polledMeters)
        {
            if (_meters.ContainsKey(hash))
            {
                _meters[hash] = seek.Current;
            }
        }

        ulong now = Refii.Entities.Engine_DrawnFrame.NowUsec;
        foreach ((uint hash, float phase) in _pulsers)
        {
            if (!_pulses.TryGetValue(hash, out Pulse? pulse))
            {
                _pulses[hash] = new Pulse { Phase = phase, Stated = phase, StatedUsec = now };
                continue;
            }

            if (phase == pulse.Stated)
            {
                continue;
            }

            float seconds = (now - pulse.StatedUsec) * 1e-6f;
            float moved = PhaseGap(phase - pulse.Stated);
            if (seconds > 0f && seconds < 0.5f)
            {
                pulse.Rate += ((moved / seconds) - pulse.Rate) * 0.5f;
            }

            pulse.Stated = phase;
            pulse.StatedUsec = now;
            float error = PhaseGap(phase - pulse.Phase);
            pulse.Phase = System.MathF.Abs(error) > 0.25f ? phase : Wrap01(pulse.Phase + (error * 0.3f));
        }

        _gonePulses.Clear();
        foreach ((uint hash, Pulse pulse) in _pulses)
        {
            if (_pulsers.ContainsKey(hash))
            {
                _pulsers[hash] = pulse.Phase;
            }
            else
            {
                _gonePulses.Add(hash);
            }
        }

        // A pulser the guest no longer holds stops running here too.
        foreach (uint hash in _gonePulses)
        {
            _pulses.Remove(hash);
        }
    }

    private readonly List<uint> _gonePulses = new();

    /// <summary>
    /// The localised string book, for resolving the tag ids the Lua route states; null drops
    /// that half of the text channel.
    /// </summary>
    public Refii.Text.Engine_BabelDatabase? Strings { get; set; }

    /// <summary>
    /// A meter's value, 0..100, or null when the guest has not published it. Null is not zero:
    /// never written and genuinely at zero are different states.
    /// </summary>
    public float? Meter(Handle handle) =>
        _meters.TryGetValue(handle.Hash, out float value) ? value : Under?.Meter(handle);

    /// <summary>A pulser's loop phase 0..1, or null when the guest holds none by that name.</summary>
    public float? Pulser(Handle handle) =>
        _pulsers.TryGetValue(handle.Hash, out float phase) ? phase : Under?.Pulser(handle);

    private Dictionary<uint, float> _pulsers = new();

    /// <summary>A meter as a 0..1 fraction, for a bar's scale.</summary>
    public float? Fraction(Handle handle) =>
        Meter(handle) is { } value ? System.Math.Clamp(value / 100f, 0f, 1f) : null;

    /// <summary>A published text field, or null when the guest has not written it.</summary>
    public string? Text(Handle handle) =>
        _texts.TryGetValue(handle.Hash, out string? value) ? value : Under?.Text(handle);

    /// <summary>
    /// Whether the guest has published anything at all; the HUD draws nothing until it has.
    /// </summary>
    public bool Live => _meters.Count != 0 || _texts.Count != 0 || Under?.Live == true;

    /// <summary>One line for the run, naming what is missing.</summary>
    /// <summary>
    /// A cheap key over exactly what <see cref="Describe"/> would print, so the string is built
    /// only on the frames where the line would actually change.
    /// </summary>
    /// <remarks>
    /// `Describe` allocates; comparing its output every frame is per-frame garbage, and GC
    /// pauses stall the guest thread.
    ///
    /// The key covers the same inputs the line does — how many were found, the two published
    /// counts, and which names are missing, in order — so a change in any of them still prints.
    /// </remarks>
    public long DescribeKey(IEnumerable<Handle> wanted)
    {
        long key = (_meters.Count * 1000003L) ^ _texts.Count;
        int found = 0;
        foreach (Handle handle in wanted)
        {
            if (_meters.ContainsKey(handle.Hash) || _texts.ContainsKey(handle.Hash))
            {
                ++found;
            }
            else
            {
                key = (key * 1000003L) ^ handle.Hash;
            }
        }

        return (key * 1000003L) ^ found;
    }

    public string Describe(IEnumerable<Handle> wanted)
    {
        var missing = new List<string>();
        int found = 0;
        foreach (Handle handle in wanted)
        {
            if (_meters.ContainsKey(handle.Hash) || _texts.ContainsKey(handle.Hash))
            {
                ++found;
            }
            else
            {
                missing.Add(handle.Name);
            }
        }

        return $"{found} bound, {_meters.Count} meter(s) and {_texts.Count} text field(s) " +
            $"published" +
            (missing.Count == 0 ? string.Empty : $"; NOT PUBLISHED: {string.Join(", ", missing)}");
    }
}
