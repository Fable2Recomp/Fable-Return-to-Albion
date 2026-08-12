using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Refii.Bnk;
using Refii.Core;
using Refii.Entities;
using Refii.Gdb;
using Refii.Level;
using Refii.Models;

namespace Refii.Launcher.Tools;

public partial class EntityCheck : Node
{
	[Export] public string Filter { get; set; } = string.Empty;

	[Export] public int Limit { get; set; } = 3;

	private BnkFileSystem? _filesystem;

	public override void _Ready()
	{
		string dataRoot = Engine_GameData.Resolve();
		if (dataRoot.Length == 0)
		{
			GD.Print("no game data found; set REFII_GAME_DATA.");
			GetTree().Quit(1);
			return;
		}

		GD.Print($"[entity] mounting {dataRoot}");
		_filesystem = BnkFileSystem.Mount(dataRoot);
		GD.Print($"[entity] mounted {_filesystem.FileCount:N0} files " +
			$"from {_filesystem.Archives.Count} banks");

		Engine_BnkMeshLoader.SetResolver(ResolveBytes);
		Engine_BnkMeshLoader.SetExistsProbe(p => _filesystem.TryGetFile(p, out _));
		Engine_PropModelIndex.SetModelSource(EnumerateModelAssets);
		Engine_EntityIndex.SetResolver(ResolveBytes);

		IReadOnlyList<EntityEntry> entries = Engine_EntityIndex.Entries;
		GD.Print($"[entity] catalog {entries.Count:N0} entries");

		Audit(entries);

		string filter = System.Environment.GetEnvironmentVariable("REFII_ENTITY") ?? Filter;
		foreach (EntityEntry entry in entries
			.Where(e => filter.Length == 0 ||
				e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
			.Take(Limit))
		{
			Report(entry);
		}

		GetTree().Quit(0);
	}

	private void Audit(IReadOnlyList<EntityEntry> entries)
	{
		int withEyes = 0;
		int resolvedBoth = 0;
		int resolvedNone = 0;
		var unresolved = new List<string>();

		foreach (EntityEntry entry in entries)
		{
			var paths = new List<string>();
			foreach (uint hash in entry.ModelHashes)
			{
				if (Engine_PropModelIndex.Resolve(hash, Array.Empty<string>()) is string path)
				{
					paths.Add(path);
				}
			}

			bool hasLeft = paths.Any(p => Engine_EntityMesh.EyeSideOf(p) == EyeSide.Left);
			bool hasRight = paths.Any(p => Engine_EntityMesh.EyeSideOf(p) == EyeSide.Right);
			if (!hasLeft && !hasRight)
			{
				continue;
			}

			++withEyes;

			bool left = Engine_EntityMesh.TryEyeOffset(paths, EyeSide.Left, out _);
			bool right = Engine_EntityMesh.TryEyeOffset(paths, EyeSide.Right, out _);

			if (left && right)
			{
				++resolvedBoth;
			}
			else if (!left && !right)
			{
				++resolvedNone;
				if (unresolved.Count < 12)
				{
					unresolved.Add($"{entry.Name} 0x{entry.EntityHash:X8} :: " +
						string.Join(" | ", paths));
				}
			}
		}

		GD.Print($"[entity] {withEyes:N0} entries carry eye models; " +
			$"{resolvedBoth:N0} resolve both eye bones, {resolvedNone:N0} resolve neither");
		foreach (string name in unresolved)
		{
			GD.Print($"[entity]    unresolved: {name}");
		}
	}

	private void Report(EntityEntry entry)
	{
		GD.Print(new string('-', 72));
		GD.Print($"[entity] {entry.Name}  0x{entry.EntityHash:X8}  " +
			$"{entry.ModelHashes.Count} model hash(es)");

		var paths = new List<string>();
		foreach (uint hash in entry.ModelHashes)
		{
			string? path = Engine_PropModelIndex.Resolve(hash, Array.Empty<string>());
			if (path is null)
			{
				GD.Print($"[entity]   0x{hash:X8}  MISSING");
				continue;
			}

			paths.Add(path);

			byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(path);
			if (bytes is null ||
				!Engine_MdlData.TryParseInfo(bytes, path, out MdlInfo info, out _) ||
				!Engine_MdlGeometry.TryBuild(bytes, info, out List<MdlGeometry> geometry))
			{
				GD.Print($"[entity]   0x{hash:X8}  {path}  (no geometry)");
				continue;
			}

			int verts = 0;
			var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
			var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
			foreach (MdlGeometry geo in geometry)
			{
				verts += geo.VertexCount;
				for (int v = 0; v < geo.VertexCount; v++)
				{
					var p = new Vector3(
						geo.Positions[v * 3], geo.Positions[v * 3 + 1], geo.Positions[v * 3 + 2]);
					min = min.Min(p);
					max = max.Max(p);
				}
			}

			GD.Print($"[entity]   {Engine_EntityMesh.EyeSideOf(path),-5} {path}");
			GD.Print($"[entity]      bones={info.Bones.Count,4} verts={verts,6} " +
				$"min={Describe(min)} max={Describe(max)}");
		}

		foreach (EyeSide side in new[] { EyeSide.Left, EyeSide.Right })
		{
			GD.Print(Engine_EntityMesh.TryEyeOffset(paths, side, out Vector3 offset)
				? $"[entity]   {side} eye offset {Describe(offset)}"
				: $"[entity]   {side} eye bone NOT FOUND");
		}
	}

	private static string Describe(Vector3 v) => $"({v.X:F3},{v.Y:F3},{v.Z:F3})";

	private byte[]? ResolveBytes(string virtualPath)
	{
		if (_filesystem is not null && _filesystem.TryGetFile(virtualPath, out BnkFile file))
		{
			try
			{
				return file.ReadAllBytes();
			}
			catch (Exception)
			{
				return null;
			}
		}

		return null;
	}

	private IEnumerable<PropModelAsset> EnumerateModelAssets()
	{
		if (_filesystem is null)
		{
			yield break;
		}

		foreach (BnkFile file in _filesystem.EnumerateAllFiles())
		{
			if (file.VirtualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
			{
				yield return new PropModelAsset(file.VirtualPath, file.SourceArchivePath);
			}
		}
	}
}
