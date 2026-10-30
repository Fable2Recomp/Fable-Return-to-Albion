namespace Refii.Titles.Fable3;

using Godot;
using Refii.Text;
using Refii.Ui;

/// <summary>
/// Fable 3's string book, in the culture the guest runs in, for everything the shell draws.
/// </summary>
/// <remarks>
/// THE GUEST RESOLVES A TAG BY ITS FNV-1. Its text manager (`*(*(0x83556540+8)+144)`) is a map at
/// +28 keyed by the 32-bit hash (`sub_8230DA98`: `lwz` the key, `sub_8230EE58(v, mgr+28, &key)`,
/// compared against the end node at +36), built from `text/book.babel` of the culture folder the
/// language mount names (`sub_822E7C60`: "text/book.babel" and "patch_text/book.babel" mounted
/// under "text"). Engine_BabelDatabase is that table: FNV-1 keys, so Fable 2's reader answers
/// Fable 3's tags — `TEXT_LEVEL_ALBION_BOWERSTONECASTLE` (0x434ECABC) is "Bowerstone Castle" in
/// the shipped en-uk book.
///
/// Fable 2's UI hands its book to the shared consumers when its presentation host loads it
/// (GameUi, `StringsChanged`). Fable 3 has no presentation host, so this does the same handover:
/// without it <see cref="Refii.Game.GameWhereabouts"/> never had a book and every place name
/// showed as its raw tag. Read through the mount first (mod overlays), from disk until the mount
/// is up, and again whenever the culture or the mount changes.
/// </remarks>
public partial class Fable3Strings : Node
{
	/// <summary>The book in force, read now if the culture or the mount changed; null when none reads.</summary>
	public static Engine_BabelDatabase? Current
	{
		get
		{
			Refresh();
			return _strings;
		}
	}

	private static Engine_BabelDatabase? _strings;
	private static string _culture = string.Empty;
	private static bool _mounted;
	private static bool _read;

	public override void _Process(double delta) => Refresh();

	private static void Refresh()
	{
		bool mounted = Refii.Assets.Engine_GameAssets.Instance is { IsMounted: true };
		if (_read && _culture == Engine_GameCulture.Current && _mounted == mounted)
		{
			return;
		}

		_read = true;
		_culture = Engine_GameCulture.Current;
		_mounted = mounted;
		Engine_BabelDatabase? strings = Engine_UiPresentationHost.ReadStrings(
			Refii.Core.Engine_GameData.Resolve(), diskFallback: true, out string source);
		if (strings is null)
		{
			return;
		}

		_strings = strings;
		Refii.Game.GameWhereabouts.Strings = strings;
		GD.Print($"[fable3-strings] {strings.Count} strings in {_culture} {source}");
	}
}
