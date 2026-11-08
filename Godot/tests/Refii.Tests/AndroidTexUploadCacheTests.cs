using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using GdUnit4;
using Godot;
using Refii.Bnk;
using Refii.Textures;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The phone's kept upload form: the same pixels as a decode, and any doubt is a miss.</summary>
[TestSuite]
public sealed class AndroidTexUploadCacheTests
{
	private const string TexturePath = "art/t.tex";

	private static readonly int[] Table = new int[AndroidTexLz4.TableEntries];

	private static byte[] Noise(int length, int seed)
	{
		var bytes = new byte[length];
		new Random(seed).NextBytes(bytes);
		return bytes;
	}

	private static byte[] Encode(byte[] raw)
	{
		var packed = new byte[AndroidTexLz4.Bound(raw.Length)];
		return packed.AsSpan(0, AndroidTexLz4.Encode(raw, packed, Table)).ToArray();
	}

	private static IEnumerable<byte[]> Inputs()
	{
		foreach (int length in new[] { 0, 1, 3, 4, 5, 11, 12, 13, 14, 15, 16, 17, 31, 32, 33, 255, 256, 270, 271, 4096, 70_000 })
		{
			yield return Noise(length, length);
			yield return new byte[length];

			// Repeats nearer and further than the sixteen bytes the copy takes at a time.
			foreach (int period in new[] { 1, 2, 3, 7, 16, 17, 64 })
			{
				var repeating = new byte[length];
				for (int i = 0; i < length; ++i)
				{
					repeating[i] = (byte)(i % period * 37);
				}

				yield return repeating;
			}
		}

		// Longer than a match can reach, with both kinds of long length.
		byte[] block = Noise(1000, 5);
		yield return Enumerable.Range(0, 200).SelectMany(_ => block).ToArray();
		yield return Noise(200_000, 6).Concat(new byte[200_000]).Concat(Noise(200_000, 6)).ToArray();
	}

	[TestCase]
	public void LzRoundTrips()
	{
		foreach (byte[] raw in Inputs())
		{
			byte[] packed = Encode(raw);
			AssertThat(packed.Length <= AndroidTexLz4.Bound(raw.Length)).IsTrue();
			var back = new byte[raw.Length];
			AssertThat(AndroidTexLz4.Decode(packed, back)).OverrideFailureMessage($"length {raw.Length}").IsTrue();
			AssertThat(back.AsSpan().SequenceEqual(raw)).OverrideFailureMessage($"length {raw.Length}").IsTrue();
		}
	}

	[TestCase]
	public void LzRefusesTheWrongSize()
	{
		byte[] raw = Noise(1000, 1);
		byte[] packed = Encode(raw);
		AssertThat(AndroidTexLz4.Decode(packed, new byte[999])).IsFalse();
		AssertThat(AndroidTexLz4.Decode(packed, new byte[1001])).IsFalse();
	}

	[TestCase]
	public void LzSurvivesAnyDamage()
	{
		// Guard pages would catch a stray write better; this at least runs every path on bad input
		// with the output in a canary, under a decoder that must only ever answer yes or no.
		var random = new Random(77);
		byte[] texture = Enumerable.Range(0, 400).SelectMany(i => Noise(16, i % 9)).ToArray();
		byte[] packed = Encode(texture);
		for (int round = 0; round < 20_000; ++round)
		{
			byte[] damaged = packed[..(round % 7 == 0 ? random.Next(packed.Length) : packed.Length)];
			for (int flips = random.Next(1, 5); flips > 0 && damaged.Length > 0; --flips)
			{
				damaged[random.Next(damaged.Length)] = (byte)random.Next(256);
			}

			var output = new byte[texture.Length + 64];
			output.AsSpan(texture.Length).Fill(0xA5);
			AndroidTexLz4.Decode(damaged, output.AsSpan(0, texture.Length));
			AssertThat(output.AsSpan(texture.Length).IndexOfAnyExcept((byte)0xA5)).IsEqual(-1);
		}

		// And plain garbage.
		for (int round = 0; round < 5_000; ++round)
		{
			var output = new byte[256 + 64];
			output.AsSpan(256).Fill(0xA5);
			AndroidTexLz4.Decode(Noise(random.Next(1, 300), round), output.AsSpan(0, 256));
			AssertThat(output.AsSpan(256).IndexOfAnyExcept((byte)0xA5)).IsEqual(-1);
		}
	}

