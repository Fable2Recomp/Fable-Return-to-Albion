using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// THE HUD STANDS ON ITS FILE AND WHAT THE GUEST TELLS IT. The guest's `hud.bgf` presentation is
/// headless (hooks/gui_headless_presentation.cpp): it has no element tree to sample, so every term
/// the HUD view reads comes from Godot's own playback of the file (<see cref="Engine_UiSlidePlayback"/>)
/// and the statements the guest makes to the presentation's elements
/// (<see cref="Engine_UiSlideStatements"/>, keyed to the file's ordinals):
///
/// <list type="bullet">
/// <item>SLIDES: each component's rest slide (<see cref="Engine_UiRestState"/>) under the slides the
/// host states by name (the registry toggles, faders and slide sets, a mod's `hud_slide`), entered
/// and played by the playback, which also takes every told `GotoSlide` and play / pause.</item>
/// <item>DRAW BITS: on, except where the guest told `active = false`; a told `active = true` is the
/// later `setActive` over a slide's entry (<see cref="SwitchedOn"/>).</item>
/// <item>OPACITY, POSITION, SCALE: the playing tracks and every told attribute write, in the
/// playback's store (<see cref="Animated"/>) — SetOpacity's raw argument scaled by the guest's rule
/// over the element's current value. The meters' components stand at `goToTime(trunc(pct × 10))`
/// (`sub_823FDFF8`), the meter registry's own scrub.</item>
/// <item>TEXTS and TEXTURES: the last told to each element since the presentation was first seen.</item>
/// </list>
/// </summary>
public sealed class Engine_UiHudStand
{
	public Engine_UiHudStand(Engine_UiScreen screen)
	{
		Screen = screen;
		Count = screen.Nodes.Length;
		RootHash = Count > 0 ? screen.Nodes[0].Element.NameHash : 0u;
		_playback = new Engine_UiSlidePlayback(screen);
		Active = new bool[Count];
		SwitchedOn = new bool[Count];
		Texts = new string?[Count];
		Textures = new uint[Count];
		Transform = new float?[]?[Count];
		_rowPool = new float?[]?[Count];
		_stated = new int[Count];
		_lastStated = new int[Count];
		System.Array.Fill(_lastStated, -1);
	}

	public Engine_UiScreen Screen { get; }

	/// <summary>The document's element count; every array here is this long.</summary>
	public int Count { get; }

	/// <summary>The file's root name hash, the key the presentation's statements are held under.</summary>
	public uint RootHash { get; }

	/// <summary>The presentation this stand was last stood for, 0 before the first.</summary>
	public uint Presentation { get; private set; }

	private readonly Engine_UiSlidePlayback _playback;

	/// <summary>The slide each element plays this frame (the guest's numbering, 0 the master).</summary>
	public int[] Slides { get; private set; } = System.Array.Empty<int>();

	/// <summary>Each element's own draw bit: false where the guest told `active = false`.</summary>
	public bool[] Active { get; }

	/// <summary>Where the guest told `active = true`: the later write over a slide's entry.</summary>
	public bool[] SwitchedOn { get; }

	/// <summary>The playback's store per element and channel, NaN where nothing wrote (Engine_UiElementResolve.Animate).</summary>
	public float[] Animated => _playback.Values;

	/// <summary>Elements outside their time window this frame.</summary>
	public bool[] Closed => _playback.Closed;

	/// <summary>The text last told to each element, null where none was.</summary>
	public string?[] Texts { get; }

	/// <summary>The texture hash last told to each element, 0 where none was.</summary>
	public uint[] Textures { get; }

	/// <summary>
	/// Per element, position x/y/z and scale x/y/z as the playback stands them (the order of
	/// <see cref="Engine_UiLiveAttributes.TransformIds"/>), null where none of the six is written.
	/// </summary>
	public float?[]?[] Transform { get; }

	private readonly float?[]?[] _rowPool;

	/// <summary>Bumped when <see cref="Texts"/> or <see cref="Textures"/> took a new statement.</summary>
	public ulong Generation { get; private set; }

	private readonly int[] _stated;
	private readonly int[] _lastStated;
	private int[] _rest = System.Array.Empty<int>();

	private ulong _toldWrites;
	private ulong _toldSeen;
	private ulong _textureGeneration = ulong.MaxValue;
	private IReadOnlyList<(int Ordinal, float Percent)>? _meters;

	/// <summary>
	/// One guest frame. <paramref name="stated"/> is the host's slide per element (0 where it
	/// states none); <paramref name="drawn"/> last frame's draw gate; <paramref name="played"/>
	/// whether a stated entry plays (a registry write through `sub_82426CA8`);
	/// <paramref name="meters"/> each meter component's percentage.
	/// </summary>
	public void Stand(uint presentation, IReadOnlyList<int>? stated, IReadOnlyList<bool>? drawn, long now,
		System.Func<int, bool>? played = null, IReadOnlyList<(int Ordinal, float Percent)>? meters = null)
	{
		if (presentation != Presentation)
		{
			// A presentation first seen: everything told to it counts, its creation's statements
			// included — the HUD is made and filled in the same guest frame (`sub_823CA428`).
			Presentation = presentation;
			_playback.Reset();
			_toldWrites = 0;
			_toldSeen = 0;
			_textureGeneration = ulong.MaxValue;
			System.Array.Clear(Texts);
			System.Array.Clear(Textures);
		}

		bool restChanged = _rest.Length != Count;
		for (int i = 0; i < Count; ++i)
		{
			_stated[i] = stated is not null && i < stated.Count ? stated[i] : 0;
			restChanged |= _stated[i] != _lastStated[i];
		}

		if (restChanged)
		{
			System.Array.Copy(_stated, _lastStated, Count);
			_rest = Engine_UiRestState.Slides(Screen, _stated);
		}

		_meters = meters;
		Slides = _playback.Step(_rest, drawn, now, played, Timed, Told, Engine_UiSlideStatements.Sequence);

		bool?[] told = _playback.ToldActive;
		for (int i = 0; i < Count; ++i)
		{
			Active[i] = told[i] != false;
			SwitchedOn[i] = told[i] == true;
		}

		StandTransforms();
		TakeTexts();
	}

