using System;
using System.IO;
using System.Linq;
using Godot;
using Refii.Bnk;
using Refii.Core;
using Refii.Text;
using Refii.Ui;

namespace Refii.Launcher.Tools;

/// <summary>
/// Writes one file out of the mounted banks, for probing a decoder against real data.
///
/// The destination MUST be outside the repository and any build output: extracted game
/// data is derived and not redistributable.
/// </summary>
public partial class BankExtract : Node
{
	public override void _Ready()
	{
		string want = OS.GetEnvironment("REFII_EXTRACT");
		string dest = OS.GetEnvironment("REFII_EXTRACT_TO");
		if (want.Length == 0 || dest.Length == 0)
		{
			GD.Print("set REFII_EXTRACT and REFII_EXTRACT_TO.");
			GetTree().Quit(1);
			return;
		}

		using BnkFileSystem fs = BnkFileSystem.Mount(Engine_GameData.Resolve());
		BnkFile? file = fs.EnumerateAllFiles()
			.FirstOrDefault(f => f.VirtualPath.Contains(want, StringComparison.OrdinalIgnoreCase));

		if (file is null)
		{
			GD.Print($"no file matching '{want}'");
			GetTree().Quit(1);
			return;
		}

		byte[] bytes = file.ReadAllBytes();
		Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
		File.WriteAllBytes(dest, bytes);
		GD.Print($"[extract] {file.VirtualPath} -> {dest} ({bytes.Length:N0} bytes)");
		GetTree().Quit(0);
	}
}