	[TestCase]
	public void TheHeaderReadsBackAndRefusesNonsense()
	{
		var header = new AndroidTexUploadHeader(0x1122334455667788ul, 512, 256, 35, true, true, true, 1000, 900, 0xCAFEul);
		var bytes = new byte[AndroidTexUploadHeader.Size];
		header.Write(bytes);
		AssertThat(AndroidTexUploadHeader.TryRead(bytes, out AndroidTexUploadHeader back)).IsTrue();
		AssertThat(back == header).IsTrue();

		AssertThat(AndroidTexUploadHeader.TryRead(bytes.AsSpan(0, 20), out _)).IsFalse();
		foreach (AndroidTexUploadHeader bad in new[]
		{
			header with { Width = 0 },
			header with { Height = -1 },
			header with { Length = 0 },
			header with { Length = 65 * 1024 * 1024 },
			header with { StoredLength = 0 },
			header with { StoredLength = AndroidTexLz4.Bound(1000) + 1 },
		})
		{
			bad.Write(bytes);
			AssertThat(AndroidTexUploadHeader.TryRead(bytes, out _)).IsFalse();
		}

		header.Write(bytes);
		bytes[0] ^= 1;
		AssertThat(AndroidTexUploadHeader.TryRead(bytes, out _)).IsFalse();
	}

	[TestCase]
	public void TheChecksumSeesEveryByte()
	{
		byte[] bytes = Noise(67, 3);
		ulong clean = AndroidTexUploadHeader.ChecksumOf(bytes);
		for (int i = 0; i < bytes.Length; ++i)
		{
			bytes[i] ^= 0x10;
			AssertThat(AndroidTexUploadHeader.ChecksumOf(bytes) != clean).IsTrue();
			bytes[i] ^= 0x10;
		}

		AssertThat(AndroidTexUploadHeader.ChecksumOf(bytes.AsSpan(0, 66)) != clean).IsTrue();
		AssertThat(AndroidTexUploadHeader.ChecksumOf(new byte[8]) != AndroidTexUploadHeader.ChecksumOf(new byte[16])).IsTrue();
	}

	/// <summary>Holds the transcoder's output to <see cref="Engine_AstcTranscode.Version"/>.</summary>
	[TestCase]
	public void ChangedTranscoderOutputNeedsAVersionBump()
	{
		byte[] bc1 = Noise(8 * 20_000, 101), bc3 = Noise(16 * 20_000, 102);

		// Random bytes rarely make the blocks real textures are full of: opaque BC3 and flat alpha.
		for (int i = 0; i < bc3.Length; i += 48)
		{
			bc3[i] = bc3[i + 1];
		}

		var astc1 = new byte[16 * 20_000];
		var astc3 = new byte[16 * 20_000];
		AssertThat(Engine_AstcTranscode.Transcode(bc1, false, astc1)).IsTrue();
		AssertThat(Engine_AstcTranscode.Transcode(bc3, true, astc3)).IsTrue();
		string digest = Convert.ToHexString(Refii.Core.Engine_Hash.Sha256(astc1.Concat(astc3).ToArray())).ToLowerInvariant();
		AssertThat($"{Engine_AstcTranscode.Version}:{digest}")
			.OverrideFailureMessage($"The transcoder's output changed ({digest}). Kept upload forms are the old blocks: bump Engine_AstcTranscode.Version, then this number.")
			.IsEqual("1:a9f96207f0b8a8689d55df3470429ebe34c360a197e4aeca1826e8c8c14ff4bd");
	}

	/// <summary>A mount of its own, the phone's policy, and a place for the files.</summary>
	private sealed class Scene : IDisposable
	{
		private readonly AndroidTexSourceKeys.MountLookup _lookupBefore = AndroidTexSourceKeys.Lookup;
		private readonly AndroidTextureUploadPolicy _policyBefore = AndroidTextureUpload.Policy;
		private readonly List<string> _entries = new();
		public readonly string Root = System.IO.Path.Combine(Refii.Tests.TestTemp.Root, "refii-upload-cache-" + Guid.NewGuid().ToString("N"));
		public BnkFileSystem Mount;
		public int Reads;
		public bool NoIdentity;