	/// <summary>
	/// Whether a binding's stated percentage is played on its component (`goToTime(pct × 10)`).
	/// Meters only, and never a layout meter (<see cref="Engine_UiBindings.IsLayoutMeter"/>):
	/// `TopBoxY` owns `Health`'s position.y track inside `PlayerHealth` and `TopBoxX`/`TopBoxY`
	/// the quest box's slab — the guest's box fit (`sub_823FC510`) states them from text extents a
	/// headless element does not have, and the view places those widgets itself
	/// (Engine_HudView.BindTracks skips the same names), so the stand leaves them at rest.
	/// </summary>
	public static bool PlaysMeter(Engine_UiBindings.Binding binding) =>
		binding.Kind == Engine_UiBindings.Kind.Meter && !Engine_UiBindings.IsLayoutMeter(binding.Name);

	/// <summary>Restarts every component, as a HUD presentation coming back does.</summary>
	public void Reset()
	{
		Presentation = 0;
		_playback.Reset();
		System.Array.Fill(_lastStated, -1);
	}

	private Engine_UiSlideStatements.Told? Told(int ordinal) =>
		Engine_UiSlideStatements.Of(Presentation, RootHash, ordinal);

	private void Timed(long now)
	{
		foreach (Engine_UiSlideStatements.Write write in Engine_UiSlideStatements.WritesSince(
			Presentation, RootHash, _toldWrites))
		{
			_toldWrites = write.Sequence;
			if (write.IsActive)
			{
				_playback.TellActive(write.Ordinal, write.Value != 0, write.Sequence);
			}
			else if ((write.Attribute & Engine_UiSlideStatements.ScaledOpacity) != 0)
			{
				float raw = System.BitConverter.UInt32BitsToSingle(write.Value);
				float value = Engine_UiSlideStatements.SetOpacityValue(raw, CurrentOpacity(write.Ordinal));
				_playback.Tell(write.Ordinal, write.Attribute & ~Engine_UiSlideStatements.ScaledOpacity,
					System.BitConverter.SingleToUInt32Bits(value), write.Sequence);
			}
			else
			{
				_playback.Tell(write.Ordinal, write.Attribute, write.Value, write.Sequence);
			}
		}

		if (_meters is { } meters)
		{
			foreach ((int ordinal, float percent) in meters)
			{
				_playback.GoToTime(ordinal, (int)(System.Math.Clamp(percent, 0f, 100f) * 10f), now);
			}
		}
	}

	/// <summary>An element's opacity (0..100) as the stand holds it: its store, else the file's.</summary>
	private float CurrentOpacity(int ordinal)
	{
		if (ordinal < 0 || ordinal >= Count)
		{
			return 100f;
		}

		float held = _playback.Values[(ordinal * Engine_UiSlidePlayback.Channels) + (int)Engine_UiSlidePlayback.Channel.Opacity];
		return !float.IsNaN(held) ? held : Screen.Nodes[ordinal].Opacity;
	}

	private void StandTransforms()
	{
		float[] values = _playback.Values;
		for (int i = 0; i < Count; ++i)
		{
			int at = i * Engine_UiSlidePlayback.Channels;
			float?[]? row = null;
			for (int c = 0; c <= (int)Engine_UiSlidePlayback.Channel.ScaleZ; ++c)
			{
				float v = values[at + c];
				if (float.IsNaN(v))
				{
					continue;
				}

				row ??= _rowPool[i] ??= new float?[6];
				row[c] = v;
			}

			if (row is not null)
			{
				for (int c = 0; c <= (int)Engine_UiSlidePlayback.Channel.ScaleZ; ++c)
				{
					if (float.IsNaN(values[at + c]))
					{
						row[c] = null;
					}
				}
			}

			Transform[i] = row;
		}
	}

	private void TakeTexts()
	{
		ulong sequence = Engine_UiSlideStatements.Sequence;
		if (sequence != _toldSeen)
		{
			foreach ((int ordinal, string text) in Engine_UiSlideStatements.TextsSince(Presentation, RootHash, _toldSeen))
			{
				if (ordinal >= 0 && ordinal < Count && Texts[ordinal] != text)
				{
					Texts[ordinal] = text;
					++Generation;
				}
			}
		}

		if (Engine_UiSlideStatements.TextureGeneration != _textureGeneration)
		{
			_textureGeneration = Engine_UiSlideStatements.TextureGeneration;
			foreach ((int ordinal, uint hash) in Engine_UiSlideStatements.TexturesSince(Presentation, RootHash, _toldSeen))
			{
				if (ordinal >= 0 && ordinal < Count && Textures[ordinal] != hash)
				{
					Textures[ordinal] = hash;
					++Generation;
				}
			}
		}

		_toldSeen = sequence;
	}
}
