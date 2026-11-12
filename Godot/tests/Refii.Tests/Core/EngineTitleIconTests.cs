namespace Refii.Tests.Core;

using System;
using System.IO;
using GdUnit4;
using static GdUnit4.Assertions;
using Refii.Core;

[TestSuite]
public class EngineTitleIconTests
{
	private static string? RepoRoot()
	{
		string? at = AppContext.BaseDirectory;
		for (int i = 0; i < 12 && at != null; i++)
		{
			if (File.Exists(Path.Combine(at, "Godot", "project.godot")))
			{
				return at;
			}
			at = Path.GetDirectoryName(at);
		}
		return null;
	}

	[TestCase]
	public void ParseIco_HandlesInvalidDataGracefully()
	{
		AssertThat(Engine_TitleIcon.ParseIco(null)).IsNull();
		AssertThat(Engine_TitleIcon.ParseIco(Array.Empty<byte>())).IsNull();
		AssertThat(Engine_TitleIcon.ParseIco(new byte[] { 0, 0, 1, 0 })).IsNull();
	}

	[TestCase]
	public void ParseIco_DecodesBundledFableIco()
	{
		string? root = RepoRoot();
		AssertThat(root).IsNotNull();

		string icoPath = Path.Combine(root!, "Godot", "assets", "launcher", "fable.ico");
		AssertThat(File.Exists(icoPath)).IsTrue();

		byte[] bytes = File.ReadAllBytes(icoPath);
		Engine_TitleIcon.DecodedIco? parsed = Engine_TitleIcon.ParseIco(bytes);

		AssertThat(parsed.HasValue).IsTrue();
		AssertThat(parsed!.Value.Width).IsEqual(256);
		AssertThat(parsed.Value.Height).IsEqual(256);
		AssertThat(parsed.Value.Rgba).IsNotNull();
		AssertThat(parsed.Value.Rgba!.Length).IsEqual(256 * 256 * 4);
	}
}
