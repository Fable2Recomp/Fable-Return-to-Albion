using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// Menu-entry child sources: the template keeps the shape, a named source supplies the rows.
/// `capture` = spawn-capture rows whose parent is the entry's name; `capture:&lt;name&gt;` =
/// the same under another parent name; anything else supplies nothing and warns once.
/// <see cref="Register"/> adds providers without this file knowing them.
/// </summary>
public static class Engine_UiMenuSources
{
	/// <summary>One row a source supplies: what a spawn states, plus whether it commits by identity.</summary>
	/// <param name="Committable">True when the name/id pair is the guest's own; invented rows dispatch by name or command.</param>
	public readonly record struct Row(
		string Name,
		string Label,
		string Icon,
		int EventType,
		uint Id,
		int Rating,
		bool Committable);

	/// <summary>A provider: given the source's argument, the rows it supplies.</summary>
	public delegate IReadOnlyList<Row> Provider(string argument);

	private static readonly Dictionary<string, Provider> Providers =
		new(System.StringComparer.OrdinalIgnoreCase)
		{
			["capture"] = CaptureRows,
			["spawn"] = SpawnRows,
		};

	private static readonly HashSet<string> Unknown = new(System.StringComparer.OrdinalIgnoreCase);

	/// <summary>Adds or replaces a source; the name is the `source` field before the colon, the rest is the argument.</summary>
	public static void Register(string name, Provider provider) => Providers[name] = provider;

	private static readonly Row[] None = System.Array.Empty<Row>();

	/// <summary>Rows for one entry's source; <paramref name="entryName"/> is the default argument.</summary>
	public static IReadOnlyList<Row> Rows(string source, string entryName)
	{
		if (source.Length == 0)
		{
			return None;
		}

		int colon = source.IndexOf(':');
		string name = colon < 0 ? source : source[..colon];
		string argument = colon < 0 || colon + 1 >= source.Length
			? entryName
			: source[(colon + 1)..];

		if (Providers.TryGetValue(name, out Provider? provider))
		{
			return provider(argument);
		}

		// Once per unknown name; a silent empty category is indistinguishable from no stock.
		if (Unknown.Add(name))
		{
			Godot.GD.PushWarning($"[menu-source] '{name}' is not a registered source — " +
				$"'{source}' supplies nothing. Registered: {string.Join(", ", Providers.Keys)}");
		}

		return None;
	}

	/// <summary>
	/// The presentation the `capture` source describes, or 0 for none; set per sample by the
	/// owning drive. Only says whether a capture-sourced menu is live.
	/// </summary>
	public static uint StatedPresentation { get; set; }

	/// <summary>`capture`: the children the guest states for <paramref name="parent"/> (raw capture, no hold).</summary>
	private static IReadOnlyList<Row> CaptureRows(string parent) =>
		SpawnRows(parent, Engine_UiSource.Current.GetMenuRows(out _));

	/// <summary>The last population seen whole, held across the partial window.</summary>
	private static List<Engine_UiModel.MenuRow>? _wholeSpawn;

	/// <summary>
	/// `spawn`: children of <paramref name="parent"/> from the spawn capture. Every row reaches
	/// `sub_826CE8D0` with `String1` (name), `String2` (resolved label), `String4` (parent),
	/// `(EventType, id)` and rating; spawn order is preserved.
	/// </summary>
	private static IReadOnlyList<Row> SpawnRows(string parent)
	{
		List<Engine_UiModel.MenuRow> spawned = SpawnedRows();

		// `DeleteAllSpawnedMenuItems` clears the capture before every repopulation with no end
		// marker; the previous whole population is held so the partial window is never read as
		// an empty menu (population and consumption are synchronous in the original).
		return SpawnRows(parent, spawned);
	}

	/// <summary>Forgets the held population — a screen change, or the menu going away.</summary>
	public static void ForgetSpawn() => _wholeSpawn = null;

	/// <summary>The captured rows; the last whole population while one is in flight. The one accessor for capture consumers.</summary>
	public static List<Engine_UiModel.MenuRow> SpawnedRows()
	{
		List<Engine_UiModel.MenuRow> spawned = Engine_UiSource.Current.GetMenuRows(out _);
		if (Engine_UiSource.Current.MenuPopulating())
		{
			return _wholeSpawn ?? spawned;
		}

		if (spawned.Count != 0)
		{
			_wholeSpawn = spawned;
		}

		return spawned;
	}

	private static IReadOnlyList<Row> SpawnRows(
		string parent, List<Engine_UiModel.MenuRow> spawned)
	{
		var rows = new List<Row>();
		foreach (Engine_UiModel.MenuRow captured in spawned)
		{
			if (captured.Name.Length == 0 ||
				!string.Equals(captured.Parent, parent, System.StringComparison.Ordinal))
			{
				continue;
			}

			rows.Add(new Row(
				captured.Name,
				captured.Label.Length != 0 ? captured.Label : captured.Name,
				Icon: string.Empty,
				captured.EventType,
				captured.Id,
				captured.Rating,
				Committable: true));
		}

		return rows;
	}
}
