using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Refii.Bnk;
using Refii.Models;
using Refii.Textures;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>Holds the phone's skip of a texture's read to the one case where the bytes cannot differ.</summary>
[TestSuite]
public sealed class AndroidTexSourceKeysTests
{
	private const string TexturePath = "art/t.tex";

	/// <summary>A game directory the mount will not go looking outside of.</summary>
	private sealed class Fixture : IDisposable
	{
		public readonly string Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "refii-source-keys-" + Guid.NewGuid().ToString("N"));

		public Fixture()
		{
			Directory.CreateDirectory(Game);
			Directory.CreateDirectory(System.IO.Path.Combine(Game, "data", Refii.Titles.Fable2.PubGames.PubGamesCatalog.FolderName));
		}

		public string Game => System.IO.Path.Combine(Root, "game");

		public string Keys => System.IO.Path.Combine(Root, "keys", "source-keys.txt");

		public string Bank(string name, params (string Path, byte[] Bytes)[] entries)
		{
			string path = System.IO.Path.Combine(Game, name);
			File.WriteAllBytes(path, BnkWriter.Write(entries.Select(e => new BnkWriterEntry { Path = e.Path, Bytes = e.Bytes }).ToList()));
			return path;
		}

		public string ModDirectory(string name)
		{
			string path = System.IO.Path.Combine(Root, name);
			Directory.CreateDirectory(path);
			return path;
		}

		public BnkFileSystem Mount(params (string Name, string AssetRoot)[] mods) =>
			BnkFileSystem.Mount(Game, overlays: mods);

