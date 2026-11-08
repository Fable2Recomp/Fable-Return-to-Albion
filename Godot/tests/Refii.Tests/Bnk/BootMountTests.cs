namespace Refii.Tests.Bnk;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Bnk;
using Refii.Core;
using Refii.Textures;
using Refii.Titles.Fable2.PubGames;
using Refii.Ui;

/// <summary>
/// What start-up does with one mount instead of two: the UI host's lists from the game's mount
/// (mods included) come out as its own mount's did, the index's hashes do not need a string per
/// spelling, and a pub game's mesh path carries what the loader needs without a parsed shape list.
/// </summary>
[TestSuite]
public class BootMountTests
{
	private static string NewTempDir()
	{
		string dir = Path.Combine(Refii.Tests.TestTemp.Root, "refii_boot_mount_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}

	private static void WriteLoose(string root, string relative, byte[] bytes)
	{
		string path = Path.Combine(root, relative);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllBytes(path, bytes);
	}

	[TestCase]
	public void ShippedListOfTheGamesMountIsTheHostsOwnList()
	{
		string dir = NewTempDir();
		try
		{
			string game = Path.Combine(dir, "game");
			Directory.CreateDirectory(game);
			File.WriteAllBytes(Path.Combine(game, "ui.bnk"), BnkWriter.Write(new[]
			{
				new BnkWriterEntry { Path = "art/gui/gameface/menu.bgf", Bytes = new byte[] { 1 } },
				new BnkWriterEntry { Path = "art/gui/menu.tex", Bytes = new byte[] { 2 } },
				new BnkWriterEntry { Path = "art/gui/shared.tex", Bytes = new byte[] { 3 } },
			}));

			// A mod that replaces one shipped file, under the data/ spelling too, and adds its own.
			string mod = Path.Combine(dir, "mod");
			WriteLoose(mod, "art/gui/shared.tex", new byte[] { 4, 4 });
			WriteLoose(mod, "art/gui/gameface/mine.bgf", new byte[] { 5 });
			WriteLoose(mod, "art/gui/mine.tex", new byte[] { 6 });

			using BnkFileSystem own = BnkFileSystem.Mount(game);
			using BnkFileSystem shared = BnkFileSystem.Mount(game, overlays: new[] { ("mod", mod) });

			var ownTextures = new List<string>();
			var ownScreens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			Engine_UiPresentationHost.ListBanks(own, false, ownTextures, ownScreens);
			var sharedTextures = new List<string>();
			var sharedScreens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			Engine_UiPresentationHost.ListBanks(shared, true, sharedTextures, sharedScreens);

			AssertThat(sharedTextures.OrderBy(p => p, StringComparer.Ordinal).ToArray())
				.IsEqual(ownTextures.OrderBy(p => p, StringComparer.Ordinal).ToArray());
			AssertThat(sharedScreens.Count).IsEqual(1);
			AssertThat(sharedScreens["menu"]).IsEqual(ownScreens["menu"]);

			// And every file of the host's own mount is served by the game's.
			foreach (BnkFile file in own.EnumerateAllFiles())
			{
				AssertThat(shared.TryGetFile(file.VirtualPath, out _)).IsTrue();
			}
		}
		finally
		{
			Directory.Delete(dir, true);
		}
	}

	[TestCase]
	public void IndexSpellingsAreTheGuestsWithoutTheStrings()
	{
		var cases = new (string Path, string Spelling)[]
		{
			("art/gui/menu.tex", "art\\gui\\menu.tex"),
			("Art\\GUI/Menu.TEX", "art\\gui\\menu.tex"),
			("data/art/x.tex", "data\\art\\x.tex"),
			("game:/art/x.tex", "art\\x.tex"),
			("game:art/x.tex", "art\\x.tex"),
			("GAME:\\art\\x.tex", "art\\x.tex"),
			("/art/x.tex", "art\\x.tex"),
			("//", string.Empty),
			("ÄrT/é.tex", "Ärt\\é.tex"),
		};

		foreach ((string path, string spelling) in cases)
		{
			Span<char> buffer = new char[path.Length];
			AssertThat(AssetHashIndex.GuestSpelling(path, buffer).ToString()).IsEqual(spelling);
		}
	}

	[TestCase]
	public void HashOfSpanIsHashOfString()
	{
		foreach (string value in new[] { string.Empty, "art\\gui\\menu.tex", "é", "Ärt\\日本語.tex", new string('a', 500) })
		{
			AssertThat(Engine_GameHash.Fnv1(value.AsSpan())).IsEqual(Engine_GameHash.Fnv1(value));
		}
	}

	[TestCase]
	public void IndexAnswersEverySpellingOfALongPath()
	{
		// Past the shared stack buffer, and the two spellings the guest may hash.
		string longName = "art/" + new string('n', 300) + ".tex";
		var entry = new BnkEntry { RelativePath = longName };
		AssetHashIndex.Build(new[] { new BnkFile("x", entry, longName, _ => Array.Empty<byte>()) });
		try
		{
			string guest = longName.Replace('/', '\\');
			AssertThat(AssetHashIndex.PathForHash(AssetHashIndex.Hash(guest))).IsEqual(longName);
			AssertThat(AssetHashIndex.PathForHash(AssetHashIndex.Hash("data\\" + guest))).IsEqual(longName);
		}
		finally
		{
			AssetHashIndex.Clear();
		}
	}

	[TestCase]
	[RequireGodotRuntime]
	public void PeekReadsTheSizeOfWhatLoadWouldServe()
	{
		string dir = NewTempDir();
		string? was = Environment.GetEnvironmentVariable(Engine_AppRoot.EnvironmentVariable);
		Environment.SetEnvironmentVariable(Engine_AppRoot.EnvironmentVariable, dir);
		Engine_AppRoot.Invalidate();
		try
		{
			string one = Engine_TexCache.BaseKey(new byte[] { 1, 2, 3 });
			string cube = Engine_TexCache.BaseKey(new byte[] { 4, 5, 6 });
			AssertThat(Engine_TexCache.TryPeek(one, out _, out _)).IsFalse();

			Godot.Image image = Godot.Image.CreateEmpty(12, 20, false, Godot.Image.Format.Rgba8);
			Engine_TexCache.Store(one, image, false);
			AssertThat(Engine_TexCache.TryPeek(one, out int width, out int height)).IsTrue();
			AssertThat(width).IsEqual(12);
			AssertThat(height).IsEqual(20);
			Godot.Image? loaded = Engine_TexCache.TryLoad(one, out _);
			AssertThat(loaded!.GetWidth()).IsEqual(width);
			AssertThat(loaded.GetHeight()).IsEqual(height);

			// A cube is not what the 2D load serves either.
			Godot.Image face = Godot.Image.CreateEmpty(4, 4, false, Godot.Image.Format.Rgba8);
			Engine_TexCache.Store(cube, new[] { face, face, face, face, face, face }, false);
			AssertThat(Engine_TexCache.TryLoad(cube, out _) is null).IsTrue();
			AssertThat(Engine_TexCache.TryPeek(cube, out _, out _)).IsFalse();
		}
		finally
		{
			Environment.SetEnvironmentVariable(Engine_AppRoot.EnvironmentVariable, was);
			Engine_AppRoot.Invalidate();
			Directory.Delete(dir, true);
		}
	}

	[TestCase]
	public void MeshPathReadsBack()
	{
		AssertThat(Engine_SwfAssets.TryParseMeshPath(
			"FlashGames/Keystone/shapes/120_f3.mesh", out string folder, out int shape, out int fill)).IsTrue();
		AssertThat(folder).IsEqual("Keystone");
		AssertThat(shape).IsEqual(120);
		AssertThat(fill).IsEqual(3);

		AssertThat(Engine_SwfAssets.TryParseMeshPath("flashgames/magedice/SHAPES/7_F1.MESH", out folder, out shape, out fill)).IsTrue();
		AssertThat(folder).IsEqual("magedice");
		AssertThat(shape).IsEqual(7);
		AssertThat(fill).IsEqual(1);

		foreach (string bad in new[]
		{
			"FlashGames/Keystone/shapes/120.mesh",
			"FlashGames/Keystone/shapes/x_f3.mesh",
			"FlashGames/Keystone/shapes/120_f.mesh",
			"FlashGames/Keystone/shapes/120_f-1.mesh",
			"FlashGames/Keystone/120_f3.mesh",
			"FlashGames//shapes/120_f3.mesh",
			"Other/Keystone/shapes/120_f3.mesh",
			"FlashGames/Keystone/shapes/120_f3.png",
		})
		{
			AssertThat(Engine_SwfAssets.TryParseMeshPath(bad, out _, out _, out _)).IsFalse();
		}
	}
}
