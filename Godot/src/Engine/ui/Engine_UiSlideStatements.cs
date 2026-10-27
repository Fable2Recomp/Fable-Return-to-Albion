using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// THE SLIDES THE GUEST TELLS ITS PRESENTATIONS, by element: every explicit `GotoSlide`
/// (`sub_82BB5760`), play / pause (`sub_82BB5CB8`), attribute, `active`, text and texture write as
/// gui_slide_hooks.cpp queues them (taken by <see cref="Take"/>) —
/// native screen code, the GUI registries, Lua `goToSlide`, a file's Logic commands, the page
/// arrows — keyed by presentation address and element ordinal. A book reads them through
/// <see cref="Engine_UiSlidePlayback"/>, which enters the slide and plays or pauses it as the
/// guest's time interface does.
///
/// Statements carry one global sequence so the playback can tell a statement made after a
/// component came live from one the walk's own entry superseded. A presentation rebuilt at the
/// same address (a different root name hash) starts clean.
/// </summary>
public static class Engine_UiSlideStatements
{
	/// <summary>The last entry and play state told to one element.</summary>
	public readonly record struct Told(int Slide, ulong Sequence, bool? Paused, ulong PauseSequence);

	/// <summary>One attribute or `active` write told to an element, in statement order.</summary>
	public readonly record struct Write(int Ordinal, uint Attribute, uint Value, bool IsActive, ulong Sequence);

	private sealed class Held
	{
		public uint RootHash;
		public readonly Dictionary<int, Told> ByOrdinal = new();
		public readonly List<Write> Writes = new();
		public readonly Dictionary<int, (string Text, ulong Sequence)> Texts = new();
		public readonly Dictionary<int, (uint Hash, ulong Sequence)> Textures = new();
	}

	/// <summary>Bumped whenever any element is told a texture.</summary>
	public static ulong TextureGeneration { get; private set; }

	/// <summary>The textures told to a presentation's elements after <paramref name="since"/>.</summary>
	public static IEnumerable<(int Ordinal, uint Hash)> TexturesSince(uint presentation, uint rootHash, ulong since)
	{
		if (presentation == 0 || !_said.TryGetValue(presentation, out Held? held) || held.RootHash != rootHash)
		{
			yield break;
		}

		foreach ((int ordinal, (uint hash, ulong sequence)) in held.Textures)
		{
			if (sequence > since)
			{
				yield return (ordinal, hash);
			}
		}
	}

	private static readonly Dictionary<uint, Held> _said = new();

	/// <summary>Writes kept per presentation; older ones are dropped first.</summary>
	private const int WriteCap = 4096;

	/// <summary>Bumped by every statement.</summary>
	public static ulong Sequence { get; private set; }

	private static Held HeldFor(uint presentation, uint rootHash)
	{
		if (!_said.TryGetValue(presentation, out Held? held) || held.RootHash != rootHash)
		{
			held = new Held { RootHash = rootHash };
			_said[presentation] = held;
		}

		return held;
	}

	private static Dictionary<int, Told> For(uint presentation, uint rootHash) => HeldFor(presentation, rootHash).ByOrdinal;

	/// <summary>`@Gui.SetAttribute pres rootHash ordinal attrId value`: Lua, Logic or native code wrote the attribute.</summary>
	public static void Attribute(uint presentation, uint rootHash, int ordinal, uint attribute, uint value) =>
		Add(HeldFor(presentation, rootHash), new Write(ordinal, attribute, value, false, ++Sequence));

	/// <summary>`@Gui.Active pres rootHash ordinal on`: the element's `active` was set.</summary>
	public static void Active(uint presentation, uint rootHash, int ordinal, bool on) =>
		Add(HeldFor(presentation, rootHash), new Write(ordinal, 0u, on ? 1u : 0u, true, ++Sequence));

	private static void Add(Held held, Write write)
	{
		if (held.Writes.Count >= WriteCap)
		{
			held.Writes.RemoveRange(0, WriteCap / 4);
		}

		held.Writes.Add(write);
	}

	/// <summary>The writes told to a presentation after <paramref name="since"/>, in order.</summary>
	public static IEnumerable<Write> WritesSince(uint presentation, uint rootHash, ulong since)
	{
		if (presentation == 0 || !_said.TryGetValue(presentation, out Held? held) || held.RootHash != rootHash)
		{
			yield break;
		}

		List<Write> writes = held.Writes;
		int start = writes.Count;
		while (start > 0 && writes[start - 1].Sequence > since)
		{
			--start;
		}

		for (int i = start; i < writes.Count; ++i)
		{
			yield return writes[i];
		}
	}

	/// <summary>`@Gui.GotoSlide pres rootHash ordinal slide`: the element enters the slide at t = 0.</summary>
	public static void GotoSlide(uint presentation, uint rootHash, int ordinal, int slide)
	{
		Dictionary<int, Told> told = For(presentation, rootHash);
		told.TryGetValue(ordinal, out Told was);
		told[ordinal] = was with { Slide = slide, Sequence = ++Sequence };
	}