		public Scene(byte[] source, bool lowTier = true)
		{
			string game = System.IO.Path.Combine(Root, "game");
			Directory.CreateDirectory(game);
			Directory.CreateDirectory(System.IO.Path.Combine(game, "data", Refii.Titles.Fable2.PubGames.PubGamesCatalog.FolderName));
			File.WriteAllBytes(System.IO.Path.Combine(game, "a.bnk"),
				BnkWriter.Write(new List<BnkWriterEntry> { new() { Path = TexturePath, Bytes = source } }));
			Mount = BnkFileSystem.Mount(game);

			AndroidTexSourceKeys.ResetForTests();
			AndroidTexSourceKeys.Enabled = true;
			AndroidTexSourceKeys.FilePathOverride = System.IO.Path.Combine(Root, "keys", "source-keys.txt");
			AndroidTexSourceKeys.Lookup = LookUp;
			AndroidTexUploadCache.ResetForTests();
			AndroidTexUploadCache.Enabled = true;
			AndroidTexUploadCache.RootOverride = Epoch;
			Tier(lowTier);
		}

		/// <summary>The cache root's shape, so the real sweep can be pointed at it.</summary>
		public string CacheRoot => System.IO.Path.Combine(Root, "textures");

		public string Epoch => System.IO.Path.Combine(CacheRoot, $"v{Engine_TexCache.ConverterVersion}", "upload");

		public string[] Files => Directory.Exists(Epoch) ? Directory.GetFiles(Epoch, "*", SearchOption.AllDirectories) : Array.Empty<string>();

		public void Tier(bool low) => AndroidTextureUpload.SetPolicyForTests(AndroidTextureUploadPolicy.Capture(true,
			format => format == RenderingDevice.DataFormat.Astc4X4UnormBlock, true, low));

		private bool LookUp(string path, Func<string, byte[]?> read, out BnkFileSystem? mount, out BnkFile? file)
		{
			mount = Mount;
			file = null;
			if (NoIdentity || !Mount.TryGetFile(path, out BnkFile resolved))
			{
				return false;
			}

			file = resolved;
			return true;
		}

		public byte[]? Read(string path)
		{
			Reads++;
			return Mount.TryGetFile(path, out BnkFile file) ? file.ReadAllBytes() : null;
		}

		/// <summary>The cached decode of the mounted bytes, as a real load would find it.</summary>
		public void CacheDecode(byte[] source, Image bc, bool hasAlpha)
		{
			string key = Engine_TexCache.BaseKey(source);
			Engine_TexCache.Store(key, bc, hasAlpha);
			_entries.Add(key);
		}

		/// <summary>The part of <see cref="Engine_TexLibrary.Get"/> this changes. The caller owns the image.</summary>
		public Image? Load(out bool hasAlpha, out bool served)
		{
			Image? image = AndroidTexUploadCache.TryServe(TexturePath, Read, out AndroidTexSourceKeys.Ticket ticket, out hasAlpha);
			served = image is not null;
			image ??= Engine_TexLibrary.Decode(TexturePath, TexSemantic.Ui, Read, out hasAlpha);
			Image? upload = AndroidTexUploadCache.Remember(ticket, image, hasAlpha);
			if (!ReferenceEquals(upload, image))
			{
				image?.Dispose();
			}

			return upload;
		}

		public void Dispose()
		{
			AndroidTexUploadCache.WriterGateForTests?.Set();
			AndroidTexUploadCache.WriterGateForTests = null;
			AndroidTexUploadCache.ResetForTests();
			AndroidTexUploadCache.Enabled = false;
			AndroidTexUploadCache.RootOverride = null;
			AndroidTexUploadCache.MaximumQueuedBytes = 32 * 1024 * 1024;
			AndroidTextureUpload.SetPolicyForTests(_policyBefore);
			AndroidTexSourceKeys.ResetForTests();
			AndroidTexSourceKeys.Enabled = false;
			AndroidTexSourceKeys.FilePathOverride = null;
			AndroidTexSourceKeys.Lookup = _lookupBefore;
			Mount.Dispose();
			foreach (string key in _entries)
			{
				File.Delete(System.IO.Path.Combine(Engine_TexCache.CacheRoot, $"v{Engine_TexCache.ConverterVersion}", key[..2], key + ".rftx"));
			}

			Directory.Delete(Root, true);
		}
	}

