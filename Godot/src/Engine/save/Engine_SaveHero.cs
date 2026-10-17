using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Refii.Save;

/// <summary>
/// The hero out of a saved game's own files — read on the host, never loaded by the guest.
/// </summary>
/// <remarks>
/// TWO FORMATS, BOTH THE GUEST'S, BOTH VERIFIED AGAINST A REAL SAVE.
///
/// `mainsave.bin` is a sequence of `[u32 BE compressed][u32 BE raw][zlib]` chunks that form ONE
/// continuous zlib stream (the guest inflates with `Z_SYNC_FLUSH`; a chunk on its own is
/// truncated). The inflated bytes are tokenised XML: `00 name` opens an element, `03 name value`
/// is an attribute, `02 value` element text, `01` closes, `05` ends a document. A name is a
/// u16 index into a string table that RESTARTS PER DOCUMENT; an index equal to the table's size
/// introduces a new string as `u32 len` (NUL included) + bytes. A value is a type byte then
/// payload: 1 string (a name), 2 u32, 3 i32, 4 f32, 5 u8 bool. Every integer is big-endian.
/// Documents follow one another: `Header`, then `EntityManager`, whose `Entities` hold one
/// `Entity` per thing in the world. The hero is `UniqueID {UIDGameID 1, UIDEditorID 0}`;
/// its `EntityComponents` are `EC` elements keyed by `Hash = FNV1(name + "Component")` with the
/// class's `CEC` prefix dropped. Nothing after `EntityManager` is needed, so nothing after it
/// is parsed.
///
/// `herosave.bin` is a u32 BE length then plain XML: `Money`, `Gender` (2 = male), `Morality`,
/// `Purity`, `Renown`, `HeroAbility0..14`, `Experience0..3`, `TotalExperience0..3` as
/// `type="..."` elements whose text is the value (a float carries a trailing `%X` of its raw
/// bits, which is exact where the `%f` is not).
///
/// Worn clothing is stored as a UNIT INDEX into the owner's inventory — the writer sums
/// `Count` over the items before the worn one — so a worn record id is the inventory item at
/// that cumulative index.
/// </remarks>
public static class Engine_SaveHero
{
	/// <summary>Everything the kit carries, read off the files.</summary>
	public sealed class Hero
	{
		public bool Female;
		public int Money;
		public float Morality, Purity;
		public int Renown;
		public float Strength, Skill, Will, Fat, Height, Tan;
		public int[] AbilityLevels = new int[15];
		public float[] Experience = new float[4];
		public float[] TotalExperience = new float[4];
		public uint Face;
		public uint Melee, Ranged;
		public List<uint> Worn = new();
		public List<(uint Record, int Count)> Inventory = new();
		public float ChapterProgress;
		public HashSet<string> CompletedQuests = new(StringComparer.OrdinalIgnoreCase);
		public bool HasInventoryInstanceData;
		public Dictionary<int, InventoryInstance> InventoryInstances = new();
		public List<uint> UnlockedExpressions = new();
		public List<(int Direction, int Slot, int Spell)> AssignedSpells = new();
		public List<string> UnlockedTitles = new();
		public List<string> FreeTitles = new();
		public uint Title;
	}

	public sealed record Augmentation(uint Primary, uint Secondary);
	public sealed record Dye(byte Red, byte Green, byte Blue, float Brightness);
	public sealed class InventoryInstance
	{
		public List<Augmentation>? Augmentations;
		public Dye[]? Dyes;
		public bool Unsupported;
	}

	/// <summary>
	/// Finds the save directory named <paramref name="saveName"/> under any user of the game's
	/// content root — `<root>/<xuid>/4D5307F1/00000001/<name>/` — newest first.
	/// </summary>
	public static string? FindSaveDirectory(string userDataRoot, string saveName)
	{
		if (string.IsNullOrEmpty(userDataRoot) || string.IsNullOrEmpty(saveName) ||
			saveName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !Directory.Exists(userDataRoot))
		{
			return null;
		}

		string? best = null;
		DateTime bestTime = DateTime.MinValue;
		foreach (string user in Directory.EnumerateDirectories(userDataRoot))
		{
			string candidate = Path.Combine(user, "4D5307F1", "00000001", saveName);
			if (!File.Exists(Path.Combine(candidate, "mainsave.bin")))
			{
				continue;
			}

			DateTime when = File.GetLastWriteTimeUtc(Path.Combine(candidate, "mainsave.bin"));
			if (when > bestTime)
			{
				bestTime = when;
				best = candidate;
			}
		}

		return best;
	}

