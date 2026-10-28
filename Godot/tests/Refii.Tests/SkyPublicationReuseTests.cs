using System;
using System.Reflection;
using GdUnit4;
using Refii.Level;
using Refii.Lighting;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The sky's per-frame publication written into kept instances: each Into form, given an
/// instance full of another frame's values, comes out field for field as the allocating form
/// does on a new one, and allocates nothing.
/// </summary>
[TestSuite]
public sealed class SkyPublicationReuseTests
{
	private const int Rounds = 200;

	private static void Fill(object target, Random rng)
	{
		foreach (PropertyInfo p in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			if (p.Name == nameof(SkyTheme.Revision))
			{
				continue;
			}

			if (p.PropertyType == typeof(float[]))
			{
				var values = (float[])p.GetValue(target)!;
				for (int i = 0; i < values.Length; i++)
				{
					values[i] = Value(rng);
				}
			}
			else if (p.CanWrite && p.PropertyType == typeof(float))
			{
				p.SetValue(target, Value(rng));
			}
			else if (p.CanWrite && p.PropertyType == typeof(bool))
			{
				p.SetValue(target, rng.Next(2) == 0);
			}
			else if (p.CanWrite && p.PropertyType == typeof(uint))
			{
				p.SetValue(target, (uint)rng.Next());
			}
		}
	}

	/// <summary>Mostly ordinary values, with zeros, negatives and the odd non-finite one.</summary>
	private static float Value(Random rng) => rng.Next(20) switch
	{
		0 => 0f,
		1 => float.NaN,
		2 => -rng.NextSingle() * 3f,
		_ => rng.NextSingle() * 4f,
	};

