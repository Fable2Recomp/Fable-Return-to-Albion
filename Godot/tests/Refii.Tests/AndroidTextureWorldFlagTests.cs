using System;
using System.IO;
using System.Linq;
using System.Reflection;
using GdUnit4;
using Godot;
using Refii.Textures;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The low-memory tier's colour reduction, which only a world material's texture may take: anything
/// that reads its own size to place itself keeps it. Normal maps shrink whichever is asked.
/// </summary>
[TestSuite]
public sealed class AndroidTextureWorldFlagTests
{
    private const int Colour = AndroidTextureUploadPolicy.ReducedColorMapMinimumEdge;
    private const int Normal = AndroidTextureUploadPolicy.ReducedNormalMapMinimumEdge;

    /// <summary>An Adreno 640 in the low-memory tier: no BC at all, ASTC where asked.</summary>
    private static AndroidTextureUploadPolicy LowTier(bool astc = true) =>
        new(true, false, false, false, false, false, true, astc, true, true);

    /// <summary>The same GPU with memory to spare.</summary>
    private static readonly AndroidTextureUploadPolicy Plenty = LowTier() with { ReduceNormalMaps = false };

    private static readonly AndroidTextureUploadPolicy Desktop = new(false, true, true, true, true, true, true);

    [TestCase]
    public void ColourMapsShrinkOnlyForAWorldMaterial()
    {
        AndroidTextureUploadPolicy low = LowTier();
        foreach (Image.Format format in new[] { Image.Format.Dxt1, Image.Format.Dxt5 })
        {
            AssertThat(low.DropsTopMip(format, Colour, Colour, true, world: true)).IsTrue();
            AssertThat(low.DropsTopMip(format, Colour, Colour, true, world: false)).IsFalse();
            AssertThat(low.DropsTopMip(format, 4096, 4096, true, world: true)).IsTrue();
            AssertThat(low.DropsTopMip(format, 4096, 4096, true, world: false)).IsFalse();

            // Smaller than the colour edge, on either side: world or not, it keeps its size.
            AssertThat(low.DropsTopMip(format, Colour - 1, 4096, true, world: true)).IsFalse();
            AssertThat(low.DropsTopMip(format, 4096, Colour - 1, true, world: true)).IsFalse();
            AssertThat(low.DropsTopMip(format, Normal, Normal, true, world: true)).IsFalse();

            // Nothing to fall back on.
            AssertThat(low.DropsTopMip(format, Colour, Colour, false, world: true)).IsFalse();
        }
    }

    [TestCase]
    public void NormalMapsShrinkEitherWay()
    {
        AndroidTextureUploadPolicy low = LowTier();
        foreach (bool world in new[] { true, false })
        {
            AssertThat(low.DropsTopMip(Image.Format.RgtcRg, Normal, Normal, true, world)).IsTrue();
            AssertThat(low.DropsTopMip(Image.Format.RgtcRg, Normal - 1, Normal, true, world)).IsFalse();
            AssertThat(low.DropsTopMip(Image.Format.RgtcRg, Normal, Normal, false, world)).IsFalse();
        }
    }

    /// <summary>The flag asks for a reduction the policy allows. It cannot make one happen anywhere else.</summary>
    [TestCase]
    public void TheFlagGrantsNothingThePolicyDoesNot()
    {
        AndroidTextureUploadPolicy withBc = LowTier() with { Bc1 = true, Bc3 = true, Bc5 = true };
        foreach (AndroidTextureUploadPolicy policy in new[] { Plenty, withBc, Desktop })
        {
            foreach (Image.Format format in new[] { Image.Format.Dxt1, Image.Format.Dxt5, Image.Format.RgtcRg })
            {
                AssertThat(policy.DropsTopMip(format, 4096, 4096, true, world: true)).IsFalse();
                AssertThat(policy.DropsTopMip(format, 4096, 4096, true, world: false)).IsFalse();
            }
        }

        foreach (Image.Format format in new[] { Image.Format.Dxt3, Image.Format.RgtcR, Image.Format.Rgb8, Image.Format.Rgba8, Image.Format.R8 })
        {
            AssertThat(LowTier().DropsTopMip(format, 4096, 4096, true, world: true)).IsFalse();
        }
    }

