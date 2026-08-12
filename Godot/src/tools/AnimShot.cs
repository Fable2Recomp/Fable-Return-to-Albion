using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Refii.Animations;
using Refii.Bnk;
using Refii.Core;
using Refii.Entities;
using Refii.Gdb;
using Refii.Level;
using Refii.Models;

namespace Refii.Launcher.Tools;

public partial class AnimShot : Node3D
{
	[Export] public string OutputDirectory { get; set; } = string.Empty;

	[Export] public int MinimumFrames { get; set; } = 12;

	[Export] public bool ShowMesh { get; set; } = true;

	private BnkFileSystem? _filesystem;

	public override async void _Ready()
	{
		string dataRoot = Engine_GameData.Resolve();
		if (dataRoot.Length == 0)
		{
			GD.Print("[shot] no game data found");
			GetTree().Quit(1);
			return;
		}

		_filesystem = BnkFileSystem.Mount(dataRoot);
		GD.Print($"[shot] mounted {_filesystem.FileCount:N0} files");

		Engine_BnkMeshLoader.SetResolver(ResolveBytes);
		Engine_BnkMeshLoader.SetExistsProbe(p => _filesystem.TryGetFile(p, out _));
		Engine_PropModelIndex.SetModelSource(EnumerateModelAssets);
		Engine_EntityIndex.SetResolver(ResolveBytes);
		Engine_AnimCatalog.SetFileSource(EnumerateAllPaths);

		if (!Engine_AnimCatalog.EnsureLoaded() || !Engine_AnimCatalog.EnsureBindings())
		{
			GD.Print("[shot] catalog unavailable");
			GetTree().Quit(1);
			return;
		}

		if (!TryPickTarget(out string entityName, out string modelPath,
				out AnimClip clip, out string clipLabel))
		{
			GD.Print("[shot] no suitable entity found");
			GetTree().Quit(1);
			return;
		}

		GD.Print($"[shot] {entityName} :: {modelPath}");
		GD.Print($"[shot] clip {clipLabel} ({clip.Name}) {clip.TocFrameCount} frames @ {clip.Fps} fps");

		byte[]? bytes = ResolveBytes(modelPath);
		if (bytes is null ||
			!Engine_MdlData.TryParseInfo(bytes, modelPath, out MdlInfo info, out _))
		{
			GetTree().Quit(1);
			return;
		}

		Skeleton3D? skeleton = Engine_AnimSkeleton.Build(info);
		if (skeleton is null)
		{
			GetTree().Quit(1);
			return;
		}

		AddChild(skeleton);

		var mesh = new ArrayMesh();
		Engine_EntityMesh.Append(mesh, new[] { modelPath }, Engine_PropStreamer.Resolve, true);

		MeshInstance3D? surface = null;
		if (ShowMesh && mesh.GetSurfaceCount() != 0)
		{
			surface = new MeshInstance3D { Mesh = mesh, Name = "Surface" };
			skeleton.AddChild(surface);
			surface.Skin = skeleton.CreateSkinFromRestTransforms();
			GD.Print($"[shot] mesh {mesh.GetSurfaceCount()} surface(s)");
		}
		else
		{
			GD.Print($"[shot] rig only ({mesh.GetSurfaceCount()} surface(s) available)");
		}

		Animation? animation = Engine_AnimBuilder.Build(
			clip, Engine_AnimCatalog.ActiveDecoder, skeleton, Engine_AnimSkeleton.SkeletonName);

		if (animation is null)
		{
			GD.Print("[shot] animation failed to build");
			GetTree().Quit(1);
			return;
		}

		var library = new AnimationLibrary();
		library.AddAnimation("clip", animation);

		var player = new AnimationPlayer { Name = "AnimationPlayer" };
		AddChild(player);
		player.AddAnimationLibrary("lib", library);
		player.RootNode = player.GetPathTo(this);

		GD.Print($"[shot] animation {animation.GetTrackCount()} track(s) {animation.Length:F2}s");

		var bones = new MeshInstance3D { Name = "Bones" };
		AddChild(bones);

		Aabb bounds = MeasureSkeleton(skeleton);
		SetUpView(bounds);

		string directory = OutputDirectory.Length != 0
			? OutputDirectory
			: ProjectSettings.GlobalizePath("user://");

		float length = (float)animation.Length;
		float[] times = { 0.0f, length * 0.35f, length * 0.7f };

		for (int i = -1; i < times.Length; i++)
		{
			if (i < 0)
			{
				skeleton.ResetBonePoses();
				GD.Print("[shot] capturing rest pose (no animation)");
			}
			else
			{
				player.Play("lib/clip");
				player.Seek(times[i], true);
				player.Pause();
			}

			DrawSkeleton(bones, skeleton);

			for (int f = 0; f < 3; f++)
			{
				await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
			}

			await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

			Image image = GetViewport().GetTexture().GetImage();
			string label = i < 0 ? "rest" : i.ToString();
			string path = System.IO.Path.Combine(directory, $"anim_pose_{label}.png");
			image.SavePng(path);
			GD.Print(i < 0
				? $"[shot] saved {path} (rest)"
				: $"[shot] saved {path} at t={times[i]:F2}s");
		}

		GetTree().Quit(0);
	}

