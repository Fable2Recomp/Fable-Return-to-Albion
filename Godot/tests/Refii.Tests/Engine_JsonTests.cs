using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GdUnit4;
using Refii.AndroidRuntime;
using Refii.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The NativeAOT graph writer and the compile-time contracts against reflection-based
/// System.Text.Json: the Android NativeAOT export must write the same files as the Mono one.
/// </summary>
[TestSuite]
public sealed class Engine_JsonTests
{
	public enum Mode { Off, On = 5 }
	public enum Wide : ulong { Big = ulong.MaxValue }
	public interface IShape { int Sides { get; } }
	public class Base { public string BaseName { get; set; } = "b"; public virtual int Over => 1; }

	public sealed class Derived : Base, IShape
	{
		public int Sides => 3;
		public override int Over => 2;
		public int Extra { get; init; } = 7;
		private int Hidden => 9;
		public static int Static => 4;
		public int this[int i] => i + Hidden;
		internal int Internal => 5;
	}

	public struct Vec { public float X; public float Y; public float Length => MathF.Sqrt(X * X + Y * Y); }
	public sealed record Snapshot(long Loads, int Count, float? Ratio, Mode Mode, IReadOnlyList<string> Names, float[] Values, Vec At);
	public sealed class Holder { public IShape Shape { get; set; } = new Derived(); public object Any { get; set; } = new Derived(); public Base AsBase { get; set; } = new Derived(); }

	private static void Same<T>(T value, JsonSerializerOptions? options = null) =>
		AssertThat(Engine_Json.SerializeGraph(value, typeof(T), options)).IsEqual(JsonSerializer.Serialize(value, options));

	/// <summary>Anonymous types, nullable members, enums and nested arrays: the Android status file's shapes.</summary>
	[TestCase]
	public void AnonymousGraphsMatchTheSerializer()
	{
		Same(new
		{
			schema = 1, utc = "2026", phase = (string?)null,
			nested = new { max_fps = 0, vsync = "Enabled", target_ms = 16.6667, f = 1.1f },
			maybe = (float?)2.5f, none = (int?)null, mode = Mode.On, wide = Wide.Big, flag = true,
			arr = new[] { new { a = 1 }, new { a = 2 } },
		});
		Same(new { nan = double.NaN, inf = float.PositiveInfinity, ninf = double.NegativeInfinity, ok = 1.5, list = new List<double> { double.NaN } },
			new JsonSerializerOptions { WriteIndented = true, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals });
		Same(new { schema = 1, game_root = "/g", save_import_root = (string?)null, skip = true, d = new Dictionary<string, object?> { ["n"] = null } },
			new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
		Same(new { s = "<é>&" }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true });
	}

	/// <summary>The debug channel's replies: dictionaries of boxed values of every kind.</summary>
	[TestCase]
	public void ObjectDictionariesMatchTheSerializer()
	{
		Same(new Dictionary<string, object?>
		{
			["ok"] = true, ["i"] = 1, ["l"] = 2L, ["d"] = 0.1, ["f"] = 0.1f, ["s"] = "<tag> & 'q' \"é\" \u2028",
			["n"] = null, ["ia"] = new[] { 1, 2 }, ["fa"] = new[] { 1.5f }, ["ul"] = ulong.MaxValue, ["by"] = (byte)7,
			["u16"] = (ushort)8, ["sb"] = (sbyte)-1, ["dec"] = 1.25m, ["anon"] = new { x = 1 },
			["nested"] = new Dictionary<string, int> { ["a"] = 1 }, ["e"] = Mode.Off, ["c"] = 'z', ["bytes"] = new byte[] { 1, 2, 255 },
			["dt"] = new DateTime(2026, 10, 24, 1, 2, 3, DateTimeKind.Utc), ["ts"] = TimeSpan.FromMilliseconds(1234.5),
			["g"] = Guid.Empty, ["tuple"] = (1, "x"), ["obj"] = new List<object?> { 1, "a", null, new { z = 2 } },
			["node"] = JsonNode.Parse("{\"a\":[1,2,{\"b\":null}]}"), ["el"] = JsonDocument.Parse("[true]").RootElement,
			["frames"] = new List<Refii.Game.GameDebugServer.PadFrame> { new(1, 2, 3, -4, 5, 6, 7) },
		});
		Same(new { byInt = new Dictionary<int, string> { [3] = "x" }, byEnum = new Dictionary<Mode, int> { [Mode.On] = 1 },
			byLong = new SortedDictionary<long, bool> { [-2] = true } });
		Same(new { grid = new[] { new[] { 1f }, new[] { 2f, 3f } }, sets = new HashSet<string> { "x" }, q = new Queue<int>(new[] { 4 }),
			e = Enumerable.Range(0, 3).Select(i => i * 2) });
	}