    /// <summary>An upload nobody marked keeps its size: the safe side is the default. The policy's own
    /// default is the other way round, so callers of the upload calls must not be read as asking it.</summary>
    [TestCase]
    public void AnUnmarkedUploadIsNotAWorldOne()
    {
        foreach (Type type in new[] { typeof(AndroidTextureUpload), typeof(Engine_TextureUpload) })
        {
            foreach (string name in new[] { "CreateTexture", "SetImage", "CreateLayered" })
            {
                ParameterInfo world = type.GetMethod(name)!.GetParameters().Single(p => p.Name == "world");
                AssertThat($"{type.Name}.{name}: {world.DefaultValue}").IsEqual($"{type.Name}.{name}: False");
            }
        }

        ParameterInfo policy = typeof(AndroidTextureUploadPolicy).GetMethod("DropsTopMip")!.GetParameters().Single(p => p.Name == "world");
        AssertThat(policy.DefaultValue).IsEqual(true);
    }

    /// <summary>The interface reads its textures' sizes to lay itself out, and no part of it may ask.</summary>
    [TestCase]
    public void TheInterfaceNeverAsksForAWorldUpload()
    {
        string src = Path.GetFullPath(Path.Combine(SourceDirectory(), "..", "..", "src"));
        string[] offenders = new[] { Path.Combine("Engine", "ui"), Path.Combine("Titles", "Fable2", "ui"), Path.Combine("Titles", "Fable2", "pubgames") }
            .Select(folder => Path.Combine(src, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
            .Where(file => File.ReadAllText(file).Contains("world: true", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(src, file))
            .ToArray();
        AssertThat(string.Join("\n", offenders)).IsEqual(string.Empty);
    }

    private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetDirectoryName(path)!;

    /// <summary>A policy in place of the captured one, which a desktop run never captures.</summary>
    private sealed class PolicyInPlace : IDisposable
    {
        private static readonly FieldInfo Field =
            typeof(AndroidTextureUpload).GetField("_policy", BindingFlags.NonPublic | BindingFlags.Static)!;

        private readonly object _previous = Field.GetValue(null)!;

        public PolicyInPlace(AndroidTextureUploadPolicy policy) => Field.SetValue(null, policy);

        public void Dispose() => Field.SetValue(null, _previous);
    }

    private static Image Prepare(Image image, bool world) =>
        (Image)typeof(AndroidTextureUpload).GetMethod("Prepare", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { image, world })!;

    /// <summary>Whole 4x4 blocks in every level down to 1x1, as the game's textures are stored.</summary>
    private static int BlocksInChain(int width, int height, bool mipmaps)
    {
        int blocks = 0;
        while (true)
        {
            blocks += (width + 3) / 4 * ((height + 3) / 4);
            if (!mipmaps || (width == 1 && height == 1)) return blocks;
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }
    }

    private static Image Blocks(Image.Format format, int width, int height, bool mipmaps = true)
    {
        int size = format == Image.Format.Dxt1 ? 8 : 16;
        var data = new byte[BlocksInChain(width, height, mipmaps) * size];
        new Random(width * 31 + height + (int)format).NextBytes(data);
        return Image.CreateFromData(width, height, mipmaps, format, data);
    }

    private static string Hex(Image image) => Convert.ToHexString(image.GetData());

    /// <summary>A reduced image is the full one without its first level, block for block, so a world
    /// texture is not re-encoded by being shrunk.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void AWorldColourMapIsTheFullOneWithoutItsTopLevel()
    {
        using var policy = new PolicyInPlace(LowTier());
        foreach ((Image.Format format, bool bc3) in new[] { (Image.Format.Dxt1, false), (Image.Format.Dxt5, true) })
        {
            using Image source = Blocks(format, Colour, Colour);
            string before = Hex(source);

            using Image world = Prepare(source, true);
            using Image plain = Prepare(source, false);

            AssertThat(world.GetFormat()).IsEqual(Image.Format.Astc4X4);
            AssertThat(world.GetWidth()).IsEqual(Colour / 2);
            AssertThat(world.GetHeight()).IsEqual(Colour / 2);
            AssertThat(world.HasMipmaps()).IsTrue();

            AssertThat(plain.GetFormat()).IsEqual(Image.Format.Astc4X4);
            AssertThat(plain.GetWidth()).IsEqual(Colour);
            AssertThat(plain.HasMipmaps()).IsTrue();

            // Not marked world: the bytes are the plain transcode of the whole chain.
            var whole = new byte[source.GetData().Length / (bc3 ? 16 : 8) * 16];
            AssertThat(Engine_AstcTranscode.Transcode(source.GetData(), bc3, whole)).IsTrue();
            AssertThat(Hex(plain)).IsEqual(Convert.ToHexString(whole));

            // Marked: the same blocks, from the second level on.
            int top = Colour / 4 * (Colour / 4) * 16;
            AssertThat(Hex(world)).IsEqual(Convert.ToHexString(whole.AsSpan(top)));

            // The caller's image is the caller's.
            AssertThat(source.GetWidth()).IsEqual(Colour);
            AssertThat(source.GetFormat()).IsEqual(format);
            AssertThat(Hex(source)).IsEqual(before);
        }
    }

    [TestCase]
    [RequireGodotRuntime]
    public void ANonSquareWorldMapHalvesBothSides()
    {
        using var policy = new PolicyInPlace(LowTier());
        using Image wide = Blocks(Image.Format.Dxt1, 2048, Colour);
        using Image world = Prepare(wide, true);
        AssertThat(world.GetWidth()).IsEqual(1024);
        AssertThat(world.GetHeight()).IsEqual(Colour / 2);
        AssertThat(world.GetFormat()).IsEqual(Image.Format.Astc4X4);
        AssertThat(world.GetData().Length).IsEqual(BlocksInChain(1024, Colour / 2, true) * 16);

        // The short side decides: under the edge, it stays.
        using Image thin = Blocks(Image.Format.Dxt1, 2048, Colour / 2);
        using Image kept = Prepare(thin, true);
        AssertThat(kept.GetWidth()).IsEqual(2048);
        AssertThat(kept.GetHeight()).IsEqual(Colour / 2);
    }

    [TestCase]
    [RequireGodotRuntime]
    public void ASmallOrMiplessWorldMapKeepsItsSize()
    {
        using var policy = new PolicyInPlace(LowTier());
        using Image small = Blocks(Image.Format.Dxt5, Colour / 2, Colour / 2);
        using Image keptSmall = Prepare(small, true);
        AssertThat(keptSmall.GetWidth()).IsEqual(Colour / 2);

        using Image mipless = Blocks(Image.Format.Dxt1, Colour, Colour, mipmaps: false);
        using Image keptMipless = Prepare(mipless, true);
        AssertThat(keptMipless.GetWidth()).IsEqual(Colour);
        AssertThat(keptMipless.HasMipmaps()).IsFalse();
    }

    /// <summary>A GPU without ASTC expands to RGBA8 instead, under the same rule.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void WithoutAstcTheRuleIsTheSame()
    {
        using var policy = new PolicyInPlace(LowTier(astc: false));
        using Image source = Blocks(Image.Format.Dxt1, Colour, Colour);
        using Image world = Prepare(source, true);
        using Image plain = Prepare(source, false);
        AssertThat(world.GetFormat()).IsEqual(Image.Format.Rgba8);
        AssertThat(plain.GetFormat()).IsEqual(Image.Format.Rgba8);
        AssertThat(world.GetWidth()).IsEqual(Colour / 2);
        AssertThat(plain.GetWidth()).IsEqual(Colour);
        AssertThat(Hex(world)).IsEqual(Convert.ToHexString(plain.GetData().AsSpan(Colour * Colour * 4)));
    }

    [TestCase]
    [RequireGodotRuntime]
    public void ANormalMapIsTheSameWhicheverIsAsked()
    {
        using var policy = new PolicyInPlace(LowTier());
        using Image source = Blocks(Image.Format.RgtcRg, Normal, Normal);
        using Image world = Prepare(source, true);
        using Image plain = Prepare(source, false);
        AssertThat(world.GetFormat()).IsEqual(Image.Format.Rg8);
        AssertThat(world.GetWidth()).IsEqual(Normal / 2);
        AssertThat(plain.GetWidth()).IsEqual(Normal / 2);
        AssertThat(Hex(world)).IsEqual(Hex(plain));
    }

    /// <summary>Where nothing has to change, nothing is copied, world or not.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void ASupportedFormatIsHandedBackAsItIs()
    {
        using Image source = Blocks(Image.Format.Dxt1, Colour, Colour);
        foreach (AndroidTextureUploadPolicy allowed in new[] { Desktop, LowTier() with { Bc1 = true } })
        {
            using var policy = new PolicyInPlace(allowed);
            AssertThat(ReferenceEquals(Prepare(source, true), source)).IsTrue();
            AssertThat(ReferenceEquals(Prepare(source, false), source)).IsTrue();
        }
    }

    /// <summary>Memory to spare: a world map transcodes at its full size, the same bytes as an unmarked one.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void AboveTheLowTierTheFlagChangesNoByte()
    {
        using var policy = new PolicyInPlace(Plenty);
        using Image source = Blocks(Image.Format.Dxt5, Colour, Colour);
        using Image world = Prepare(source, true);
        using Image plain = Prepare(source, false);
        AssertThat(world.GetWidth()).IsEqual(Colour);
        AssertThat(Hex(world)).IsEqual(Hex(plain));
    }

    /// <summary>What a worker prepares ahead is effects and the pub games' art, which is not world
    /// material: a colour map keeps its size there, in the same tier.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void PreparingAheadIsNotAWorldUpload()
    {
        using var policy = new PolicyInPlace(LowTier());
        using Image colour = Blocks(Image.Format.Dxt5, Colour, Colour);
        using Image ahead = AndroidTextureUpload.PrepareAhead(colour);
        AssertThat(ahead.GetFormat()).IsEqual(Image.Format.Astc4X4);
        AssertThat(ahead.GetWidth()).IsEqual(Colour);

        using Image normal = Blocks(Image.Format.RgtcRg, Normal, Normal);
        using Image normalAhead = AndroidTextureUpload.PrepareAhead(normal);
        AssertThat(normalAhead.GetWidth()).IsEqual(Normal / 2);
    }

    /// <summary>The three calls that reach the GPU carry the flag down: the texture's own size is what
    /// a shader or a layout then sees.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void TheUploadCallsCarryTheFlag()
    {
        using var policy = new PolicyInPlace(LowTier());
        using Image source = Blocks(Image.Format.Dxt1, Colour, Colour);

        using (ImageTexture world = AndroidTextureUpload.CreateTexture(source, world: true))
            AssertThat(world.GetWidth()).IsEqual(Colour / 2);
        using (ImageTexture unmarked = AndroidTextureUpload.CreateTexture(source))
            AssertThat(unmarked.GetWidth()).IsEqual(Colour);

        // A replacement goes through the same rule as the texture it replaces.
        using (ImageTexture swapped = AndroidTextureUpload.CreateTexture(source, world: true))
        {
            AndroidTextureUpload.SetImage(swapped, source, world: true);
            AssertThat(swapped.GetWidth()).IsEqual(Colour / 2);
        }

        using (ImageTexture unmarked = AndroidTextureUpload.CreateTexture(source))
        {
            AndroidTextureUpload.SetImage(unmarked, source);
            AssertThat(unmarked.GetWidth()).IsEqual(Colour);
        }

        var layers = new Godot.Collections.Array<Image> { source, source };
        using (var array = new Texture2DArray())
        {
            AssertThat(AndroidTextureUpload.CreateLayered(array, layers, world: true)).IsEqual(Error.Ok);
            AssertThat(array.GetWidth()).IsEqual(Colour / 2);
            AssertThat(array.GetLayers()).IsEqual(2);
        }

        using (var array = new Texture2DArray())
        {
            AssertThat(AndroidTextureUpload.CreateLayered(array, layers)).IsEqual(Error.Ok);
            AssertThat(array.GetWidth()).IsEqual(Colour);
        }

        AssertThat(source.GetWidth()).IsEqual(Colour);
        AssertThat(source.GetFormat()).IsEqual(Image.Format.Dxt1);
    }

    /// <summary>Desktop goes straight to Godot, whatever the flag says.</summary>
    [TestCase]
    [RequireGodotRuntime]
    public void TheDesktopNeverShrinks()
    {
        using var policy = new PolicyInPlace(LowTier());
        using Image source = Blocks(Image.Format.Dxt1, Colour, Colour);
        using ImageTexture texture = Engine_TextureUpload.CreateTexture(source, world: true);
        AssertThat(texture.GetWidth()).IsEqual(Colour);
        Engine_TextureUpload.SetImage(texture, source, world: true);
        AssertThat(texture.GetWidth()).IsEqual(Colour);
        using var array = new Texture2DArray();
        var layers = new Godot.Collections.Array<Image> { source };
        AssertThat(Engine_TextureUpload.CreateLayered(array, layers, world: true)).IsEqual(Error.Ok);
        AssertThat(array.GetWidth()).IsEqual(Colour);
    }
}
