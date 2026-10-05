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
			string sourcePath = !string.IsNullOrEmpty(overlay.PackagePath) ? overlay.PackagePath : overlay.AssetRoot;
			if (File.Exists(sourcePath) && sourcePath.EndsWith(".f2mod", StringComparison.OrdinalIgnoreCase))
			{
				if (F2ModPackage.TryOpen(sourcePath, out F2ModPackage? pkg, out _) && pkg is not null)
				{
					using (pkg)
					{
						foreach (F2ModEntry entry in pkg.Entries)
						{
							string? suffix = null;
							foreach (string candidate in Suffixes)
							{
								if (entry.Path.EndsWith(candidate, StringComparison.OrdinalIgnoreCase))
								{
									suffix = candidate;
									break;
								}
							}

							if (suffix is null)
							{
								continue;
							}

							byte[] gltf;
							try
							{
								gltf = pkg.ReadEntryBytes(entry);
							}
							catch (Exception exception)
							{
								conversions.Add(new MeshConversion
								{
									Target = entry.Path, Mod = overlay.Name, Error = exception.Message,
								});
								continue;
							}

							string relative = entry.Path.Replace('\\', '/').TrimStart('/');
							if (relative.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
							{
								relative = relative["files/".Length..];
							}
							string target = ModLoadOrder.Normalize(relative);
							target = target[..^(suffix.Length - ".mdl".Length)];

							ProcessMesh(mounted, conversions, overlay.Name, target, gltf);
						}
					}
				}
				continue;
			}

			if (!Directory.Exists(overlay.AssetRoot))
			{
				continue;
			}

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

				string normRel = relative.Replace('\\', '/').TrimStart('/');
				if (normRel.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
				{
					normRel = normRel["files/".Length..];
				}
				string target = ModLoadOrder.Normalize(normRel);
				target = target[..^(suffix.Length - ".mdl".Length)];

				ProcessMesh(mounted, conversions, overlay.Name, target, gltf);
			}
		}

		return conversions;
	}

	private static void ProcessMesh(
		BnkFileSystem mounted,
		List<MeshConversion> conversions,
		string modName,
		string target,
		byte[] gltf)
	{
		var conversion = new MeshConversion { Target = target, Mod = modName };

		if (!mounted.TryGetFile(target, out BnkFile template))
		{
			bool foundAlias = false;
			foreach (string alias in BnkFileSystem.AliasesFor(target))
			{
				if (mounted.TryGetFile(alias, out template))
				{
					target = alias;
					foundAlias = true;
					break;
				}
			}

			if (!foundAlias)
			{
				conversion.Error =
					$"there is no '{target}' to replace — a model mod edits a model the game " +
					"already has, because the file states far more than its mesh.";
				conversions.Add(conversion);
				return;
			}
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
			return;
		}

		if (!Engine_MdlData.TryParseInfo(original, target, out MdlInfo info, out string parseError))
		{
			conversion.Error = $"'{target}' could not be read: {parseError}";
			conversions.Add(conversion);
			return;
		}

		if (!TryLoadMesh(gltf, out Mesh? mesh, out string loadError) || mesh is null)
		{
			conversion.Error = loadError;
			conversions.Add(conversion);
			return;
		}

		if (!Engine_MdlImport.TryFromSurface(mesh, 0, out MdlImportedMesh imported, out string importError))
		{
			conversion.Error = importError;
			conversions.Add(conversion);
			return;
		}

		if (!Engine_MdlWriter.TryReplaceMesh(
				original, info, 0,
				imported.Positions, imported.Uvs, imported.Normals, imported.Tangents,
				imported.Indices, out byte[] rebuilt, out string writeError))
		{
			conversion.Error = writeError;
			conversions.Add(conversion);
			return;
		}

		conversion.VertexCount = imported.VertexCount;
		conversion.TriangleCount = imported.TriangleCount;
		conversions.Add(conversion);

		mounted.AddVirtualFile(
			target, $"<mesh:{target}>", rebuilt.LongLength, () => rebuilt, modName);
	}

	/// <summary>Reads a glTF into a scene and takes its first mesh.</summary>
	private static bool TryLoadMesh(byte[] gltf, out Mesh? mesh, out string error)
	{
		mesh = null;
		error = string.Empty;

		try
		{
			var document = new GltfDocument();
			var state = new GltfState
			{
				HandleBinaryImageMode = GltfState.HandleBinaryImageModeEnum.DiscardTextures,
			};
			Error appended = document.AppendFromBuffer(gltf, string.Empty, state);
			if (appended != Error.Ok)
			{
				error = $"the glTF could not be read ({appended}).";
				return false;
			}

			// First check GltfState meshes directly:
			var gltfMeshes = state.GetMeshes();
			if (gltfMeshes is not null)
			{
				foreach (GltfMesh gltfMesh in gltfMeshes)
				{
					if (gltfMesh.Mesh is { } imp)
					{
						ArrayMesh array = imp.GetMesh();
						if (array is not null && array.GetSurfaceCount() > 0)
						{
							mesh = array;
							return true;
						}
					}
				}
			}

			Node scene = document.GenerateScene(state);
			if (scene is not null)
			{
				mesh = Engine_MdlImport.FindFirstMesh(scene);
				scene.QueueFree();
			}

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