	/// <summary>`@Gui.Paused pres rootHash ordinal paused`: the element's slide plays or pauses where it stands.</summary>
	public static void Paused(uint presentation, uint rootHash, int ordinal, bool paused)
	{
		Dictionary<int, Told> told = For(presentation, rootHash);
		told.TryGetValue(ordinal, out Told was);
		told[ordinal] = was with { Paused = paused, PauseSequence = ++Sequence };
	}

	private static readonly uint[] TakeBuffer = new uint[6 * 2048];

	/// <summary>
	/// Takes everything the guest told since the last call (HostBridge.TakeToldStatements) — once a
	/// frame, before the books stand.
	/// </summary>
	public static void Take()
	{
		int records;
		do
		{
			records = Refii.Host.HostBridge.TakeToldStatements(TakeBuffer);
			for (int r = 0; r < records; ++r)
			{
				int at = r * 6;
				uint presentation = TakeBuffer[at + 1];
				uint root = TakeBuffer[at + 2];
				int ordinal = (int)TakeBuffer[at + 3];
				uint a = TakeBuffer[at + 4];
				switch (TakeBuffer[at])
				{
					case 1u: GotoSlide(presentation, root, ordinal, (int)a); break;
					case 2u: Paused(presentation, root, ordinal, a != 0); break;
					case 3u: Attribute(presentation, root, ordinal, a, TakeBuffer[at + 5]); break;
					case 4u: Active(presentation, root, ordinal, a != 0); break;
					case 5u:
						HeldFor(presentation, root).Textures[ordinal] = (a, ++Sequence);
						++TextureGeneration;
						break;
				}
			}
		}
		while (records * 6 == TakeBuffer.Length);

		int texts;
		do
		{
			texts = Refii.Host.HostBridge.TakeToldTexts(TextWords, TextUnits);
			int at = 0;
			for (int r = 0; r < texts; ++r)
			{
				int length = (int)TextWords[(r * 4) + 3];
				var chars = new char[length];
				for (int c = 0; c < length; ++c)
				{
					chars[c] = (char)TextUnits[at + c];
				}

				at += length;
				HeldFor(TextWords[r * 4], TextWords[(r * 4) + 1]).Texts[(int)TextWords[(r * 4) + 2]] =
					(new string(chars), ++Sequence);
			}
		}
		while (texts * 4 == TextWords.Length);
	}

	private static readonly uint[] TextWords = new uint[4 * 256];

	private static readonly ushort[] TextUnits = new ushort[64 * 1024];

	/// <summary>The text the guest last wrote into an element after <paramref name="since"/>, or null.</summary>
	public static string? TextOf(uint presentation, uint rootHash, int ordinal, ulong since) =>
		presentation != 0 && _said.TryGetValue(presentation, out Held? held) && held.RootHash == rootHash &&
		held.Texts.TryGetValue(ordinal, out var told) && told.Sequence > since
			? told.Text
			: null;

	/// <summary>What a presentation has been told, for the `rest:` check: entries, writes, texts, textures.</summary>
	public static string Summary(uint presentation, uint rootHash)
	{
		if (presentation == 0)
		{
			return "no presentation key";
		}

		if (!_said.TryGetValue(presentation, out Held? held))
		{
			return $"nothing told to 0x{presentation:X8}";
		}

		return held.RootHash != rootHash
			? $"0x{presentation:X8} was told under root 0x{held.RootHash:X8}, the book's root is 0x{rootHash:X8}"
			: $"0x{presentation:X8}: {held.ByOrdinal.Count} element(s) told a slide, {held.Writes.Count} write(s), " +
				$"{held.Texts.Count} text(s), {held.Textures.Count} texture(s); sequence {Sequence}";
	}

	/// <summary>The last `active` write told to an element after <paramref name="since"/>, or null.</summary>
	public static bool? ActiveOf(uint presentation, uint rootHash, int ordinal, ulong since)
	{
		if (presentation == 0 || !_said.TryGetValue(presentation, out Held? held) || held.RootHash != rootHash)
		{
			return null;
		}

		for (int i = held.Writes.Count - 1; i >= 0 && held.Writes[i].Sequence > since; --i)
		{
			if (held.Writes[i].IsActive && held.Writes[i].Ordinal == ordinal)
			{
				return held.Writes[i].Value != 0;
			}
		}

		return null;
	}

	/// <summary>What one element was last told, or null; never across a rebuilt presentation.</summary>
	public static Told? Of(uint presentation, uint rootHash, int ordinal) =>
		presentation != 0 && _said.TryGetValue(presentation, out Held? held) && held.RootHash == rootHash &&
		held.ByOrdinal.TryGetValue(ordinal, out Told told)
			? told
			: null;
}
