using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Refii.Bnk;
using Refii.Models;

namespace Refii.Mods;

/// <summary>One model a mod authored, and what became of it.</summary>
public sealed class MeshConversion
{
	public string Target = string.Empty;

	public string Mod = string.Empty;

	public int VertexCount;

	public int TriangleCount;

	public string Error = string.Empty;

	public bool Ok => Error.Length == 0;
}

/// <summary>
/// Lets a mod ship a model as `foo.mdl.glb` and have the game read `foo.mdl`.
///
/// THE MODEL BEING REPLACED IS THE TEMPLATE. A `.mdl` states far more than geometry — bounds,
/// material names, the movement block, the mesh-kind fields that decide which renderable class
/// wraps it — and those are carried from the file the mod replaces rather than invented. What the
/// glTF supplies is the mesh: positions, UVs, normals, tangents and triangles.
///
/// CONVERTED IN MEMORY, NEVER TO DISK, like every other overlay.
/// </summary>
public static class ModMeshOverlays
{
	public static readonly string[] Suffixes = { ".mdl.glb", ".mdl.gltf" };

	public static List<MeshConversion> Apply(
		BnkFileSystem mounted, IReadOnlyList<ModOverlay> overlays)
	{
		var conversions = new List<MeshConversion>();

		foreach (ModOverlay overlay in overlays)
		{
			foreach (string file in ModManifest.EnumerateAssets(overlay.AssetRoot))
			{
				string? suffix = null;
				foreach (string candidate in Suffixes)
				{
					if (file.EndsWith(candidate, StringComparison.OrdinalIgnoreCase))
					{
						suffix = candidate;
						break;
					}
				}

				if (suffix is null)
				{
					continue;
				}

				string relative;
				byte[] gltf;
				try
				{
					relative = Path.GetRelativePath(overlay.AssetRoot, file);
					gltf = File.ReadAllBytes(file);
				}
				catch (Exception exception)
				{
					conversions.Add(new MeshConversion
					{
						Target = file, Mod = overlay.Name, Error = exception.Message,
					});
					continue;
				}

				string target = ModLoadOrder.Normalize(relative);
				target = target[..^(suffix.Length - ".mdl".Length)];

				var conversion = new MeshConversion { Target = target, Mod = overlay.Name };

				if (!mounted.TryGetFile(target, out BnkFile template))
				{
					conversion.Error =
						$"there is no '{target}' to replace — a model mod edits a model the game " +
						"already has, because the file states far more than its mesh.";
					conversions.Add(conversion);
					continue;
				}

				byte[] original;
				try
				{
					original = template.ReadAllBytes();
				}
				catch (Exception exception)
				{
					conversion.Error = exception.Message;
					conversions.Add(conversion);
					continue;
				}

				if (!Engine_MdlData.TryParseInfo(original, target, out MdlInfo info, out string parseError))
				{
					conversion.Error = $"'{target}' could not be read: {parseError}";
					conversions.Add(conversion);
					continue;
				}

				if (!TryLoadMesh(gltf, out Mesh? mesh, out string loadError) || mesh is null)
				{
					conversion.Error = loadError;
					conversions.Add(conversion);
					continue;
				}

				if (!Engine_MdlImport.TryFromSurface(mesh, 0, out MdlImportedMesh imported, out string importError))
				{
					conversion.Error = importError;
					conversions.Add(conversion);
					continue;
				}

				if (!Engine_MdlWriter.TryReplaceMesh(
						original, info, 0,
						imported.Positions, imported.Uvs, imported.Normals, imported.Tangents,
						imported.Indices, out byte[] rebuilt, out string writeError))
				{
					conversion.Error = writeError;
					conversions.Add(conversion);
					continue;
				}

				conversion.VertexCount = imported.VertexCount;
				conversion.TriangleCount = imported.TriangleCount;
				conversions.Add(conversion);

				mounted.AddVirtualFile(
					target, $"<mesh:{target}>", rebuilt.LongLength, () => rebuilt, overlay.Name);
			}
		}

		return conversions;
	}

	/// <summary>Reads a glTF into a scene and takes its first mesh.</summary>
	private static bool TryLoadMesh(byte[] gltf, out Mesh? mesh, out string error)
	{
		mesh = null;
		error = string.Empty;

		try
		{
			var document = new GltfDocument();
			var state = new GltfState();
			Error appended = document.AppendFromBuffer(gltf, string.Empty, state);
			if (appended != Error.Ok)
			{
				error = $"the glTF could not be read ({appended}).";
				return false;
			}

			Node scene = document.GenerateScene(state);
			if (scene is null)
			{
				error = "the glTF holds no scene.";
				return false;
			}

			mesh = Engine_MdlImport.FindFirstMesh(scene);
			scene.QueueFree();

			if (mesh is null)
			{
				error = "the glTF holds no mesh with a surface.";
				return false;
			}

			return true;
		}
		catch (Exception exception)
		{
			error = exception.Message;
			return false;
		}
	}

	public static string Describe(MeshConversion conversion) =>
		conversion.Ok
			? $"{conversion.Target}: {conversion.VertexCount} vertices, " +
			  $"{conversion.TriangleCount} triangles from {conversion.Mod}"
			: $"{conversion.Target} ({conversion.Mod}): {conversion.Error}";
}
