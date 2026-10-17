using System;
using Godot;
using Refii.Bnk;
using Refii.Core;
using Refii.Models;

namespace Refii.Launcher.Tools;

/// <summary>
/// Where a character's carry dummies sit, as the host composes them: for each `Character.Carry*`
/// attachment in the hero's part gmds, its node, the file's transform (model space), the node's
/// bind in model space, and the transform relative to the node that the attachment placer uses.
/// A sheath dummy relative to its own dummy bone should be centimetres, not the height of a back.
///
///   godot --headless --path Godot res://debug/attach_probe.tscn
/// </summary>
public partial class AttachProbe : Node
{
	private BnkFileSystem? _banks;

	public override void _Ready()
	{
		string data = Engine_GameData.Resolve();
		if (data.Length == 0)
		{
			GD.PrintErr("[attach-probe] game data must resolve.");
			GetTree().Quit(1);
			return;
		}

		_banks = BnkFileSystem.Mount(data);
		Engine_BnkMeshLoader.SetResolver(Read);
		foreach (string part in CharacterLightLab.Parts)
		{
			Engine_GmdSkeleton? gmd = Engine_GmdSkeleton.TryParse(Engine_BnkMeshLoader.ReadBytes(part + ".gmd"));
			if (gmd is null)
			{
				continue;
			}

			foreach (GmdAttachment attachment in gmd.Attachments)
			{
				if (!attachment.Name.StartsWith("Character.Carry", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				string nodeName = attachment.Node >= 0 && attachment.Node < gmd.Names.Length ? gmd.Names[attachment.Node] : "<root>";
				Transform3D bind = attachment.Node >= 0 && gmd.TryResolve(nodeName, out Transform3D b) ? b : Transform3D.Identity;
				gmd.TryResolveEntry(attachment.Name, 0, out int _, out Transform3D relative);
				GD.Print($"[attach-probe] {System.IO.Path.GetFileNameWithoutExtension(part)} '{attachment.Name}' node {attachment.Node} '{nodeName}': " +
					$"file {attachment.Local.Origin} ({attachment.Local.Origin.Length():0.000} m), node bind {bind.Origin} " +
					$"({bind.Origin.Length():0.000} m), relative {relative.Origin} ({relative.Origin.Length():0.000} m)");
			}
		}

		GetTree().Quit();
	}

	private byte[]? Read(string virtualPath)
	{
		if (_banks is null || !_banks.TryGetFile(virtualPath, out BnkFile file))
		{
			return null;
		}

		try
		{
			return file.ReadAllBytes();
		}
		catch (Exception)
		{
			return null;
		}
	}
}
