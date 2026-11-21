namespace Refii.Titles.Fable2;

using System.Collections.Generic;
using System.Globalization;
using Refii.Ui;

/// <summary>
/// The pause map's legend as the guest STATES it: SetMapKey (TU1 `sub_826A3AC8`, disc
/// `sub_8240FE80`; hooks/gui_headless_presentation.cpp), posted as `@PauseMap.Key` with the
/// presentation, the row, the label's element path and text (the guest's lookup of the row's tag),
/// the icon's element path and the resource hash it stores. Only a row the call wrote (both
/// elements resolved) is stated. The fill `sub_826A3658` (disc `sub_8240FA10`) states rows from 1
/// upward and advances only on a written row, so a row-1 statement starts a new legend; the rows
/// past the last are hidden by the fill's own show/hide, which stays the guest's.
/// </summary>
public sealed class Engine_UiPauseMapKey
{
	private readonly Dictionary<string, string> _texts = new(System.StringComparer.Ordinal);
	private readonly Dictionary<string, uint> _textures = new(System.StringComparer.Ordinal);
	private uint _presentation;

	/// <summary>`@PauseMap.Key`: presentation (hex), row, label path, image path, text, hash (hex), tag.</summary>
	public void State(Engine_UiModel.GuiNativeCall call)
	{
		if (!uint.TryParse(call.Argument(0).Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
				out uint presentation) ||
			!int.TryParse(call.Argument(1).Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int row) ||
			!uint.TryParse(call.Argument(5).Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture,
				out uint hash) ||
			call.Argument(2).Text is not { Length: > 0 } label ||
			call.Argument(3).Text is not { Length: > 0 } image)
		{
			return;
		}

		if (presentation != _presentation || row == 1)
		{
			_presentation = presentation;
			_texts.Clear();
			_textures.Clear();
		}

		_texts[label] = call.Argument(4).Text ?? string.Empty;
		_textures[image] = hash;
	}

	/// <summary>The stated labels by element path for this presentation's legend, or null.</summary>
	public IReadOnlyDictionary<string, string>? Texts(uint presentation) =>
		presentation != 0u && presentation == _presentation && _texts.Count != 0 ? _texts : null;

	/// <summary>The stated icons by element path for this presentation's legend, or null.</summary>
	public IReadOnlyDictionary<string, uint>? Textures(uint presentation) =>
		presentation != 0u && presentation == _presentation && _textures.Count != 0 ? _textures : null;
}
