using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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

		if (!TryConvert(gltf, original, out byte[] rebuilt, out string writeError, out int vertexCount, out int triangleCount))
		{
			conversion.Error = writeError;
			conversions.Add(conversion);
			return;
		}

		conversion.VertexCount = vertexCount;
		conversion.TriangleCount = triangleCount;
		conversions.Add(conversion);

		mounted.AddVirtualFile(
			target, $"<mesh:{target}>", rebuilt.LongLength, () => rebuilt, modName);
	}

	/// <summary>
	/// Converts a glTF/GLB binary payload into a Refii <c>.mdl</c> binary using <paramref name="originalMdl"/> as the header/material/skeleton template.
	/// </summary>
	public static bool TryConvert(byte[] gltf, byte[] originalMdl, out byte[] rebuilt, out string error)
		=> TryConvert(gltf, originalMdl, out rebuilt, out error, out _, out _);

	/// <summary>
	/// Converts a glTF/GLB binary payload into a Refii <c>.mdl</c> binary using <paramref name="originalMdl"/> as the header/material/skeleton template.
	/// </summary>
	public static bool TryConvert(
		byte[] gltf,
		byte[] originalMdl,
		out byte[] rebuilt,
		out string error,
		out int vertexCount,
		out int triangleCount)
	{
		rebuilt = Array.Empty<byte>();
		error = string.Empty;
		vertexCount = 0;
		triangleCount = 0;

		if (gltf is null || gltf.Length == 0)
		{
			error = "glTF payload is empty.";
			return false;
		}

		if (originalMdl is null || originalMdl.Length == 0)
		{
			error = "Template .mdl payload is empty.";
			return false;
		}

		if (!Engine_MdlData.TryParseInfo(originalMdl, string.Empty, out MdlInfo info, out string parseError))
		{
			error = $"Template .mdl could not be parsed: {parseError}";
			return false;
		}

		if (!TryLoadAllSurfaces(gltf, out List<MdlImportedSurface> surfaces, out string loadError) || surfaces.Count == 0)
		{
			// Fallback to legacy single surface loader if needed:
			if (TryLoadMesh(gltf, out Mesh? fallbackMesh, out _) && fallbackMesh is not null &&
			    Engine_MdlImport.TryFromSurface(fallbackMesh, 0, out MdlImportedMesh impFallback, out _))
			{
				surfaces = new List<MdlImportedSurface>
				{
					new()
					{
						MaterialIndex = 0,
						Positions = impFallback.Positions,
						Uvs = impFallback.Uvs,
						Normals = impFallback.Normals,
						Tangents = impFallback.Tangents,
						Indices = impFallback.Indices,
					}
				};
			}
			else
			{
				error = loadError;
				return false;
			}
		}

		int maxMatIdx = surfaces.Max(s => s.MaterialIndex);
		int requiredMatCount = Math.Max(1, maxMatIdx + 1);

		byte[] templateMdl = originalMdl;
		MdlInfo templateInfo = info;
		if (templateInfo.Meshes.Count > 0 && templateInfo.Meshes[0].Materials.Count < requiredMatCount)
		{
			if (!Engine_MdlWriter.TryEnsureMaterialSlots(originalMdl, requiredMatCount, out byte[] expanded, out string expandErr))
			{
				error = $"Failed to expand material slots in template .mdl from {templateInfo.Meshes[0].Materials.Count} to {requiredMatCount}: {expandErr}";
				return false;
			}
			templateMdl = expanded;
			templateInfo = new MdlInfo();
			if (!Engine_MdlEngine.TryParse(templateMdl, templateInfo))
			{
				error = "Expanded template .mdl failed parse";
				return false;
			}
		}

		int totalVerts = surfaces.Sum(s => s.VertexCount);
		int totalIndices = surfaces.Sum(s => s.Indices.Length);

		if (totalVerts > ushort.MaxValue)
		{
			error = $"{totalVerts} vertices across {surfaces.Count} surfaces; indices are 16-bit, so a mesh cannot exceed 65,535.";
			return false;
		}

		var allPositions = new float[totalVerts * 3];
		var allUvs = new float[totalVerts * 2];
		var allNormals = new float[totalVerts * 3];
		var allTangents = new float[totalVerts * 4];
		var allIndices = new ushort[totalIndices];
		var sections = new List<MdlSectionSpec>(surfaces.Count);

		int vOffset = 0;
		int iOffset = 0;

		for (int s = 0; s < surfaces.Count; s++)
		{
			MdlImportedSurface surf = surfaces[s];
			int surfVertCount = surf.VertexCount;
			int surfIndexCount = surf.Indices.Length;

			Array.Copy(surf.Positions, 0, allPositions, vOffset * 3, surfVertCount * 3);
			if (surf.Uvs.Length >= surfVertCount * 2)
				Array.Copy(surf.Uvs, 0, allUvs, vOffset * 2, surfVertCount * 2);
			if (surf.Normals.Length >= surfVertCount * 3)
				Array.Copy(surf.Normals, 0, allNormals, vOffset * 3, surfVertCount * 3);
			if (surf.Tangents.Length >= surfVertCount * 4)
				Array.Copy(surf.Tangents, 0, allTangents, vOffset * 4, surfVertCount * 4);

			for (int idx = 0; idx < surfIndexCount; idx++)
			{
				allIndices[iOffset + idx] = (ushort)(surf.Indices[idx] + vOffset);
			}

			sections.Add(new MdlSectionSpec
			{
				MaterialIndex = surf.MaterialIndex,
				StartIndex = iOffset,
				TriangleCount = surf.TriangleCount,
			});

			vOffset += surfVertCount;
			iOffset += surfIndexCount;
		}

		if (!Engine_MdlWriter.TryReplaceMesh(
				templateMdl, templateInfo, 0,
				allPositions, allUvs, allNormals, allTangents,
				allIndices, sections, out rebuilt, out string writeError))
		{
			error = writeError;
			return false;
		}

		vertexCount = totalVerts;
		triangleCount = totalIndices / 3;
		return true;
	}

	/// <summary>
	/// Reads all surfaces across meshes in a glTF/GLB binary, resolving each surface's material index.
	/// </summary>
	public static bool TryLoadAllSurfaces(
		byte[] gltf,
		out List<MdlImportedSurface> surfaces,
		out string error,
		Transform3D? forcedRootTransform = null)
	{
		surfaces = new List<MdlImportedSurface>();
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

			var primMaterialIndices = ExtractPrimitiveMaterialIndicesFromGltf(gltf);

			Transform3D? meshSceneXform = null;
			if (Engine_GmdWriter.TryExtractFirstMeshSceneTransform(gltf, out Transform3D extractedXform) &&
			    !extractedXform.IsEqualApprox(Transform3D.Identity))
			{
				meshSceneXform = extractedXform;
			}
			Transform3D rootXform = forcedRootTransform ?? meshSceneXform ?? Transform3D.Identity;

			var gltfMeshes = state.GetMeshes();
			if (gltfMeshes is not null && gltfMeshes.Count > 0)
			{
				int globalPrimIdx = 0;
				for (int m = 0; m < gltfMeshes.Count; m++)
				{
					if (gltfMeshes[m].Mesh is { } imp)
					{
						ArrayMesh array = imp.GetMesh();
						if (array is not null && array.GetSurfaceCount() > 0)
						{
							for (int s = 0; s < array.GetSurfaceCount(); s++)
							{
								if (Engine_MdlImport.TryFromSurface(array, s, out MdlImportedMesh impMesh, out _, rootXform))
								{
									int matIdx = s;
									if (primMaterialIndices.TryGetValue((m, s), out int mappedMatIdx))
									{
										matIdx = mappedMatIdx;
									}
									else if (primMaterialIndices.TryGetValue((-1, globalPrimIdx), out int gIdx))
									{
										matIdx = gIdx;
									}

									surfaces.Add(new MdlImportedSurface
									{
										MaterialIndex = matIdx,
										Positions = impMesh.Positions,
										Uvs = impMesh.Uvs,
										Normals = impMesh.Normals,
										Tangents = impMesh.Tangents,
										Indices = impMesh.Indices,
									});
								}
								globalPrimIdx++;
							}
						}
					}
				}

				if (surfaces.Count > 0)
				{
					return true;
				}
			}

			Node scene = document.GenerateScene(state);
			if (scene is not null)
			{
				CollectSurfacesFromNode(scene, rootXform, surfaces, primMaterialIndices);
				scene.QueueFree();
			}

			if (surfaces.Count == 0)
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

	private static void CollectSurfacesFromNode(
		Node node,
		Transform3D parentTransform,
		List<MdlImportedSurface> surfaces,
		Dictionary<(int MeshIdx, int PrimIdx), int> primMaterialIndices)
	{
		Transform3D currentTransform = parentTransform;
		if (node is Node3D node3D)
		{
			currentTransform = parentTransform * node3D.Transform;
		}

		Mesh? mesh = null;
		if (node is MeshInstance3D mi && mi.Mesh is not null)
		{
			mesh = mi.Mesh;
		}
		else if (node is ImporterMeshInstance3D imi && imi.Mesh is not null)
		{
			mesh = imi.Mesh.GetMesh();
		}

		if (mesh is not null && mesh.GetSurfaceCount() > 0)
		{
			for (int s = 0; s < mesh.GetSurfaceCount(); s++)
			{
				if (Engine_MdlImport.TryFromSurface(mesh, s, out MdlImportedMesh impMesh, out _, currentTransform))
				{
					int matIdx = s;
					if (primMaterialIndices.TryGetValue((-1, surfaces.Count), out int gIdx))
					{
						matIdx = gIdx;
					}
					else if (primMaterialIndices.TryGetValue((0, s), out int mIdx))
					{
						matIdx = mIdx;
					}

					surfaces.Add(new MdlImportedSurface
					{
						MaterialIndex = matIdx,
						Positions = impMesh.Positions,
						Uvs = impMesh.Uvs,
						Normals = impMesh.Normals,
						Tangents = impMesh.Tangents,
						Indices = impMesh.Indices,
					});
				}
			}
		}

		foreach (Node child in node.GetChildren())
		{
			CollectSurfacesFromNode(child, currentTransform, surfaces, primMaterialIndices);
		}
	}

	private static Dictionary<(int MeshIdx, int PrimIdx), int> ExtractPrimitiveMaterialIndicesFromGltf(byte[] gltf)
	{
		var map = new Dictionary<(int MeshIdx, int PrimIdx), int>();
		try
		{
			string jsonStr;
			if (gltf.Length >= 20 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(gltf.AsSpan(0, 4)) == 0x46546C67u)
			{
				uint jsonLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(gltf.AsSpan(12, 4));
				jsonStr = Encoding.UTF8.GetString(gltf, 20, (int)Math.Min(jsonLen, gltf.Length - 20));
			}
			else
			{
				jsonStr = Encoding.UTF8.GetString(gltf);
			}

			using var doc = JsonDocument.Parse(jsonStr);
			if (doc.RootElement.TryGetProperty("meshes", out JsonElement meshesEl) &&
			    meshesEl.ValueKind == JsonValueKind.Array)
			{
				int mIdx = 0;
				int globalPIdx = 0;
				foreach (JsonElement meshEl in meshesEl.EnumerateArray())
				{
					if (meshEl.TryGetProperty("primitives", out JsonElement primsEl) &&
					    primsEl.ValueKind == JsonValueKind.Array)
					{
						int pIdx = 0;
						foreach (JsonElement primEl in primsEl.EnumerateArray())
						{
							if (primEl.TryGetProperty("material", out JsonElement matEl) &&
							    matEl.TryGetInt32(out int matVal))
							{
								map[(mIdx, pIdx)] = matVal;
								map[(-1, globalPIdx)] = matVal;
							}
							pIdx++;
							globalPIdx++;
						}
					}
					mIdx++;
				}
			}
		}
		catch
		{
		}
		return map;
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
