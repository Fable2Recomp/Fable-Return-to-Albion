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

public partial class AnimCheck : Node
{
	[Export] public int ClipSamples { get; set; } = 6;

	[Export] public int EntitySamples { get; set; } = 6;

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

		GD.Print($"[anim] mounting {dataRoot}");
		_filesystem = BnkFileSystem.Mount(dataRoot);
		GD.Print($"[anim] mounted {_filesystem.FileCount:N0} files " +
			$"from {_filesystem.Archives.Count} banks");

		Engine_BnkMeshLoader.SetResolver(ResolveBytes);
		Engine_BnkMeshLoader.SetExistsProbe(p => _filesystem.TryGetFile(p, out _));
		Engine_PropModelIndex.SetModelSource(EnumerateModelAssets);
		Engine_EntityIndex.SetResolver(ResolveBytes);
		Engine_AnimCatalog.SetFileSource(EnumerateAllPaths);

		ProbeFiles();

		if (!Engine_AnimCatalog.EnsureLoaded())
		{
			GD.Print("[anim] catalog failed to load");
			GetTree().Quit(0);
			return;
		}

		DumpStateRecords();
		ReportBindings();
		ReportClips();
		ReportEntities();
		ReportDecode();
		ReportBuild();

		GetTree().Quit(0);
	}

	private void ProbeFiles()
	{
		foreach (string path in new[] { Engine_AnimBank.TocPath, Engine_AnimDataFile.DataPath })
		{
			byte[]? bytes = ResolveBytes(path);
			GD.Print(bytes is null
				? $"[anim] MISSING {path}"
				: $"[anim] found {path} ({bytes.Length:N0} bytes)");
		}

		int gdbCount = EnumerateGdbPaths().Count();
		GD.Print($"[anim] {gdbCount:N0} gdb file(s) visible");

		if (_filesystem is null)
		{
			return;
		}

		var hits = new List<string>();
		var extensions = new Dictionary<string, int>();

		foreach (BnkFile file in _filesystem.EnumerateAllFiles())
		{
			string lower = file.VirtualPath.ToLowerInvariant();

			if (lower.Contains("animation_toc") || lower.Contains("animation_data") ||
				lower.Contains("anims"))
			{
				if (hits.Count < 24)
				{
					hits.Add($"{file.VirtualPath}  ({file.Size:N0} bytes)");
				}
			}

			int dot = lower.LastIndexOf('.');
			if (dot >= 0)
			{
				string ext = lower[dot..];
				if (ext.Contains("anim"))
				{
					extensions.TryGetValue(ext, out int count);
					extensions[ext] = count + 1;
				}
			}
		}

		GD.Print($"[anim] name probe found {hits.Count} candidate(s)");
		foreach (string hit in hits)
		{
			GD.Print($"[anim]   {hit}");
		}

		foreach (KeyValuePair<string, int> pair in extensions.OrderByDescending(p => p.Value))
		{
			GD.Print($"[anim]   extension {pair.Key} x{pair.Value}");
		}
	}

	private void ProbeFieldNames()
	{
		string[] keywords =
		{
			"anim", "state", "transition", "blend", "graph", "tree",
			"layer", "priority", "mask", "alias", "loop",
		};

		var found = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

		foreach (string path in EnumerateGdbPaths())
		{
			byte[]? bytes = ResolveBytes(path);
			if (bytes is null || bytes.Length == 0 ||
				!GdbView.TryParse(bytes, out GdbView view, out _))
			{
				continue;
			}

			foreach (KeyValuePair<uint, string> pair in view.Names)
			{
				string name = pair.Value;
				if (name.Length == 0 || name.Length > 48)
				{
					continue;
				}

				foreach (string key in keywords)
				{
					if (name.Contains(key, StringComparison.OrdinalIgnoreCase))
					{
						found.TryGetValue(name, out int count);
						found[name] = count + 1;
						break;
					}
				}
			}
		}

		GD.Print($"[anim] field-name probe: {found.Count} distinct animation-ish name(s)");
		foreach (KeyValuePair<string, int> pair in
			found.Where(p => p.Value > 1).OrderByDescending(p => p.Value))
		{
			GD.Print($"[anim]   SCHEMA {pair.Key}  x{pair.Value}");
		}
	}

	private void DumpStateRecords()
	{
		string[] targets =
		{
			"AnimationManagerComponent", "States", "ChangeStateWhenTriggered",
		};

		int dumped = 0;

		foreach (string path in EnumerateGdbPaths())
		{
			if (dumped >= 3)
			{
				break;
			}

			byte[]? bytes = ResolveBytes(path);
			if (bytes is null || bytes.Length == 0 ||
				!GdbView.TryParse(bytes, out GdbView view, out _))
			{
				continue;
			}

			var wanted = new HashSet<uint>();
			foreach (KeyValuePair<uint, string> pair in view.Names)
			{
				if (targets.Contains(pair.Value, StringComparer.OrdinalIgnoreCase))
				{
					wanted.Add(pair.Key);
				}
			}

			if (wanted.Count == 0)
			{
				continue;
			}

			for (int i = 0; i < view.RecordCount && dumped < 3; i++)
			{
				bool hit = false;
				foreach ((uint hash, GdbFieldType type, uint raw) in view.RawFields(i))
				{
					if (wanted.Contains(hash))
					{
						hit = true;
						break;
					}
				}

				if (!hit)
				{
					continue;
				}

				dumped++;
				GD.Print($"[anim] ---- state record #{dumped} from {path} ----");
				DumpRecord(view, i, 0, new HashSet<int>());
			}
		}
	}

	private static void DumpRecord(GdbView view, int index, int depth, HashSet<int> seen)
	{
		if (depth > 3 || !seen.Add(index))
		{
			return;
		}

		string pad = new(' ', depth * 3);
		GD.Print($"[anim] {pad}rec 0x{view.RecordHash(index):X8} '{view.RecordName(index)}'");

		foreach ((uint hash, GdbFieldType type, uint raw) in view.RawFields(index))
		{
			string name = view.Names.TryGetValue(hash, out string? n) ? n : $"#{hash:X8}";
			string val = view.Names.TryGetValue(raw, out string? rn) ? rn : $"0x{raw:X8}";
			GD.Print($"[anim] {pad}   {name} : {type} = {val}");

			if (type is GdbFieldType.Reference &&
				view.TryLookup(raw, out int child) && child != index)
			{
				DumpRecord(view, child, depth + 1, seen);
			}
		}
	}

	private void ReportClips()
	{
		IReadOnlyList<AnimClip> clips = Engine_AnimCatalog.AllClips;
		int named = clips.Count(c => !c.Name.StartsWith("id_", StringComparison.Ordinal));
		int withTracks = clips.Count(c => c.TrackMap is { Count: > 0 });

		GD.Print($"[anim] {clips.Count:N0} clip(s); {withTracks:N0} with track maps; " +
			$"{named:N0} with resolved names");

		foreach (AnimClip clip in clips.Take(ClipSamples))
		{
			GD.Print($"[anim]   {clip.Name,-28} key0=0x{clip.Key0:X8} key1=0x{clip.Key1:X8} " +
				$"fps={clip.Fps,6:F2} frames={clip.TocFrameCount,5} " +
				$"bytes={clip.DataSizeBytes,7} tracks={clip.TrackMap?.Count ?? 0}");
		}
	}

	private void ReportBindings()
	{
		bool ok = Engine_AnimCatalog.EnsureBindings();
		IReadOnlyList<ModelAnimationBinding> bindings = Engine_AnimCatalog.AllBindings;

		GD.Print($"[anim] bindings resolved: {ok}, {bindings.Count:N0} binding(s)");

		var byModel = new Dictionary<uint, int>();
		foreach (ModelAnimationBinding binding in bindings)
		{
			byModel.TryGetValue(binding.ModelPathHash, out int count);
			byModel[binding.ModelPathHash] = count + 1;
		}

		GD.Print($"[anim] {byModel.Count:N0} distinct model(s) carry animations");

		foreach (ModelAnimationBinding binding in bindings.Take(ClipSamples))
		{
			GD.Print($"[anim]   model=0x{binding.ModelPathHash:X8} clip={binding.ClipIndex,6} " +
				$"key=0x{binding.AnimationKey:X8} name={binding.AnimationName}");
		}
	}

	private void ReportEntities()
	{
		IReadOnlyList<EntityEntry> entries = Engine_EntityIndex.Entries;
		GD.Print($"[anim] entity catalog {entries.Count:N0} entries");

		int withAnims = 0;
		int byRawHash = 0;
		var samples = new List<string>();

		foreach (EntityEntry entry in entries)
		{
			var clipSet = new HashSet<int>();
			string first = string.Empty;

			foreach (uint hash in entry.ModelHashes)
			{
				IReadOnlyList<ModelAnimationBinding> raw =
					Engine_AnimCatalog.BindingsForModelHash(hash);

				if (raw.Count != 0)
				{
					byRawHash++;
					foreach (ModelAnimationBinding binding in raw)
					{
						clipSet.Add(binding.ClipIndex);
					}

					if (first.Length == 0)
					{
						first = raw[0].AnimationName;
					}

					continue;
				}
			}

			int total = clipSet.Count;
			if (total == 0)
			{
				continue;
			}

			withAnims++;
			if (samples.Count < EntitySamples)
			{
				var seen = new HashSet<uint>();
				int repeated = 0;
				foreach (uint hash in entry.ModelHashes)
				{
					if (!seen.Add(hash))
					{
						repeated++;
					}
				}

				samples.Add($"{entry.Name,-14} entity=0x{entry.EntityHash:X8} " +
					$"models={entry.ModelHashes.Count} distinct={seen.Count} " +
					$"repeated={repeated} {total,5} clip(s)  first={first}");
			}
		}

		GD.Print($"[anim] model matches: {byRawHash} by raw hash");
		GD.Print($"[anim] {withAnims:N0} entity/entities have bound animations");
		foreach (string line in samples)
		{
			GD.Print($"[anim]   {line}");
		}
	}

	private void ReportDecode()
	{
		IReadOnlyList<AnimClip> clips = Engine_AnimCatalog.AllClips;
		var pose = new DecodedPose();
		int ok = 0;
		int failed = 0;
		int attempted = 0;

		foreach (AnimClip clip in clips)
		{
			if (clip.DataSizeBytes == 0 || clip.Fps <= 0.0f || attempted >= 200)
			{
				continue;
			}

			attempted++;
			if (Engine_AnimCatalog.ActiveDecoder.Decode(clip, 0.0f, pose) && pose.Ok)
			{
				ok++;
			}
			else
			{
				failed++;
			}
		}

		GD.Print($"[anim] decode probe: {ok}/{attempted} clip(s) produced a pose");


		AnimClip? sample = clips.FirstOrDefault(
			c => c.DataSizeBytes != 0 && c.Fps > 0.0f && c.TrackMap is { Count: > 0 });

		if (sample is null)
		{
			GD.Print("[anim] no sample clip for pose dump");
			return;
		}

		if (!Engine_AnimCatalog.ActiveDecoder.Decode(sample, 0.0f, pose) || !pose.Ok)
		{
			GD.Print($"[anim] sample {sample.Name} failed to decode");
			return;
		}

		GD.Print($"[anim] sample {sample.Name}: {pose.BoneCount} bone(s)");
		for (int i = 0; i < Math.Min(4, pose.BoneCount); i++)
		{
			string bone = sample.TrackMap is not null && i < sample.TrackMap.Count
				? sample.TrackMap[i].Name
				: $"bone_{i}";

			GD.Print($"[anim]   {bone,-24} " +
				$"q=({pose.BoneQuats[i * 4]:F3},{pose.BoneQuats[i * 4 + 1]:F3}," +
				$"{pose.BoneQuats[i * 4 + 2]:F3},{pose.BoneQuats[i * 4 + 3]:F3}) " +
				$"t=({pose.BoneTrans[i * 3]:F3},{pose.BoneTrans[i * 3 + 1]:F3}," +
				$"{pose.BoneTrans[i * 3 + 2]:F3})");
		}
	}

	private void ReportBuild()
	{
		foreach (EntityEntry entry in Engine_EntityIndex.Entries)
		{
			foreach (uint modelHash in entry.ModelHashes)
			{
				IReadOnlyList<ModelAnimationBinding> bound =
					Engine_AnimCatalog.BindingsForModelHash(modelHash);

				if (bound.Count == 0)
				{
					continue;
				}

				if (Engine_PropModelIndex.Resolve(modelHash, Array.Empty<string>())
					is not string path)
				{
					continue;
				}

				byte[]? bytes = ResolveBytes(path);
				if (bytes is null ||
					!Engine_MdlData.TryParseInfo(bytes, path, out MdlInfo info, out _) ||
					!info.HasBoneTransforms || info.Bones.Count == 0)
				{
					continue;
				}

				Skeleton3D? skeleton = Engine_AnimSkeleton.Build(info);
				if (skeleton is null)
				{
					continue;
				}

				List<string> boneNames = Engine_AnimSkeleton.BoneNames(skeleton);
				GD.Print($"[anim] build target {entry.Name} :: {path}");
				GD.Print($"[anim]   skeleton {boneNames.Count} bone(s), {bound.Count} binding(s)");

				int built = 0;
				foreach (ModelAnimationBinding binding in bound.Take(4))
				{
					AnimClip? clip = Engine_AnimCatalog.ClipAt(binding.ClipIndex);
					if (clip is null)
					{
						continue;
					}

					Animation? animation = Engine_AnimBuilder.Build(
						clip, Engine_AnimCatalog.ActiveDecoder, skeleton,
						Engine_AnimSkeleton.SkeletonName);

					if (animation is null)
					{
						GD.Print($"[anim]   {binding.AnimationName,-24} BUILD FAILED");
						continue;
					}

					built++;
					GD.Print($"[anim]   {binding.AnimationName,-24} " +
						$"{animation.GetTrackCount(),4} track(s)  {animation.Length,6:F2}s");
				}

				skeleton.QueueFree();
				GD.Print($"[anim] built {built} animation(s) end to end");
				return;
			}
		}

		GD.Print("[anim] no entity with both a rig and bindings");
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

	private IEnumerable<string> EnumerateGdbPaths() =>
		EnumerateAllPaths().Where(p => p.EndsWith(".gdb", StringComparison.OrdinalIgnoreCase));

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