	private static void AssertSame(object expected, object actual)
	{
		foreach (PropertyInfo p in expected.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
		{
			if (p.Name == nameof(SkyTheme.Revision))
			{
				continue;
			}

			object? e = p.GetValue(expected);
			object? a = p.GetValue(actual);
			if (e is float[] ea)
			{
				var aa = (float[])a!;
				AssertThat(aa.Length).IsEqual(ea.Length);
				for (int i = 0; i < ea.Length; i++)
				{
					AssertThat(BitConverter.SingleToInt32Bits(aa[i]))
						.OverrideFailureMessage($"{p.Name}[{i}]: {aa[i]} for {ea[i]}")
						.IsEqual(BitConverter.SingleToInt32Bits(ea[i]));
				}
			}
			else if (e is float ef)
			{
				AssertThat(BitConverter.SingleToInt32Bits((float)a!))
					.OverrideFailureMessage($"{p.Name}: {a} for {ef}")
					.IsEqual(BitConverter.SingleToInt32Bits(ef));
			}
			else
			{
				AssertThat(Equals(a, e)).OverrideFailureMessage($"{p.Name}: {a} for {e}").IsTrue();
			}
		}
	}

	private static SkyTheme Theme(Random rng)
	{
		var theme = new SkyTheme();
		Fill(theme, rng);
		return theme;
	}

	private static LightingTheme Lighting(Random rng)
	{
		var theme = new LightingTheme();
		Fill(theme, rng);
		return theme;
	}

	[TestCase]
	public void BlendSkyIntoAKeptThemeIsTheNewBlend()
	{
		var rng = new Random(17);
		var kept = new SkyTheme();
		for (int round = 0; round < Rounds; round++)
		{
			SkyTheme a = Theme(rng), b = Theme(rng);
			float w = rng.NextSingle();
			Fill(kept, rng);
			int revision = kept.Revision;
			AssertThat(ReferenceEquals(Engine_ThemeTimeline.BlendSkyInto(a, b, w, kept), kept)).IsTrue();
			AssertSame(Engine_ThemeTimeline.BlendSky(a, b, w), kept);
			AssertThat(kept.Revision).IsNotEqual(revision);
		}
	}

	[TestCase]
	public void BlendLightingIntoAKeptThemeIsTheNewBlend()
	{
		var rng = new Random(23);
		var kept = new LightingTheme();
		for (int round = 0; round < Rounds; round++)
		{
			LightingTheme a = Lighting(rng), b = Lighting(rng);
			float w = rng.NextSingle();
			Fill(kept, rng);
			Engine_ThemeTimeline.BlendLightingInto(a, b, w, kept);
			AssertSame(Engine_ThemeTimeline.BlendLighting(a, b, w), kept);
		}
	}

	[TestCase]
	public void SkyFrameIntoAKeptFrameIsTheNewFrame()
	{
		var rng = new Random(29);
		var kept = new SkyFrame();
		for (int round = 0; round < Rounds; round++)
		{
			SkyTheme theme = Theme(rng);
			float time = rng.NextSingle() * 1.5f;
			Fill(kept, rng);
			int revision = kept.Revision;
			Engine_SkyFrame.EvaluateInto(theme, time, kept);
			AssertSame(Engine_SkyFrame.Evaluate(theme, time), kept);
			AssertThat(kept.Revision).IsNotEqual(revision);
		}
	}

	[TestCase]
	public void LightingFrameIntoAKeptFrameIsTheNewFrame()
	{
		var rng = new Random(31);
		var kept = new LightingFrame();
		for (int round = 0; round < Rounds; round++)
		{
			SkyTheme sky = Theme(rng);
			LightingTheme lighting = Lighting(rng);
			SkyFrame frame = Engine_SkyFrame.Evaluate(sky, rng.NextSingle());
			Fill(kept, rng);
			Engine_LightingFrame.EvaluateInto(sky, lighting, frame, kept);
			AssertSame(Engine_LightingFrame.Evaluate(sky, lighting, frame), kept);

			var copy = new LightingFrame();
			Fill(copy, rng);
			AssertSame(kept, copy.CopyFrom(kept));
		}
	}

	[TestCase]
	public void AtmosphereInputsIntoAKeptInstanceAreTheNewInputs()
	{
		var rng = new Random(37);
		var kept = new AtmosphereInputs();
		for (int round = 0; round < Rounds; round++)
		{
			SkyTheme theme = Theme(rng);
			SkyFrame frame = Engine_SkyFrame.Evaluate(theme, rng.NextSingle());
			Engine_SkyAtmosphere.FromFrameInto(frame, theme, kept);
			Fill(kept, rng);
			// The alpha slots FromFrame never writes: a kept instance only ever holds what
			// FromFrameInto wrote, so they are put back to a new instance's 0 as it would leave them.
			kept.SkyColour[3] = kept.ComplementaryColour[3] = kept.SunsetColour[3] = 0f;
			Engine_SkyAtmosphere.FromFrameInto(frame, theme, kept);
			AssertSame(Engine_SkyAtmosphere.FromFrame(frame, theme), kept);
			AssertSame(Engine_SkyAtmosphere.FromFrame(frame, theme), Engine_SkyAtmosphere.FromFrameScratch(frame, theme));
		}
	}

	private static long Bytes(Action step)
	{
		step();
		long before = GC.GetAllocatedBytesForCurrentThread();
		for (int i = 0; i < 100; i++)
		{
			step();
		}

		return (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
	}

	/// <summary>
	/// Once warm, the kept forms allocate nothing but what the lighting model's own switch reads
	/// cost (an environment-variable read per ask), and the published objects are no longer made.
	/// </summary>
	[TestCase]
	public void TheKeptPathAllocatesNoPublishedObjects()
	{
		var rng = new Random(41);
		SkyTheme a = Theme(rng), b = Theme(rng);
		LightingTheme la = Lighting(rng), lb = Lighting(rng);
		var theme = new SkyTheme();
		var blend = new LightingTheme();
		var frame = new SkyFrame();
		var lighting = new LightingFrame();
		Engine_ThemeTimeline.BlendSkyInto(a, b, 0.5f, theme);
		Engine_ThemeTimeline.BlendLightingInto(la, lb, 0.5f, blend);
		Engine_SkyFrame.EvaluateInto(theme, 0.5f, frame);

		AssertThat(Bytes(() => Engine_ThemeTimeline.BlendSkyInto(a, b, 0.5f, theme))).IsEqual(0L);
		AssertThat(Bytes(() => Engine_ThemeTimeline.BlendLightingInto(la, lb, 0.5f, blend))).IsEqual(0L);
		AssertThat(Bytes(() => Engine_SkyFrame.EvaluateInto(theme, 0.5f, frame))).IsEqual(0L);
		AssertThat(Bytes(() => Engine_SkyAtmosphere.FromFrameScratch(frame, theme))).IsEqual(0L);

		long switches = Bytes(() => _ = Engine_LightingFrame.ModernModel);
		long kept = Bytes(() => Engine_LightingFrame.EvaluateInto(theme, blend, frame, lighting));
		long made = Bytes(() => Engine_LightingFrame.Evaluate(theme, blend, frame));
		AssertThat(kept).OverrideFailureMessage($"kept {kept}, one switch read {switches}").IsLessEqual(switches * 3);
		AssertThat(made - kept).OverrideFailureMessage($"kept {kept}, made {made}").IsGreater(200L);
	}
}
