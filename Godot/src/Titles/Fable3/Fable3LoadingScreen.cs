namespace Refii.Titles.Fable3;

using Godot;
using Refii.Host;

/// <summary>
/// Fable 3's loading screen: the art the guest chose, at the guest's own cover opacity.
/// </summary>
/// <remarks>
/// A C++ quad screen in the guest, not a presentation, so the two statements its bridge reads are
/// the whole interface (Fable3/src/hostbridge/src/level_feed.cpp, from the table
/// Fable3/src/hooks/src/loading_screen_scene.cpp states at the screen's create, destroy, update
/// and close): the screen and its fade (+0xD0 as its update leaves it, rising as it comes up and
/// falling after the close), and the art name the guest picked (stated at its decision
/// sub_823030B0, e.g. `qc010_opening.tex`). The art lives under
/// `art/gui/loadingscreens/`; a bare name is looked up there, any other spelling as stated.
/// </remarks>
public partial class Fable3LoadingScreen : Control
{
	private const string ArtFolder = "art/gui/loadingscreens/";

	private ColorRect _backdrop = null!;
	private TextureRect _art = null!;
	private string _loaded = string.Empty;

	public override void _Ready()
	{
		SetAnchorsPreset(LayoutPreset.FullRect);
		MouseFilter = MouseFilterEnum.Ignore;
		Visible = false;

		_backdrop = new ColorRect { Color = Colors.Black, MouseFilter = MouseFilterEnum.Ignore };
		_backdrop.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(_backdrop);

		_art = new TextureRect
		{
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
			MouseFilter = MouseFilterEnum.Ignore,
		};
		_art.SetAnchorsPreset(LayoutPreset.FullRect);
		AddChild(_art);
	}

	public override void _Process(double delta)
	{
		if (HostBridge.GetLoadingScreen() is not { Screen: not 0u } loading)
		{
			Visible = false;
			return;
		}

		if (HostBridge.GetLoadingScreenArt() is { Name.Length: > 0 } art &&
			!string.Equals(art.Name, _loaded, System.StringComparison.OrdinalIgnoreCase))
		{
			_loaded = art.Name;
			_art.Texture = Resolve(art.Name);
			GD.Print(_art.Texture is null
				? $"[fable3-loading] the guest picked '{art.Name}' (kind {art.Type}) and it did not resolve"
				: $"[fable3-loading] showing '{art.Name}' (kind {art.Type})");
		}

		float cover = Mathf.Clamp(loading.Fade, 0f, 1f);
		Modulate = new Color(1f, 1f, 1f, cover);
		Visible = cover > 0f;
	}

	private static Texture2D? Resolve(string stated)
	{
		string path = stated.Replace('\\', '/').ToLowerInvariant();
		return Fable3Art.Load(path) ?? (path.Contains('/') ? null : Fable3Art.Load(ArtFolder + path));
	}
}
