using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using Refii.Hero;
using Refii.Models;
using Refii.Textures;
using static GdUnit4.Assertions;
using Layer = Refii.Host.HostBridge.GuestTextureLayer;

namespace Refii.Tests;

/// <summary>A cheaper character composite has to be the same composite: every byte is held against the one-at-a-time sums.</summary>
[TestSuite]
public sealed class HeroCompositeCostTests
{
    /// <summary>The block encoder against its one-texel-at-a-time form, at every format, on noise, two-value and flat images, odd sizes included.</summary>
    [TestCase]
    public void BlocksMatchTheScalarEncoder()
    {
        var random = new Random(11);
        foreach (TexSurfaceFormat format in new[] { TexSurfaceFormat.Bc1, TexSurfaceFormat.Bc3, TexSurfaceFormat.Bc4, TexSurfaceFormat.Bc5 })
        {
            for (int run = 0; run < 400; run++)
            {
                int width = random.Next(1, 40), height = random.Next(1, 40);
                byte[] rgba = Pixels(random, width, height, run);
                byte[] expected = ScalarEncoder.Encode(rgba, width, height, format);
                AssertThat(Engine_BcEncoder.Encode(rgba, width, height, format)).IsEqual(expected);
            }
        }
    }

    /// <summary>Every blend mode, weights in and out of range and every coverage value, with the odd pixels past the last whole vector.</summary>
    [TestCase]
    public void BlendMatchesThePixelSums()
    {
        var random = new Random(5);
        byte[] colour = Pixels(random, 256, 256, 0), coverage = Pixels(random, 256, 256, 0);
        foreach (int mode in new[] { 0, 1, 2, 3, 7 })
        {
            foreach (float weight in new[] { 0f, 1e-4f, 0.3f, 1f, 1.5f, -0.2f, float.NaN, float.PositiveInfinity, 1e30f })
            {
                foreach (int pixels in new[] { 65536, 65533, 3, 1 })
                {
                    byte[] output = Pixels(random, 256, 256, 0), expected = (byte[])output.Clone();
                    Engine_HeroTextureKernels.Blend(output, colour, coverage, pixels, mode, weight);
                    ScalarBlend(expected, colour, coverage, pixels, mode, weight);
                    AssertThat(output).IsEqual(expected);
                }
            }
        }
    }

    /// <summary>The dye over all values of every channel pair, in place and not, at pivots from dark to white.</summary>
    [TestCase]
    public void DyeMatchesThePixelSums()
    {
        var pixels = new byte[256 * 256 * 4];
        for (int y = 0; y < 256; y++)
        {
            for (int x = 0; x < 256; x++)
            {
                int at = ((y * 256) + x) * 4;
                pixels[at] = (byte)x;
                pixels[at + 1] = (byte)y;
                pixels[at + 2] = (byte)(x ^ y);
                pixels[at + 3] = (byte)(x + y);
            }
        }

        foreach ((float[] colour, float[] intensity, float pivot) in new[]
        {
            (new[] { 1f, 0.5f, 0.25f }, new[] { 0.3f, 0.5f, 0.1f }, 0.55f),
            (new[] { 1f, 1f, 1f }, new[] { 0.3f, 0.6f, 0.1f }, 1f),
            (new[] { 0f, 0f, 1f }, new[] { 0.01f, 0.02f, 0.003f }, 0f),
        })
        {
            foreach (float dyed in new[] { 0f, 0.5f, 1f })
            {
                var curve = new[] { 0f, 0.1f, 0.4f, 0.7f, 1f };
                var expected = new byte[pixels.Length];
                ScalarDye(pixels, expected, 65536, colour, dyed, intensity, pivot, curve);

                var apart = new byte[pixels.Length];
                Engine_HeroTextureKernels.Colourise(pixels, apart, 65536, colour, dyed, intensity, pivot, curve);
                AssertThat(apart).IsEqual(expected);

                var inPlace = (byte[])pixels.Clone();
                Engine_HeroTextureKernels.Colourise(inPlace, inPlace, 65536, colour, dyed, intensity, pivot, curve);
                AssertThat(inPlace).IsEqual(expected);

                var tail = (byte[])pixels.Clone();
                Engine_HeroTextureKernels.Colourise(pixels, tail, 7, colour, dyed, intensity, pivot, curve);
                AssertThat(tail.AsSpan(0, 28).SequenceEqual(expected.AsSpan(0, 28))).IsTrue();
                AssertThat(tail.AsSpan(28).SequenceEqual(pixels.AsSpan(28))).IsTrue();
            }
        }
    }

