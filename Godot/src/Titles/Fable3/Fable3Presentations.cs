namespace Refii.Titles.Fable3;

using Refii.Ui;

/// <summary>
/// The shared presentation host over Fable 3's banks, standing no screen of its own: Fable 3's
/// owned surfaces read their `.bgf` documents, pattern books, theme and textures from it, as
/// Fable 2's UI reads its own (art/gui/gameface/*.bgf in guiscripts.bnk, GUI art in
/// art/gui/gui_textures.bnk and art/gui_memmount/…, every name hashed as the shared table does).
/// </summary>
public partial class Fable3Presentations : Engine_UiPresentationHost
{
	/// <summary>Raised once the banks are indexed and documents can be read.</summary>
	public event System.Action? Loaded;

	protected override void OnLoaded() => Loaded?.Invoke();
}
