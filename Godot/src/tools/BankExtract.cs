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

		// REFII_EXTRACT_ALL=1: every entry matching goes to REFII_EXTRACT_TO (a directory)
		// under its own virtual path — for sweeping a whole class of files (every script,
		// say) through an offline tool. The destination rule is the same: outside the tree.
		if (OS.GetEnvironment("REFII_EXTRACT_ALL") == "1")
		{
			int written = 0;
			foreach (BnkFile entry in fs.EnumerateAllFiles())
			{
				if (!entry.VirtualPath.Contains(want, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				string target = Path.Combine(dest, entry.VirtualPath.Replace('\\', '/').TrimStart('/'));
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				File.WriteAllBytes(target, entry.ReadAllBytes());
				written++;
			}

			GD.Print($"[extract] {written} entr(ies) matching '{want}' -> {dest}");
			GetTree().Quit(0);
			return;
		}

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
