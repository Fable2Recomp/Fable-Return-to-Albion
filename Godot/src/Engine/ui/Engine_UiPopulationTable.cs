using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// One menu's static `PopulationTable`, as the guest's population script assigns it to
/// `g_Menu.PopulationTable` for a `MenuPopulationScript` name. A title states its own
/// (<see cref="Refii.Core.Engine_TitleGame.PopulationTable"/>).
/// </summary>
public sealed class Engine_UiPopulationTable
{
	/// <summary>One row of a static table, as `CreateMenuItem` will receive it.</summary>
	public sealed class Entry
	{
		public required string Name { get; init; }
		public required string TextTag { get; init; }
		public required string IconTexture { get; init; }
		public required Engine_UiMenuConstants.EventType EventType { get; init; }

		/// <summary>The `Parent` field — empty for a root entry.</summary>
		public string Parent { get; init; } = string.Empty;
	}

	public required string Script { get; init; }

	/// <summary>`HighlightIdx`, 1-based, or 0 where the script states none.</summary>
	public int HighlightIndex { get; init; }

	public List<Entry> Entries { get; init; } = new();
}
