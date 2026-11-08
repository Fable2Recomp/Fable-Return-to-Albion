using System;
using GdUnit4;
using Godot;
using Refii.Textures;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>The composed ORM cache answers with the bytes the uncached pack produces, for every input it is keyed on.</summary>
[TestSuite]
public sealed class ComposedOrmCacheTests
{
	private const float PowerMin = 1f, PowerMax = 100f, Ao = 0.5f, Metal = 1f;

	private static byte[] Noise(int seed, int width, int height)
	{
		var bytes = new byte[width * height * 4];
		new Random(seed).NextBytes(bytes);
		return bytes;
	}

	private static ImageTexture Texture(byte[] rgba, int width, int height, bool compress)
	{
		using Image image = Image.CreateFromData(width, height, false, Image.Format.Rgba8, rgba);
		if (compress)
		{
			image.GenerateMipmaps(true);
			Engine_TexImage.CompressBlocks(image, true);
		}

		return ImageTexture.CreateFromImage(image);
	}

	private static (byte[]? Data, bool Height) Uncached(
		Texture2D? specular, Texture2D? normal, float min, float max, float ao, float metal, bool height)
	{
		using Image? spec = specular?.GetImage();
		using Image? norm = normal?.GetImage();
		ImageTexture? packed = Engine_TexLibrary.PackComposedOrm(spec, norm, min, max, ao, metal, height, out bool hasHeight);
		using Image? image = packed?.GetImage();
		return (image?.GetData(), hasHeight);
	}

	private static (byte[]? Data, bool Height, ImageTexture? Texture) Cached(
		Texture2D? specular, Texture2D? normal, float min, float max, float ao, float metal, bool height)
	{
		ImageTexture? packed = Engine_TexLibrary.ComposedOrm(specular, normal, min, max, ao, metal, height, out bool hasHeight);
		using Image? image = packed?.GetImage();
		return (image?.GetData(), hasHeight, packed);
	}

	private static void AssertSame((byte[]? Data, bool Height) uncached, (byte[]? Data, bool Height, ImageTexture? Texture) cached)
	{
		AssertThat(cached.Height).IsEqual(uncached.Height);
		AssertThat(cached.Data is null).IsEqual(uncached.Data is null);
		if (uncached.Data is not null)
		{
			AssertThat(cached.Data).IsEqual(uncached.Data);
		}
	}

	[TestCase, RequireGodotRuntime]
	public void AKeptOrmIsTheBytesTheUncachedPackProduces()
	{
		Engine_TexLibrary.ForgetComposedOrms();
		byte[] specBytes = Noise(1, 64, 64);
		byte[] normBytes = Noise(2, 64, 64);
		ImageTexture spec = Texture(specBytes, 64, 64, false);
		ImageTexture norm = Texture(normBytes, 64, 64, true);

		(byte[]? Data, bool Height) reference = Uncached(spec, norm, PowerMin, PowerMax, Ao, Metal, true);
		AssertThat(reference.Data).IsNotNull();
		AssertThat(reference.Height).IsTrue();

		(long packed, long byIdentity, long byContent) = Engine_TexLibrary.ComposedOrmWork;
		var first = Cached(spec, norm, PowerMin, PowerMax, Ao, Metal, true);
		AssertSame(reference, first);
		AssertThat(Engine_TexLibrary.ComposedOrmWork.Packed).IsEqual(packed + 1);

		// Equal content in other textures: found by content, the same texture, no second pack.
		ImageTexture specAgain = Texture(specBytes, 64, 64, false);
		ImageTexture normAgain = Texture(normBytes, 64, 64, true);
		var again = Cached(specAgain, normAgain, PowerMin, PowerMax, Ao, Metal, true);
		AssertSame(reference, again);
		AssertThat(ReferenceEquals(again.Texture, first.Texture)).IsTrue();
		AssertThat(Engine_TexLibrary.ComposedOrmWork.Packed).IsEqual(packed + 1);
		AssertThat(Engine_TexLibrary.ComposedOrmWork.ByContent).IsEqual(byContent + 1);

		// Unchanging textures: the first ask keys their identity, the second needs no read-back.
		Engine_TexLibrary.MarkUnchanging(specAgain);
		Engine_TexLibrary.MarkUnchanging(normAgain);
		Cached(specAgain, normAgain, PowerMin, PowerMax, Ao, Metal, true);
		var byId = Cached(specAgain, normAgain, PowerMin, PowerMax, Ao, Metal, true);
		AssertSame(reference, byId);
		AssertThat(ReferenceEquals(byId.Texture, first.Texture)).IsTrue();
		AssertThat(Engine_TexLibrary.ComposedOrmWork.ByIdentity).IsEqual(byIdentity + 1);
		AssertThat(Engine_TexLibrary.ComposedOrmWork.Packed).IsEqual(packed + 1);
	}

