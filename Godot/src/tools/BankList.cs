using System;
using System.Linq;
using Godot;
using Refii.Bnk;
using Refii.Core;

namespace Refii.Launcher.Tools;

/// <summary>
/// Lists what is in the mounted banks, and optionally shows a text entry's shape.
///
/// The counterpart to <see cref="BankExtract"/>, which can only fetch a file you can already
/// name. Finding out what is THERE needed a bank browser or a guess, and guessing at bank
/// paths is how a synthesised name ends up in an index — see the eight prop models that
/// resolved to `dotXSI` paths present in no bank at all.
///
/// Nothing is written. `REFII_LIST` filters by substring; `REFII_LIST_HEAD` additionally
/// prints the first N lines of each match, for entries that are text. That reads the shipped
/// data in place, which is the same thing the game does — extracted game data is derived and
/// not redistributable, so it stays out of the repo and out of any build.
/// </summary>
public partial class BankList : Node
{
	public override void _Ready()
	{
		string want = OS.GetEnvironment("REFII_LIST");
		string dataRoot = Engine_GameData.Resolve();
		if (dataRoot.Length == 0)
		{
			GD.Print("[bank-list] no game data.");
			GetTree().Quit(1);
			return;
		}

		using BnkFileSystem fs = BnkFileSystem.Mount(dataRoot);
		// Comma-separated terms, ALL of which must match. One substring cannot express
		// "the gui scripts" when the folder also holds the presentations they belong to.
		string[] terms = want.Split(',', StringSplitOptions.RemoveEmptyEntries
			| StringSplitOptions.TrimEntries);

		BnkFile[] matches = fs.EnumerateAllFiles()
			.Where(f => terms.Length == 0 || terms.All(
				t => f.VirtualPath.Contains(t, StringComparison.OrdinalIgnoreCase)))
			.OrderBy(f => f.VirtualPath, StringComparer.OrdinalIgnoreCase)
			.ToArray();

		GD.Print($"[bank-list] {matches.Length} entr(ies) matching '{want}'");

		// Sorted by SIZE when asked, because for compiled-script banks the size is the
		// signal: the shipped .lua entries are Lua 5.1 bytecode and most are tiny per-element
		// behaviour stubs, so the few large chunks are where the actual logic lives.
		bool bySize = OS.GetEnvironment("REFII_LIST_BY_SIZE") == "1";
		if (bySize)
		{
			var sized = matches
				.Select(f => (f.VirtualPath, Length: f.ReadAllBytes().Length))
				.OrderByDescending(e => e.Length)
				.Take(60);

			foreach ((string path, int length) in sized)
			{
				GD.Print($"    {length,9:N0}  {path}");
			}
		}
		else
		{
			foreach (BnkFile file in matches.Take(400))
			{
				GD.Print($"    {file.VirtualPath}");
			}
		}

		if (!int.TryParse(OS.GetEnvironment("REFII_LIST_HEAD"), out int head) || head <= 0)
		{
			GetTree().Quit();
			return;
		}

		// The shape of one entry, not its contents: enough to tell a Lua source file from
		// compiled bytecode and to see how a script is organised, without transcribing it.
		foreach (BnkFile file in matches.Take(4))
		{
			byte[] bytes = file.ReadAllBytes();
			bool binary = bytes.Take(256).Any(b => b == 0);
			GD.Print($"\n=== {file.VirtualPath} ({bytes.Length:N0} bytes, " +
				$"{(binary ? "BINARY" : "text")}) ===");

			if (binary)
			{
				continue;
			}

			string[] lines = System.Text.Encoding.UTF8.GetString(bytes)
				.Split('\n', StringSplitOptions.None);
			foreach (string line in lines.Take(head))
			{
				GD.Print($"    {line.TrimEnd('\r')}");
			}
		}

		GetTree().Quit();
	}
}
