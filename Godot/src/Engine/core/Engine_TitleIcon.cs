namespace Refii.Core;

using Godot;

/// <summary>
/// The game window's icon: the running title's own dashboard image — the PNG at image id 0x8000
/// of the title's XDBF, the one the Xbox 360 dashboard shows (Fable 2's kneeling hero, Fable 3's
/// crowned III). Taken from the XDBF the achievements already parse
/// (<see cref="Refii.Achievements.Engine_Achievements"/>), so both titles get it the same way and
/// it comes from the player's own install each run: nothing is shipped or cached. The launcher
/// keeps its own icon.
/// </summary>
public static class Engine_TitleIcon
{
	/// <summary>The XDBF image id of a title's own image.</summary>
	public const uint XdbfTitleImageId = 0x8000u;

	/// <summary>Shows the title image <paramref name="database"/> carries; a title that ships none leaves the icon as it is.</summary>
	public static void Adopt(Refii.Achievements.Engine_XdbfDatabase database)
	{
		if (database.Image(XdbfTitleImageId) is not { Length: > 0 } png)
		{
			GD.Print("[title-icon] the XDBF carries no title image; the icon is left as it is");
			return;
		}

		var image = new Image();
		if (image.LoadPngFromBuffer(png) != Error.Ok || image.IsEmpty())
		{
			GD.PushWarning("[title-icon] the XDBF title image is not a readable PNG");
			return;
		}

		DisplayServer.SetIcon(image);
		GD.Print($"[title-icon] {Engine_Title.Active.Name}: the XDBF title image ({image.GetWidth()}x{image.GetHeight()}) is the window icon");
	}
}