	[TestCase, RequireGodotRuntime]
	public void EveryInputTheKeyNamesChangesTheAnswer()
	{
		Engine_TexLibrary.ForgetComposedOrms();
		byte[] specBytes = Noise(3, 32, 32);
		byte[] normBytes = Noise(4, 32, 32);
		ImageTexture spec = Texture(specBytes, 32, 32, false);
		ImageTexture norm = Texture(normBytes, 32, 32, true);
		Engine_TexLibrary.MarkUnchanging(spec);
		Engine_TexLibrary.MarkUnchanging(norm);
		Cached(spec, norm, PowerMin, PowerMax, Ao, Metal, true);

		byte[] specChanged = (byte[])specBytes.Clone();
		specChanged[3] ^= 0x80;
		byte[] normChanged = (byte[])normBytes.Clone();
		normChanged[0] ^= 0x40;
		ImageTexture otherSpec = Texture(specChanged, 32, 32, false);
		ImageTexture otherNorm = Texture(normChanged, 32, 32, true);
		ImageTexture flatNorm = Texture(new byte[32 * 32 * 4], 32, 32, true);

		var cases = new (Texture2D? Spec, Texture2D? Norm, float Min, float Max, float Ao, float Metal, bool Height)[]
		{
			(spec, norm, PowerMin, PowerMax, Ao, Metal, true),
			(otherSpec, norm, PowerMin, PowerMax, Ao, Metal, true),
			(spec, otherNorm, PowerMin, PowerMax, Ao, Metal, true),
			(spec, flatNorm, PowerMin, PowerMax, Ao, Metal, true),
			(null, norm, PowerMin, PowerMax, Ao, Metal, true),
			(spec, null, PowerMin, PowerMax, Ao, Metal, true),
			(spec, norm, 2f, PowerMax, Ao, Metal, true),
			(spec, norm, PowerMin, 50f, Ao, Metal, true),
			(spec, norm, PowerMin, PowerMax, 0f, Metal, true),
			(spec, norm, PowerMin, PowerMax, Ao, 0.25f, true),
			(spec, norm, PowerMin, PowerMax, Ao, Metal, false),
			(null, null, PowerMin, PowerMax, Ao, Metal, true),
		};

		// Twice over: the first pass packs (or answers by content), the second answers from what was kept.
		for (int pass = 0; pass < 2; pass++)
		{
			foreach (var c in cases)
			{
				AssertSame(
					Uncached(c.Spec, c.Norm, c.Min, c.Max, c.Ao, c.Metal, c.Height),
					Cached(c.Spec, c.Norm, c.Min, c.Max, c.Ao, c.Metal, c.Height));
			}
		}
	}

	[TestCase, RequireGodotRuntime]
	public void PastTheLimitOnlyWhatIsStillHeldIsFoundAgain()
	{
		Engine_TexLibrary.ForgetComposedOrms();
		var held = new ImageTexture?[Engine_TexLibrary.ComposedOrmLimit + 4];
		for (int i = 0; i < held.Length; i++)
		{
			held[i] = Engine_TexLibrary.ComposedOrm(
				Texture(Noise(100 + i, 8, 8), 8, 8, false), null, PowerMin, PowerMax, Ao, Metal, false, out _);
			AssertThat(held[i]).IsNotNull();
		}

		long packed = Engine_TexLibrary.ComposedOrmWork.Packed;
		ImageTexture? first = Engine_TexLibrary.ComposedOrm(
			Texture(Noise(100, 8, 8), 8, 8, false), null, PowerMin, PowerMax, Ao, Metal, false, out _);
		AssertThat(ReferenceEquals(first, held[0])).IsTrue();
		AssertThat(Engine_TexLibrary.ComposedOrmWork.Packed).IsEqual(packed);
		Engine_TexLibrary.ForgetComposedOrms();
	}
}
