using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Refii.Bnk;
using Refii.Core;
using Refii.Text;
using Refii.Ui;
using Refii.Models;

namespace Refii.Launcher.Tools;

/// <summary>
/// Measures the .mdl path across the WHOLE asset set, not a hand-picked sample.
///
/// The texture work was only trustworthy because a single number — 146 of 147 — said
/// where it stood, and a regression moved that number. Geometry needs the same
/// instrument before anything is built on top of it: a loader that works on the three
/// models someone happened to open tells you nothing about the other twenty thousand.
///
/// Header-only stubs are counted SEPARATELY from failures. A stub is a correct parse of
/// a file that genuinely carries no geometry (the body lives in another bank entry, or
/// the model is a pure LOD placeholder), so folding it into the failure count would
/// understate the loader by however many stubs the build happens to contain.
/// </summary>
public partial class MdlSurvey : Node
{
	/// <summary>Godot's <c>RenderingServer::MAX_MESH_SURFACES</c>, which is not exposed to C#.</summary>
	private const int MaxMeshSurfaces = 256;

	/// <summary>0 surveys everything; a positive value caps the sample for a quick pass.</summary>
	[Export] public int Limit { get; set; }

	/// <summary>How many distinct failure reasons to print, worst-first.</summary>
	[Export] public int ReasonsShown { get; set; } = 12;

	/// <summary>How many example paths to print per failure reason.</summary>
	[Export] public int ExamplesPerReason { get; set; } = 2;

