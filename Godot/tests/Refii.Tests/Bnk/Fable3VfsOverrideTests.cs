namespace Refii.Tests.Bnk;

using System;
using System.IO;
using System.Text;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Bnk;
using Refii.Core;

/// <summary>
/// What Fable 3's VFS (Fable3/src/vfs, Fable3/src/hooks/src/vfs_hooks.cpp) relies on the host
/// mount for. The guest's root file system keys its table by the FNV-1 of each bank name as stored
/// — lower case, backslashes, relative to <c>data\</c> — and the hook hands that hash to the host:
/// the hash index must name the mount's path for it, and the mount's file at that path must be
/// the last enabled mod's, stated an override. No Godot type is touched.
/// </summary>
[TestSuite]
public class Fable3VfsOverrideTests
{
	private static string NewTempDir()
	{
		string dir = Path.Combine(Path.GetTempPath(), "refii_f3vfs_" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		return dir;
	}

	private static void Write(string root, string relative, string text)
	{
		string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);
	}

	/// <summary>Record hashes as the disc's format-4 banks store them (gamescripts_r.bnk,
	/// guiscripts.bnk): each is the FNV-1 of its record's name, and names the mount's path.</summary>
	[TestCase]
	public void GuestRecordHashNamesTheMountPath()
	{
		AssetHashIndex.RegisterPath("scripts/ai/aibase.lua");
		AssetHashIndex.RegisterPath("art/gui/gameface/abilitiesmanager.lua");

		AssertThat(Engine_GameHash.Fnv1(@"scripts\ai\aibase.lua")).IsEqual(0xAE3AA150u);
		AssertThat(AssetHashIndex.PathForHash(0xAE3AA150u)).IsEqual("scripts/ai/aibase.lua");
		AssertThat(AssetHashIndex.PathForHash(0x1BCEAED1u))
			.IsEqual("art/gui/gameface/abilitiesmanager.lua");
	}

	/// <summary>A mount path spelled with capitals and forward slashes is keyed by the guest's
	/// spelling, and by the <c>data\</c>-prefixed one the databases are named with
	/// (<c>data\Globals\Globals.gdb</c> before the file stream strips the data root).</summary>
	[TestCase]
	public void MountSpellingIsKeyedAsTheGuestHashesIt()
	{
		AssetHashIndex.RegisterPath("Globals/Globals.gdb");

		AssertThat(AssetHashIndex.PathForHash(Engine_GameHash.Fnv1(@"globals\globals.gdb")))
			.IsEqual("globals/globals.gdb");
		AssertThat(AssetHashIndex.PathForHash(Engine_GameHash.Fnv1(@"data\globals\globals.gdb")))
			.IsEqual("globals/globals.gdb");
	}

	/// <summary>Two enabled mods replacing one script: the later in load order is the mount's
	/// file at the path the hash names, its bytes are what the host reads, and it is an override.
	/// </summary>
	[TestCase]
	public void LastModWinsAndIsStatedAnOverride()
	{
		string root = NewTempDir();
		try
		{
			string game = Path.Combine(root, "game");
			string first = Path.Combine(root, "first");
			string second = Path.Combine(root, "second");
			Directory.CreateDirectory(game);
			Write(first, "scripts/ai/aibase.lua", "-- first");
			Write(second, "scripts/ai/aibase.lua", "-- second");

			using BnkFileSystem fs = BnkFileSystem.Mount(
				game, overlays: new[] { ("first", first), ("second", second) });
			AssetHashIndex.RegisterPath("scripts/ai/aibase.lua");

			string? path = AssetHashIndex.PathForHash(0xAE3AA150u);
			AssertThat(path).IsEqual("scripts/ai/aibase.lua");
			AssertThat(fs.TryGetFile(path!, out BnkFile file)).IsTrue();
			AssertThat(file.SourceMod).IsEqual("second");
			AssertThat(file.IsOverride).IsTrue();
			AssertThat(Encoding.UTF8.GetString(file.ReadAllBytes())).IsEqual("-- second");
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	/// <summary>
	/// THE HOST DOES NOT TELL A MOD'S FILE FROM THE GAME'S OWN LOOSE FILE: both are overrides to
	/// it, and only <see cref="BnkFile.SourceMod"/> — which does not cross the shell ABI —
	/// separates them. This is why the hook leaves a file the guest itself opens from disk to the
	/// guest; when this changes, so can that rule.
	/// </summary>
	[TestCase]
	public void GameLooseFileIsAnOverrideWithNoMod()
	{
		string root = NewTempDir();
		try
		{
			string game = Path.Combine(root, "game");
			Write(game, "language/en-uk/text/book.babel", "text");

			using BnkFileSystem fs = BnkFileSystem.Mount(game);

			AssertThat(fs.TryGetFile("language/en-uk/text/book.babel", out BnkFile file)).IsTrue();
			AssertThat(file.SourceMod).IsEqual(string.Empty);
			AssertThat(file.IsOverride).IsTrue();
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}
}