	/// <summary>Records, structs and class hierarchies: member order, declared-type contracts and exclusions.</summary>
	[TestCase]
	public void NamedTypesMatchTheSerializer()
	{
		Same(new AndroidGameInstaller.Prepared("/d", "/d/game", "abc", null, 3, 5_000_000_000));
		Same(new Snapshot(9, 2, null, Mode.On, new[] { "a", "b" }, new[] { 0.25f, 3e-8f, 1e20f }, new Vec { X = 3, Y = 4 }));
		Same(new Derived());
		Same(new Holder());
		Same<Base>(new Derived());
		Same<object>(new Derived());
		Same("C:\\a/b \"q\"");
	}

	/// <summary>What the serializer refuses, the graph writer refuses too.</summary>
	[TestCase]
	public void RefusalsMatchTheSerializer()
	{
		AssertThrown(() => Engine_Json.SerializeGraph(new { v = float.NaN }, typeof(object))).IsInstanceOf<ArgumentException>();
		AssertThrown(() => Engine_Json.SerializeGraph(new { t = typeof(int) }, typeof(object))).IsInstanceOf<NotSupportedException>();
		AssertThrown(() => Engine_Json.SerializeGraph(1, typeof(int), new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } }))
			.IsInstanceOf<NotSupportedException>();
	}

	/// <summary>The compile-time contracts read and write what reflection did.</summary>
	[TestCase]
	public void GeneratedContractsMatchReflection()
	{
		var state = new AndroidInstallState.State(new AndroidGameInstaller.Prepared("/d", "/d/game", "abc", null, 3, 5),
			new AndroidPhoneCompiler.Result("/r", "/r/librefii_generated.so", "00ff", "1"));
		string text = JsonSerializer.Serialize(state, AndroidJsonContext.Default.State);
		AssertThat(text).IsEqual(JsonSerializer.Serialize(state));
		AssertThat(JsonSerializer.Deserialize(text, AndroidJsonContext.Default.State)).IsEqual(state);

		// The bundle a module was built from round-trips, and a state written before it existed reads as unknown.
		var built = state with { Compiled = state.Compiled! with { Bundle = "beef" } };
		AssertThat(JsonSerializer.Deserialize(JsonSerializer.Serialize(built, AndroidJsonContext.Default.State),
			AndroidJsonContext.Default.State)).IsEqual(built);
		AssertThat(text.Contains(",\"Bundle\":\"\"", StringComparison.Ordinal)).IsTrue();
		AssertThat(JsonSerializer.Deserialize(text.Replace(",\"Bundle\":\"\"", ""), AndroidJsonContext.Default.State)!
			.Compiled!.Bundle).IsEqual("");

		var frame = new Refii.Game.GameDebugServer.PadFrame(1, 0, 255, -32768, 32767, 0, 1);
		string line = JsonSerializer.Serialize(frame, Engine_JsonContext.Default.PadFrame);
		AssertThat(line).IsEqual(JsonSerializer.Serialize(frame));
		AssertThat(JsonSerializer.Deserialize(line, Engine_JsonContext.Default.PadFrame)).IsEqual(frame);

		var manifest = new Refii.Lighting.Engine_LevelShadowPrebuild.ShadowBakeManifest { Resolution = 2048, PlacedReachScale = 1.5f, ShadowMapVersion = 3 };
		AssertThat(JsonSerializer.Serialize(manifest, Engine_JsonContext.Indented.ShadowBakeManifest))
			.IsEqual(JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
	}

	/// <summary>Desktop processes are not Android; the NativeAOT build answers through GODOT_ANDROID.</summary>
	[TestCase]
	public void DesktopIsNotAndroid()
	{
		AssertThat(Engine_Platform.IsAndroid).IsEqual(OperatingSystem.IsAndroid());
		AssertThat(Engine_Platform.IsDesktopLinux).IsEqual(OperatingSystem.IsLinux());
	}
}