	/// <summary>A full chain of noise blocks, plus flat stretches so its file packs.</summary>
	private static Image Bc(Image.Format format, int width, int height, int seed, bool mipmaps = true)
	{
		using Image empty = Image.CreateEmpty(width, height, mipmaps, format);
		byte[] data = Noise((int)empty.GetDataSize(), seed);
		Array.Clear(data, data.Length / 2, data.Length / 4);
		return Image.CreateFromData(width, height, mipmaps, format, data);
	}

	private static void AssertSame(Image got, Image want)
	{
		AssertThat(got.GetFormat()).IsEqual(want.GetFormat());
		AssertThat(got.GetWidth()).IsEqual(want.GetWidth());
		AssertThat(got.GetHeight()).IsEqual(want.GetHeight());
		AssertThat(got.HasMipmaps()).IsEqual(want.HasMipmaps());
		AssertThat(got.GetData().AsSpan().SequenceEqual(want.GetData())).IsTrue();
	}

	[TestCase(Image.Format.Dxt1, 1024, true, Image.Format.Astc4X4, 512)]
	[TestCase(Image.Format.Dxt1, 256, true, Image.Format.Astc4X4, 256)]
	[TestCase(Image.Format.Dxt1, 1024, false, Image.Format.Astc4X4, 1024)]
	[TestCase(Image.Format.Dxt5, 512, true, Image.Format.Astc4X4, 256)]
	[TestCase(Image.Format.Dxt5, 128, false, Image.Format.Astc4X4, 128)]
	[TestCase(Image.Format.RgtcRg, 256, true, Image.Format.Rg8, 128)]
	[TestCase(Image.Format.RgtcRg, 128, true, Image.Format.Rg8, 128)]
	[TestCase(Image.Format.RgtcRg, 512, false, Image.Format.Rg8, 512)]
	[RequireGodotRuntime]
	public void AKnownTextureComesBackAsTheUploadWouldHaveMadeIt(Image.Format format, int edge, bool low, Image.Format target, int uploadEdge)
	{
		byte[] source = Noise(256, edge);
		using var scene = new Scene(source, low);
		using Image bc = Bc(format, edge, edge, edge + (int)format);
		scene.CacheDecode(source, bc, hasAlpha: true);
		using Image reference = AndroidTextureUpload.PrepareAhead(bc, world: true);

		using (Image? first = scene.Load(out bool alpha, out bool served))
		{
			AssertThat(served).IsFalse();
			AssertThat(alpha).IsTrue();
			AssertSame(first!, reference);
		}

		AssertThat(scene.Reads).IsEqual(1);
		AndroidTexUploadCache.FlushForTests();
		AssertThat(AndroidTexUploadCache.Written).IsEqual(1L);

		using (Image? second = scene.Load(out bool alpha, out bool served))
		{
			AssertThat(served).IsTrue();
			AssertThat(alpha).IsTrue();
			AssertThat(second!.GetFormat()).IsEqual(target);
			AssertThat(second.GetWidth()).IsEqual(uploadEdge);
			AssertSame(second, reference);
		}

		AssertThat(scene.Reads).IsEqual(1);
		AssertThat(AndroidTexUploadCache.Served).IsEqual(1L);
		AssertThat(AndroidTexUploadCache.Written).IsEqual(1L);

		// The next launch: the key from its file, the form from its own.
		AndroidTexSourceKeys.ResetForTests();
		using (Image? third = scene.Load(out _, out bool served))
		{
			AssertThat(served).IsTrue();
			AssertSame(third!, reference);
		}

		AssertThat(scene.Reads).IsEqual(1);

		// The decode itself is still there for everything else.
		AndroidTexUploadCache.Enabled = false;
		using Image? decoded = Engine_TexLibrary.Decode(TexturePath, TexSemantic.Ui, scene.Read, out _);
		AssertSame(decoded!, bc);
	}

	[TestCase]
	[RequireGodotRuntime]
	public void AnEntryKeepsItsAlphaClassification()
	{
		byte[] source = Noise(64, 1);
		using var scene = new Scene(source);
		using Image bc = Bc(Image.Format.Dxt5, 64, 64, 1);
		scene.CacheDecode(source, bc, hasAlpha: false);
		scene.Load(out bool first, out _)?.Dispose();
		AndroidTexUploadCache.FlushForTests();
		using Image? hit = scene.Load(out bool second, out bool served);
		AssertThat(served).IsTrue();
		AssertThat(first).IsFalse();
		AssertThat(second).IsFalse();
	}