	private bool TryPickTarget(
		out string entityName, out string modelPath, out AnimClip clip, out string clipLabel)
	{
		entityName = string.Empty;
		modelPath = string.Empty;
		clip = null!;
		clipLabel = string.Empty;

		foreach (EntityEntry entry in Engine_EntityIndex.Entries)
		{
			foreach (uint modelHash in entry.ModelHashes)
			{
				IReadOnlyList<ModelAnimationBinding> bound =
					Engine_AnimCatalog.BindingsForModelHash(modelHash);

				if (bound.Count == 0 ||
					Engine_PropModelIndex.Resolve(modelHash, Array.Empty<string>())
						is not string path)
				{
					continue;
				}

				byte[]? bytes = ResolveBytes(path);
				if (bytes is null ||
					!Engine_MdlData.TryParseInfo(bytes, path, out MdlInfo info, out _) ||
					!info.HasBoneTransforms || info.Bones.Count < 16)
				{
					continue;
				}

				foreach (ModelAnimationBinding binding in bound)
				{
					AnimClip? candidate = Engine_AnimCatalog.ClipAt(binding.ClipIndex);
					if (candidate is null || candidate.TocFrameCount < MinimumFrames)
					{
						continue;
					}

					entityName = entry.Name;
					modelPath = path;
					clip = candidate;
					clipLabel = binding.AnimationName;
					return true;
				}
			}
		}

		return false;
	}

	private static Aabb MeasureSkeleton(Skeleton3D skeleton)
	{
		var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
		var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

		for (int i = 0; i < skeleton.GetBoneCount(); i++)
		{
			Vector3 p = skeleton.GetBoneGlobalPose(i).Origin;
			min = min.Min(p);
			max = max.Max(p);
		}

		return new Aabb(min, max - min);
	}

	private void DrawSkeleton(MeshInstance3D holder, Skeleton3D skeleton)
	{
		var lines = new ImmediateMesh();
		var material = new StandardMaterial3D
		{
			ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
			AlbedoColor = new Color(1.0f, 0.75f, 0.2f),
			VertexColorUseAsAlbedo = false,
			NoDepthTest = true,
		};

		lines.SurfaceBegin(Mesh.PrimitiveType.Lines, material);

		for (int i = 0; i < skeleton.GetBoneCount(); i++)
		{
			int parent = skeleton.GetBoneParent(i);
			if (parent < 0)
			{
				continue;
			}

			lines.SurfaceAddVertex(skeleton.GetBoneGlobalPose(parent).Origin);
			lines.SurfaceAddVertex(skeleton.GetBoneGlobalPose(i).Origin);
		}

		lines.SurfaceEnd();
		holder.Mesh = lines;
	}

	private void SetUpView(Aabb bounds)
	{
		Vector3 centre = bounds.GetCenter();
		float radius = MathF.Max(bounds.Size.Length() * 0.5f, 1.0f);

		var camera = new Camera3D { Name = "Camera3D", Current = true };
		AddChild(camera);

		Vector3 eye = centre + new Vector3(radius * 1.6f, radius * 0.55f, radius * 2.1f);
		camera.GlobalPosition = eye;
		camera.LookAt(centre, Vector3.Up);
		camera.Near = 0.01f;
		camera.Far = radius * 40.0f;

		var key = new DirectionalLight3D { Name = "Key", LightEnergy = 1.5f };
		AddChild(key);
		key.GlobalPosition = centre + new Vector3(radius, radius * 2.0f, radius);
		key.LookAt(centre, Vector3.Up);

		var environment = new Godot.Environment
		{
			BackgroundMode = Godot.Environment.BGMode.Color,
			BackgroundColor = new Color(0.08f, 0.09f, 0.11f),
			AmbientLightSource = Godot.Environment.AmbientSource.Color,
			AmbientLightColor = new Color(0.45f, 0.47f, 0.52f),
			AmbientLightEnergy = 1.0f,
		};

		AddChild(new WorldEnvironment { Environment = environment, Name = "WorldEnvironment" });

		GD.Print($"[shot] bounds centre {centre} radius {radius:F2}");
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

	private IEnumerable<string> EnumerateAllPaths()
	{
		if (_filesystem is null)
		{
			yield break;
		}

		foreach (BnkFile file in _filesystem.EnumerateAllFiles())
		{
			yield return file.VirtualPath;
		}
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
