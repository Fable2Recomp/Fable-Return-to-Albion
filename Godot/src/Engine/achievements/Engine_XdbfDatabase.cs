using System;
using System.Collections.Generic;
using System.Text;

namespace Refii.Achievements;

/// <summary>
/// The title's XDBF resource — the section named by the title id inside `default.xex` — read in
/// place. Big-endian throughout. Layout (free60 XDBF): a 24-byte header, `EntryCount` 18-byte
/// entries `{section u16, id u64, offset u32, size u32}`, `FreeCount` 8-byte free slots, then
/// the content the entry offsets index. The achievement table is metadata entry `XACH`
/// (0x24-byte records), strings are one `XSTR` table per language keyed by language id, the
/// default language is metadata entry `XSTC`, and icons are PNG blobs in the image section keyed
/// by the record's image id.
/// </summary>
public sealed class Engine_XdbfDatabase
{
	public readonly record struct Achievement(
		uint Id, uint ImageId, uint Gamerscore, uint Flags,
		ushort LabelId, ushort DescriptionId, ushort UnachievedId);

	private const uint MagicXdbf = 0x58444246; // 'XDBF'
	private const uint MagicXstc = 0x58535443; // 'XSTC'
	private const uint MagicXstr = 0x58535452; // 'XSTR'
	private const uint MagicXach = 0x58414348; // 'XACH'

	private const ushort SectionMetadata = 1;
	private const ushort SectionImage = 2;
	private const ushort SectionStringTable = 3;

	private const ulong IdXstc = 0x58535443;
	private const ulong IdXach = 0x58414348;

	public const uint LanguageEnglish = 1;

	private readonly record struct Entry(ushort Section, ulong Id, uint Offset, uint Size);

	private readonly byte[] _bytes;
	private readonly List<Entry> _entries = new();
	private readonly int _content;
	private readonly Dictionary<uint, Dictionary<ushort, string>> _strings = new();

	public IReadOnlyList<Achievement> Achievements { get; }

	public uint DefaultLanguage { get; }

	private Engine_XdbfDatabase(byte[] bytes, int content, List<Entry> entries)
	{
		_bytes = bytes;
		_content = content;
		_entries = entries;
		DefaultLanguage = ReadDefaultLanguage();
		Achievements = ReadAchievements();
	}

	public static Engine_XdbfDatabase? TryParse(byte[]? bytes)
	{
		if (bytes is null || bytes.Length < 24 || U32(bytes, 0) != MagicXdbf)
		{
			return null;
		}

		uint entryCount = U32(bytes, 8);
		uint entryUsed = U32(bytes, 12);
		uint freeCount = U32(bytes, 16);
		long entriesAt = 24;
		long filesAt = entriesAt + (18L * entryCount);
		long content = filesAt + (8L * freeCount);
		if (content > bytes.Length || entryUsed > entryCount)
		{
			return null;
		}

		var entries = new List<Entry>((int)entryUsed);
		for (uint i = 0; i < entryUsed; i++)
		{
			int at = (int)(entriesAt + (18L * i));
			entries.Add(new Entry(U16(bytes, at), U64(bytes, at + 2), U32(bytes, at + 10), U32(bytes, at + 14)));
		}

		return new Engine_XdbfDatabase(bytes, (int)content, entries);
	}

	/// <summary>A string from the language's table, or empty when either is absent.</summary>
	public string String(uint language, ushort id)
	{
		if (!_strings.TryGetValue(language, out Dictionary<ushort, string>? table))
		{
			table = ReadStringTable(language);
			_strings[language] = table;
		}

		return table.TryGetValue(id, out string? text) ? text : string.Empty;
	}

	/// <summary>True when the language has a string table at all.</summary>
	public bool HasLanguage(uint language) => Find(SectionStringTable, language) is not null;

	/// <summary>The PNG bytes of an image entry, or null when the title ships none.</summary>
	public byte[]? Image(uint imageId)
	{
		if (Find(SectionImage, imageId) is not { } entry || !Fits(entry))
		{
			return null;
		}

		var png = new byte[entry.Size];
		Array.Copy(_bytes, _content + (int)entry.Offset, png, 0, (int)entry.Size);
		return png;
	}

	private uint ReadDefaultLanguage()
	{
		if (Find(SectionMetadata, IdXstc) is not { } entry || !Fits(entry) || entry.Size < 16)
		{
			return LanguageEnglish;
		}

		int at = _content + (int)entry.Offset;
		return U32(_bytes, at) == MagicXstc ? U32(_bytes, at + 12) : LanguageEnglish;
	}

	private List<Achievement> ReadAchievements()
	{
		var list = new List<Achievement>();
		if (Find(SectionMetadata, IdXach) is not { } entry || !Fits(entry) || entry.Size < 14)
		{
			return list;
		}

		int at = _content + (int)entry.Offset;
		if (U32(_bytes, at) != MagicXach)
		{
			return list;
		}

		int count = U16(_bytes, at + 12);
		int record = at + 14;
		int end = at + (int)entry.Size;
		for (int i = 0; i < count && record + 0x24 <= end; i++, record += 0x24)
		{
			list.Add(new Achievement(
				Id: U16(_bytes, record),
				LabelId: U16(_bytes, record + 2),
				DescriptionId: U16(_bytes, record + 4),
				UnachievedId: U16(_bytes, record + 6),
				ImageId: U32(_bytes, record + 8),
				Gamerscore: U16(_bytes, record + 12),
				Flags: U32(_bytes, record + 16)));
		}

		return list;
	}

	private Dictionary<ushort, string> ReadStringTable(uint language)
	{
		var table = new Dictionary<ushort, string>();
		if (Find(SectionStringTable, language) is not { } entry || !Fits(entry) || entry.Size < 14)
		{
			return table;
		}

		int at = _content + (int)entry.Offset;
		if (U32(_bytes, at) != MagicXstr)
		{
			return table;
		}

		int count = U16(_bytes, at + 12);
		int cursor = at + 14;
		int end = at + (int)entry.Size;
		for (int i = 0; i < count && cursor + 4 <= end; i++)
		{
			ushort id = U16(_bytes, cursor);
			int length = U16(_bytes, cursor + 2);
			cursor += 4;
			if (cursor + length > end)
			{
				break;
			}

			table[id] = Encoding.UTF8.GetString(_bytes, cursor, length);
			cursor += length;
		}

		return table;
	}

	private Entry? Find(ushort section, ulong id)
	{
		foreach (Entry entry in _entries)
		{
			if (entry.Section == section && entry.Id == id)
			{
				return entry;
			}
		}

		return null;
	}

	private bool Fits(Entry entry) =>
		(long)_content + entry.Offset + entry.Size <= _bytes.Length;

	private static ushort U16(byte[] b, int at) => (ushort)((b[at] << 8) | b[at + 1]);

	private static uint U32(byte[] b, int at) =>
		((uint)b[at] << 24) | ((uint)b[at + 1] << 16) | ((uint)b[at + 2] << 8) | b[at + 3];

	private static ulong U64(byte[] b, int at) => ((ulong)U32(b, at) << 32) | U32(b, at + 4);
}