	[TestCase]
	[RequireGodotRuntime]
	public void TheTierDecidesTheFormAndTheFileIsReplaced()
	{
		byte[] source = Noise(64, 2);
		using var scene = new Scene(source, lowTier: true);
		using Image bc = Bc(Image.Format.RgtcRg, 512, 512, 2);
		scene.CacheDecode(source, bc, hasAlpha: false);
		scene.Load(out _, out _)?.Dispose();
		AndroidTexUploadCache.FlushForTests();
		string file = scene.Files.Single();

		scene.Tier(low: false);
		using (Image? full = scene.Load(out _, out bool served))
		{
			AssertThat(served).IsFalse();
			AssertThat(full!.GetWidth()).IsEqual(512);
		}

		AssertThat(AndroidTexUploadCache.Missed).IsEqual(1L);
		AndroidTexUploadCache.FlushForTests();
		AssertThat(scene.Files.Single()).IsEqual(file);
		using (Image? hit = scene.Load(out _, out bool served))
		{
			AssertThat(served).IsTrue();
			AssertThat(hit!.GetWidth()).IsEqual(512);
		}
	}

	[TestCase]
	[RequireGodotRuntime]
	public void TheRecipeCoversTheTranscoderAndThePolicy()
	{
		AndroidTextureUploadPolicy baseline = AndroidTextureUploadPolicy.Capture(true, _ => false);
		ulong recipe = AndroidTexUploadCache.RecipeFor(baseline);
		AssertThat(AndroidTexUploadCache.RecipeFor(baseline)).IsEqual(recipe);
		AssertThat(AndroidTexUploadCache.RecipeFor(baseline, Engine_AstcTranscode.Version + 1) != recipe).IsTrue();
		AssertThat(AndroidTexUploadCache.RecipeFor(baseline with { ReduceNormalMaps = true }) != recipe).IsTrue();
		AssertThat(AndroidTexUploadCache.RecipeFor(baseline with { Astc4x4 = true }) != recipe).IsTrue();
		AssertThat(AndroidTexUploadCache.RecipeFor(baseline with { TranscodeAstc = false }) != recipe).IsTrue();
		AssertThat(AndroidTexUploadCache.RecipeFor(baseline with { Bc5 = true }) != recipe).IsTrue();
	}

	[TestCase]
	[RequireGodotRuntime]
	public void ADamagedFileIsAMissAndIsWrittenAgain()
	{
		byte[] source = Noise(64, 3);
		using var scene = new Scene(source);
		using Image bc = Bc(Image.Format.Dxt1, 256, 256, 3);
		scene.CacheDecode(source, bc, hasAlpha: false);
		using Image reference = AndroidTextureUpload.PrepareAhead(bc, world: true);
		scene.Load(out _, out _)?.Dispose();
		AndroidTexUploadCache.FlushForTests();
		string file = scene.Files.Single();
		byte[] good = File.ReadAllBytes(file);

		var damage = new List<byte[]>
		{
			Array.Empty<byte>(),
			good[..10],
			good[..(good.Length - 1)],
			good.Concat(new byte[] { 0 }).ToArray(),
			new byte[good.Length],
		};
		foreach (int at in new[] { 4, AndroidTexUploadHeader.Size, good.Length / 2, good.Length - 1 })
		{
			byte[] flipped = (byte[])good.Clone();
			flipped[at] ^= 0x40;
			damage.Add(flipped);
		}

		foreach (byte[] bad in damage)
		{
			File.WriteAllBytes(file, bad);
			using (Image? fallback = scene.Load(out _, out bool served))
			{
				AssertThat(served).IsFalse();
				AssertSame(fallback!, reference);
			}

			AndroidTexUploadCache.FlushForTests();
			AssertThat(File.ReadAllBytes(file).AsSpan().SequenceEqual(good)).IsTrue();
		}

		File.Delete(file);
		scene.Load(out _, out bool afterDelete)?.Dispose();
		AssertThat(afterDelete).IsFalse();
	}

