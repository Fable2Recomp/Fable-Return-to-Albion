namespace Refii.Tests.Save;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Save;

/// <summary>
/// The contract the native content manager is told on its command line and Godot reads by: a title
/// id picks a game folder in the profile, the XUID picks nothing, Fable 3 reads Fable 2's saves from
/// the same folder Fable 2 writes them, and the lookups never cross a title or a profile.
/// </summary>
[TestSuite]
public class SaveLayoutTests
{
	private const uint Fable2 = 0x4D5307F1;
	private const uint Fable3 = 0x4D5308D6;

	[TestCase]
	public void ATitleIdPicksItsGameFolder()
	{
		AssertThat(Engine_SaveLayout.GameFolder(Fable2)).IsEqual("Fable 2");
		AssertThat(Engine_SaveLayout.GameFolder(Fable3)).IsEqual("Fable 3");
		AssertThat(Engine_SaveLayout.GameFolder(0x5841091D)).IsEqual("5841091D");
		AssertThat(Engine_SaveLayout.TitleFoldersArgument()).IsEqual("4D5307F1=Fable 2;4D5308D6=Fable 3");
		AssertThat(Engine_SaveLayout.Root(Path.Combine("x", "assets"))).IsEqual(Path.Combine("x", "assets", "saves"));

		string profile = Path.Combine("saves", "Patient Hedgehog");
		AssertThat(Engine_SaveLayout.SaveGames(profile, Fable2)).IsEqual(Path.Combine(profile, "Fable 2", "00000001"));
		AssertThat(Engine_SaveLayout.Headers(profile, Fable3)).IsEqual(Path.Combine(profile, "Fable 3", "Headers", "00000001"));
		AssertThat(Engine_SaveLayout.Settings(profile, Fable2)).IsEqual(Path.Combine(profile, "Fable 2", "profile"));
		AssertThat(Engine_SaveLayout.Identity(profile)).IsEqual(Path.Combine(profile, "rexnet"));
		AssertThat(Engine_SaveLayout.OlderSaves(profile, Fable2)).IsEqual(Path.Combine(profile, "Fable 2", "Older saves"));
	}

	[TestCase]
	public void FolderNamesAreMadeSafe()
	{
		AssertThat(Engine_SaveLayout.SanitizeFolderName("Patient Hedgehog")).IsEqual("Patient Hedgehog");
		AssertThat(Engine_SaveLayout.SanitizeFolderName("a/b\\c:d")).IsEqual("a b c d");
		AssertThat(Engine_SaveLayout.SanitizeFolderName("..")).IsEqual(string.Empty);
		AssertThat(Engine_SaveLayout.SanitizeFolderName(".hidden. ")).IsEqual("hidden");
		AssertThat(Engine_SaveLayout.SanitizeFolderName("  ")).IsEqual(string.Empty);
		AssertThat(Engine_SaveLayout.SanitizeFolderName(null)).IsEqual(string.Empty);
	}

	[TestCase]
	public void ASaveIsFoundInItsOwnProfileAndTitleOnly()
	{
		string root = Path.Combine(Refii.Tests.TestTemp.Root, "refii-layout-" + Guid.NewGuid().ToString("N"));
		try
		{
			string mine = Path.Combine(root, "Mine"), other = Path.Combine(root, "Other");
			Directory.CreateDirectory(Path.Combine(mine, "Fable 2", "00000001", "Hero000"));
			File.WriteAllText(Path.Combine(mine, "Fable 2", "00000001", "Hero000", "mainsave.bin"), "f2");
			Directory.CreateDirectory(Path.Combine(mine, "Fable 3", "00000001", "Hero001"));
			File.WriteAllText(Path.Combine(mine, "Fable 3", "00000001", "Hero001", "mainsave.bin"), "f3");
			Directory.CreateDirectory(Path.Combine(other, "Fable 2", "00000001", "Hero001"));
			File.WriteAllText(Path.Combine(other, "Fable 2", "00000001", "Hero001", "mainsave.bin"), "theirs");
			Directory.CreateDirectory(Path.Combine(mine, "Fable 2", "Older saves", "x", "00000001", "Hero002"));
			File.WriteAllText(Path.Combine(mine, "Fable 2", "Older saves", "x", "00000001", "Hero002", "mainsave.bin"), "older");

			AssertThat(Engine_SaveDirectory.Find(mine, "Hero000", Fable2)).IsEqual(Path.Combine(mine, "Fable 2", "00000001", "Hero000"));
			AssertThat(Engine_SaveDirectory.Find(mine, "Hero001", Fable2)).IsNull();
			AssertThat(Engine_SaveDirectory.Find(mine, "Hero001", Fable3)).IsEqual(Path.Combine(mine, "Fable 3", "00000001", "Hero001"));
			AssertThat(Engine_SaveDirectory.Find(mine, "Hero002", Fable2)).IsNull();
			AssertThat(Engine_SaveDirectory.Find(mine, "..", Fable2)).IsNull();

			// Fable 3 enumerates Fable 2 (its alternate title 0x4D5307F1) in the same profile: the very
			// folder Fable 2 saves into, so its Fable-2-save import finds them.
			AssertThat(Engine_SaveLayout.SaveGames(mine, Fable2)).IsEqual(Path.GetDirectoryName(Engine_SaveDirectory.Find(mine, "Hero000", Fable2))!);

			// The New Game catalog reads Fable 2's slots only: never Fable 3's saves or the Older saves.
			var scan = Refii.Titles.Fable2.Engine_NewGameCatalog.Scan(new[] { Refii.Titles.Fable2.Engine_NewGameCatalog.SaveGamesOf(mine) });
			AssertThat(scan.Saves.Count + scan.Errors.Count).IsEqual(1);
			AssertThat(string.Join("\n", scan.Errors)).Contains("Hero000");
		}
		finally
		{
			if (Directory.Exists(root)) Directory.Delete(root, true);
		}
	}
}