		public void Dispose() => Directory.Delete(Root, true);
	}

	private static AndroidTexSourceId IdOf(BnkFileSystem mount, string path = TexturePath)
	{
		AssertThat(mount.TryGetFile(path, out BnkFile file)).IsTrue();
		AssertThat(AndroidTexSourceIdentity.TryCompute(file, out AndroidTexSourceId id)).IsTrue();
		return id;
	}

	private static bool HasIdentity(BnkFileSystem mount, string path = TexturePath)
	{
		AssertThat(mount.TryGetFile(path, out BnkFile file)).IsTrue();
		return AndroidTexSourceIdentity.TryCompute(file, out _);
	}

	private static byte[] Bytes(int length, byte seed)
	{
		var bytes = new byte[length];
		new Random(seed).NextBytes(bytes);
		return bytes;
	}

	private static AndroidTexBaseKey Key(string hex)
	{
		AssertThat(AndroidTexBaseKey.TryParse(Encoding.ASCII.GetBytes(hex), out AndroidTexBaseKey key)).IsTrue();
		return key;
	}

	private static string Hex(byte digit) => new string('0', 62) + digit.ToString("x2");

	private static readonly AndroidTexSourceId Generation1 = new(1, 1);
	private static readonly AndroidTexSourceId Generation2 = new(2, 2);

	[TestCase]
	public void AShippedEntryKeepsItsIdentityAcrossMounts()
	{
		using var fixture = new Fixture();
		fixture.Bank("a.bnk", (TexturePath, Bytes(64, 1)));
		using BnkFileSystem first = fixture.Mount();
		using BnkFileSystem second = fixture.Mount();
		AssertThat(IdOf(first) == IdOf(second)).IsTrue();
	}

	[TestCase]
	public void EveryPartOfARejoinedFileIsInItsIdentity()
	{
		using var fixture = new Fixture();
		fixture.Bank("a.bnk", (TexturePath, Bytes(64, 1)));
		fixture.Bank("b.bnk", (TexturePath, Bytes(128, 2)));
		AndroidTexSourceId both;
		using (BnkFileSystem mount = fixture.Mount())
		{
			AssertThat(mount.TryGetFile(TexturePath, out BnkFile file)).IsTrue();
			AssertThat(file.Shadowed.Count).IsEqual(1);
			both = IdOf(mount);
		}

		// The size of the part that lost the name, then where it lies in its bank.
		fixture.Bank("a.bnk", (TexturePath, Bytes(65, 1)));
		AndroidTexSourceId resized;
		using (BnkFileSystem mount = fixture.Mount())
		{
			resized = IdOf(mount);
			AssertThat(resized == both).IsFalse();
		}

		fixture.Bank("a.bnk", ("art/first.bin", Bytes(32, 3)), (TexturePath, Bytes(65, 1)));
		using (BnkFileSystem mount = fixture.Mount())
		{
			AssertThat(IdOf(mount) == resized).IsFalse();
		}
	}

	[TestCase]
	public void AnEntryInANestedBankHasAnIdentityAndItsOuterBankIsWatched()
	{
		using var fixture = new Fixture();
		byte[] inner = BnkWriter.Write(new[] { new BnkWriterEntry { Path = TexturePath, Bytes = Bytes(64, 1) } });
		string outer = fixture.Bank("outer.bnk", ("inner.bnk", inner));
		using BnkFileSystem mount = fixture.Mount();

		AssertThat(mount.TryGetFile(TexturePath, out BnkFile file)).IsTrue();
		AssertThat(file.SourceArchivePath).IsEqual("inner.bnk");
		AssertThat(HasIdentity(mount)).IsTrue();

		AssertThat(AndroidTexSourceIdentity.TryComputeGeneration(mount, out AndroidTexSourceId before)).IsTrue();
		File.SetLastWriteTimeUtc(outer, File.GetLastWriteTimeUtc(outer).AddSeconds(5));
		AssertThat(AndroidTexSourceIdentity.TryComputeGeneration(mount, out AndroidTexSourceId after)).IsTrue();
		AssertThat(before == after).IsFalse();
	}

	[TestCase]
	public void TheGenerationWatchesEveryBankAndGivesUpOnAMissingOne()
	{
		using var fixture = new Fixture();
		string a = fixture.Bank("a.bnk", (TexturePath, Bytes(64, 1)));
		string dat = System.IO.Path.Combine(fixture.Root, "b.bnk.dat");
		File.WriteAllBytes(dat, Bytes(64, 2));
		var paths = new List<string> { a, dat };

		AssertThat(AndroidTexSourceIdentity.TryComputeGeneration(paths, out AndroidTexSourceId start)).IsTrue();
		AssertThat(AndroidTexSourceIdentity.TryComputeGeneration(new List<string> { dat, a }, out AndroidTexSourceId reordered)).IsTrue();
		AssertThat(reordered == start).IsTrue();

		DateTime stamp = File.GetLastWriteTimeUtc(dat);
		File.SetLastWriteTimeUtc(dat, stamp.AddSeconds(1));
		AssertThat(AndroidTexSourceIdentity.TryComputeGeneration(paths, out AndroidTexSourceId touched)).IsTrue();
		AssertThat(touched == start).IsFalse();

		// The same mtime and a new length.
		File.WriteAllBytes(dat, Bytes(65, 2));
		File.SetLastWriteTimeUtc(dat, stamp);
		AssertThat(AndroidTexSourceIdentity.TryComputeGeneration(paths, out AndroidTexSourceId resized)).IsTrue();
		AssertThat(resized == start).IsFalse();

		File.Delete(dat);
		AssertThat(AndroidTexSourceIdentity.TryComputeGeneration(paths, out _)).IsFalse();
		AssertThat(AndroidTexSourceIdentity.TryComputeGeneration(new List<string>(), out _)).IsFalse();
	}

	[TestCase]
	public void ATitleUpdateBankChangesTheIdentityAndTheGeneration()
	{
		using var fixture = new Fixture();
		fixture.Bank("a.bnk", (TexturePath, Bytes(64, 1)));
		string update = fixture.ModDirectory("update");
		File.WriteAllBytes(System.IO.Path.Combine(update, "tu.bnk"), BnkWriter.Write(new[] { new BnkWriterEntry { Path = TexturePath, Bytes = Bytes(64, 2) } }));

		using BnkFileSystem plain = fixture.Mount();
		using BnkFileSystem updated = BnkFileSystem.Mount(fixture.Game, updateRoot: update);
		AssertThat(IdOf(plain) == IdOf(updated)).IsFalse();

		AssertThat(AndroidTexSourceIdentity.TryComputeGeneration(plain, out AndroidTexSourceId a)).IsTrue();
		AssertThat(AndroidTexSourceIdentity.TryComputeGeneration(updated, out AndroidTexSourceId b)).IsTrue();
		AssertThat(a == b).IsFalse();
	}

	[TestCase]
	public void NothingAModOrALooseFileServesHasAnIdentity()
	{
		using var fixture = new Fixture();
		fixture.Bank("a.bnk", (TexturePath, Bytes(64, 1)));

		// A file of the game's own, loose.
		string loose = System.IO.Path.Combine(fixture.Game, "art");
		Directory.CreateDirectory(loose);
		File.WriteAllBytes(System.IO.Path.Combine(loose, "loose.tex"), Bytes(16, 9));
		using (BnkFileSystem mount = fixture.Mount())
		{
			AssertThat(HasIdentity(mount)).IsTrue();
			AssertThat(HasIdentity(mount, "art/loose.tex")).IsFalse();
		}

		// A mod's loose file, and a mod's bank, over the shipped entry.
		string looseMod = fixture.ModDirectory("loose-mod");
		Directory.CreateDirectory(System.IO.Path.Combine(looseMod, "art"));
		File.WriteAllBytes(System.IO.Path.Combine(looseMod, "art", "t.tex"), Bytes(16, 4));
		string bankMod = fixture.ModDirectory("bank-mod");
		File.WriteAllBytes(System.IO.Path.Combine(bankMod, "mod.bnk"), BnkWriter.Write(new[] { new BnkWriterEntry { Path = TexturePath, Bytes = Bytes(16, 5) } }));

		using (BnkFileSystem mount = fixture.Mount(("loose-mod", looseMod)))
		{
			AssertThat(HasIdentity(mount)).IsFalse();
		}

		using (BnkFileSystem mount = fixture.Mount(("bank-mod", bankMod)))
		{
			AssertThat(HasIdentity(mount)).IsFalse();
		}

		// Either order, the pin included: the last mod wins, then the first is pinned over it.
		using (BnkFileSystem mount = fixture.Mount(("loose-mod", looseMod), ("bank-mod", bankMod)))
		{
			AssertThat(mount.Promote(TexturePath, "loose-mod")).IsTrue();
			AssertThat(HasIdentity(mount)).IsFalse();
		}

		// A mod's compiled image, a merged table: whatever is added to a mount from memory.
		using (BnkFileSystem mount = fixture.Mount())
		{
			mount.AddVirtualFile(TexturePath, "<image:t>", 16, () => Bytes(16, 6), "a-mod");
			AssertThat(HasIdentity(mount)).IsFalse();
		}
	}

	[TestCase]
	public void AWinningBankEntryThatShadowsALooseFileHasNoIdentity()
	{
		// A loose file's `data/`-less alias doesn't displace a larger bank entry, but the rejoin still
		// looks at it as a part.
		using var fixture = new Fixture();
		fixture.Bank("a.bnk", (TexturePath, Bytes(4096, 1)));
		Directory.CreateDirectory(System.IO.Path.Combine(fixture.Game, "data", "art"));
		File.WriteAllBytes(System.IO.Path.Combine(fixture.Game, "data", TexturePath), Bytes(16, 2));
		using BnkFileSystem mount = fixture.Mount();

		AssertThat(mount.TryGetFile(TexturePath, out BnkFile file)).IsTrue();
		AssertThat(file.IsOverride).IsFalse();
		AssertThat(file.Shadowed.Any(part => part.IsOverride)).IsTrue();
		AssertThat(HasIdentity(mount)).IsFalse();
	}

	[TestCase]
	public void OnlyAReaderEndingAtTheMountCounts()
	{
		Func<string, byte[]?> mountRead = _ => null;
		Func<string, byte[]?>? meshBefore = Engine_BnkMeshLoader.Resolver;
		Func<string, byte[]?>? textureBefore = Engine_BnkTextureLoader.Resolver;
		try
		{
			Engine_BnkMeshLoader.SetResolver(mountRead);
			Engine_BnkTextureLoader.SetResolver(mountRead);
			AssertThat(AndroidTexSourceKeys.ReadsThroughMount(Engine_BnkMeshLoader.ReadBytes, mountRead)).IsTrue();
			AssertThat(AndroidTexSourceKeys.ReadsThroughMount(Engine_BnkTextureLoader.ReadBytes, mountRead)).IsTrue();

			// A caller's own closure, even around the same loader.
			AssertThat(AndroidTexSourceKeys.ReadsThroughMount(path => Engine_BnkMeshLoader.ReadBytes(path), mountRead)).IsFalse();

			// The prebuild's mount, swapped into the loader.
			Engine_BnkMeshLoader.SetResolver(_ => null);
			AssertThat(AndroidTexSourceKeys.ReadsThroughMount(Engine_BnkMeshLoader.ReadBytes, mountRead)).IsFalse();
			Engine_BnkMeshLoader.SetResolver(null);
			AssertThat(AndroidTexSourceKeys.ReadsThroughMount(Engine_BnkMeshLoader.ReadBytes, mountRead)).IsFalse();
		}
		finally
		{
			Engine_BnkMeshLoader.SetResolver(meshBefore);
			Engine_BnkTextureLoader.SetResolver(textureBefore);
		}
	}

	[TestCase]
	public void TheMapComesBackFromItsFile()
	{
		using var fixture = new Fixture();
		var id = new AndroidTexSourceId(5, 6);
		using (var map = new AndroidTexSourceKeyMap(fixture.Keys))
		{
			map.Open(Generation1);
			map.Set(id, Key(Hex(1)));
		}

		using var again = new AndroidTexSourceKeyMap(fixture.Keys);
		again.Open(Generation1);
		AssertThat(again.TryGet(id, out AndroidTexBaseKey key)).IsTrue();
		AssertThat(key.ToHex()).IsEqual(Hex(1));
		AssertThat(key.ToHex().Length).IsEqual(64);
	}

	[TestCase]
	public void ACorrectionWinsOnReload()
	{
		using var fixture = new Fixture();
		var id = new AndroidTexSourceId(5, 6);
		using (var map = new AndroidTexSourceKeyMap(fixture.Keys))
		{
			map.Open(Generation1);
			map.Set(id, Key(Hex(1)));
			long oneLine = new FileInfo(fixture.Keys).Length;
			map.Set(id, Key(Hex(1)));
			AssertThat(new FileInfo(fixture.Keys).Length).IsEqual(oneLine);
			map.Set(id, Key(Hex(2)));
			AssertThat(map.TryGet(id, out AndroidTexBaseKey now)).IsTrue();
			AssertThat(now.ToHex()).IsEqual(Hex(2));
		}

		using var again = new AndroidTexSourceKeyMap(fixture.Keys);
		again.Open(Generation1);
		AssertThat(again.Count).IsEqual(1);
		AssertThat(again.TryGet(id, out AndroidTexBaseKey key)).IsTrue();
		AssertThat(key.ToHex()).IsEqual(Hex(2));
	}

	[TestCase]
	public void ATornLastLineCostsOnlyThatLine()
	{
		using var fixture = new Fixture();
		var first = new AndroidTexSourceId(1, 2);
		var torn = new AndroidTexSourceId(3, 4);
		var later = new AndroidTexSourceId(7, 8);
		using (var map = new AndroidTexSourceKeyMap(fixture.Keys))
		{
			map.Open(Generation1);
			map.Set(first, Key(Hex(1)));
			map.Set(torn, Key(Hex(2)));
		}

		using (FileStream file = new(fixture.Keys, FileMode.Open, System.IO.FileAccess.Write))
		{
			file.SetLength(file.Length - 20);
		}

		using (var map = new AndroidTexSourceKeyMap(fixture.Keys))
		{
			map.Open(Generation1);
			AssertThat(map.TryGet(first, out _)).IsTrue();
			AssertThat(map.TryGet(torn, out _)).IsFalse();

			// Written after the torn line, this one must not join it.
			map.Set(later, Key(Hex(3)));
		}

		using var again = new AndroidTexSourceKeyMap(fixture.Keys);
		again.Open(Generation1);
		AssertThat(again.Count).IsEqual(2);
		AssertThat(again.TryGet(later, out AndroidTexBaseKey key)).IsTrue();
		AssertThat(key.ToHex()).IsEqual(Hex(3));
	}

	[TestCase]
	public void AGarbledLineIsSkipped()
	{
		using var fixture = new Fixture();
		var first = new AndroidTexSourceId(1, 2);
		var last = new AndroidTexSourceId(3, 4);
		using (var map = new AndroidTexSourceKeyMap(fixture.Keys))
		{
			map.Open(Generation1);
			map.Set(first, Key(Hex(1)));
		}

		File.AppendAllText(fixture.Keys, "not a line\n" + new string('z', 32) + " " + new string('0', 64) + "\n\n");
		using (var map = new AndroidTexSourceKeyMap(fixture.Keys))
		{
			map.Open(Generation1);
			map.Set(last, Key(Hex(2)));
		}

		using var again = new AndroidTexSourceKeyMap(fixture.Keys);
		again.Open(Generation1);
		AssertThat(again.Count).IsEqual(2);
	}

	[TestCase]
	public void AnotherGenerationStartsTheFileAgain()
	{
		using var fixture = new Fixture();
		var id = new AndroidTexSourceId(5, 6);
		using (var map = new AndroidTexSourceKeyMap(fixture.Keys))
		{
			map.Open(Generation1);
			map.Set(id, Key(Hex(1)));
		}

		long full = new FileInfo(fixture.Keys).Length;
		using (var map = new AndroidTexSourceKeyMap(fixture.Keys))
		{
			map.Open(Generation2);
			AssertThat(map.Count).IsEqual(0);
			AssertThat(new FileInfo(fixture.Keys).Length < full).IsTrue();

			// The same again within a run, for a mod's bank turned on during it.
			map.Set(id, Key(Hex(2)));
			map.Open(Generation1);
			AssertThat(map.Count).IsEqual(0);
			map.Set(id, Key(Hex(3)));
		}

		using var again = new AndroidTexSourceKeyMap(fixture.Keys);
		again.Open(Generation1);
		AssertThat(again.TryGet(id, out AndroidTexBaseKey key)).IsTrue();
		AssertThat(key.ToHex()).IsEqual(Hex(3));
	}

	[TestCase]
	public void AFileWithATornHeaderIsStartedAgain()
	{
		using var fixture = new Fixture();
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fixture.Keys)!);
		File.WriteAllText(fixture.Keys, "refii-tex-sou");
		using var map = new AndroidTexSourceKeyMap(fixture.Keys);
		map.Open(Generation1);
		AssertThat(map.Count).IsEqual(0);
		map.Set(new AndroidTexSourceId(1, 2), Key(Hex(1)));

		using var again = new AndroidTexSourceKeyMap(fixture.Keys);
		again.Open(Generation1);
		AssertThat(again.Count).IsEqual(1);
	}

	[TestCase]
	public void AnUnwritableFileLeavesTheMapWorkingFromMemory()
	{
		using var fixture = new Fixture();

		// A directory where the file should be.
		Directory.CreateDirectory(fixture.Keys);
		using var map = new AndroidTexSourceKeyMap(fixture.Keys);
		map.Open(Generation1);
		map.Set(new AndroidTexSourceId(1, 2), Key(Hex(1)));
		AssertThat(map.TryGet(new AndroidTexSourceId(1, 2), out _)).IsTrue();
	}

	[TestCase]
	public void ManyThreadsLoseNothing()
	{
		using var fixture = new Fixture();
		const int perThread = 500;
		const int threads = 8;
		using (var map = new AndroidTexSourceKeyMap(fixture.Keys))
		{
			map.Open(Generation1);
			Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, thread =>
			{
				for (int i = 0; i < perThread; ++i)
				{
					var id = new AndroidTexSourceId((ulong)thread + 1, (ulong)i);
					map.Set(id, new AndroidTexBaseKey((ulong)thread, (ulong)i, 0, 0));
					map.TryGet(id, out _);
				}
			});
			AssertThat(map.Count).IsEqual(threads * perThread);
		}

		using var again = new AndroidTexSourceKeyMap(fixture.Keys);
		again.Open(Generation1);
		AssertThat(again.Count).IsEqual(threads * perThread);
		AssertThat(again.TryGet(new AndroidTexSourceId(3, 77), out AndroidTexBaseKey key)).IsTrue();
		AssertThat(key == new AndroidTexBaseKey(2, 77, 0, 0)).IsTrue();
	}

	[TestCase]
	public void FourteenThousandEntriesStayUnderAMegabyte()
	{
		using var fixture = new Fixture();
		using (var writer = new AndroidTexSourceKeyMap(fixture.Keys))
		{
			writer.Open(Generation1);
			for (int i = 0; i < 14_000; ++i)
			{
				writer.Set(new AndroidTexSourceId((ulong)i + 1, (ulong)i * 31), new AndroidTexBaseKey((ulong)i, 1, 2, 3));
			}
		}

		long file = new FileInfo(fixture.Keys).Length;
		AssertThat(file < 1_500_000).IsTrue();

		long before = GC.GetTotalMemory(forceFullCollection: true);
		var map = new AndroidTexSourceKeyMap(fixture.Keys);
		map.Open(Generation1);
		long held = GC.GetTotalMemory(forceFullCollection: true) - before;
		AssertThat(map.Count).IsEqual(14_000);
		AssertThat(held < 1_000_000).OverrideFailureMessage($"{held:N0} bytes for 14,000 entries").IsTrue();
		GC.KeepAlive(map);
		map.Dispose();
	}

	/// <summary>The real decode, with a mount of its own in place of the game's and a reader that counts.</summary>
	private sealed class Decoding : IDisposable
	{
		private readonly AndroidTexSourceKeys.MountLookup _lookupBefore = AndroidTexSourceKeys.Lookup;
		private readonly List<string> _entries = new();
		public BnkFileSystem Mount;
		public int Reads;
		public Action? DuringRead;

		public Decoding(Fixture fixture)
		{
			Mount = fixture.Mount();
			AndroidTexSourceKeys.ResetForTests();
			AndroidTexSourceKeys.Enabled = true;
			AndroidTexSourceKeys.FilePathOverride = fixture.Keys;
			AndroidTexSourceKeys.Lookup = LookUp;
		}

		private bool LookUp(string path, Func<string, byte[]?> read, out BnkFileSystem? mount, out BnkFile? file)
		{
			mount = Mount;
			bool found = Mount.TryGetFile(path, out BnkFile resolved);
			file = found ? resolved : null;
			return found;
		}

		public byte[]? Read(string path)
		{
			Reads++;
			byte[]? bytes = Mount.TryGetFile(path, out BnkFile file) ? file.ReadAllBytes() : null;
			DuringRead?.Invoke();
			return bytes;
		}

		public Image? Decode(out bool hasAlpha) => Engine_TexLibrary.Decode(TexturePath, TexSemantic.Ui, Read, out hasAlpha);

		/// <summary>A cache entry for these source bytes, so a decode has no need of a real .tex.</summary>
		public string Cache(byte[] source, bool hasAlpha = true)
		{
			string key = Engine_TexCache.BaseKey(source);
			using Image image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
			image.Fill(new Color(1, 0, 0));
			Engine_TexCache.Store(key, image, hasAlpha);
			_entries.Add(key);
			return key;
		}

		public void Evict(string key) => File.Delete(EntryPath(key));

		private static string EntryPath(string key) =>
			System.IO.Path.Combine(Engine_TexCache.CacheRoot, $"v{Engine_TexCache.ConverterVersion}", key[..2], key + ".rftx");

		public void Dispose()
		{
			AndroidTexSourceKeys.ResetForTests();
			AndroidTexSourceKeys.Enabled = false;
			AndroidTexSourceKeys.FilePathOverride = null;
			AndroidTexSourceKeys.Lookup = _lookupBefore;
			Mount.Dispose();
			foreach (string key in _entries)
			{
				File.Delete(EntryPath(key));
			}
		}
	}

	[TestCase]
	[RequireGodotRuntime]
	public void AKnownTextureIsServedWithoutARead()
	{
		using var fixture = new Fixture();
		byte[] source = Bytes(128, 11);
		fixture.Bank("a.bnk", (TexturePath, source));
		using var decoding = new Decoding(fixture);
		decoding.Cache(source);

		using (Image? first = decoding.Decode(out bool alpha))
		{
			AssertThat(first is not null).IsTrue();
			AssertThat(alpha).IsTrue();
			AssertThat(decoding.Reads).IsEqual(1);
			AssertThat(AndroidTexSourceKeys.Served).IsEqual(0L);
		}

		using (Image? second = decoding.Decode(out bool alpha))
		{
			AssertThat(second is not null).IsTrue();
			AssertThat(second!.GetWidth()).IsEqual(4);
			AssertThat(second.GetPixel(0, 0).R).IsEqual(1f);
			AssertThat(alpha).IsTrue();
			AssertThat(decoding.Reads).IsEqual(1);
			AssertThat(AndroidTexSourceKeys.Served).IsEqual(1L);
		}

		// The next launch, from the file.
		AndroidTexSourceKeys.ResetForTests();
		using (Image? third = decoding.Decode(out _))
		{
			AssertThat(third is not null).IsTrue();
			AssertThat(decoding.Reads).IsEqual(1);
		}
	}

	[TestCase]
	[RequireGodotRuntime]
	public void ASweptEntryFallsBackAndAWrongKeyIsCorrected()
	{
		using var fixture = new Fixture();
		byte[] source = Bytes(128, 12);
		fixture.Bank("a.bnk", (TexturePath, source));
		using var decoding = new Decoding(fixture);
		string key = decoding.Cache(source);
		decoding.Decode(out _)?.Dispose();

		// The sweep took it: the full path runs. Nothing can decode these made-up bytes, so null.
		decoding.Evict(key);
		AssertThat(decoding.Decode(out _) is null).IsTrue();
		AssertThat(decoding.Reads).IsEqual(2);
		AssertThat(AndroidTexSourceKeys.Stale).IsEqual(1L);

		// A key that leads nowhere is replaced by what the read hashed to.
		AssertThat(decoding.Mount.TryGetFile(TexturePath, out BnkFile file)).IsTrue();
		AssertThat(AndroidTexSourceIdentity.TryCompute(file, out AndroidTexSourceId id)).IsTrue();
		AssertThat(AndroidTexSourceIdentity.TryComputeGeneration(decoding.Mount, out AndroidTexSourceId generation)).IsTrue();
		AndroidTexSourceKeys.ResetForTests();
		using (var map = new AndroidTexSourceKeyMap(fixture.Keys))
		{
			map.Open(generation);
			map.Set(id, Key(Hex(9)));
		}

		decoding.Cache(source);
		int reads = decoding.Reads;
		decoding.Decode(out _)?.Dispose();
		AssertThat(decoding.Reads).IsEqual(reads + 1);

		using var reloaded = new AndroidTexSourceKeyMap(fixture.Keys);
		reloaded.Open(generation);
		AssertThat(reloaded.TryGet(id, out AndroidTexBaseKey known)).IsTrue();
		AssertThat(known.ToHex()).IsEqual(key);
	}

	[TestCase]
	[RequireGodotRuntime]
	public void AModIsAlwaysReadAndNeverRemembered()
	{
		using var fixture = new Fixture();
		byte[] shipped = Bytes(128, 13);
		byte[] modded = Bytes(128, 14);
		fixture.Bank("a.bnk", (TexturePath, shipped));
		string mod = fixture.ModDirectory("mod");
		Directory.CreateDirectory(System.IO.Path.Combine(mod, "art"));
		File.WriteAllBytes(System.IO.Path.Combine(mod, "art", "t.tex"), modded);

		using var decoding = new Decoding(fixture);
		decoding.Cache(shipped);
		decoding.Cache(modded);
		decoding.Decode(out _)?.Dispose();
		decoding.Decode(out _)?.Dispose();
		AssertThat(decoding.Reads).IsEqual(1);

		BnkFileSystem shippedMount = decoding.Mount;
		using (BnkFileSystem withMod = fixture.Mount(("mod", mod)))
		{
			decoding.Mount = withMod;
			decoding.Decode(out _)?.Dispose();
			decoding.Decode(out _)?.Dispose();
			AssertThat(decoding.Reads).IsEqual(3);
		}

		// The mod is off again, and the shipped key is still the one known.
		decoding.Mount = shippedMount;
		decoding.Decode(out _)?.Dispose();
		AssertThat(decoding.Reads).IsEqual(3);
		AssertThat(AndroidTexSourceKeys.Served).IsEqual(2L);
	}

	[TestCase]
	[RequireGodotRuntime]
	public void AMountChangingDuringTheReadTeachesNothing()
	{
		using var fixture = new Fixture();
		byte[] shipped = Bytes(128, 15);
		byte[] modded = Bytes(128, 16);
		fixture.Bank("a.bnk", (TexturePath, shipped));
		string mod = fixture.ModDirectory("mod");
		Directory.CreateDirectory(System.IO.Path.Combine(mod, "art"));
		File.WriteAllBytes(System.IO.Path.Combine(mod, "art", "t.tex"), modded);

		using var decoding = new Decoding(fixture);
		decoding.Cache(modded);
		using BnkFileSystem withMod = fixture.Mount(("mod", mod));
		BnkFileSystem shippedMount = decoding.Mount;

		// The ticket is written against the game's file, the bytes come from the mod.
		decoding.DuringRead = () => decoding.Mount = withMod;
		var swapped = new Func<string, byte[]?>(path =>
		{
			decoding.Reads++;
			decoding.DuringRead?.Invoke();
			return decoding.Mount.TryGetFile(path, out BnkFile file) ? file.ReadAllBytes() : null;
		});
		Engine_TexLibrary.Decode(TexturePath, TexSemantic.Ui, swapped, out _)?.Dispose();

		decoding.DuringRead = null;
		decoding.Mount = shippedMount;
		int reads = decoding.Reads;
		decoding.Decode(out _);
		AssertThat(decoding.Reads).IsEqual(reads + 1);
		AssertThat(AndroidTexSourceKeys.Served).IsEqual(0L);
	}

	[TestCase]
	[RequireGodotRuntime]
	public void EnhancementAlwaysReads()
	{
		using var fixture = new Fixture();
		byte[] source = Bytes(128, 17);
		fixture.Bank("a.bnk", (TexturePath, source));
		using var decoding = new Decoding(fixture);
		decoding.Cache(source);
		decoding.Decode(out _)?.Dispose();
		decoding.Decode(out _)?.Dispose();
		AssertThat(decoding.Reads).IsEqual(1);

		Engine_TexEnhance.Register(new StubUpscaler());
		Engine_TexEnhance.SetEnabled(true);
		try
		{
			AssertThat(Engine_TexEnhance.Enabled).IsTrue();
			decoding.Decode(out _)?.Dispose();
			AssertThat(decoding.Reads).IsEqual(2);
		}
		finally
		{
			Engine_TexEnhance.SetEnabled(false);
			Engine_TexEnhance.Register(null);
		}

		decoding.Decode(out _)?.Dispose();
		AssertThat(decoding.Reads).IsEqual(2);
	}

	[TestCase]
	[RequireGodotRuntime]
	public void DisabledMeansTheOldPathAndNoFile()
	{
		using var fixture = new Fixture();
		byte[] source = Bytes(128, 18);
		fixture.Bank("a.bnk", (TexturePath, source));
		using var decoding = new Decoding(fixture);
		AndroidTexSourceKeys.Enabled = false;
		decoding.Cache(source);
		decoding.Decode(out _)?.Dispose();
		decoding.Decode(out _)?.Dispose();
		AssertThat(decoding.Reads).IsEqual(2);
		AssertThat(File.Exists(fixture.Keys)).IsFalse();
	}

	private sealed class StubUpscaler : ITexUpscaler
	{
		public string Id => "stub";

		public bool TryUpscale(Image source, TexFilterClass filter, int scale, out Image? result)
		{
			result = null;
			return false;
		}
	}
}
