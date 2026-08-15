using System;
using System.Collections.Generic;

using Refii.Core;
using Refii.Gdb;

namespace Refii.Text;

/// <summary>
/// Turns a level KEY into the name the game shows the player.
///
/// The guest never stores the display name. What it keeps — on the loading screen at
/// `screen+268`, and everywhere else a level is named — is the level's own key: `BWSMarket`,
/// `Caves\GargoylesCave`, `Tombs\BWSMarket\Nightmare Hollow`. `sub_8231CE98` is what turns one
/// into "Bowerstone Market", and it does it in two steps that both have to be right.
///
/// FIRST IT ASKS THE WORLD, which is a runtime map at `world+0x18` keyed by
/// <c>FNV1(lower(worldName + normalisedKey))</c>. That map is empty unless a script has written
/// to it: its only writer is the Lua binding `SetLevelDisplayName`, and the shipped scripts call
/// it exactly once — `gameflow.txt` renaming Thag's Cave for chapter 2. So the override matters,
/// but the common path is the miss.
///
/// THEN IT ASKS THE GDB, and this is the table. `LevelNameTextTags` is a record in
/// `globals.gdb` holding 76 string properties, each keyed by the FNV-1 of a normalised level key
/// and valued with a Babel tag. The tag then goes through the string database like any other.
///
/// THE TWO OBVIOUS SHIPPED TABLES ARE BOTH THE WRONG ONE, which is worth writing down because
/// both look right and one of them even produces the correct answer for Bowerstone.
/// <c>loadingscreendata.txt</c> maps the same keys to a region and an ART name and contains no
/// text tags at all. <c>fasttravellist.txt</c> does carry tags, but they are `TEXT_REGION_*` —
/// the fast-travel UI's namespace — and it maps every cave to its PARENT region, so
/// `Caves\GargoylesCave` would announce itself as Bowerstone Market.
///
/// Verified against the shipped `globals.gdb` rather than taken on report: the name-pair table
/// maps <c>FNV1("LevelNameTextTags") = 0x56A9C11A</c> to record <c>0xD1ACE1FA</c>, that record
/// carries 76 string fields, and field <c>FNV1("bwsmarket") = 0xC5A7203F</c> holds
/// <c>TEXT_LEVEL_BOWERSTONE_MARKET</c>.
/// </summary>
public sealed class Engine_LevelNames
{
	/// <summary>Where the table lives. Resolved through the bank resolver, not the file system.</summary>
	public const string GlobalsPath = "globals/globals.gdb";

	/// <summary>
	/// <c>FNV1("LevelNameTextTags")</c>, and the record that name aliases to.
	///
	/// Both are tried, in that order. The alias is an indirection in the name-pair table rather
	/// than a record hash, so a database that resolves aliases answers to the first and the
	/// shipped one answers to the second; asking for both means neither arrangement has to be
	/// assumed.
	/// </summary>
	private const uint TableNameHash = 0x56A9C11Au;

	private const uint TableRecordHash = 0xD1ACE1FAu;

	private readonly Dictionary<uint, string> _tags = new();

	/// <summary>Overrides written by the Lua binding <c>SetLevelDisplayName</c>, checked first.</summary>
	private readonly Dictionary<string, string> _overrides =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>How many entries the table gave up, for the log line that reports the load.</summary>
	public int Count => _tags.Count;

	/// <summary>Why the table did not load, or empty when it did.</summary>
	public string Failure { get; private set; } = string.Empty;

	/// <summary>
	/// Reads the table out of `globals.gdb`. Never throws: a level named by its key is a far
	/// better failure than a loading screen that does not appear.
	/// </summary>
	public static Engine_LevelNames Load(Func<string, byte[]?> read)
	{
		var names = new Engine_LevelNames();

		byte[]? bytes;
		try
		{
			bytes = read(GlobalsPath);
		}
		catch (Exception exception)
		{
			names.Failure = exception.Message;
			return names;
		}

		if (bytes is null)
		{
			names.Failure = $"{GlobalsPath} not found";
			return names;
		}

		if (GdbDatabase.TryCreate(bytes, GlobalsPath, out string error) is not { } database)
		{
			names.Failure = error;
			return names;
		}

		int index = database.IndexOfHash(TableNameHash);
		if (index < 0)
		{
			index = database.IndexOfHash(TableRecordHash);
		}

		if (index < 0)
		{
			names.Failure = "no LevelNameTextTags record";
			return names;
		}

		foreach (GdbField field in database.FieldsAt(index))
		{
			// String fields only. The record is nothing but key/tag pairs, so anything else in
			// it is a schema field rather than a level.
			if (field.Type == GdbFieldType.String && field.Text.Length > 0)
			{
				names._tags[field.Hash] = field.Text;
			}
		}

		return names;
	}

	/// <summary>
	/// Records a script override, as the Lua binding <c>SetLevelDisplayName</c> does.
	///
	/// Keyed by the RAW level key rather than by the world-qualified hash the guest uses. The
	/// difference cannot bite here: the map is world-scoped in the guest, and a host that has
	/// one world loaded at a time cannot hold two entries that would collide.
	/// </summary>
	public void Override(string levelKey, string tag) => _overrides[levelKey] = tag;

	/// <summary>
	/// The Babel TAG for a level key, or empty when the table does not name it.
	///
	/// Separate from the localisation step so a caller can tell "this level has no entry" from
	/// "the string database has no such tag" — the engine's own last resort is to display the
	/// raw tag, which is only reachable if the tag is known.
	/// </summary>
	public string TagFor(string levelKey)
	{
		if (levelKey.Length == 0)
		{
			return string.Empty;
		}

		if (_overrides.TryGetValue(levelKey, out string? overridden))
		{
			return overridden;
		}

		// The SAME hash the string database uses — the GDB keys its properties with FNV-1 too, so
		// there is one hash in this whole path and no place for two to disagree.
		return _tags.TryGetValue(Engine_GameHash.Fnv1(Normalise(levelKey)), out string? tag)
			? tag
			: string.Empty;
	}

	/// <summary>
	/// The level name as the player should see it, or the raw key when nothing names it.
	///
	/// The key is deliberately the fallback rather than an empty string: a banner reading
	/// "Loading BWSMarket" tells the player where they are going and tells us the table missed,
	/// where a blank one says nothing to either.
	/// </summary>
	public string DisplayName(string levelKey, Engine_BabelDatabase? strings)
	{
		string tag = TagFor(levelKey);
		if (tag.Length == 0)
		{
			return levelKey;
		}

		return strings is not null && strings.TryGetString(tag, out string text) && text.Length > 0
			? text
			: tag;
	}

	/// <summary>
	/// The key as the table stores it: lowercased, with separators and brackets turned into
	/// underscores.
	///
	/// `Caves\GargoylesCave` becomes `caves_gargoylescave`; `Tombs\BWSMarket\Nightmare Hollow`
	/// becomes `tombs_bwsmarket_nightmare_hollow`. Both are entries in the shipped record.
	///
	/// THE DOUBLED SEPARATORS ARE REPLACED FIRST, as whole units, which is why they are not
	/// simply folded into the single-character pass: `\\` is ONE underscore, not two, and a
	/// character-at-a-time replacement would produce a key that hashes to nothing.
	/// </summary>
	public static string Normalise(string levelKey)
	{
		string value = levelKey.ToLowerInvariant()
			.Replace("//", "_")
			.Replace("\\\\", "_");

		var built = new System.Text.StringBuilder(value.Length);
		foreach (char c in value)
		{
			built.Append(c is ' ' or '/' or '\\' or '(' or ')' ? '_' : c);
		}

		return built.ToString();
	}
}