    /// <summary>The whole chain encodes as each level would alone, without the image's data being copied out first.</summary>
    [TestCase, RequireGodotRuntime]
    public void CompressingAChainMatchesEncodingItsLevels()
    {
        byte[] rgba = Pixels(new Random(3), 64, 32, 0);
        using Image image = Image.CreateFromData(64, 32, false, Image.Format.Rgba8, rgba);
        image.GenerateMipmaps(false);
        byte[] chain = image.GetData();
        var expected = new List<byte>();
        int offset = 0, width = 64, height = 32;
        while (true)
        {
            expected.AddRange(Engine_BcEncoder.Encode(chain[offset..(offset + (width * height * 4))], width, height, TexSurfaceFormat.Bc5)!);
            offset += width * height * 4;
            if (width == 1 && height == 1) break;
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }

        AssertThat(Engine_TexImage.CompressBlocks(image, true)).IsTrue();
        AssertThat(image.GetFormat()).IsEqual(Image.Format.RgtcRg);
        AssertThat(image.GetData()).IsEqual(expected.ToArray());
    }

    /// <summary>Two workers wanting one composite: the second waits for the first, and the source is read once.</summary>
    [TestCase, RequireGodotRuntime]
    public void OneCompositeInFlightIsNotBuiltTwice()
    {
        const string path = "fixture/shared-morph.tex";
        Engine_HeroTextureMorph.Invalidate();
        Engine_TexPathIndex.SetPathSource(() => new[] { path });
        int reads = 0;
        using var reading = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        Engine_BnkMeshLoader.SetResolver(_ =>
        {
            Interlocked.Increment(ref reads);
            reading.Set();
            release.Wait(TimeSpan.FromSeconds(20));
            return null;
        });
        try
        {
            uint original = Engine_TexPathIndex.PathHash(path);
            var layers = new[] { new Layer { Texture = original, HasTint = true, Weight = 1, Brightness = 1 } };
            long joined = Engine_HeroTextureMorph.SavedWork.CompositesJoined;
            Task<Texture2D?> first = Task.Run(() => Engine_HeroTextureMorph.Composite(original, layers, null, Engine_HeroTextureMorph.OverlayMap.Diffuse));
            AssertThat(reading.Wait(TimeSpan.FromSeconds(20))).IsTrue();
            Task<Texture2D?> second = Task.Run(() => Engine_HeroTextureMorph.Composite(original, layers, null, Engine_HeroTextureMorph.OverlayMap.Diffuse));
            for (int wait = 0; wait < 200 && Engine_HeroTextureMorph.SavedWork.CompositesJoined == joined && !second.IsCompleted; wait++)
            {
                if (wait == 50) release.Set();
                Thread.Sleep(10);
            }

            release.Set();
            Task.WaitAll(first, second);
            AssertThat(reads).IsEqual(1);
            AssertThat(Engine_HeroTextureMorph.SavedWork.CompositesJoined).IsEqual(joined + 1);
        }
        finally
        {
            release.Set();
            Engine_HeroTextureMorph.Invalidate();
            Engine_TexPathIndex.SetPathSource(null);
            Engine_BnkMeshLoader.SetResolver(null);
        }
    }