	/// <summary>Why the last <see cref="Read"/> returned null, for the log; empty on success.</summary>
	public static string LastError { get; private set; } = string.Empty;

	/// <summary>Reads the hero out of a save directory, or null when a file is missing or malformed.</summary>
	public static Hero? Read(string saveDirectory)
	{
		LastError = string.Empty;
		try
		{
			var hero = new Hero();
			ReadHeroSave(Path.Combine(saveDirectory, "herosave.bin"), hero);
			if (ReadMainSave(Path.Combine(saveDirectory, "mainsave.bin"), hero))
			{
				return hero;
			}

			LastError = LastError.Length != 0 ? LastError : "mainsave.bin holds no hero entity";
			return null;
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or XmlException or IndexOutOfRangeException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or OverflowException)
		{
			LastError = $"{exception.GetType().Name}: {exception.Message}";
			return null;
		}
	}

	// --- herosave.bin -----------------------------------------------------------------------

	private static void ReadHeroSave(string path, Hero hero)
	{
		if (!File.Exists(path))
		{
			return;
		}

		if (new FileInfo(path).Length > 1024 * 1024)
			throw new InvalidDataException("herosave.bin exceeds the supported size limit");
		byte[] file = File.ReadAllBytes(path);
		if (file.Length < 4)
		{
			throw new InvalidDataException("herosave.bin is truncated");
		}

		int length = (file[0] << 24) | (file[1] << 16) | (file[2] << 8) | file[3];
		if (length <= 0 || length > file.Length - 4 || length > 1024 * 1024)
			throw new InvalidDataException("herosave.bin has an invalid XML length");
		string xml = Encoding.UTF8.GetString(file, 4, length);
		var doc = new XmlDocument { XmlResolver = null };
		using var xmlReader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
		{
			DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024,
		});
		doc.Load(xmlReader);
		XmlElement? root = doc.DocumentElement;
		if (root is null || root.Name != "HeroSave")
		{
			throw new InvalidDataException("herosave.bin does not contain HeroSave");
		}

		hero.Money = Int(root, "Money");
		hero.Female = Int(root, "Gender") == 1;
		hero.Morality = Float(root, "Morality");
		hero.Purity = Float(root, "Purity");
		hero.Renown = Int(root, "Renown");
		for (int i = 0; i < 15; ++i)
		{
			hero.AbilityLevels[i] = Int(root, "HeroAbility" + i);
		}

		for (int i = 0; i < 4; ++i)
		{
			hero.Experience[i] = Float(root, "Experience" + i);
			hero.TotalExperience[i] = Float(root, "TotalExperience" + i);
		}

		static string? Text(XmlElement root, string name) =>
			root.SelectSingleNode(name)?.InnerText?.Trim();

		static int Int(XmlElement root, string name) =>
			int.TryParse(Text(root, name), System.Globalization.NumberStyles.Integer,
				System.Globalization.CultureInfo.InvariantCulture, out int value) ? value : 0;

