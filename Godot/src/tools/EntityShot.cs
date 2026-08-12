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

public partial class EntityShot : Node3D
{
	[Export] public string Filter { get; set; } = "Abbot";

	[Export] public string OutputPath { get; set; } = "user://entity_shot.png";

	private BnkFileSystem? _filesystem;

	private int _frames;

	private string _output = string.Empty;

	public override void _Ready()
	{
		string dataRoot = Engine_GameData.Resolve();
		if (dataRoot.Length == 0)
		{
			GD.Print("no game data found; set REFII_GAME_DATA.");
			GetTree().Quit(1);
			return;
		}

		_filesystem = BnkFileSystem.Mount(dataRoot);
		Refii.Textures.Engine_BnkTextureLoader.SetResolver(ResolveBytes);
		Engine_BnkMeshLoader.SetResolver(ResolveBytes);
		Engine_BnkMeshLoader.SetExistsProbe(p => _filesystem.TryGetFile(p, out _));
		Engine_PropModelIndex.SetModelSource(EnumerateModelAssets);
		Engine_EntityIndex.SetResolver(ResolveBytes);

		string filter = System.Environment.GetEnvironmentVariable("REFII_ENTITY") ?? Filter;
		EntityEntry? entry = Engine_EntityIndex.Entries
			.Where(e => e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
			.OrderByDescending(e => e.ModelHashes.Count)
			.FirstOrDefault();

		if (entry is null)
		{
			GD.Print($"[shot] no entity matched '{filter}'");
			GetTree().Quit(1);
			return;
		}

		var paths = new List<string>();
		foreach (uint hash in entry.ModelHashes)
		{
			if (Engine_PropModelIndex.Resolve(hash, Array.Empty<string>()) is string path)
			{
				paths.Add(path);
			}
		}

		ArrayMesh mesh;

		if (System.Environment.GetEnvironmentVariable("REFII_SHOT_RESOURCE") == "1" ||
			System.Environment.GetEnvironmentVariable("REFII_SHOT_ZOMBIE") == "1")
		{
			Engine_EntityResource? resource =
				Engine_EntityIndex.BuildResource(Engine_EntityIndex.VirtualPathOf(entry));

			if (resource is null)
			{
				GD.Print("[shot] resource build failed");
				GetTree().Quit(1);
				return;
			}

			if (System.Environment.GetEnvironmentVariable("REFII_SHOT_ZOMBIE") == "1")
			{
				resource.Set("Zombie Eyes", true);
				GD.Print($"[shot] zombie eyes = {resource.Get("Zombie Eyes")}");
			}

			mesh = resource;
		}
		else
		{
			mesh = new ArrayMesh();

			if (System.Environment.GetEnvironmentVariable("REFII_SHOT_RAW") == "1")
			{
				AppendRaw(mesh, paths);
			}
			else
			{
				Engine_EntityMesh.Append(mesh, paths, ResolvePart);
			}
		}

		var instance = new MeshInstance3D { Mesh = mesh };
		AddChild(instance);

		Aabb bounds = mesh.GetAabb();
		GD.Print($"[shot] {entry.Name} surfaces={mesh.GetSurfaceCount()} " +
			$"bounds={bounds.Position} .. {bounds.End}");

		bool head = System.Environment.GetEnvironmentVariable("REFII_SHOT_HEAD") == "1";
		Vector3 target = head
			? new Vector3(0f, bounds.End.Y - 0.09f, 0f)
			: bounds.GetCenter();
		float distance = head ? 0.45f : bounds.Size.Y * 1.2f;

		float yaw = Mathf.DegToRad(
			float.TryParse(
				System.Environment.GetEnvironmentVariable("REFII_SHOT_YAW"), out float degrees)
				? degrees
				: 0f);

		var offset = new Vector3(-Mathf.Sin(yaw) * distance, 0f, -Mathf.Cos(yaw) * distance);

		var camera = new Camera3D
		{
			Position = target + offset,
			Fov = 45f,
		};
		AddChild(camera);
		camera.LookAt(target, Vector3.Up);

		var light = new DirectionalLight3D
		{
			LightEnergy = 1.4f,
			Rotation = new Vector3(-0.6f, Mathf.Pi, 0f),
		};
		AddChild(light);

		var environment = new WorldEnvironment
		{
			Environment = new Godot.Environment
			{
				BackgroundMode = Godot.Environment.BGMode.Color,
				BackgroundColor = new Color(0.15f, 0.17f, 0.2f),
				AmbientLightSource = Godot.Environment.AmbientSource.Color,
				AmbientLightColor = new Color(0.6f, 0.6f, 0.65f),
				AmbientLightEnergy = 0.8f,
			},
		};
		AddChild(environment);

		_output = System.Environment.GetEnvironmentVariable("REFII_SHOT_OUT") ?? OutputPath;
	}

	public override void _Process(double delta)
	{
		if (_output.Length == 0 || ++_frames < 5)
		{
			return;
		}

		Image image = GetViewport().GetTexture().GetImage();
		Error error = image.SavePng(_output);
		GD.Print($"[shot] wrote {ProjectSettings.GlobalizePath(_output)} ({error})");
		GetTree().Quit(error == Error.Ok ? 0 : 1);
	}

	private void AppendRaw(ArrayMesh target, IReadOnlyList<string> paths)
	{
		foreach (string path in paths)
		{
			ArrayMesh? part = ResolvePart(path);
			if (part is null)
			{
				continue;
			}

			for (int surface = 0; surface < part.GetSurfaceCount(); surface++)
			{
				target.AddSurfaceFromArrays(
					part.SurfaceGetPrimitiveType(surface), part.SurfaceGetArrays(surface));

				int added = target.GetSurfaceCount() - 1;
				target.SurfaceSetName(added, part.SurfaceGetName(surface));
				target.SurfaceSetMaterial(added, part.SurfaceGetMaterial(surface));
			}
		}
	}

	private ArrayMesh? ResolvePart(string modelPath)
	{
		byte[]? bytes = Engine_BnkMeshLoader.ReadBytes(modelPath);
		return bytes is null
			? null
			: Engine_MdlMesh.TryCreateMesh(bytes, modelPath, out _, out _);
	}

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