    /// <summary>Past the limit a composite a material still draws with is found again; one nothing holds is not kept alive.</summary>
    [TestCase, RequireGodotRuntime]
    public void TheLimitForgetsOnlyWhatNothingHolds()
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;
        Engine_HeroTextureMorph.Invalidate();
        Type morph = typeof(Engine_HeroTextureMorph);
        var composited = (IDictionary)morph.GetField("Composited", Private)!.GetValue(null)!;
        MethodInfo makeRoom = morph.GetMethod("MakeRoomForComposite", Private)!;
        MethodInfo revive = morph.GetMethod("Revive", Private)!;

        using var material = new ShaderMaterial { Shader = new Shader { Code = "shader_type spatial; uniform sampler2D t;" } };
        WeakReference<ImageTexture> unheld = Fill();
        WeakReference<ImageTexture> Fill()
        {
            using Image image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
            var held = ImageTexture.CreateFromImage(image);
            material.SetShaderParameter("t", held);
            composited["held"] = held;
            var other = ImageTexture.CreateFromImage(image);
            composited["unheld"] = other;
            for (int i = composited.Count; i < Engine_HeroTextureMorph.CompositedLimit; i++)
            {
                composited[$"k{i}"] = other;
            }

            return new WeakReference<ImageTexture>(other);
        }