	[TestCase]
	[RequireGodotRuntime]
	public void NoIdentityMeansNoLookupAndNoFile()
	{
		byte[] source = Noise(64, 4);
		using var scene = new Scene(source);
		using Image bc = Bc(Image.Format.Dxt1, 64, 64, 4);
		scene.CacheDecode(source, bc, hasAlpha: false);
		scene.NoIdentity = true;

		using Image? image = AndroidTexUploadCache.TryServe(TexturePath, scene.Read, out AndroidTexSourceKeys.Ticket ticket, out _);
		AssertThat(image is null).IsTrue();
		AssertThat(ticket.Issued).IsFalse();
		using Image decoded = Bc(Image.Format.Dxt1, 64, 64, 5);
		AssertThat(ReferenceEquals(AndroidTexUploadCache.Remember(ticket, decoded, false), decoded)).IsTrue();
		AndroidTexUploadCache.FlushForTests();
		AssertThat(scene.Files.Length).IsEqual(0);
		AssertThat(AndroidTexUploadCache.Missed).IsEqual(0L);
	}

	[TestCase]
	[RequireGodotRuntime]
	public void AMountSwapDuringTheDecodeKeepsNothing()
	{
		byte[] source = Noise(64, 5);
		using var scene = new Scene(source);
		using Image bc = Bc(Image.Format.Dxt1, 64, 64, 5);
		scene.CacheDecode(source, bc, hasAlpha: false);

		using Image? none = AndroidTexUploadCache.TryServe(TexturePath, scene.Read, out AndroidTexSourceKeys.Ticket ticket, out _);
		using Image? image = Engine_TexLibrary.Decode(TexturePath, TexSemantic.Ui, scene.Read, out bool alpha);

		// A new mount makes new file objects, whatever it serves.
		BnkFileSystem old = scene.Mount;
		scene.Mount = BnkFileSystem.Mount(System.IO.Path.Combine(scene.Root, "game"));
		old.Dispose();

		using Image? upload = AndroidTexUploadCache.Remember(ticket, image, alpha);
		AndroidTexUploadCache.FlushForTests();
		AssertThat(upload!.GetFormat()).IsEqual(Image.Format.Astc4X4);
		AssertThat(scene.Files.Length).IsEqual(0);
	}

	[TestCase]
	[RequireGodotRuntime]
	public void AFullQueueLeavesTheRestForLaterAndNeverWaits()
	{
		byte[] source = Noise(64, 6);
		using var scene = new Scene(source);
		using Image bc = Bc(Image.Format.Dxt1, 256, 256, 6);
		scene.CacheDecode(source, bc, hasAlpha: false);

		using var gate = new ManualResetEventSlim(false);
		AndroidTexUploadCache.WriterGateForTests = gate;
		AndroidTexUploadCache.MaximumQueuedBytes = 1;

		// The first goes in whatever the cap, or an oversized texture could never be kept.
		var clock = Stopwatch.StartNew();
		for (int i = 0; i < 5; ++i)
		{
			using Image? upload = scene.Load(out _, out _);
			AssertThat(upload!.GetFormat()).IsEqual(Image.Format.Astc4X4);
		}

		AssertThat(clock.Elapsed.TotalSeconds < 30).IsTrue();
		AssertThat(AndroidTexUploadCache.Skipped).IsEqual(4L);
		AssertThat(AndroidTexUploadCache.Written).IsEqual(0L);
		gate.Set();
		AndroidTexUploadCache.FlushForTests();
		AndroidTexUploadCache.WriterGateForTests = null;
		AssertThat(AndroidTexUploadCache.Written).IsEqual(1L);

		AndroidTexUploadCache.MaximumQueuedBytes = 32 * 1024 * 1024;
		scene.Load(out _, out bool served)?.Dispose();
		AssertThat(served).IsTrue();
	}