	public override void _Ready()
	{
		string dataRoot = Engine_GameData.Resolve();
		if (dataRoot.Length == 0)
		{
			GD.Print("no game data found; set REFII_GAME_DATA or stage assets/game.");
			GetTree().Quit(1);
			return;
		}

		GD.Print($"[mdl] mounting {dataRoot}");
		using BnkFileSystem filesystem = BnkFileSystem.Mount(dataRoot);
		GD.Print($"[mdl] mounted {filesystem.FileCount:N0} files " +
			$"from {filesystem.Archives.Count} banks");

		List<BnkFile> models = filesystem.EnumerateAllFiles()
			.Where(f => f.VirtualPath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
			.OrderBy(f => f.VirtualPath, StringComparer.OrdinalIgnoreCase)
			.ToList();

		if (Limit > 0 && models.Count > Limit)
		{
			models = models.Take(Limit).ToList();
		}

		GD.Print($"[mdl] surveying {models.Count:N0} models");

		int built = 0;
		int stubs = 0;
		int failed = 0;
		int unreadable = 0;
		int overflowed = 0;
		int widest = 0;
		int widestRaw = 0;
		long surfaces = 0;
		long vertices = 0;
		var fattest = new List<(string Path, int Surfaces, int Materials)>();
		var reasons = new Dictionary<string, (int Count, List<string> Examples)>();

		ulong startedMs = Time.GetTicksMsec();
		foreach (BnkFile model in models)
		{
			byte[] bytes;
			try
			{
				bytes = model.ReadAllBytes();
			}
			catch (Exception exception)
			{
				++unreadable;
				Record(reasons, "read: " + exception.GetType().Name, model.VirtualPath);
				continue;
			}

			// Count geometry BEFORE Godot sees it. ArrayMesh.GetSurfaceCount() saturates at
			// the cap, so asking the mesh how many surfaces it wanted always answers 256 —
			// the truncation is invisible from the other side of it.
			if (Engine_MdlData.TryParseInfo(bytes, model.VirtualPath, out MdlInfo probe, out _) &&
				!probe.IsStub &&
				Engine_MdlGeometry.TryBuild(bytes, probe, out List<MdlGeometry> raw))
			{
				int drawable = raw.Count(g => g.VertexCount != 0 && g.TriangleCount != 0);
				if (drawable > widestRaw)
				{
					widestRaw = drawable;
				}

				if (drawable >= MaxMeshSurfaces)
				{
					int distinctMaterials = Engine_MdlMerge.ByMaterial(raw).Count;
					fattest.Add((model.VirtualPath, drawable, distinctMaterials));
				}
			}

			ArrayMesh? mesh;
			string error;
			bool isStub;
			try
			{
				mesh = Engine_MdlMesh.TryCreateMesh(
					bytes, model.VirtualPath, out error, out isStub);
			}
			catch (Exception exception)
			{
				// A throw is strictly worse than a null return: it means the parser walked
				// off the end of a buffer rather than deciding the file was unsupported.
				++failed;
				Record(reasons, "THREW " + exception.GetType().Name, model.VirtualPath);
				continue;
			}

			if (isStub)
			{
				++stubs;
				continue;
			}

			if (mesh is null)
			{
				++failed;
				Record(reasons, error.Length == 0 ? "(no reason given)" : error, model.VirtualPath);
				continue;
			}

			++built;
			int surfaceCount = mesh.GetSurfaceCount();
			surfaces += surfaceCount;
			for (int s = 0; s < surfaceCount; ++s)
			{
				vertices += mesh.SurfaceGetArrayLen(s);
			}

			// Godot refuses surface 256 and every one after it, so a model that asks for
			// more is silently TRUNCATED — it loads, it looks fine in the count above, and
			// the missing pieces only show up as holes in the world. Track it explicitly.
			if (surfaceCount >= MaxMeshSurfaces)
			{
				++overflowed;
			}

			widest = Math.Max(widest, surfaceCount);
		}

		double seconds = (Time.GetTicksMsec() - startedMs) / 1000.0;
		int decodable = built + failed;

		GD.Print(new string('-', 68));
		GD.Print($"[mdl] built      {built,7:N0}" +
			(decodable > 0 ? $"   ({built * 100.0 / decodable:F1}% of non-stub)" : string.Empty));
		GD.Print($"[mdl] stubs      {stubs,7:N0}   (header only, no geometry present)");
		GD.Print($"[mdl] failed     {failed,7:N0}");
		GD.Print($"[mdl] unreadable {unreadable,7:N0}");
		GD.Print($"[mdl] surfaces   {surfaces,7:N0}   vertices {vertices:N0}");
		GD.Print($"[mdl] widest    {widest,7:N0}   surfaces Godot accepted (cap {MaxMeshSurfaces})");
		GD.Print($"[mdl] wanted    {widestRaw,7:N0}   surfaces the widest model actually has");
		GD.Print($"[mdl] TRUNCATED {overflowed,7:N0}   models lost surfaces to that cap");
		foreach ((string path, int count, int materials) in fattest)
		{
			GD.Print($"[mdl]        {count,5:N0} surfaces / {materials,4:N0} materials  {path}");
		}

		GD.Print($"[mdl] {seconds:F1}s for {models.Count:N0} models");

		if (reasons.Count != 0)
		{
			GD.Print(new string('-', 68));
			foreach ((string reason, (int count, List<string> examples)) in
				reasons.OrderByDescending(pair => pair.Value.Count).Take(ReasonsShown))
			{
				GD.Print($"[mdl] {count,6:N0}  {reason}");
				foreach (string example in examples)
				{
					GD.Print($"[mdl]           {example}");
				}
			}

			if (reasons.Count > ReasonsShown)
			{
				GD.Print($"[mdl] ... and {reasons.Count - ReasonsShown} more distinct reasons");
			}
		}

		GetTree().Quit(0);
	}

	private void Record(
		Dictionary<string, (int Count, List<string> Examples)> reasons,
		string reason,
		string path)
	{
		if (!reasons.TryGetValue(reason, out (int Count, List<string> Examples) entry))
		{
			entry = (0, new List<string>());
			reasons[reason] = entry;
		}

		if (entry.Examples.Count < ExamplesPerReason)
		{
			entry.Examples.Add(path);
		}

		reasons[reason] = (entry.Count + 1, entry.Examples);
	}
}