        try
        {
            makeRoom.Invoke(null, null);
            AssertThat(Engine_HeroTextureMorph.CacheMetrics.CompositedEntries).IsEqual(0);
            GC.Collect();

            var found = (ImageTexture?)revive.Invoke(null, new object[] { "held" });
            AssertThat(found).IsNotNull();
            AssertThat(ReferenceEquals(found, material.GetShaderParameter("t").As<Texture2D>())).IsTrue();
            AssertThat(Engine_HeroTextureMorph.CacheMetrics.CompositedEntries).IsEqual(1);
            AssertThat(unheld.TryGetTarget(out _)).IsFalse();
            AssertThat(revive.Invoke(null, new object[] { "unheld" })).IsNull();
        }
        finally { Engine_HeroTextureMorph.Invalidate(); }
    }

    /// <summary>Over budget the longest unused sources go, and the one just decoded and those in use stay.</summary>
    [TestCase]
    public void SourcesOverBudgetGoLongestUnusedFirst()
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;
        Engine_HeroTextureMorph.Invalidate();
        Type morph = typeof(Engine_HeroTextureMorph);
        Type decoded = morph.GetNestedType("DecodedTexture", BindingFlags.NonPublic)!;
        var cache = (IDictionary)morph.GetField("DecodedCache", Private)!.GetValue(null)!;
        FieldInfo bytes = morph.GetField("_decodedBytes", Private)!;
        object Source(long used)
        {
            object value = Activator.CreateInstance(decoded, true)!;
            decoded.GetProperty("Rgba")!.SetValue(value, new byte[100]);
            decoded.GetProperty("LastUsed")!.SetValue(value, used);
            return value;
        }

        try
        {
            object newest = Source(1);
            cache.Add("a", Source(5));
            cache.Add("b", Source(2));
            cache.Add("c", Source(9));
            cache.Add("new", newest);
            bytes.SetValue(null, 400L);
            morph.GetMethod("TrimDecodedTo", Private)!.Invoke(null, new[] { 200L, newest });
            AssertThat(cache.Contains("new")).IsTrue();
            AssertThat(cache.Contains("c")).IsTrue();
            AssertThat(cache.Contains("a")).IsFalse();
            AssertThat(cache.Contains("b")).IsFalse();
            AssertThat((long)bytes.GetValue(null)!).IsEqual(200L);
        }
        finally
        {
            bytes.SetValue(null, 0L);
            Engine_HeroTextureMorph.Invalidate();
        }
    }

    private static byte[] Pixels(Random random, int width, int height, int kind)
    {
        var pixels = new byte[width * height * 4];
        switch (kind % 4)
        {
            case 0:
                random.NextBytes(pixels);
                break;
            case 1:
                byte a = (byte)random.Next(256), b = (byte)random.Next(256);
                for (int i = 0; i < pixels.Length; i++) pixels[i] = random.Next(2) == 0 ? a : b;
                break;
            case 2:
                Array.Fill(pixels, (byte)random.Next(256));
                break;
            default:
                for (int i = 0; i < pixels.Length; i++) pixels[i] = (byte)(128 + random.Next(-3, 4));
                break;
        }

        return pixels;
    }

    private static void ScalarBlend(byte[] output, byte[] colour, byte[] coverage, int pixels, int mode, float weight)
    {
        for (int at = 0; at < pixels * 4; at += 4)
        {
            float alpha = mode switch { 0 => 1.0f, 3 => weight, _ => (coverage[at + 3] / 255.0f) * weight };
            if (alpha <= 0.0f) continue;
            for (int k = 0; k < 3; k++)
            {
                float value = mode == 2
                    ? output[at + k] + (colour[at + k] * alpha)
                    : (output[at + k] * (1.0f - alpha)) + (colour[at + k] * alpha);
                output[at + k] = (byte)Math.Clamp(value + 0.5f, 0.0f, 255.0f);
            }
        }
    }

    private static void ScalarDye(byte[] from, byte[] to, int pixels, float[] colour, float dyed, float[] intensity, float pivot, float[] curve)
    {
        for (int at = 0; at < pixels * 4; at += 4)
        {
            float tr = from[at] / 255.0f, tg = from[at + 1] / 255.0f, tb = from[at + 2] / 255.0f;
            float t = 4.0f * ((tr * intensity[0]) + (tg * intensity[1]) + (tb * intensity[2]));
            int i = Math.Clamp((int)t, 0, 3);
            float f = Math.Clamp(t - i, 0.0f, 1.0f);
            float shaped = curve[i] + ((curve[i + 1] - curve[i]) * f);
            float lo = pivot > 1e-5f ? MathF.Min(shaped / pivot, 1.0f) : 1.0f;
            float hi = pivot < 1.0f - 1e-5f ? MathF.Max(shaped - pivot, 0.0f) / (1.0f - pivot) : 0.0f;
            for (int k = 0; k < 3; k++)
            {
                float col = lo * colour[k];
                col += hi * (1.0f - col);
                float texel = k == 0 ? tr : k == 1 ? tg : tb;
                float value = texel + ((col - texel) * dyed);
                to[at + k] = (byte)Math.Clamp((value * 255.0f) + 0.5f, 0.0f, 255.0f);
            }

            to[at + 3] = from[at + 3];
        }
    }

    /// <summary>The encoder as it was written first, a texel at a time; what the vector form has to reproduce.</summary>
    private static class ScalarEncoder
    {
        public static byte[] Encode(byte[] rgba, int width, int height, TexSurfaceFormat format)
        {
            int blocksW = Math.Max(1, (width + 3) / 4), blocksH = Math.Max(1, (height + 3) / 4);
            int blockBytes = format is TexSurfaceFormat.Bc3 or TexSurfaceFormat.Bc5 ? 16 : 8;
            var output = new byte[blocksW * blocksH * blockBytes];
            var texels = new byte[64];
            int cursor = 0;
            for (int by = 0; by < blocksH; ++by)
            {
                for (int bx = 0; bx < blocksW; ++bx)
                {
                    for (int y = 0; y < 4; ++y)
                    {
                        int sy = Math.Min(height - 1, (by * 4) + y);
                        for (int x = 0; x < 4; ++x)
                        {
                            int sx = Math.Min(width - 1, (bx * 4) + x);
                            Array.Copy(rgba, ((sy * width) + sx) * 4, texels, ((y * 4) + x) * 4, 4);
                        }
                    }

                    switch (format)
                    {
                        case TexSurfaceFormat.Bc1: Bc1(texels, output, cursor); break;
                        case TexSurfaceFormat.Bc4: Bc4(texels, 0, output, cursor); break;
                        case TexSurfaceFormat.Bc3: Bc4(texels, 3, output, cursor); Bc1(texels, output, cursor + 8); break;
                        default: Bc4(texels, 0, output, cursor); Bc4(texels, 1, output, cursor + 8); break;
                    }

                    cursor += blockBytes;
                }
            }

            return output;
        }

        private static void Bc1(byte[] texels, byte[] output, int offset)
        {
            int minR = 255, minG = 255, minB = 255, maxR = 0, maxG = 0, maxB = 0;
            for (int i = 0; i < 16; ++i)
            {
                int r = texels[i * 4], g = texels[(i * 4) + 1], b = texels[(i * 4) + 2];
                minR = Math.Min(minR, r); maxR = Math.Max(maxR, r);
                minG = Math.Min(minG, g); maxG = Math.Max(maxG, g);
                minB = Math.Min(minB, b); maxB = Math.Max(maxB, b);
            }

            ushort high = (ushort)(((maxR >> 3) << 11) | ((maxG >> 2) << 5) | (maxB >> 3));
            ushort low = (ushort)(((minR >> 3) << 11) | ((minG >> 2) << 5) | (minB >> 3));
            if (high < low) (high, low) = (low, high);
            output[offset] = (byte)high;
            output[offset + 1] = (byte)(high >> 8);
            output[offset + 2] = (byte)low;
            output[offset + 3] = (byte)(low >> 8);
            if (high == low) return;

            var pr = new int[4];
            var pg = new int[4];
            var pb = new int[4];
            Unpack(high, out pr[0], out pg[0], out pb[0]);
            Unpack(low, out pr[1], out pg[1], out pb[1]);
            pr[2] = ((2 * pr[0]) + pr[1]) / 3; pg[2] = ((2 * pg[0]) + pg[1]) / 3; pb[2] = ((2 * pb[0]) + pb[1]) / 3;
            pr[3] = (pr[0] + (2 * pr[1])) / 3; pg[3] = (pg[0] + (2 * pg[1])) / 3; pb[3] = (pb[0] + (2 * pb[1])) / 3;

            uint indices = 0;
            for (int i = 0; i < 16; ++i)
            {
                int best = 0, bestDistance = int.MaxValue;
                for (int p = 0; p < 4; ++p)
                {
                    int dr = texels[i * 4] - pr[p], dg = texels[(i * 4) + 1] - pg[p], db = texels[(i * 4) + 2] - pb[p];
                    int distance = (dr * dr) + (dg * dg) + (db * db);
                    if (distance < bestDistance) { bestDistance = distance; best = p; }
                }

                indices |= (uint)best << (i * 2);
            }

            BitConverter.TryWriteBytes(output.AsSpan(offset + 4), indices);
        }

        private static void Unpack(ushort value, out int r, out int g, out int b)
        {
            int r5 = (value >> 11) & 0x1F, g6 = (value >> 5) & 0x3F, b5 = value & 0x1F;
            r = (r5 << 3) | (r5 >> 2);
            g = (g6 << 2) | (g6 >> 4);
            b = (b5 << 3) | (b5 >> 2);
        }

        private static void Bc4(byte[] texels, int channel, byte[] output, int offset)
        {
            int min = 255, max = 0;
            for (int i = 0; i < 16; ++i)
            {
                int v = texels[(i * 4) + channel];
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }

            output[offset] = (byte)max;
            output[offset + 1] = (byte)min;
            if (max == min) return;

            var palette = new int[8];
            palette[0] = max;
            palette[1] = min;
            for (int p = 1; p <= 6; ++p) palette[p + 1] = (((7 - p) * max) + (p * min)) / 7;

            ulong indices = 0;
            for (int i = 0; i < 16; ++i)
            {
                int v = texels[(i * 4) + channel], best = 0, bestDistance = int.MaxValue;
                for (int p = 0; p < 8; ++p)
                {
                    int distance = Math.Abs(v - palette[p]);
                    if (distance < bestDistance) { bestDistance = distance; best = p; }
                }

                indices |= (ulong)best << (i * 3);
            }

            for (int i = 0; i < 6; ++i) output[offset + 2 + i] = (byte)(indices >> (i * 8));
        }
    }
}