	[TestCase]
	[RequireGodotRuntime]
	public void TheBudgetSweepCountsAndEvictsThemAndALoadThenFallsBack()
	{
		byte[] source = Noise(64, 7);
		using var scene = new Scene(source);
		using Image bc = Bc(Image.Format.Dxt1, 256, 256, 7);
		scene.CacheDecode(source, bc, hasAlpha: false);
		using Image reference = AndroidTextureUpload.PrepareAhead(bc, world: true);
		scene.Load(out _, out _)?.Dispose();
		AndroidTexUploadCache.FlushForTests();
		string file = scene.Files.Single();
		long size = new FileInfo(file).Length;

		// What a kill during a write leaves.
		string stray = file + ".9.tmp";
		File.WriteAllBytes(stray, new byte[16]);

		// Within budget: kept, and only the leftover goes.
		Engine_TexCache.SweepNow(scene.CacheRoot, size);
		AssertThat(File.Exists(file)).IsTrue();
		AssertThat(File.Exists(stray)).IsFalse();

		Engine_TexCache.SweepNow(scene.CacheRoot, size - 1);
		AssertThat(File.Exists(file)).IsFalse();

		using (Image? fallback = scene.Load(out _, out bool served))
		{
			AssertThat(served).IsFalse();
			AssertSame(fallback!, reference);
		}

		// A converter bump is a stale epoch, which goes whole.
		AndroidTexUploadCache.FlushForTests();
		AssertThat(File.Exists(file)).IsTrue();
		string oldEpoch = System.IO.Path.Combine(scene.CacheRoot, "v0", "upload", "aa");
		Directory.CreateDirectory(oldEpoch);
		File.WriteAllBytes(System.IO.Path.Combine(oldEpoch, "a.rftx"), new byte[16]);
		Engine_TexCache.SweepNow(scene.CacheRoot, long.MaxValue);
		AssertThat(Directory.Exists(System.IO.Path.Combine(scene.CacheRoot, "v0"))).IsFalse();
		AssertThat(File.Exists(file)).IsTrue();
	}

	[TestCase]
	[RequireGodotRuntime]
	public void OffChangesNothing()
	{
		byte[] source = Noise(64, 8);
		using var scene = new Scene(source);
		using Image bc = Bc(Image.Format.Dxt1, 64, 64, 8);
		scene.CacheDecode(source, bc, hasAlpha: false);
		scene.Load(out _, out _)?.Dispose();
		AndroidTexUploadCache.FlushForTests();

		AndroidTexUploadCache.Enabled = false;
		using Image? none = AndroidTexUploadCache.TryServe(TexturePath, scene.Read, out AndroidTexSourceKeys.Ticket ticket, out _);
		AssertThat(none is null).IsTrue();
		AssertThat(ticket.Issued).IsFalse();
		using Image decoded = Bc(Image.Format.Dxt1, 64, 64, 8);
		AssertThat(ReferenceEquals(AndroidTexUploadCache.Remember(ticket, decoded, false), decoded)).IsTrue();
	}

	[TestCase]
	[RequireGodotRuntime]
	public void ThePolicyLeavingAFormatAloneKeepsNothing()
	{
		byte[] source = Noise(64, 9);
		using var scene = new Scene(source);
		AndroidTextureUpload.SetPolicyForTests(AndroidTextureUploadPolicy.Capture(true, _ => true, true, true));
		using Image bc = Bc(Image.Format.Dxt1, 64, 64, 9);
		scene.CacheDecode(source, bc, hasAlpha: false);
		using Image? image = scene.Load(out _, out bool served);
		AndroidTexUploadCache.FlushForTests();
		AssertThat(served).IsFalse();
		AssertThat(image!.GetFormat()).IsEqual(Image.Format.Dxt1);
		AssertThat(scene.Files.Length).IsEqual(0);
	}

	[TestCase]
	[RequireGodotRuntime]
	public void TheLibraryUploadsTheFormWithoutARead()
	{
		byte[] source = Noise(64, 10);
		using var scene = new Scene(source);
		using Image bc = Bc(Image.Format.Dxt1, 256, 256, 10);
		scene.CacheDecode(source, bc, hasAlpha: true);

		Engine_TexLibrary.Clear();
		Texture2D? first = Engine_TexLibrary.Get(TexturePath, TexSemantic.Albedo, scene.Read, out bool alpha);
		AssertThat(first is not null).IsTrue();
		AssertThat(alpha).IsTrue();
		AssertThat(scene.Reads).IsEqual(1);
		AssertThat(AndroidTexUploadCache.Served).IsEqual(0L);
		AndroidTexUploadCache.FlushForTests();
		AssertThat(AndroidTexUploadCache.Written).IsEqual(1L);

		Engine_TexLibrary.Clear();
		Texture2D? second = Engine_TexLibrary.Get(TexturePath, TexSemantic.Albedo, scene.Read, out bool again);
		AssertThat(second is not null).IsTrue();
		AssertThat(again).IsTrue();
		AssertThat(scene.Reads).IsEqual(1);
		AssertThat(AndroidTexUploadCache.Served).IsEqual(1L);
		Engine_TexLibrary.Clear();
	}
}