		static float Float(XmlElement root, string name)
		{
			string? text = Text(root, name);
			if (text is null)
			{
				return 0f;
			}

			// "%f %X": the hex word is the exact value; the decimal is a rounded courtesy.
			int space = text.LastIndexOf(' ');
			if (space > 0 && uint.TryParse(text[(space + 1)..], System.Globalization.NumberStyles.HexNumber,
				System.Globalization.CultureInfo.InvariantCulture, out uint bits))
			{
				return BitConverter.Int32BitsToSingle(unchecked((int)bits));
			}

			return float.TryParse(text, System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float value) ? value : 0f;
		}
	}

	// --- mainsave.bin -----------------------------------------------------------------------

	private sealed class Node
	{
		public string Name = string.Empty;
		public Dictionary<string, object> Attributes = new();
		public object? Text;
		public List<Node> Children = new();

		public Node? Child(string name) => Children.Find(c => c.Name == name);
		public IEnumerable<Node> All(string name) => Children.FindAll(c => c.Name == name);
	}

	private sealed class Reader
	{
		private readonly byte[] _b;
		private int _p;
		private readonly List<string> _strings = new();

		public Reader(byte[] bytes) => _b = bytes;
		public bool AtEnd => _p >= _b.Length;

		private byte U8() => _b[_p++];
		private ushort U16() { ushort v = (ushort)((_b[_p] << 8) | _b[_p + 1]); _p += 2; return v; }
		private uint U32() { uint v = (uint)((_b[_p] << 24) | (_b[_p + 1] << 16) | (_b[_p + 2] << 8) | _b[_p + 3]); _p += 4; return v; }

		private string Name()
		{
			int index = U16();
			if (index == _strings.Count)
			{
				int length = (int)U32();
				if (length < 1 || length > 1024 * 1024 || length > _b.Length - _p)
					throw new InvalidDataException("invalid save string length");
				string text = Encoding.Latin1.GetString(_b, _p, length).TrimEnd('\0');
				_p += length;
				_strings.Add(text);
			}

			return _strings[index];
		}

		private object Value()
		{
			byte type = U8();
			return type switch
			{
				1 => Name(),
				2 => U32(),
				3 => unchecked((int)U32()),
				4 => BitConverter.Int32BitsToSingle(unchecked((int)U32())),
				5 => U8() != 0,
				_ => throw new InvalidDataException($"value type {type} at {_p - 1}"),
			};
		}

		/// <summary>One document: a fresh string table, elements to depth zero, then `05`.</summary>
		public Node Document()
		{
			_strings.Clear();
			var stack = new Stack<Node>();
			Node? root = null;
			while (true)
			{
				byte token = U8();
				switch (token)
				{
					case 0:
					{
						if (stack.Count >= 128) throw new InvalidDataException("save nesting limit exceeded");
						var node = new Node { Name = Name() };
						if (stack.Count == 0) root = node; else stack.Peek().Children.Add(node);
						stack.Push(node);
						break;
					}
					case 3:
					{
						string key = Name();
						stack.Peek().Attributes[key] = Value();
						break;
					}
					case 2:
						stack.Peek().Text = Value();
						break;
					case 1:
						stack.Pop();
						if (stack.Count == 0)
						{
							if (U8() != 5) throw new InvalidDataException("document did not end with 05");
							return root!;
						}
						break;
					default:
						throw new InvalidDataException($"token {token} at {_p - 1}");
				}
			}
		}
	}

	private static byte[] Inflate(string path)
	{
		if (new FileInfo(path).Length > 64 * 1024 * 1024)
			throw new InvalidDataException("save exceeds the supported size limit");
		byte[] file = File.ReadAllBytes(path);
		// The chunks are one zlib stream: strip the two length words from each and feed the
		// payloads to a single inflater.
		using var joined = new MemoryStream();
		int pos = 0;
		while (pos + 8 <= file.Length)
		{
			int compressed = (file[pos] << 24) | (file[pos + 1] << 16) | (file[pos + 2] << 8) | file[pos + 3];
			pos += 8;
			if (compressed <= 0 || compressed > file.Length - pos)
				throw new InvalidDataException("save compression chunk is truncated");
			joined.Write(file, pos, compressed);
			pos += compressed;
		}

		joined.Position = 0;
		using var inflater = new ZLibStream(joined, CompressionMode.Decompress);
		using var output = new MemoryStream();

		// THE GUEST NEVER FINISHES THE STREAM: every chunk ends on a sync flush, the last one
		// too, so a strict inflater reports a truncated stream at the very end. Everything it
		// produced up to then is the whole file; keep it.
		var buffer = new byte[1 << 16];
		try
		{
			int read;
			while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
			{
				output.Write(buffer, 0, read);
				if (output.Length > 128 * 1024 * 1024)
					throw new IOException("save exceeds the supported decompressed size limit");
			}
		}
		catch (InvalidDataException) when (output.Length > 0)
		{
		}

		return output.ToArray();
	}

	private static uint Fnv1(string text)
	{
		uint hash = 0x811C9DC5u;
		foreach (byte b in Encoding.ASCII.GetBytes(text))
		{
			hash = unchecked(hash * 0x01000193u) ^ b;
		}

		return hash;
	}

	private static bool ReadMainSave(string path, Hero hero)
	{
		if (!File.Exists(path))
		{
			return false;
		}

		byte[] inflated = Inflate(path);
		var reader = new Reader(inflated);
		Node header = reader.Document();         // Header
		Node manager = reader.Document();        // EntityManager
		if (manager.Name != "EntityManager" || manager.Child("Entities") is not { } entities)
		{
			LastError = $"after {inflated.Length} inflated byte(s): documents '{header.Name}' then '{manager.Name}' " +
				$"({manager.Children.Count} child(ren): {string.Join(",", manager.Children.ConvertAll(c => c.Name))})";
			return false;
		}

		Node? heroEntity = null;
		foreach (Node entity in entities.All("Entity"))
		{
			if (entity.Child("UniqueID") is { } uid &&
				uid.Child("UIDGameID")?.Text is uint game && game == 1u &&
				uid.Child("UIDEditorID")?.Text is uint editor && editor == 0u)
			{
				heroEntity = entity;
				break;
			}
		}

		if (heroEntity?.Child("EntityComponents") is not { } components)
		{
			int total = 0;
			foreach (Node _ in entities.All("Entity")) ++total;
			LastError = $"{total} entities, none with UniqueID {{1,0}} carrying EntityComponents";
			return false;
		}

		var byHash = new Dictionary<uint, Node>();
		foreach (Node ec in components.All("EC"))
		{
			if (ec.Attributes.TryGetValue("Hash", out object? hash) && hash is uint key)
			{
				byHash[key] = ec;
			}
		}

		// Inventory first: worn items index into it.
		if (byHash.TryGetValue(Fnv1("InventoryComponent"), out Node? inventory))
		{
			foreach (Node item in inventory.All("InventoryItem"))
			{
				uint record = item.Child("RecordID")?.Text is uint r ? r : 0u;
				int count = item.Child("Count")?.Text is int c ? c : 1;
				if (record != 0u)
				{
					var instance = ReadInventoryInstance(item);
					if (instance is not null)
					{
						hero.HasInventoryInstanceData = true;
						hero.InventoryInstances.Add(hero.Inventory.Count, instance);
					}
					hero.Inventory.Add((record, Math.Max(1, count)));
				}
			}
		}

		if (byHash.TryGetValue(Fnv1("AppearanceModifierManagerComponent"), out Node? modifiers))
		{
			foreach (Node modifier in modifiers.All("Modifier"))
			{
				if (modifier.Child("InventoryIndex")?.Text is int index && ItemAtUnit(hero.Inventory, index) is uint record)
				{
					hero.Worn.Add(record);
				}
			}
		}

		if (byHash.TryGetValue(Fnv1("WeaponSetManagerComponent"), out Node? weapons))
		{
			hero.Melee = weapons.Child("LastMeleeWeaponID")?.Text is uint m ? m : 0u;
			hero.Ranged = weapons.Child("LastRangedWeaponID")?.Text is uint g ? g : 0u;
		}

		if (byHash.TryGetValue(Fnv1("StatsComponent"), out Node? stats))
		{
			hero.ChapterProgress = stats.Child("ChapterProgress")?.Text is float chapter ? chapter : 0f;
			hero.Morality = stats.Child("Morality")?.Text is float mo ? mo : hero.Morality;
			hero.Purity = stats.Child("Purity")?.Text is float pu ? pu : hero.Purity;
			hero.Renown = stats.Child("Renown")?.Text is int re ? re : hero.Renown;
			hero.Tan = stats.Child("TanPercentage")?.Text is float ta ? ta : 0f;
			hero.Fat = stats.Child("FatPercentage")?.Text is float fa ? fa : 0f;
			hero.Height = stats.Child("HeightPercentage")?.Text is float he ? he : 0f;
			hero.Strength = stats.Child("StrengthPercentage")?.Text is float st ? st : 0f;
			hero.Skill = stats.Child("SkillPercentage")?.Text is float sk ? sk : 0f;
			hero.Will = stats.Child("WillPercentage")?.Text is float wi ? wi : 0f;
			foreach (Node ability in stats.All("HeroAbility"))
			{
				if (ability.Child("Index")?.Text is int index && ability.Child("Level")?.Text is int level &&
					index >= 0 && index < hero.AbilityLevels.Length)
				{
					hero.AbilityLevels[index] = level;
				}
			}
			hero.Title = stats.Child("TitleRecordID")?.Text is uint title ? title : 0u;
			foreach (Node titleNode in stats.All("UnlockedTitleRecordName"))
				if (titleNode.Child("Name")?.Text is string titleName) hero.UnlockedTitles.Add(titleName);
			foreach (Node titleNode in stats.All("FreeToBuyTitleRecordName"))
				if (titleNode.Child("Name")?.Text is string titleName) hero.FreeTitles.Add(titleName);
		}

		if (byHash.TryGetValue(Fnv1("ExperienceComponent"), out Node? experience))
		{
			string[] types = { "General", "Strength", "Skill", "Will" };
			for (int i = 0; i < types.Length; i++)
			{
				if (experience.Child(types[i] + "Exp")?.Text is float value) hero.Experience[i] = value;
				if (experience.Child("Total" + types[i] + "ExpEarned")?.Text is float total) hero.TotalExperience[i] = total;
			}
		}

		if (byHash.TryGetValue(Fnv1("ExpressionPerformerComponent"), out Node? expressions))
			foreach (Node info in expressions.All("ExpressionInfo"))
				if (info.Child("Expression") is { } expression && expression.Child("Locked")?.Text is false &&
					expression.Child("RecordId")?.Text is uint record && record != 0)
					hero.UnlockedExpressions.Add(record);

		if (byHash.TryGetValue(Fnv1("SpellManagerComponent"), out Node? spells))
			for (int direction = 0; direction < 2; direction++)
				for (int slot = 0; slot < 5; slot++)
					if (spells.Child($"AssignedSpells{direction}_{slot}")?.Text is int spell)
						hero.AssignedSpells.Add((direction, slot, spell));

		if (byHash.TryGetValue(Fnv1("GraphicAppearanceMorphComponent"), out Node? morph))
		{
			hero.Face = morph.Child("CharacterUID")?.Text is uint face ? face : 0u;
		}

		if (byHash.TryGetValue(Fnv1("QuestTrackerComponent"), out Node? quests))
		{
			foreach (Node quest in quests.All("Quest"))
			{
				Node? info = quest.Child("QuestInfo");
				if (info?.Child("Completed")?.Text is true && info.Child("QuestName")?.Text is string name)
					hero.CompletedQuests.Add(name);
			}
		}

		return true;
	}

	private static InventoryInstance? ReadInventoryInstance(Node item)
	{
		uint declared = Unsigned(item.Child("NumInstanceData"), 0);
		var data = new InventoryInstance();
		int found = 0;
		foreach (Node instance in item.All("InstanceData"))
		{
			found++;
			switch (Unsigned(instance.Child("InstanceType")))
			{
				case 1 when data.Augmentations is null:
					data.Augmentations = new();
					foreach (Node augment in instance.All("Augmentation"))
						data.Augmentations.Add(new(Unsigned(augment.Child("Primary")), Unsigned(augment.Child("Secondary"))));
					break;
				case 2 when data.Dyes is null:
					data.Dyes = new Dye[2];
					for (int i = 0; i < 2; i++)
					{
						Node colour = instance.Child("Colour" + (i + 1)) ?? throw new InvalidDataException("Missing item dye colour.");
						if (instance.Child("Brightness" + (i + 1))?.Text is not float brightness || !float.IsFinite(brightness))
							throw new InvalidDataException("Invalid item dye brightness.");
						data.Dyes[i] = new(checked((byte)Unsigned(colour.Child("Red"))), checked((byte)Unsigned(colour.Child("Green"))),
							checked((byte)Unsigned(colour.Child("Blue"))), brightness);
					}
					break;
				default:
					data.Unsupported = true;
					break;
			}
		}
		if (found != declared) throw new InvalidDataException("The saved item instance count does not match its data.");
		return found == 0 ? null : data;
	}

	private static uint Unsigned(Node? node, uint? fallback = null) => node?.Text switch
	{
		uint value => value,
		int value when value >= 0 => (uint)value,
		null when fallback.HasValue => fallback.Value,
		_ => throw new InvalidDataException("Invalid saved inventory value.")
	};

	private static uint? ItemAtUnit(List<(uint Record, int Count)> inventory, int unit)
	{
		int accumulated = 0;
		foreach ((uint record, int count) in inventory)
		{
			if (unit < accumulated + count)
			{
				return record;
			}

			accumulated += count;
		}

		return null;
	}
}
