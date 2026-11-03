namespace Refii.Titles.Fable3;

using System.Collections.Generic;
using Refii.Mods;
using Refii.Ui;

/// <summary>
/// The host's settings pages for Fable 3, one row each, opened from the guest's Game Settings by X
/// (<see cref="Fable3HostOptions"/>): the controller and keyboard, the dynamic sky and weather, and
/// every enabled mod's settings — the same shared pages Fable 2 lists in its Options book. A opens
/// the row's page; B on that page comes back here.
/// </summary>
public sealed class Fable3SettingsHub : Engine_UiHostOptionsPage
{
	public static Fable3SettingsHub Page { get; } = new();

	public override string Name => "Fable3Settings";

	public override string Title => "Settings";

	private readonly List<(string Title, string Command)> _rows = new();

	private void Build()
	{
		_rows.Clear();
		_rows.Add((Engine_UiControlsPage.PageTitle, Engine_UiControlsPage.OpenCommand));
		_rows.Add((Engine_UiWeatherOptions.Title, Engine_UiWeatherOptions.OpenCommand));
		foreach (ModSettingsUi.Page mod in ModSettingsUi.Pages)
		{
			_rows.Add((mod.Title, ModSettingsUi.OpenCommandPrefix + mod.Mod));
		}
	}

	public override int Count
	{
		get
		{
			if (_rows.Count == 0)
			{
				Build();
			}

			return _rows.Count;
		}
	}

	public override string RowTitle(int row) => row >= 0 && row < _rows.Count ? _rows[row].Title : string.Empty;

	public override int Choices(int row) => 1;

	public override string Caption(int row, int index) => string.Empty;

	public override int[] Read()
	{
		Build();
		return new int[_rows.Count];
	}

	/// <summary>A opens the cursor row's page; the hub stays beneath it.</summary>
	public override bool Commit(System.Func<int, int> index)
	{
		int row = Fable3HostOptions.Cursor;
		if (row >= 0 && row < _rows.Count && For(_rows[row].Command) is { } page)
		{
			Fable3HostOptions.Push(page);
		}

		return false;
	}
}
