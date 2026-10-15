using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Refii.Assets;
using Refii.Core;

namespace Refii.Launcher.Tools;

/// <summary>
/// Displays a built REGION, lit exactly as the editor and the running game light it.
///
/// Lighting is the one thing neither of the existing instruments can answer. `model_viewer`
/// lights a single model with its own key/fill rig, so it says nothing about the level's sun;
/// `mdl_survey` counts. A region's lighting is only visible with the region's own sun,
/// environment and baked data standing together — which is what this loads.
///
/// It goes through <see cref="Engine_GameAssets.TryBuildLevel"/>, the SAME construction path
/// the editor's Game:// tree and <c>GameWorld</c> both use, so a fault seen here is a fault in
/// all three rather than in a fourth copy of the scene builder.
///
/// Orbit with the left mouse button, zoom with the wheel, WASD/QE to fly the pivot,
/// <c>[</c>/<c>]</c> to step time of day.
///
/// Screenshot mode (<c>REFII_LEVEL_SHOT=&lt;path&gt;</c>) frames the region, renders a few
/// frames and quits. The shot path must live OUTSIDE the repository and any build output: a
/// render of game geometry is derived game data and is not redistributable.
/// </summary>
public partial class LevelViewer : Node3D
{
	/// <summary>Level name to show. <c>REFII_LEVEL</c> overrides it.</summary>
	[Export] public string LevelName { get; set; } = "BWSSlums";

	/// <summary>Frames to render before the screenshot, so streaming and materials settle.</summary>
	[Export] public int ShotWarmupFrames { get; set; } = 90;

	private Camera3D _camera = null!;
	private Label _caption = null!;
	private Node3D? _region;
	private MeshInstance3D? _labHero;

	private float _yaw = 0.6f;
	private float _pitch = 0.35f;
	private float _distance = 40.0f;
	private Vector3 _target = Vector3.Zero;
	private bool _orbiting;

	private string _shotPath = string.Empty;
	private int _debugView = -1;
	private float _bakeWeight = -1f;
	private int _framesRendered;
	private bool _classicLoaded;
	private long _classicLoadFrom;
	private int _framesWaited;

	/// <summary>Viewport render times summed over the frames before the shot: one sample is noise.</summary>
	private double _cpuSum, _gpuSum, _setupSum;
	private int _timed;
	private bool _requested;

	/// <summary>
	/// Frames between sun reports, read from <c>REFII_EXPOSURE_DUMP</c>. Zero is off.
	/// </summary>
	/// <remarks>
	/// THE SAME SWITCH AS THE EXPOSURE DUMP, ON PURPOSE. The two readings only answer anything
	/// TOGETHER: a frame whose pixels sit far below the tone curve's clip point is either lit
	/// weakly or exposed correctly for a weak scene, and the sun's own colour times its energy is
	/// what separates those. Two independent switches would make it possible to read one without
	/// the other, which is the mistake this pairing exists to prevent.
	/// </remarks>
	private int _reportPeriod;

	private long _frames;

	/// <summary>Hold the atmosphere off, from <c>REFII_LEVEL_FOG=off</c>.</summary>
	private bool _fogOff;

	public override void _Ready()
	{
		_shotPath = OS.GetEnvironment("REFII_LEVEL_SHOT");
		GD.Print($"[level-viewer] depth pre-pass {ProjectSettings.GetSetting("rendering/driver/depth_prepass/enable")}");

		if (int.TryParse(OS.GetEnvironment("REFII_EXPOSURE_DUMP"), out int period) && period > 0)
		{
			_reportPeriod = period;
		}

		// Streaming is time-boxed per frame, so a big region is still paging in long after the
		// default warmup — and a shot taken then shows a half-built world and reads as missing
		// geometry. REFII_LEVEL_WARMUP buys the frames when the question needs the whole region.
		if (int.TryParse(OS.GetEnvironment("REFII_LEVEL_WARMUP"), out int warmup) && warmup > 0)
		{
			ShotWarmupFrames = warmup;
		}

		// REFII_LEVEL_SHADOW_PROXY=0: every prop casts its own shadow, surface by surface. The frame
		// must match the default's and the draw calls must not.
		if (OS.GetEnvironment("REFII_LEVEL_SHADOW_PROXY") == "0")
		{
			Refii.Level.Engine_PropShadow.Enabled = false;
			Refii.Entities.Engine_ShadowProxy.Enabled = false;
		}

		string requested = OS.GetEnvironment("REFII_LEVEL");
		if (requested.Length != 0)
		{
			LevelName = requested;
		}

		// The same auto-exposure the game runs, so the editor and the game agree on exposure.
		_camera = new Camera3D
		{
			Name = "Camera",
			Current = true,
			Near = 0.05f,
			Far = 20_000f,
			Attributes = Refii.Lighting.Engine_LevelLighting.BuildCameraAttributes(),

			// The guest's tone curve, measured per frame. Godot's tonemapper is Linear
			// wherever this runs — see Engine_LevelLighting.ConfigureEnvironment.
			Compositor = Refii.Lighting.Engine_LevelLighting.BuildCompositor(),
		};
		AddChild(_camera);
		Refii.Lighting.Engine_DepthPrepassMarker.Attach(_camera);

		var layer = new CanvasLayer { Name = "Hud" };
		AddChild(layer);
		_caption = new Label
		{
			Name = "Caption",
			Position = new Vector2(12.0f, 8.0f),
			AutowrapMode = TextServer.AutowrapMode.Off,
		};
		_caption.AddThemeColorOverride("font_color", new Color(0.92f, 0.93f, 0.96f));
		_caption.AddThemeColorOverride("font_outline_color", new Color(0.0f, 0.0f, 0.0f));
		_caption.AddThemeConstantOverride("outline_size", 4);
		layer.AddChild(_caption);
		_caption.Text = "mounting banks...";

		// THE CLASSIC LAB. With REFII_LIGHTING_MODEL=classic the region builds on the title's own
		// shaders, and REFII_CLASSIC_FRAME=<json> replays a frame recorded from a live run (the
		// debug channel's `classic` op: frame, lightsRaw, mistsRaw) in place of the guest.
		if (Refii.Models.Engine_ClassicMaterial.Active)
		{
			string recorded = OS.GetEnvironment("REFII_CLASSIC_FRAME");
			if (recorded.Length != 0)
			{
				GD.Print(Refii.Models.Engine_ClassicPath.LoadReplay(recorded, out string replayError)
					? $"[level-viewer] classic frame replayed from {recorded}"
					: $"[level-viewer] classic frame NOT loaded from {recorded}: {replayError}");
			}
			Refii.Models.Engine_ClassicPath.Ensure(this);
			// REFII_CLASSIC_SHADOWMAPS=capture|black|white: the same lever as the debug channel's.
			string maps = OS.GetEnvironment("REFII_CLASSIC_SHADOWMAPS");
			if (maps is "capture" or "black" or "white")
			{
				Refii.Models.Engine_ClassicShadows.MapMode = maps;
			}
		}

		string dataRoot = Engine_GameData.Resolve();
		if (dataRoot.Length == 0)
		{
			GD.PrintErr("[level-viewer] no game data found; set REFII_GAME_DATA.");
			GetTree().Quit(1);
			return;
		}

		if (Engine_GameAssets.Instance is not { } assets)
		{
			GD.PrintErr("[level-viewer] Engine_GameAssets autoload is not present.");
			GetTree().Quit(1);
			return;
		}

		// Subscribe BEFORE mounting: the mount can complete between these two statements.
		assets.Mounted += OnMounted;
		assets.MountFailed += reason =>
		{
			GD.PrintErr($"[level-viewer] could not mount {dataRoot}: {reason}");
			GetTree().Quit(1);
		};

		if (assets.IsMounted)
		{
			OnMounted(assets.FileCount);
		}
		else if (!assets.IsMounting)
		{
			assets.Mount(dataRoot);
		}
	}

	public override void _Process(double delta)
	{
		if (_debugView >= 0)
		{
			RenderingServer.GlobalShaderParameterSet("fable_light_debug", _debugView);
		}

		if (_bakeWeight > 0f)
		{
			RenderingServer.GlobalShaderParameterSet("fable_bake_weight", _bakeWeight);
		}

		if (_region is null)
		{
			return;
		}

		Refii.Models.Engine_ClassicPath.Pump(this, _camera, GetViewport().GetVisibleRect().Size);
		if (_shotPath.Length != 0 && _framesRendered == ShotWarmupFrames - 1)
		{
			GD.Print(Refii.Models.Engine_ClassicPath.Status());
			if (OS.GetEnvironment("REFII_LEVEL_SHADOW_PROBE").Length != 0)
			{
				ShadowProbe();
			}
		}

		if (_fogOff)
		{
			RenderingServer.GlobalShaderParameterSet("atmos_fog_on", false);
		}

		if (_reportPeriod > 0 && ++_frames % _reportPeriod == 0)
		{
			ReportSun();
		}

		if (_shotPath.Length == 0)
		{
			Fly((float)delta);
			return;
		}

		// The props page in over frames; a shot before they are in shows an empty region. The
		// warm-up starts when the streamer is done, or after a bound in case it never is.
		if (_framesRendered == 0 && ++_framesWaited < 2400 &&
			Refii.Level.Engine_PropStreamer.TryGetProgress(out int streamed, out int streamTotal) &&
			streamed < streamTotal)
		{
			_caption.Text = $"streaming {streamed}/{streamTotal}...";
			return;
		}

		// THE GAME'S LOAD HOLD, classic share (GameWorld -> Engine_ClassicLoad): bakes applied,
		// compositions compiled and the light-group warm built before the shot's warm-up starts.
		if (_framesRendered == 0 && !_classicLoaded && _region is not null && Refii.Models.Engine_ClassicMaterial.Active &&
			++_framesWaited < 2400)
		{
			if (_classicLoadFrom == 0)
			{
				_classicLoadFrom = System.Diagnostics.Stopwatch.GetTimestamp();
				Refii.Models.Engine_ClassicLoad.Begin(_region);
			}
			int settling = 0;
			foreach (Node node in _region.FindChildren("*", nameof(Refii.Level.Engine_PropStreamer), true, false))
			{
				settling += ((Refii.Level.Engine_PropStreamer)node).Settling;
			}
			string wait = Refii.Models.Engine_ClassicLoad.Pump(settling == 0);
			if (wait.Length != 0 || settling != 0)
			{
				_caption.Text = $"classic load: {(settling != 0 ? $"{settling} bake(s)" : wait)}...";
				return;
			}
			Refii.Models.Engine_ClassicLoad.End();
			_classicLoaded = true;
			{
				var shapes = new Dictionary<long, int>();
				var meshUse = new Dictionary<ulong, int>();
				int visible = 0, transfer = 0;
				foreach (Node found in _region.FindChildren("*", nameof(MeshInstance3D), true, false))
				{
					if (found is not MeshInstance3D m || m.Mesh is not { } drawn || !m.IsVisibleInTree())
					{
						continue;
					}
					++visible;
					long key = (long)drawn.GetInstanceId() * 31 + (long)(m.MaterialOverride?.GetInstanceId() ?? 0UL);
					bool own = false;
					for (int i = 0; i < drawn.GetSurfaceCount(); i++)
					{
						key = key * 31 + (long)(m.GetSurfaceOverrideMaterial(i)?.GetInstanceId() ?? 0UL);
						own |= Refii.Models.Engine_ClassicTransfer.SurfaceOverride(m, i) is not null;
					}
					transfer += own ? 1 : 0;
					shapes[key] = shapes.GetValueOrDefault(key) + 1;
					meshUse[drawn.GetInstanceId()] = meshUse.GetValueOrDefault(drawn.GetInstanceId()) + 1;
				}
				var paths = new Dictionary<string, HashSet<ulong>>();
				foreach (Node found in _region.FindChildren("*", nameof(MeshInstance3D), true, false))
				{
					if (found is MeshInstance3D m && m.Mesh is { } drawn && m.IsVisibleInTree() &&
						m.HasMeta(Refii.Level.Engine_PropStreamer.ModelPathMeta))
					{
						string path = m.GetMeta(Refii.Level.Engine_PropStreamer.ModelPathMeta).AsString();
						if (!paths.TryGetValue(path, out HashSet<ulong>? set))
						{
							paths[path] = set = new HashSet<ulong>();
						}
						set.Add(drawn.GetInstanceId());
					}
				}
				int pathMeshes = 0, pathsSplit = 0;
				foreach (HashSet<ulong> set in paths.Values)
				{
					pathMeshes += set.Count;
					pathsSplit += set.Count > 1 ? 1 : 0;
				}
				GD.Print($"[level-viewer] model paths: {paths.Count} distinct, drawn through {pathMeshes} meshes ({pathsSplit} paths split across several)");
				int once = 0, meshOnce = 0;
				foreach (int n in shapes.Values) once += n == 1 ? 1 : 0;
				foreach (int n in meshUse.Values) meshOnce += n == 1 ? 1 : 0;
				GD.Print($"[level-viewer] drawn census: {visible} visible mesh node(s), {shapes.Count} distinct shapes " +
					$"({once} drawn once), {transfer} with a transfer pair, {meshUse.Count} meshes ({meshOnce} used by one node)");
			}
			foreach (Node found in GetTree().Root.FindChildren("*", nameof(SubViewport), true, false))
			{
				RenderingServer.ViewportSetMeasureRenderTime(((SubViewport)found).GetViewportRid(), true);
			}
			{
				// What the main view draws, by kind: every surface of every node inside the camera's
				// frustum, and each pass its material chains (terrain layers, light groups, glass).
				Godot.Collections.Array<Plane> planes = _camera!.GetFrustum();
				var byKind = new SortedDictionary<string, int>();
				void Count(string kind, int n) => byKind[kind] = byKind.GetValueOrDefault(kind) + n;
				foreach (Node found in _region.FindChildren("*", nameof(GeometryInstance3D), true, false))
				{
					if (found is not GeometryInstance3D g || !g.IsVisibleInTree() || (g.Layers & 1u) == 0)
					{
						continue;
					}
					Aabb box = g.GlobalTransform * g.GetAabb();
					bool inside = true;
					foreach (Plane plane in planes)
					{
						Vector3 c = box.GetCenter(), h = box.Size * 0.5f;
						float r = Mathf.Abs(plane.Normal.X) * h.X + Mathf.Abs(plane.Normal.Y) * h.Y + Mathf.Abs(plane.Normal.Z) * h.Z;
						if (plane.DistanceTo(c) > r) { inside = false; break; }
					}
					if (!inside)
					{
						continue;
					}
					Mesh? mesh = g switch { MeshInstance3D mi => mi.Mesh, MultiMeshInstance3D mm => mm.Multimesh?.Mesh, _ => null };
					if (mesh is null)
					{
						continue;
					}
					for (int i = 0; i < mesh.GetSurfaceCount(); i++)
					{
						Material? m = g.MaterialOverride ?? (g is MeshInstance3D mi2 ? mi2.GetSurfaceOverrideMaterial(i) : null) ?? mesh.SurfaceGetMaterial(i);
						int passes = 0;
						string family = m?.GetMeta("xenos_family", "other").AsString() ?? "none";
						bool lightPass = false;
						for (Material? pass = m; pass is not null && passes < 32; pass = pass.NextPass)
						{
							passes++;
							lightPass |= pass.HasMeta("xenos_group_variant");
						}
						string kind = g is MultiMeshInstance3D ? "batch:" + family : family;
						Count(kind + " surfaces", 1);
						Count(kind + " passes", passes);
						if (lightPass)
						{
							Count(kind + " lit surfaces", 1);
						}
					}
				}
				GD.Print("[level-viewer] main-view census: " + string.Join(", ", System.Linq.Enumerable.Select(byKind, kv => $"{kv.Key} {kv.Value}")));
			}
			GD.Print("[level-viewer] classic casters: " + string.Join(", ", System.Linq.Enumerable.Select(
				Refii.Models.Engine_ClassicCasters.Census(), kv => $"{kv.Key} {kv.Value}")));
			GD.Print($"[level-viewer] classic load hold done in " +
				$"{System.Diagnostics.Stopwatch.GetElapsedTime(_classicLoadFrom).TotalSeconds:0.00} s after streaming");
		}

		// REFII_LEVEL_HERO=1: the hero's composed parts (CharacterLightLab.Parts) standing at the
		// view's target, a determinant +1 node as the game's entities are, lit by the call the
		// game's entity feed makes (LightEntityAt at its region-local place).
		if (_framesRendered == 0 && _labHero is null && OS.GetEnvironment("REFII_LEVEL_HERO") == "1" && _region is not null)
		{
			ArrayMesh? body = Refii.Models.Engine_MdlComposite.Build(
				CharacterLightLab.Parts,
				path => Refii.Models.Engine_BnkMeshLoader.ReadBytes(path) is { } bytes
					? Refii.Models.Engine_MdlMesh.TryCreateMesh(bytes, path, out _, out _) : null,
				out int resolved, out int missing);
			_labHero = new MeshInstance3D { Name = "LabHero", Mesh = body };
			AddChild(_labHero);
			_labHero.GlobalPosition = _target + new Vector3(0f, -1.2f, 0f);
			Vector3 local = _region.GlobalTransform.AffineInverse() * _labHero.GlobalPosition;
			Refii.Lighting.Engine_LevelBakedLighting.LightEntityAt(_labHero, local, Refii.Level.Engine_SkyEnvironmentNode.Current?.Lighting);
			int overrides = 0;
			for (int i = 0; i < (body?.GetSurfaceCount() ?? 0); i++)
			{
				if (_labHero.GetSurfaceOverrideMaterial(i) is not null) overrides++;
			}
			if (_labHero.GetSurfaceOverrideMaterial(0) is ShaderMaterial prtMat &&
				prtMat.GetShaderParameter("vs_g_PRTConstants").Obj is Godot.Collections.Array prt)
			{
				GD.Print($"[level-viewer] lab hero prt[0..3]: {string.Join(" ", System.Linq.Enumerable.Take(prt, 4))} probe {_labHero.GetMeta(Refii.Lighting.Engine_LevelBakedLighting.ProbeMetaName)}");
			}
			GD.Print($"[level-viewer] lab hero: parts {resolved}/{resolved + missing}, surfaces {body?.GetSurfaceCount()}, probe {_labHero.HasMeta(Refii.Lighting.Engine_LevelBakedLighting.ProbeMetaName)}, transfer overrides {overrides}");
		}

		// REFII_LEVEL_PAINT=Name: geometry under nodes matching Name drawn with a plain red
		// material, to tell "not drawn" from "drawn wrong".
		if (_framesRendered == 0)
		{
			foreach (string name in OS.GetEnvironment("REFII_LEVEL_PAINT").Split(',', StringSplitOptions.RemoveEmptyEntries))
			{
				var red = new StandardMaterial3D { AlbedoColor = new Color(1f, 0f, 0f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
				int painted = 0;
				foreach (Node node in GetTree().Root.FindChildren(name.Trim(), "GeometryInstance3D", true, false))
				{
					((GeometryInstance3D)node).MaterialOverride = red;
					++painted;
				}
				GD.Print($"[level-viewer] painted {painted} node(s) named '{name.Trim()}'");
			}
		}

		// REFII_LEVEL_HIDE=Name,Name: nodes under the region hidden for the shot, to bisect a
		// frame's cost or picture by what it leaves out.
		if (_framesRendered == 0)
		{
			foreach (string name in OS.GetEnvironment("REFII_LEVEL_HIDE").Split(',', StringSplitOptions.RemoveEmptyEntries))
			{
				int hidden = 0;
				foreach (Node node in GetTree().Root.FindChildren(name.Trim(), "", true, false))
				{
					if (node is Node3D spatial)
					{
						if (hidden < 6)
						{
							Mesh? hm = spatial is MeshInstance3D hmi ? hmi.Mesh : spatial is MultiMeshInstance3D hmm ? hmm.Multimesh?.Mesh : null;
							string family = hm is not null && hm.GetSurfaceCount() > 0 && hm.SurfaceGetMaterial(0) is ShaderMaterial hsm && hsm.HasMeta(Refii.Models.Engine_ClassicMaterial.FamilyMetaName)
								? hsm.GetMeta(Refii.Models.Engine_ClassicMaterial.FamilyMetaName).AsString() : "-";
							GD.Print($"[level-viewer] hiding {spatial.Name} ({spatial.GetClass()}) at {spatial.GlobalPosition} transfer={hm?.HasMeta(Refii.Models.Engine_MdlTransfer.DescriptorMeta)} probe={spatial.HasMeta(Refii.Lighting.Engine_LevelBakedLighting.ProbeMetaName)} family={family}");
						}
						if (hidden < 2 && spatial is MultiMeshInstance3D dbgMm)
						{
							GD.Print($"[level-viewer] multimesh {dbgMm.Name}: visible {dbgMm.IsVisibleInTree()} count {dbgMm.Multimesh?.InstanceCount} visibleCount {dbgMm.Multimesh?.VisibleInstanceCount} range {dbgMm.VisibilityRangeBegin}..{dbgMm.VisibilityRangeEnd} layers 0x{dbgMm.Layers:X} aabb {dbgMm.Multimesh?.GetAabb()} custom {dbgMm.Multimesh?.UseCustomData} colors {dbgMm.Multimesh?.UseColors} first {(dbgMm.Multimesh?.InstanceCount > 0 ? dbgMm.Multimesh.GetInstanceTransform(0).Origin.ToString() : "-")} override {dbgMm.MaterialOverride?.GetClass()}:{(dbgMm.MaterialOverride as ShaderMaterial)?.Shader?.ResourcePath} surface0 {(dbgMm.Multimesh?.Mesh?.SurfaceGetMaterial(0) as ShaderMaterial)?.Shader?.Code.Split('\n')[2]}");
						}
						spatial.Visible = false;
						++hidden;
					}
				}
				GD.Print($"[level-viewer] hid {hidden} node(s) named '{name.Trim()}'");
			}
		}

		// REFII_LEVEL_UNIFORM=Node:name=a,b,c,d;Node:name=a,b,c,d — a uniform overridden on every
		// ShaderMaterial (surface and NextPass chain) under the named nodes, to isolate a term.
		if (_framesRendered == 0)
		{
			foreach (string spec in OS.GetEnvironment("REFII_LEVEL_UNIFORM").Split(';', StringSplitOptions.RemoveEmptyEntries))
			{
				int colon = spec.IndexOf(':'), equals = spec.IndexOf('=');
				if (colon <= 0 || equals <= colon)
				{
					continue;
				}
				string nodeName = spec[..colon].Trim(), uniform = spec[(colon + 1)..equals].Trim();
				string text = spec[(equals + 1)..].Trim();
				bool unsigned = text.EndsWith('u');
				float[] v = text.TrimEnd('u').Split(',').Select(t => float.Parse(t.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToArray();
				Variant value = unsigned ? (uint)v[0] : v.Length switch
				{
					1 => v[0],
					2 => new Vector2(v[0], v[1]),
					3 => new Vector3(v[0], v[1], v[2]),
					_ => new Vector4(v[0], v[1], v[2], v[3]),
				};
				int set = 0;
				var seen = new HashSet<ulong>();
				foreach (Node node in GetTree().Root.FindChildren(nodeName, "", true, false))
				{
					foreach (MeshInstance3D instance in node.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().Concat(node is MeshInstance3D self ? new[] { self } : Array.Empty<MeshInstance3D>()))
					{
						int surfaces = instance.Mesh?.GetSurfaceCount() ?? 0;
						for (int i = 0; i < surfaces; i++)
						{
							for (Material? m = instance.GetActiveMaterial(i); m is not null; m = m.NextPass)
							{
								if (m is ShaderMaterial sm && seen.Add(sm.GetInstanceId()))
								{
									sm.SetShaderParameter(uniform, value);
									++set;
								}
							}
						}
					}
				}
				GD.Print($"[level-viewer] uniform '{uniform}' = ({string.Join(", ", v)}) on {set} material(s) under '{nodeName}'");
			}
			// REFII_LEVEL_LAYERS=Node:N — every NextPass chain under the node cut to its first N
			// materials, to see one layer of a multi-pass surface on its own.
			foreach (string spec in OS.GetEnvironment("REFII_LEVEL_LAYERS").Split(';', StringSplitOptions.RemoveEmptyEntries))
			{
				int colon = spec.IndexOf(':');
				if (colon <= 0 || !int.TryParse(spec[(colon + 1)..].Trim(), out int keep) || keep < 1)
				{
					continue;
				}
				string nodeName = spec[..colon].Trim();
				int cut = 0;
				foreach (Node node in GetTree().Root.FindChildren(nodeName, "", true, false))
				{
					foreach (MeshInstance3D instance in node.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
					{
						int surfaces = instance.Mesh?.GetSurfaceCount() ?? 0;
						for (int i = 0; i < surfaces; i++)
						{
							Material? m = instance.GetActiveMaterial(i);
							for (int depth = 1; m is not null; depth++, m = m.NextPass)
							{
								if (depth == keep && m.NextPass is not null)
								{
									m.NextPass = null;
									++cut;
								}
							}
						}
					}
				}
				GD.Print($"[level-viewer] kept {keep} layer(s) on {cut} chain(s) under '{nodeName}'");
			}
			// REFII_LEVEL_BENCH_AABB=1: what the per-instance reads cost, by node type, over the region.
			if (OS.GetEnvironment("REFII_LEVEL_BENCH_AABB").Length != 0)
			{
				var nodes = GetTree().Root.FindChildren("*", "GeometryInstance3D", true, false).OfType<GeometryInstance3D>().ToList();
				foreach (string type in new[] { "MeshInstance3D", "MultiMeshInstance3D" })
				{
					var group = nodes.Where(n => n.GetClass() == type).ToList();
					if (group.Count == 0)
					{
						continue;
					}
					var watch = System.Diagnostics.Stopwatch.StartNew();
					Aabb sink = default;
					foreach (GeometryInstance3D n in group)
					{
						sink = sink.Merge(n.GetAabb());
					}
					double aabbUs = watch.Elapsed.TotalMilliseconds * 1000.0 / group.Count;
					watch.Restart();
					Transform3D t = Transform3D.Identity;
					foreach (GeometryInstance3D n in group)
					{
						t = n.GlobalTransform;
					}
					double xformUs = watch.Elapsed.TotalMilliseconds * 1000.0 / group.Count;
					watch.Restart();
					bool inside = true;
					foreach (GeometryInstance3D n in group)
					{
						inside &= n.IsInsideTree();
					}
					double treeUs = watch.Elapsed.TotalMilliseconds * 1000.0 / group.Count;
					GD.Print($"[level-viewer] bench {type} x{group.Count}: GetAabb {aabbUs:0.00} us, GlobalTransform {xformUs:0.00} us, IsInsideTree {treeUs:0.00} us ({sink.Size.X:0}, {t.Origin.X:0}, {inside})");
				}
			}
			// REFII_LEVEL_HEIGHTS=1: the terrain and water surface heights under the camera's target,
			// from the meshes' own vertices (Godot space), nearest vertex per node.
			if (OS.GetEnvironment("REFII_LEVEL_HEIGHTS").Length != 0)
			{
				Vector3 target = _camera.GlobalPosition + (-_camera.GlobalBasis.Z) * (_camera.GlobalPosition.DistanceTo(_camera.GlobalPosition + (-_camera.GlobalBasis.Z)));
				string at = OS.GetEnvironment("REFII_LEVEL_AT");
				if (at.Length != 0)
				{
					float[] p = at.Split(',').Select(t => float.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
					target = new Vector3(p[0], p[1], p[2]);
				}
				foreach (string container in new[] { "Water", "VistaOcean" })
				{
					foreach (Node node in GetTree().Root.FindChildren(container, "", true, false))
					{
						foreach (GeometryInstance3D g in node.FindChildren("*", "GeometryInstance3D", true, false).OfType<GeometryInstance3D>().Append(node as GeometryInstance3D).OfType<GeometryInstance3D>())
						{
							Aabb box = g.GlobalTransform * g.GetAabb();
							bool over = target.X >= box.Position.X && target.X <= box.End.X && target.Z >= box.Position.Z && target.Z <= box.End.Z;
							GD.Print($"[level-viewer] heights: {container}/{g.Name} y {box.Position.Y:0.00}..{box.End.Y:0.00} x {box.Position.X:0}..{box.End.X:0} z {box.Position.Z:0}..{box.End.Z:0}{(over ? "  OVER THE POINT" : "")} visible={g.IsVisibleInTree()}");
						}
					}
				}
				foreach (string container in new[] { "Terrain", "Props" })
				{
					float best = float.MaxValue; Vector3 bestAt = default; int meshes = 0;
					foreach (Node node in GetTree().Root.FindChildren(container, "", true, false))
					{
						foreach (MeshInstance3D mesh in node.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
						{
							if (mesh.Mesh is not ArrayMesh am || am.GetSurfaceCount() == 0)
							{
								continue;
							}
							Aabb box = mesh.GlobalTransform * mesh.GetAabb();
							if (target.X < box.Position.X - 4 || target.X > box.End.X + 4 || target.Z < box.Position.Z - 4 || target.Z > box.End.Z + 4)
							{
								continue;
							}
							++meshes;
							var verts = (Vector3[])am.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex];
							foreach (Vector3 v in verts)
							{
								Vector3 g = mesh.GlobalTransform * v;
								float d = new Vector2(g.X - target.X, g.Z - target.Z).LengthSquared();
								if (d < best) { best = d; bestAt = g; }
							}
						}
					}
					GD.Print($"[level-viewer] heights at {target}: {container}: {(meshes == 0 ? "no mesh over the point" : $"nearest vertex {bestAt} ({MathF.Sqrt(best):0.0} m away, {meshes} mesh(es))")}");
				}
			}
			// REFII_LEVEL_TEXFLAGS=1: the diffuse textures' header flags (the title's alpha bits:
			// 0x20 alpha test, 0x40 derived, 0x80 translucent) across the level's materials.
			if (OS.GetEnvironment("REFII_LEVEL_TEXFLAGS").Length != 0)
			{
				var seenTex = new HashSet<string>();
				var tally = new Dictionary<int, int>();
				var translucent = new List<string>();
				foreach (Node n in GetTree().Root.FindChildren("*", "GeometryInstance3D", true, false))
				{
					Mesh? mesh = n is MeshInstance3D mi ? mi.Mesh : n is MultiMeshInstance3D mm ? mm.Multimesh?.Mesh : null;
					for (int i = 0; i < (mesh?.GetSurfaceCount() ?? 0); i++)
					{
						if (mesh!.SurfaceGetMaterial(i) is not { } m || !m.HasMeta(Refii.Models.Engine_MdlMaterial.TextureNamesMeta))
						{
							continue;
						}
						string[] names = m.GetMeta(Refii.Models.Engine_MdlMaterial.TextureNamesMeta).AsStringArray();
						if (names.Length == 0 || names[0].Length == 0 || !seenTex.Add(names[0]))
						{
							continue;
						}
						byte[]? bytes = Refii.Models.Engine_BnkMeshLoader.ReadBytes(names[0]);
						if (bytes is null || !Refii.Textures.TexHeader.TryParse(bytes, out Refii.Textures.TexHeader h, out _))
						{
							continue;
						}
						int bits = h.Flags & 0xE0;
						tally[bits] = tally.GetValueOrDefault(bits) + 1;
						if ((h.Flags & 0x80) != 0 && translucent.Count < 40)
						{
							translucent.Add($"{names[0]} (0x{h.Flags:X}, fmt {h.PixelFormat})");
						}
					}
				}
				GD.Print($"[level-viewer] texflags over {seenTex.Count} diffuse textures: {string.Join(", ", tally.OrderBy(p => p.Key).Select(p => $"0x{p.Key:X2}={p.Value}"))}");
				foreach (string t in translucent)
				{
					GD.Print($"[level-viewer] translucent: {t}");
				}
			}

			// REFII_LEVEL_UNBOUND=1: every sampler a classic material leaves unbound, by name, with
			// the shader entries that declare it. Unbound reads Godot's default (opaque black).
			if (OS.GetEnvironment("REFII_LEVEL_UNBOUND").Length != 0)
			{
				var unbound = new Dictionary<string, HashSet<string>>();
				var visited = new HashSet<ulong>();
				void Check(Material? m)
				{
					if (m is not ShaderMaterial sm || !visited.Add(sm.GetInstanceId()) || sm.Shader is null)
					{
						return;
					}
					string entry = System.Text.RegularExpressions.Regex.Match(sm.Shader.Code, @"// ([A-Z0-9_]+ \+ [A-Z0-9_]+)").Groups[1].Value;
					if (entry.Length == 0)
					{
						return;
					}
					if (entry.Contains("LANDSCAPEMATERIAL") && _landscapePrinted++ < 4)
					{
						Variant uvf = sm.GetShaderParameter("vs_g_UVCliffFactor_UVGroundFactor_UVCliffMacroMapFactor_UVGroundMacroMapFactor");
						Texture2D? dt = sm.GetShaderParameter("ps_g_DiffuseTextureSampler").Obj as Texture2D;
						GD.Print($"[level-viewer] landscape {entry}: uv factor {uvf} diffuse {(dt is null ? "none" : $"{dt.GetWidth()}x{dt.GetHeight()} mips={dt.GetImage()?.HasMipmaps()}")} tcs {sm.GetShaderParameter("vs_g_TexCoordScale")} pscale {sm.GetShaderParameter("vs_g_PositionScale")}");
					}
					foreach (Godot.Collections.Dictionary u in sm.Shader.GetShaderUniformList())
					{
						string name = u["name"].AsString();
						bool isSampler = u["hint_string"].AsString().Contains("Texture") || u["type"].AsInt32() == (int)Variant.Type.Object;
						if (!isSampler && (name.StartsWith("ps_") || name.StartsWith("vs_")) &&
							sm.GetShaderParameter(name).VariantType == Variant.Type.Nil &&
							!System.Text.RegularExpressions.Regex.IsMatch(sm.Shader.Code, @"uniform [^;]*\b" + name + @"\b[^;]*="))
						{
							string key = "(unset) " + name;
							if (!unbound.TryGetValue(key, out HashSet<string>? at))
							{
								unbound[key] = at = new HashSet<string>();
							}
							at.Add(entry);
						}
						if (isSampler)
						{
							if (sm.GetShaderParameter(name).Obj is null)
							{
								if (!unbound.TryGetValue(name, out HashSet<string>? where))
								{
									unbound[name] = where = new HashSet<string>();
								}
								where.Add(entry.Trim());
							}
						}
					}
					Check(sm.NextPass);
				}
				foreach (Node n in GetTree().Root.FindChildren("*", "GeometryInstance3D", true, false))
				{
					var gi = (GeometryInstance3D)n;
					Check(gi.MaterialOverride);
					if (gi is MeshInstance3D mi && mi.Mesh is { } mesh)
					{
						for (int i = 0; i < mesh.GetSurfaceCount(); i++)
						{
							Check(mi.GetActiveMaterial(i));
						}
					}
					else if (gi is MultiMeshInstance3D mm && mm.Multimesh?.Mesh is { } mmesh)
					{
						for (int i = 0; i < mmesh.GetSurfaceCount(); i++)
						{
							Check(mmesh.SurfaceGetMaterial(i));
						}
					}
				}
				foreach (var pair in unbound.OrderBy(p => p.Key))
				{
					GD.Print($"[level-viewer] unbound {pair.Key}: {pair.Value.Count} shader pair(s), e.g. {string.Join(" | ", pair.Value.Take(4))}");
				}
				GD.Print($"[level-viewer] unbound report: {visited.Count} materials checked");

				// The census: every visible instance by what draws it. A surface that is not on a
				// classic material draws with Godot's look (or its default grey) in the classic view.
				var kinds = new Dictionary<string, List<string>>();
				void Count(string kind, GeometryInstance3D gi, string what)
				{
					if (!kinds.TryGetValue(kind, out List<string>? list))
					{
						kinds[kind] = list = new List<string>();
					}
					list.Add($"{gi.GetPath()} [{what}]");
				}
				string KindOf(Material? m) => m switch
				{
					null => "no material",
					ShaderMaterial sm when sm.HasMeta(Refii.Models.Engine_ClassicMaterial.ClassicMeta) => "classic",
					ShaderMaterial sm => "shader " + (sm.Shader?.ResourcePath is { Length: > 0 } path ? path.GetFile() : "(inline)"),
					_ => m.GetClass(),
				};
				foreach (Node n in GetTree().Root.FindChildren("*", "GeometryInstance3D", true, false))
				{
					var gi = (GeometryInstance3D)n;
					if (!gi.IsVisibleInTree())
					{
						continue;
					}
					if (gi is MeshInstance3D mi && mi.Mesh is { } mesh)
					{
						for (int i = 0; i < mesh.GetSurfaceCount(); i++)
						{
							string kind = KindOf(mi.GetActiveMaterial(i));
							if (kind != "classic")
							{
								Count(kind, gi, $"{mesh.ResourceName} s{i}");
							}
						}
					}
					else if (gi is MultiMeshInstance3D mm && mm.Multimesh?.Mesh is { } mmesh)
					{
						for (int i = 0; i < mmesh.GetSurfaceCount(); i++)
						{
							string kind = KindOf(mm.MaterialOverride ?? mmesh.SurfaceGetMaterial(i));
							if (kind != "classic")
							{
								Count(kind, gi, $"{mmesh.ResourceName} s{i} x{mm.Multimesh.VisibleInstanceCount}");
							}
						}
					}
					else if (gi is not MeshInstance3D && gi is not MultiMeshInstance3D)
					{
						Count(gi.GetClass() + " " + KindOf(gi.MaterialOverride), gi, "");
					}
				}
				foreach (var pair in kinds.OrderByDescending(p => p.Value.Count))
				{
					GD.Print($"[level-viewer] census {pair.Key}: {pair.Value.Count}, e.g. {string.Join(" | ", pair.Value.Take(6))}");
				}
			}

			// REFII_LEVEL_EMBED_REPORT=1: what saving this region as a scene would embed — every
			// resource reachable from its nodes that has no file path, by kind, with its size.
			if (OS.GetEnvironment("REFII_LEVEL_EMBED_REPORT").Length != 0 && _region is not null)
			{
				var seen = new HashSet<ulong>();
				var count = new Dictionary<string, int>();
				var bytes = new Dictionary<string, long>();
				void Add(string kind, long size)
				{
					count[kind] = count.GetValueOrDefault(kind) + 1;
					bytes[kind] = bytes.GetValueOrDefault(kind) + size;
				}
				void Visit(Resource? r)
				{
					if (r is null || !seen.Add(r.GetInstanceId()) || r.ResourcePath.Length != 0 && !r.ResourcePath.Contains("::"))
					{
						return;
					}
					switch (r)
					{
						case ShaderMaterial sm:
							Add("ShaderMaterial", 512);
							Visit(sm.Shader);
							foreach (Godot.Collections.Dictionary u in sm.Shader?.GetShaderUniformList() ?? new Godot.Collections.Array())
							{
								if (sm.GetShaderParameter(u["name"].AsStringName()).Obj is Resource tex)
								{
									Visit(tex);
								}
							}
							Visit(sm.NextPass);
							break;
						case Shader sh:
							Add(sh.Code.Contains("xr_ps_constants") ? "Shader(classic)" : "Shader", sh.Code.Length);
							break;
						case ImageTexture it:
							Image? img = it.GetImage();
							Add("ImageTexture", img is null ? 0 : img.GetData().LongLength);
							break;
						case ArrayMesh am:
							long verts = 0;
							for (int i = 0; i < am.GetSurfaceCount(); i++)
							{
								verts += am.SurfaceGetArrayLen(i);
								Visit(am.SurfaceGetMaterial(i));
							}
							Add("ArrayMesh", verts * 40);
							break;
						case Material m:
							Add(m.GetClass(), 256);
							Visit(m.NextPass);
							break;
						default:
							Add(r.GetClass(), 0);
							break;
					}
				}
				foreach (Node n in _region.FindChildren("*", "GeometryInstance3D", true, false))
				{
					var g = (GeometryInstance3D)n;
					Visit(g.MaterialOverride);
					Visit(g.MaterialOverlay);
					if (g is MeshInstance3D mi)
					{
						Visit(mi.Mesh);
						for (int i = 0; i < mi.GetSurfaceOverrideMaterialCount(); i++)
						{
							Visit(mi.GetSurfaceOverrideMaterial(i));
						}
					}
					else if (g is MultiMeshInstance3D mm && mm.Multimesh is { } multi)
					{
						Add("MultiMesh", (long)multi.InstanceCount * 64);
						Visit(multi.Mesh);
					}
				}
				foreach (var pair in bytes.OrderByDescending(p => p.Value))
				{
					GD.Print($"[level-viewer] embed report: {pair.Key} x{count[pair.Key]} ~{pair.Value / (1024.0 * 1024.0):0.0} MB");
				}
			}
			// REFII_CLASSIC_THEME_TOD=<hour>: the classic frame built from this level's own themes at
			// that time of day (the editor's frame) and replayed, for a level no live run recorded.
			if (float.TryParse(OS.GetEnvironment("REFII_CLASSIC_THEME_TOD"), System.Globalization.NumberStyles.Float,
					System.Globalization.CultureInfo.InvariantCulture, out float themeTod) &&
				Refii.Models.Engine_ClassicMaterial.Active)
			{
				Refii.Level.Engine_SkyEnvironmentNode? themeSky = GetTree().Root.FindChildren("*", "WorldEnvironment", true, false)
					.OfType<Refii.Level.Engine_SkyEnvironmentNode>().FirstOrDefault();
				var themeFrame = new float[148];
				var themeLights = new float[20];
				if (themeSky is not null && Refii.Models.Engine_ClassicEditorFrame.Build(themeSky.LightingGdbs, themeTod / 24f, themeFrame, themeLights))
				{
					Refii.Models.Engine_ClassicFeed.Replay = themeFrame;
					Refii.Models.Engine_ClassicLights.Replay = themeLights;
					Refii.Models.Engine_ClassicPlacedLights.Level = _region;
					string Row(int at) => $"({themeFrame[at]:0.###}, {themeFrame[at + 1]:0.###}, {themeFrame[at + 2]:0.###})";
					GD.Print($"[level-viewer] classic frame from the level's themes at {themeTod:0.0}h: ambient {Row(0)} " +
						$"travel {Row(4)} lightmap factor {Row(8)} darkening {Row(12)} sun {Row(132)} sun-twin dir {Row(136)} close fog {Row(32)} ranges {Row(28)} " +
						$"light0 colour ({themeLights[4]:0.###}, {themeLights[5]:0.###}, {themeLights[6]:0.###}) x {themeLights[7]:0.###}");
				}
				else
				{
					GD.Print("[level-viewer] classic theme frame: no environment node or no themes");
				}
			}
			// REFII_CLASSIC_EVAL=<json with tod + frame>: the editor's classic frame computed from this
			// level's themes at the recorded time of day, diffed slot by slot against the frame a
			// live title stated at that moment.
			string evalPath = OS.GetEnvironment("REFII_CLASSIC_EVAL");
			if (evalPath.Length != 0)
			{
				using var document = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(evalPath));
				float tod = (float)document.RootElement.GetProperty("tod").GetDouble();
				float[] live = document.RootElement.GetProperty("frame").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
				Refii.Level.Engine_SkyEnvironmentNode? sky = GetTree().Root.FindChildren("*", "WorldEnvironment", true, false)
					.OfType<Refii.Level.Engine_SkyEnvironmentNode>().FirstOrDefault();
				var built = new float[148];
				var lights = new float[20];
				if (sky is null || !Refii.Models.Engine_ClassicEditorFrame.Build(sky.LightingGdbs, tod, built, lights))
				{
					GD.Print("[level-viewer] classic eval: no environment node or no themes");
				}
				else
				{
					string[] names = { "ambient", "travel(G)", "lightmap", "darkening", "atm rayleigh", "atm mie", "atm sun", "atm ranges", "atm close fog" };
					for (int row = 0; row < 37; row++)
					{
						int at = row * 4;
						string name = row < 9 ? names[row] : row < 33 ? $"theme {row - 9}" : row == 33 ? "sun colour" : row == 34 ? "sun travel" : row == 35 ? "shadow/int/452/mist" : "wind";
						float d = 0f;
						for (int k = 0; k < 4; k++)
						{
							d = MathF.Max(d, MathF.Abs(built[at + k] - live[at + k]));
						}
						GD.Print($"[level-viewer] classic eval {(d > 1e-3f ? "DIFF" : " ok ")} [{at,3}] {name,-20} ours ({built[at]:0.####}, {built[at + 1]:0.####}, {built[at + 2]:0.####}, {built[at + 3]:0.####})  live ({live[at]:0.####}, {live[at + 1]:0.####}, {live[at + 2]:0.####}, {live[at + 3]:0.####})");
					}
					if (document.RootElement.TryGetProperty("theme", out System.Text.Json.JsonElement themeElement))
					{
						float[] liveTheme = themeElement.EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
						ReadOnlySpan<float> ours = Refii.Models.Engine_ClassicEditorFrame.Theme;
						int matched = 0;
						foreach (Refii.Level.Engine_ClassicThemeParams.Param param in Refii.Level.Engine_ClassicThemeParams.All)
						{
							int at = param.Index * 4;
							float d = 0f;
							for (int k = 0; k < 4; k++)
							{
								d = MathF.Max(d, MathF.Abs(ours[at + k] - liveTheme[at + k]));
							}
							if (d <= 1e-3f * MathF.Max(1f, MathF.Abs(liveTheme[at])))
							{
								++matched;
								continue;
							}
							GD.Print($"[level-viewer] classic eval theme DIFF {param.Index,3} {param.Name,-40} ours ({ours[at]:0.####}, {ours[at + 1]:0.####}, {ours[at + 2]:0.####}, {ours[at + 3]:0.####})  live ({liveTheme[at]:0.####}, {liveTheme[at + 1]:0.####}, {liveTheme[at + 2]:0.####}, {liveTheme[at + 3]:0.####})");
						}
						GD.Print($"[level-viewer] classic eval theme: {matched}/{Refii.Level.Engine_ClassicThemeParams.Count} parameters match");
					}
				}
			}
			// REFII_LEVEL_PS_REGS=19,20,67: the live pixel-stage constant rows, as the shaders read them.
			foreach (string reg in OS.GetEnvironment("REFII_LEVEL_PS_REGS").Split(',', StringSplitOptions.RemoveEmptyEntries))
			{
				int index = int.Parse(reg.Trim());
				float[] row = Refii.Models.Engine_ClassicConstants.ReadFrame(true, index, 1);
				GD.Print($"[level-viewer] ps c{index} = ({row[0]}, {row[1]}, {row[2]}, {row[3]})");
			}
			if (OS.GetEnvironment("REFII_LEVEL_PS_REGS").Length != 0)
			{
				if (Refii.Level.Engine_ClassicSkyDome.Instance?.MaterialOverride is ShaderMaterial dome &&
					dome.GetShaderParameter("ps_g_SkyOverlayTexture0").Obj is Texture2D overlay && overlay.GetImage() is { } oi)
				{
					if (oi.IsCompressed())
					{
						oi.Decompress();
					}
					double r = 0, g = 0, b = 0, al = 0, n = 0, amin = 1, amax = 0;
					for (int y = 0; y < oi.GetHeight(); y += 4)
					{
						for (int x = 0; x < oi.GetWidth(); x += 4)
						{
							Color c = oi.GetPixel(x, y);
							r += c.R; g += c.G; b += c.B; al += c.A; n++;
							amin = Math.Min(amin, c.A); amax = Math.Max(amax, c.A);
						}
					}
					GD.Print($"[level-viewer] sky overlay {overlay.ResourcePath} {oi.GetWidth()}x{oi.GetHeight()} {oi.GetFormat()} mean rgba ({r / n:0.000}, {g / n:0.000}, {b / n:0.000}, {al / n:0.000}) alpha [{amin:0.000}, {amax:0.000}] blend {dome.GetShaderParameter("ps_g_SkyOverlayTextureBlend")}");
				}
				// The atmosphere tables as the shaders sample them.
				Image ext = Refii.Models.Engine_ClassicAtmosphere.ExtinctionTexture.GetImage();
				Image ins = Refii.Models.Engine_ClassicAtmosphere.InScatteringTexture.GetImage();
				var sb = new System.Text.StringBuilder("[level-viewer] extinction:");
				foreach (int i in new[] { 0, 1, 2, 4, 8, 16, 32, 64, 128, 255 })
				{
					Color c = ext.GetPixel(i, 0);
					sb.Append($" [{i}]=({c.R:0.000},{c.G:0.000},{c.B:0.000})");
				}
				GD.Print(sb.ToString());
				sb.Clear().Append("[level-viewer] inscattering:");
				foreach (int i in new[] { 0, 8, 16, 32, 48, 63 })
				{
					Color c = ins.GetPixel(i, 0);
					sb.Append($" [{i}]=({c.R:0.000},{c.G:0.000},{c.B:0.000})");
				}
				GD.Print(sb.ToString() + $" rebuilds {Refii.Models.Engine_ClassicAtmosphere.Rebuilds}");
			}
		}

		// REFII_LEVEL_PROBE=1: a white DYNAMIC sphere at the camera target, on the shipped material.
		// It stands where a character would and takes light the way one does, so a shot answers
		// "does a moving thing get any fill here" without the guest.
		if (_framesRendered == 0)
		{
			RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
		}

		if (_framesRendered >= ShotWarmupFrames - 240)
		{
			Rid timed = GetViewport().GetViewportRid();
			_cpuSum += RenderingServer.ViewportGetMeasuredRenderTimeCpu(timed);
			_gpuSum += RenderingServer.ViewportGetMeasuredRenderTimeGpu(timed);
			_setupSum += RenderingServer.GetFrameSetupTimeCpu();
			++_timed;
		}

		if (_framesRendered == 0 && OS.GetEnvironment("REFII_LEVEL_PROBE") == "1")
		{
			var white = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
			white.Fill(Colors.White);
			var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://src/Engine/material/standard_material.gdshader") };
			material.SetShaderParameter("albedo_tex", ImageTexture.CreateFromImage(white));
			material.SetShaderParameter("has_normal", false);
			var probe = new MeshInstance3D
			{
				Mesh = new SphereMesh { Radius = 0.45f, Height = 0.9f },
				MaterialOverride = material,
				GIMode = GeometryInstance3D.GIModeEnum.Dynamic,
				Name = "LightProbeSphere",
			};
			AddChild(probe);
			probe.GlobalPosition = _target;
			probe.SetInstanceShaderParameter("lightmap_colour", new Vector4(0f, 0f, 0f, 0f));
		}

		if (_classicLoaded && _framesRendered % 30 == 0)
		{
			GD.Print($"[level-viewer] frame {_framesRendered}: {Engine.GetFramesPerSecond():0} fps, process {Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0:0.0} ms");
		}
		if (++_framesRendered >= ShotWarmupFrames)
		{
			// THE LIVE ENVIRONMENT, read off the node the renderer consumes.
			Godot.Environment? live = Refii.Level.Engine_SkyEnvironmentNode.Current?.Environment;
			GD.Print(live is null
				? "[level-viewer] no sky environment node is current"
				: $"[level-viewer] environment: modern={Refii.Lighting.Engine_LightingFrame.ModernModel} " +
				  $"sdfgi={live.SdfgiEnabled} cascades={live.SdfgiCascades} cell={live.SdfgiMinCellSize:0.##} " +
				  $"ambient={live.AmbientLightSource} colour=({live.AmbientLightColor.R:0.###},{live.AmbientLightColor.G:0.###},{live.AmbientLightColor.B:0.###}) " +
				  $"energy={live.AmbientLightEnergy:0.###} interior={Refii.Lighting.Engine_LightingFrame.CurrentInterior}");
			foreach ((string phaseName, object? phaseValue) in Refii.Core.Engine_FramePhases.Report())
			{
				if (phaseName.StartsWith("classic", StringComparison.Ordinal) && phaseValue is Dictionary<string, object?> timing)
				{
					GD.Print($"[level-viewer] phase {phaseName}: avg {timing["avgMs"]} ms peak {timing["peakMs"]} ms alloc {timing["allocKB"]} KB");
				}
			}
			GD.Print($"[level-viewer] viewport render over {_timed} frames: cpu {_cpuSum / Math.Max(1, _timed):0.00} ms " +
				$"gpu {_gpuSum / Math.Max(1, _timed):0.00} ms, frame setup {_setupSum / Math.Max(1, _timed):0.00} ms, " +
				$"fps {Engine.GetFramesPerSecond():0}");
			GD.Print($"[level-viewer] primitives in frame: {Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame):0}, " +
				$"objects {Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame):0}");
			GD.Print($"[level-viewer] draw calls in frame: {Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame):0}, " +
				$"prop shadow batches {Refii.Level.Engine_PropShadow.Batches} ({Refii.Level.Engine_PropShadow.SurfacesMerged} surfaces merged), " +
				$"prop proxies {Refii.Entities.Engine_ShadowProxy.Active}");
			foreach (Node found in GetTree().Root.FindChildren("*", nameof(SubViewport), true, false))
			{
				if (found is SubViewport sub && sub.RenderTargetUpdateMode == SubViewport.UpdateMode.Always)
				{
					Rid rid = sub.GetViewportRid();
					GD.Print($"[level-viewer] pass {sub.Name}: draw calls {RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.DrawCallsInFrame)}, " +
						$"objects {RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.ObjectsInFrame)}, " +
						$"render cpu {RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid):0.00} ms");
				}
			}
			{
				Rid main = GetViewport().GetViewportRid();
				GD.Print($"[level-viewer] pass main: draw calls {RenderingServer.ViewportGetRenderInfo(main, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.DrawCallsInFrame)}, " +
					$"objects {RenderingServer.ViewportGetRenderInfo(main, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.ObjectsInFrame)}");
			}
			if (OS.GetEnvironment("REFII_LEVEL_MIRRORED") == "1" && _region is not null)
			{
				GD.Print("[level-viewer] mirrored: " + System.Text.Json.JsonSerializer.Serialize(
					Refii.Lighting.Engine_CasterCensus.Mirrored(_region)));
			}

			// REFII_LEVEL_CLASSIC_AMB=1: how many classic surfaces draw each ambient source, by the
			// pixel tokens of the material each one actually draws with.
			if (OS.GetEnvironment("REFII_LEVEL_CLASSIC_AMB") == "1")
			{
				var tally = new Dictionary<string, int>();
				foreach (Node node in GetTree().Root.FindChildren("*", nameof(GeometryInstance3D), true, false))
				{
					if (node is not GeometryInstance3D g || !g.IsVisibleInTree())
					{
						continue;
					}
					Mesh? m = g is MeshInstance3D mi ? mi.Mesh : g is MultiMeshInstance3D mm ? mm.Multimesh?.Mesh : null;
					for (int i = 0; m is not null && i < m.GetSurfaceCount(); i++)
					{
						Material? used = g.MaterialOverride ?? (g as MeshInstance3D)?.GetSurfaceOverrideMaterial(i) ?? m.SurfaceGetMaterial(i);
						if (used is not ShaderMaterial sm || !sm.HasMeta("xenos_pixel_tokens"))
						{
							continue;
						}
						string tokens = sm.GetMeta("xenos_pixel_tokens").AsString();
						string amb = tokens.Split(' ').FirstOrDefault(t => t.StartsWith("AMB", StringComparison.Ordinal)) ?? "AMB0";
						string key = $"{sm.GetMeta("xenos_family", "?")} {amb} {(g is MultiMeshInstance3D ? "batch" : "single")}";
						tally[key] = tally.GetValueOrDefault(key) + 1;
					}
				}
				foreach (KeyValuePair<string, int> pair in tally.OrderByDescending(p => p.Value))
				{
					GD.Print($"[level-viewer] classic ambient: {pair.Key} x{pair.Value}");
				}
			}

			if (OS.GetEnvironment("REFII_LEVEL_MATERIALS") == "1")
			{
				GD.Print("[level-viewer] materials: " + System.Text.Json.JsonSerializer.Serialize(
					Refii.Lighting.Engine_CasterCensus.Materials(GetTree().Root)));
			}

			if (OS.GetEnvironment("REFII_LEVEL_CASTERS") == "1")
			{
				GD.Print("[level-viewer] casters: " + System.Text.Json.JsonSerializer.Serialize(
					Refii.Lighting.Engine_CasterCensus.Describe(GetTree().Root)));
			}

			WriteShot();
		}
	}

	/// <summary>
	/// The sun terms the region is actually being lit by, printed beside the exposure dump.
	///
	/// READ OFF THE LIVE NODE, NOT RECOMPUTED. The DirectionalLight3D is what the renderer
	/// consumes, so reporting its own colour and energy cannot disagree with the image the way a
	/// second evaluation of the theme could — and a theme read that quietly stopped reaching the
	/// light is precisely one of the faults this is being used to look for.
	///
	/// The colour and the energy are two halves of ONE authored RGB — <c>Engine_LightingFrame.Split</c>
	/// normalises the triple by its largest channel and carries that magnitude as the energy — so
	/// the number to compare against a theme is the PRODUCT, and either half alone means nothing.
	/// </summary>
	private void ReportSun()
	{
		if (_region?.GetNodeOrNull<Refii.Level.Engine_SkyEnvironmentNode>(
				Refii.Level.Engine_SkyEnvironment.NodeName) is not { } sky)
		{
			GD.Print("[sun] the region carries no sky environment; nothing states a sun");
			return;
		}

		if (sky.Lighting is not { } lighting)
		{
			GD.Print("[sun] the sky environment has not evaluated yet");
			return;
		}

		var direction = new Vector3(
			lighting.SunDirection[0], lighting.SunDirection[1], lighting.SunDirection[2]);
		direction = direction.LengthSquared() > 1e-8f ? direction.Normalized() : Vector3.Up;

		float elevation = Mathf.RadToDeg(MathF.Asin(Math.Clamp(direction.Y, -1.0f, 1.0f)));

		var authored = new Vector3(
			lighting.SunColour[0] * lighting.SunEnergy,
			lighting.SunColour[1] * lighting.SunEnergy,
			lighting.SunColour[2] * lighting.SunEnergy);

		// Rec.709, the same weights the histogram bins with, so this number and the histogram's
		// luminances are in one space and can be held against each other directly.
		float sunLuminance = Refii.Lighting.Engine_PostFxModel.Luminance(
			authored.X, authored.Y, authored.Z);

		GD.Print($"[sun] tod={sky.TimeOfDay:0.00}h  rig={direction}  elevation={elevation:0.0}deg  " +
			$"shadow={lighting.SunShadow}");
		GD.Print($"[sun]   main light  colour={lighting.SunColour[0]:0.000}," +
			$"{lighting.SunColour[1]:0.000},{lighting.SunColour[2]:0.000}  " +
			$"energy={lighting.SunEnergy:0.000}  authored={authored}  luminance={sunLuminance:0.0000}");

		string ambient = lighting.HasAmbient
			? $"colour={lighting.AmbientColour[0]:0.000},{lighting.AmbientColour[1]:0.000}," +
			  $"{lighting.AmbientColour[2]:0.000} energy={lighting.AmbientEnergy:0.000}"
			: "NOT STATED by this theme, so Godot's ambient is left alone";

		GD.Print($"[sun]   ambient  {ambient}");

		GD.Print($"[sun]   lightmap={lighting.LightmapColour[0]:0.000}," +
			$"{lighting.LightmapColour[1]:0.000},{lighting.LightmapColour[2]:0.000}  " +
			$"darkening={lighting.DarkeningColour[0]:0.000}," +
			$"{lighting.DarkeningColour[1]:0.000},{lighting.DarkeningColour[2]:0.000}");

		// SunIntensity drives the SKY DOME, not the main light — it is registered against the Sky
		// record rather than the Lighting one. Printed here because a blown horizon over dark
		// surfaces is the two of them disagreeing, and reading only the light cannot see it.
		if (sky.Theme is { } theme)
		{
			GD.Print($"[sun]   sky dome  SunIntensity={theme.SunIntensity:0.000} " +
				$"(stated={theme.HasSunIntensity})");
		}

		if (_region.GetNodeOrNull<DirectionalLight3D>(
				Refii.Lighting.Engine_LevelLighting.SunNodeName) is { } light)
		{
			GD.Print($"[sun]   live node  colour={light.LightColor} energy={light.LightEnergy:0.000} " +
				$"shadow={light.ShadowEnabled}");
		}
		else
		{
			GD.Print("[sun]   live node  MISSING — the region has no DirectionalLight3D named " +
				$"'{Refii.Lighting.Engine_LevelLighting.SunNodeName}'");
		}
	}

	/// <summary>
	/// Builds the region. Driven by the MOUNTED SIGNAL, never by polling <c>IsMounted</c>.
	///
	/// Those are not the same moment, which is the trap. <c>Engine_GameAssets.Mount</c> publishes
	/// the filesystem from its worker thread — so <c>IsMounted</c> flips immediately — and only
	/// then defers <c>ReportMounted</c>, which is what installs the terrain, mesh and texture
	/// ResourceFormatLoaders and emits this signal. Polling the flag builds in the window between
	/// the two and loads nothing, failing on `terrain mesh did not load from game://…`
	/// intermittently, depending on which side of the deferred call the frame landed.
	/// </summary>
	private void OnMounted(int fileCount)
	{
		if (_requested || Engine_GameAssets.Instance is not { } assets)
		{
			return;
		}

		_requested = true;
		GD.Print($"[level-viewer] mounted {fileCount:N0} files");

		// REFII_LEVEL_SCENARIO picks the variant the way a running game's scenario does.
		string scenario = OS.GetEnvironment("REFII_LEVEL_SCENARIO");
		if (scenario.Length != 0)
		{
			assets.CurrentScenario = scenario;
		}
		// THE GAME'S PATH, NOT THE EDITOR'S: the region is built and mounted in memory. Saving
		// it as a scene and loading that back is the editor's cache (8 GB and a minute each way
		// for Westcliff), which a viewer run has no use for.
		_caption.Text = $"building {LevelName}...";
		if (assets.BuildLevelNode(string.Empty, LevelName, out string error) is not { } region)
		{
			GD.PrintErr($"[level-viewer] could not build {LevelName}: {error}");
			GetTree().Quit(1);
			return;
		}
		string scenePath = LevelName;

		_region = region;
		AddChild(region);

		// The water's mirrors, mounted as GameWorld mounts them: without one the classic water's
		// g_ReflectionSampler is unbound and the modern shader falls back to the sky dome.
		if (region.GetNodeOrNull<Node3D>(Refii.Level.Engine_LevelScene.WaterNodeName) is { } water)
		{
			var reflections = new Refii.Level.Engine_WaterReflection { Name = Refii.Level.Engine_WaterReflection.NodeName };
			AddChild(reflections);
			reflections.Mount(water, _camera);
		}

		// REFII_LEVEL_LOD_THRESHOLD=<pixels>: Godot's mesh LOD threshold, to force the coarser levels
		// into view (default 1). With the facing view it shows which levels are wound backwards.
		if (float.TryParse(OS.GetEnvironment("REFII_LEVEL_LOD_THRESHOLD"), System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float lodThreshold) && lodThreshold > 0f)
		{
			GetViewport().MeshLodThreshold = lodThreshold;
		}

		// REFII_LEVEL_FLOOR_SHARE=<0..1>: the bake's unoccluded share, for judging it against 0.
		if (float.TryParse(OS.GetEnvironment("REFII_LEVEL_FLOOR_SHARE"), System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float floorShare))
		{
			Refii.Lighting.Engine_LightingFrame.ModernFloorShare = Math.Clamp(floorShare, 0f, 1f);
		}

		// REFII_LEVEL_GI_HALF=1: Godot resolves the GI at half resolution and upsamples it. For
		// judging that one setting against the full-resolution frame from the same camera.
		RenderingServer.GISetUseHalfResolution(OS.GetEnvironment("REFII_LEVEL_GI_HALF") == "1");

		// REFII_LEVEL_TOD drives the same setter the editor's inspector does, AFTER the region
		// is standing. That is a different path from REFII_FORCE_TOD, which changes the time
		// the scene is BUILT at — and only this one proves the slider relights what is already
		// there rather than just re-aiming the sun over a region frozen at build time.
		string tod = OS.GetEnvironment("REFII_LEVEL_TOD");
		if (tod.Length != 0 &&
			float.TryParse(tod, System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float hours) &&
			region.GetNodeOrNull<Refii.Level.Engine_SkyEnvironmentNode>(
				Refii.Level.Engine_SkyEnvironment.NodeName) is { } sky)
		{
			sky.TimeOfDay = hours;
			GD.Print($"[level-viewer] time of day moved to {hours:0.0}h after load");
		}

		Isolate(region);
		Frame(region);

		// REFII_LEVEL_DEBUG=<n> selects the shaders' lighting debug view (the lighting_debug_view
		// cvar's numbering); REFII_LEVEL_BAKE=<w> overrides the bake weight. Both are globals the
		// sky node publishes from refii.toml, so they are re-stated every frame (see _Process).
		// The fixtures near the camera target, as built: what Godot was actually handed.
		foreach (Node node in Descendants(region))
		{
			if (node is Light3D fixture && fixture is not DirectionalLight3D &&
				fixture.GlobalPosition.DistanceTo(_target) < 6.0f)
			{
				float range = fixture is OmniLight3D omni ? omni.OmniRange : fixture is SpotLight3D spot ? spot.SpotRange : 0f;
				GD.Print($"[level-viewer] fixture '{fixture.Name}' at {fixture.GlobalPosition} {fixture.GlobalPosition.DistanceTo(_target):0.0}m energy={fixture.LightEnergy:0.00} range={range:0.0} colour={fixture.LightColor} visible={fixture.Visible} fade={fixture.DistanceFadeEnabled}/{fixture.DistanceFadeBegin:0}+{fixture.DistanceFadeLength:0}");
			}
		}

		if (int.TryParse(OS.GetEnvironment("REFII_LEVEL_DEBUG"), out int debugView))
		{
			_debugView = debugView;
		}

		if (float.TryParse(OS.GetEnvironment("REFII_LEVEL_BAKE"),
				System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float bakeWeight))
		{
			_bakeWeight = bakeWeight;
		}
		GD.Print($"[level-viewer] showing {scenePath}");
	}

	/// <summary>
	/// Splits the lit image into the terms that make it, because they fail differently and
	/// look alike once added together.
	///
	/// A region that comes out flat and dim has either a sun that is not reaching the surface
	/// or an ambient term that is drowning one that is, and the composite cannot tell those
	/// apart — both read as "the lighting needs work". <c>REFII_LEVEL_LIGHT=direct</c> zeroes
	/// the baked and ambient instance uniforms so only the sun remains; <c>=ambient</c> takes
	/// the sun's energy to zero so only the baked term remains.
	/// </summary>
	private void Isolate(Node3D region)
	{
		// THE ATMOSPHERE IS A SEPARATE TERM FROM BOTH OF THE LIGHT ONES, and until this switch
		// existed it could not be taken out of the picture at all. That is not a gap in the
		// instrument, it is the gap that let a wrong answer stand: with `direct` and `ambient` the
		// only isolations available, a frame whose luminance comes almost entirely from in-scatter
		// reports the SAME histogram under both, and the obvious reading — "neither light term
		// matters" — says nothing about what does.
		//
		// It clears the published global rather than editing a material, because that is the one
		// switch every fogged surface in the region reads: `fable_atmosphere_terms` returns false
		// on it before computing anything, so nothing is left half-attenuated.
		//
		// RE-ASSERTED EVERY FRAME, not cleared once. `Engine_SkyEnvironment.ApplyAtmosphereGlobals`
		// republishes this global from the theme on every re-evaluation, so a single clear here is
		// overwritten before the first frame is drawn — and the run then reports a fully fogged
		// frame under a flag that says fog is off, which is worse than having no switch.
		if (OS.GetEnvironment("REFII_LEVEL_FOG").Equals("off", StringComparison.OrdinalIgnoreCase))
		{
			_fogOff = true;
			GD.Print("[level-viewer] fog off: atmos_fog_on held clear every frame");
		}

		// REFII_LEVEL_EXPOSURE=<E>: the adaptive pre-exposure held at E for the run.
		if (float.TryParse(OS.GetEnvironment("REFII_LEVEL_EXPOSURE"), System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float lockedExposure) && lockedExposure > 0f)
		{
			Refii.Lighting.Engine_PostFxParams.ExposureLock = true;
			Refii.Lighting.Engine_PostFxParams.ExposureLockValue = lockedExposure;
			GD.Print($"[level-viewer] exposure held at {lockedExposure}");
		}

		// REFII_LEVEL_POSTFX=bloom,dof,rays: the post-FX stages named are switched off for the run.
		foreach (string stage in OS.GetEnvironment("REFII_LEVEL_POSTFX").Split(',', StringSplitOptions.RemoveEmptyEntries))
		{
			switch (stage.Trim().ToLowerInvariant())
			{
				case "bloom": Refii.Lighting.Engine_PostFxParams.Bloom = false; break;
				case "dof": Refii.Lighting.Engine_PostFxParams.Dof = false; break;
				case "rays": Refii.Lighting.Engine_PostFxParams.Rays = false; break;
				case "mask":
					foreach (CompositorEffect effect in _camera.Compositor?.CompositorEffects ?? new Godot.Collections.Array<CompositorEffect>())
					{
						if (effect is Refii.Models.Engine_ClassicScreenShadow)
						{
							effect.Enabled = false;
						}
					}
					break;
			}
			GD.Print($"[level-viewer] post-fx stage off: {stage}");
		}

		// REFII_LEVEL_MIST=off: drop the authored mist volumes and the volumetric fog they
		// enable, so a pair of shots differs only in the mist.
		if (OS.GetEnvironment("REFII_LEVEL_MIST").Equals("off", StringComparison.OrdinalIgnoreCase))
		{
			int dropped = 0;
			foreach (Node node in Descendants(region))
			{
				if (node is FogVolume fogVolume)
				{
					fogVolume.Visible = false;
					dropped++;
				}
			}

			foreach (Node node in Descendants(region))
			{
				if (node is Refii.Level.Engine_SkyEnvironmentNode skyNode && skyNode.Environment is { } env)
				{
					env.VolumetricFogEnabled = false;
				}
			}

			GD.Print($"[level-viewer] mist off: {dropped} volume(s) hidden");
		}

		string mode = OS.GetEnvironment("REFII_LEVEL_LIGHT");
		if (mode.Length == 0 || mode.Equals("full", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		if (mode.Equals("ambient", StringComparison.OrdinalIgnoreCase))
		{
			if (region.GetNodeOrNull<DirectionalLight3D>(
					Refii.Lighting.Engine_LevelLighting.SunNodeName) is { } off)
			{
				off.LightEnergy = 0.0f;
			}

			GD.Print("[level-viewer] ambient only: the sun is off");
			return;
		}

		int zeroed = 0;
		foreach (Node node in Descendants(region))
		{
			if (node is not GeometryInstance3D instance)
			{
				continue;
			}

			instance.SetInstanceShaderParameter("ambient_colour", new Vector4(0, 0, 0, 1));
			instance.SetInstanceShaderParameter("lightmap_colour", new Vector4(0, 0, 0, 0));
			++zeroed;
		}

		GD.Print($"[level-viewer] direct only: baked and ambient zeroed on {zeroed} instance(s)");
	}

	private static System.Collections.Generic.IEnumerable<Node> Descendants(Node root)
	{
		foreach (Node child in root.GetChildren())
		{
			yield return child;
			foreach (Node grandchild in Descendants(child))
			{
				yield return grandchild;
			}
		}
	}

	/// <summary>
	/// Puts the camera at eye height over the middle of the terrain, looking along it.
	///
	/// Not an orbit around the whole region's bounding sphere: a level is hundreds of metres
	/// across, so framing all of it puts the camera far enough away that nothing about the
	/// lighting is legible — the shadow cascades are out of range and every surface is a
	/// pixel. Standing IN the region is what the game shows.
	/// </summary>
	private void Frame(Node3D region)
	{
		Aabb bounds = default;
		bool any = false;

		foreach (Node node in region.GetChildren())
		{
			if (node is not MeshInstance3D { Mesh: not null } mesh)
			{
				continue;
			}

			Aabb box = mesh.GlobalTransform * mesh.Mesh.GetAabb();
			bounds = any ? bounds.Merge(box) : box;
			any = true;
		}

		if (!any)
		{
			_target = Vector3.Zero;
			_distance = 40.0f;
			return;
		}

		Vector3 centre = bounds.GetCenter();

		// ABOVE THE TERRAIN, NOT A FRACTION OF THE WHOLE REGION'S BOX.
		//
		// A quarter of the way up the merged bounds put the camera UNDER THE GROUND on BWSSlums:
		// the box spans the tallest building in the region, so a fraction of its height is below
		// the surface wherever the terrain does not sit on the box's floor. A run from under the
		// world is not an obviously broken picture — it is a plausible frame of sky dome and
		// undersides, and it meters as one. The exposure dump read 80% sky and reported it as the
		// region's luminance distribution.
		//
		// The terrain's own AABB is the answer, and it is the one node whose top is ground rather
		// than roof. Falling back to the merged box's top keeps a region with no terrain framed
		// from above rather than from inside it.
		List<MeshInstance3D> terrainChunks = Refii.Level.Engine_LevelScene.TerrainChunksOf(region);
		float ground = bounds.End.Y;
		for (int i = 0; i < terrainChunks.Count; i++)
		{
			float top = (terrainChunks[i].GlobalTransform * terrainChunks[i].Mesh.GetAabb()).End.Y;
			ground = i == 0 ? top : Mathf.Max(ground, top);
		}

		_target = new Vector3(centre.X, ground + 2.0f, centre.Z);
		_distance = 45.0f;

		// The sky's share of the frame is a METERING choice, not a cosmetic one — a histogram taken
		// over a frame that is mostly dome describes the dome. Steeper looks further down.
		// REFII_LEVEL_DISTANCE / REFII_LEVEL_YAW / REFII_LEVEL_AT=x,y,z: put the camera INSIDE an
		// interior. The default orbit frames the level's bounds from outside, which for a cave is
		// the back of its shell.
		if (float.TryParse(OS.GetEnvironment("REFII_LEVEL_DISTANCE"),
				System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float distance) && distance > 0f)
		{
			_distance = distance;
		}

		if (float.TryParse(OS.GetEnvironment("REFII_LEVEL_YAW"),
				System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float yaw))
		{
			_yaw = Mathf.DegToRad(yaw);
		}

		string at = OS.GetEnvironment("REFII_LEVEL_AT");
		if (at.Length != 0)
		{
			string[] xyz = at.Split(',');
			if (xyz.Length == 3 &&
				float.TryParse(xyz[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) &&
				float.TryParse(xyz[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y) &&
				float.TryParse(xyz[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
			{
				_target = new Vector3(x, y, z);
			}
		}

		if (float.TryParse(OS.GetEnvironment("REFII_LEVEL_PITCH"),
				System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out float pitch))
		{
			_pitch = Math.Clamp(Mathf.DegToRad(pitch), -1.5f, 1.5f);
		}

		Place();
	}

	private void Fly(float delta)
	{
		float speed = Input.IsKeyPressed(Key.Shift) ? 120.0f : 30.0f;
		var move = Vector3.Zero;

		if (Input.IsKeyPressed(Key.W)) move.Z -= 1.0f;
		if (Input.IsKeyPressed(Key.S)) move.Z += 1.0f;
		if (Input.IsKeyPressed(Key.A)) move.X -= 1.0f;
		if (Input.IsKeyPressed(Key.D)) move.X += 1.0f;
		if (Input.IsKeyPressed(Key.E)) move.Y += 1.0f;
		if (Input.IsKeyPressed(Key.Q)) move.Y -= 1.0f;

		if (move == Vector3.Zero)
		{
			return;
		}

		_target += _camera.GlobalTransform.Basis * move.Normalized() * speed * delta;
		Place();
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventMouseButton button)
		{
			if (button.ButtonIndex == MouseButton.Left)
			{
				_orbiting = button.Pressed;
			}
			else if (button.Pressed && button.ButtonIndex == MouseButton.WheelUp)
			{
				_distance = MathF.Max(_distance * 0.85f, 0.5f);
				Place();
			}
			else if (button.Pressed && button.ButtonIndex == MouseButton.WheelDown)
			{
				_distance = MathF.Min(_distance * 1.18f, 8000.0f);
				Place();
			}
		}
		else if (@event is InputEventMouseMotion motion && _orbiting)
		{
			_yaw -= motion.Relative.X * 0.006f;
			_pitch = Math.Clamp(_pitch - (motion.Relative.Y * 0.006f), -1.5f, 1.5f);
			Place();
		}
		else if (@event is InputEventKey { Pressed: true } key &&
			key.Keycode is Key.Bracketleft or Key.Bracketright)
		{
			StepTimeOfDay(key.Keycode == Key.Bracketright ? 0.5f : -0.5f);
		}
	}

	/// <summary>
	/// Moves the region's own time of day, through the scene's <c>Engine_SkyEnvironmentNode</c>.
	///
	/// This is the same setter the editor's inspector drives, so what this shows at 14:00 is
	/// what the editor shows at 14:00 — the point of a lighting instrument is that it cannot
	/// disagree with the thing it is checking.
	/// </summary>
	private void StepTimeOfDay(float hours)
	{
		if (_region?.GetNodeOrNull<Refii.Level.Engine_SkyEnvironmentNode>("SkyEnvironment")
			is not { } sky)
		{
			return;
		}

		sky.TimeOfDay = Mathf.PosMod(sky.TimeOfDay + hours, 24.0f);
		Place();
	}

	private void Place()
	{
		var offset = new Vector3(
			MathF.Cos(_pitch) * MathF.Sin(_yaw),
			MathF.Sin(_pitch),
			MathF.Cos(_pitch) * MathF.Cos(_yaw));

		_camera.GlobalPosition = _target + (offset * _distance);
		_camera.LookAt(_target, Vector3.Up);

		float tod = _region?.GetNodeOrNull<Refii.Level.Engine_SkyEnvironmentNode>("SkyEnvironment")
			?.TimeOfDay ?? -1.0f;

		_caption.Text = $"{LevelName}   tod={tod:0.0}h   pos={_camera.GlobalPosition}   " +
			"LMB orbit · wheel zoom · WASDQE fly · [ ] time of day";
	}

	/// <summary>
	/// Writes the terrain's baked lightmap beside the shot, when <c>REFII_LEVEL_LIGHTMAP=1</c>.
	///
	/// What the texture CONTAINS decides how it may be combined, and the two candidates look
	/// identical in a log line: full baked radiance has to replace the runtime ambient, while
	/// a shadow or occlusion mask has to multiply it. One is chromatic, the other is grey, so
	/// the picture settles it and a count never will.
	/// </summary>
	private void WriteLightmap()
	{
		if (OS.GetEnvironment("REFII_LEVEL_LIGHTMAP") != "1" || _region is null)
		{
			return;
		}

		List<MeshInstance3D> terrainChunks = Refii.Level.Engine_LevelScene.TerrainChunksOf(_region);
		if (terrainChunks.Count == 0 ||
			terrainChunks[0] is not { MaterialOverride: ShaderMaterial splat } ||
			splat.GetShaderParameter("lightmap").As<Texture2D>() is not { } lightmap)
		{
			GD.Print("[level-viewer] the terrain carries no lightmap");
			return;
		}

		string path = _shotPath + ".lightmap.png";
		Image image = lightmap.GetImage();
		GD.Print(image.SavePng(path) == Error.Ok
			? $"[level-viewer] wrote {path} ({image.GetWidth()}x{image.GetHeight()} " +
			  $"{image.GetFormat()})"
			: $"[level-viewer] could not write {path}");
	}

	private int _tourStop = -1;
	private List<(Vector3 Target, float Yaw, float Distance, float Pitch)>? _tourSpots;

	/// <summary>
	/// REFII_LEVEL_TOUR=N: after the overview, more shots from inside the level, one per stop;
	/// shots go to the shot path with _0.._N-1 before the extension. The stops are, in order of
	/// preference: REFII_LEVEL_TOUR_POINTS=x,y,z;x,y,z... (GUEST coordinates, as a placement
	/// states them: entrances, exits, spawns), walked in order with up to two extra stops on each
	/// leg, the camera 5 m behind each stop looking along the route; else N terrain chunks spread
	/// across the heightfield, the camera 18 m out (each chunk's own box gives the ground).
	/// </summary>
	private bool TourStep()
	{
		if (!int.TryParse(OS.GetEnvironment("REFII_LEVEL_TOUR"), out int stops) || stops <= 0 || _region is null)
		{
			return false;
		}
		_tourSpots ??= TourSpots(stops);
		string stem = System.IO.Path.ChangeExtension(_shotPath, null);
		string path = _tourStop < 0 ? _shotPath : $"{stem}_{_tourStop}.png";
		Error saved = GetViewport().GetTexture().GetImage().SavePng(path);
		GD.Print($"[level-viewer] tour {(_tourStop < 0 ? "overview" : _tourStop.ToString())} wrote {path} ({saved})" +
			(_tourStop >= 0 ? $" at {_tourSpots[_tourStop].Target}" : string.Empty));
		// REFII_LEVEL_MIRROR_DUMP=1: every water mirror's last render beside the shot.
		if (OS.GetEnvironment("REFII_LEVEL_MIRROR_DUMP") == "1")
		{
			int mi = 0;
			foreach (Node node in GetTree().Root.FindChildren("*", nameof(SubViewport), true, false))
			{
				if (node is SubViewport mirror && mirror.GetParent() is Refii.Level.Engine_WaterReflection)
				{
					mirror.GetTexture().GetImage()?.SavePng(path.Replace(".png", $"_mirror{mi++}.png"));
				}
			}
		}
		if (++_tourStop >= _tourSpots.Count)
		{
			GetTree().Quit(0);
			return true;
		}
		(_target, _yaw, _distance, _pitch) = _tourSpots[_tourStop];
		Place();
		// A route point beside a slope puts the camera 5 m back inside the hill, where the classic
		// terrain (single-sided, as the title's) shows nothing and the modern one its underside.
		if (GroundBelow(_camera.GlobalPosition) is float ground && _camera.GlobalPosition.Y < ground + 1.2f)
		{
			_target.Y += ground + 1.2f - _camera.GlobalPosition.Y;
			Place();
		}
		_framesRendered = ShotWarmupFrames - 45;
		return true;
	}

	private readonly Dictionary<Mesh, Vector3[]> _groundFaces = new();

	/// <summary>The highest terrain surface under <paramref name="at"/>, or null off the heightfield.</summary>
	private float? GroundBelow(Vector3 at)
	{
		float? best = null;
		foreach (MeshInstance3D chunk in Refii.Level.Engine_LevelScene.TerrainChunksOf(_region!))
		{
			if (chunk.Mesh is not { } mesh)
			{
				continue;
			}
			Aabb box = chunk.GlobalTransform * mesh.GetAabb();
			if (at.X < box.Position.X || at.X > box.End.X || at.Z < box.Position.Z || at.Z > box.End.Z)
			{
				continue;
			}
			if (!_groundFaces.TryGetValue(mesh, out Vector3[]? faces))
			{
				_groundFaces[mesh] = faces = mesh.GetFaces();
			}
			Vector3 from = new(at.X, box.End.Y + 10f, at.Z);
			for (int i = 0; i + 2 < faces.Length; i += 3)
			{
				Variant hit = Geometry3D.RayIntersectsTriangle(from, Vector3.Down,
					chunk.GlobalTransform * faces[i], chunk.GlobalTransform * faces[i + 1], chunk.GlobalTransform * faces[i + 2]);
				if (hit.VariantType == Variant.Type.Vector3 && (best is null || hit.AsVector3().Y > best))
				{
					best = hit.AsVector3().Y;
				}
			}
		}
		return best;
	}

	private List<(Vector3, float, float, float)> TourSpots(int stops)
	{
		var spots = new List<(Vector3, float, float, float)>();
		var points = new List<Vector3>();
		foreach (string item in OS.GetEnvironment("REFII_LEVEL_TOUR_POINTS").Split(';', StringSplitOptions.RemoveEmptyEntries))
		{
			string[] c = item.Split(',');
			if (c.Length == 3 &&
				float.TryParse(c[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float gx) &&
				float.TryParse(c[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float gy) &&
				float.TryParse(c[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float gz))
			{
				points.Add(_region!.GlobalTransform * new Vector3(gx, gz, gy));
			}
		}
		if (points.Count != 0)
		{
			var route = new List<Vector3>();
			for (int i = 0; i < points.Count; i++)
			{
				route.Add(points[i]);
				if (i + 1 < points.Count)
				{
					int extra = Math.Clamp((int)(points[i].DistanceTo(points[i + 1]) / 30f), 0, 2);
					for (int k = 1; k <= extra; k++)
					{
						route.Add(points[i].Lerp(points[i + 1], k / (extra + 1f)));
					}
				}
			}
			for (int i = 0; i < route.Count && spots.Count < stops; i++)
			{
				Vector3 ahead = i + 1 < route.Count ? route[i + 1] - route[i] : route[i] - route[Math.Max(i - 1, 0)];
				ahead.Y = 0f;
				float yaw = ahead.LengthSquared() > 1e-4f ? MathF.Atan2(-ahead.X, -ahead.Z) : 0f;
				spots.Add((route[i] + new Vector3(0f, 1.6f, 0f), yaw, 5f, Mathf.DegToRad(12f)));
			}
			return spots;
		}
		List<MeshInstance3D> chunks = Refii.Level.Engine_LevelScene.TerrainChunksOf(_region!);
		var centres = chunks.Select(c => c.GlobalTransform * c.Mesh.GetAabb())
			.Select(b => new Vector3(b.GetCenter().X, b.Position.Y + b.Size.Y * 0.5f + 1.5f, b.GetCenter().Z))
			.OrderBy(v => v.X).ThenBy(v => v.Z).ToList();
		for (int i = 0; i < stops && centres.Count > 0; i++)
		{
			spots.Add((centres[(int)((i + 0.5f) / stops * centres.Count)], Mathf.DegToRad(45f + 90f * i), 18f, Mathf.DegToRad(20f)));
		}
		return spots;
	}

	private void WriteShot()
	{
		WriteLightmap();
		if (TourStep())
		{
			return;
		}

		Image image = GetViewport().GetTexture().GetImage();
		Error saved = image.SavePng(_shotPath);
		GD.Print(saved == Error.Ok
			? $"[level-viewer] wrote {_shotPath}"
			: $"[level-viewer] could not write {_shotPath}: {saved}");

		GetTree().Quit(saved == Error.Ok ? 0 : 1);
	}

	/// <summary>
	/// REFII_LEVEL_SHADOW_PROBE=1: the classic cascade test done on the CPU at points around the
	/// camera's focus — the shader's own projection (PS c36..c39) against the captured map.
	/// </summary>
	private byte[]? _maskBytes;
	private int _landscapePrinted;

	private void ShadowProbe()
	{
		if (Refii.Models.Engine_ClassicScreenShadow.Mask.TextureRdRid is { IsValid: true } maskRid &&
			Refii.Models.Engine_ClassicScreenShadow.Size is { X: > 0 } maskSize &&
			RenderingServer.GetRenderingDevice() is { } maskDevice)
		{
			byte[] bytes = maskDevice.TextureGetData(maskRid, 0);
			if (bytes.Length >= maskSize.X * maskSize.Y)
			{
				Image maskImage = Image.CreateFromData(maskSize.X, maskSize.Y, false, Image.Format.L8, bytes[..(maskSize.X * maskSize.Y)]);
				string maskPath = _shotPath.Replace(".png", "_mask.png");
				maskImage.SavePng(maskPath);
				GD.Print($"[level-viewer] shadow probe: screen mask {maskSize.X}x{maskSize.Y} written to {maskPath}");
			}
		}
		if (Refii.Models.Engine_ClassicShadows.Instance is not { } shadows)
		{
			GD.Print("[level-viewer] shadow probe: no cascades");
			return;
		}
		if (shadows.CascadeTexture(0) is not Texture2Drd rdTexture || RenderingServer.GetRenderingDevice() is not { } device)
		{
			GD.Print("[level-viewer] shadow probe: cascade 0 is not a device texture");
			return;
		}
		byte[] raw = device.TextureGetData(rdTexture.TextureRdRid, 0);
		int w = Refii.Models.Engine_ClassicShadows.MapSize, h = w;
		if (raw.Length < w * h * 4)
		{
			GD.Print($"[level-viewer] shadow probe: cascade 0 returned {raw.Length} bytes");
			return;
		}
		float At(int x, int y) => BitConverter.ToSingle(raw, (y * w + x) * 4);
		double sum = 0; float max = 0f; int nonZero = 0;
		for (int y = 0; y < h; y += 4)
		{
			for (int x = 0; x < w; x += 4)
			{
				float v = At(x, y);
				sum += v; max = MathF.Max(max, v);
				if (v > 0f) ++nonZero;
			}
		}
		int samples = (w / 4) * (h / 4);
		GD.Print($"[level-viewer] shadow probe: cascade 0 {w}x{h}, mean {sum / samples:0.0000}, max {max:0.0000}, covered {100.0 * nonZero / samples:0.0}%");
		var picture = Image.CreateEmpty(w, h, false, Image.Format.L8);
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				float v = max > 0f ? At(x, y) / max : 0f;
				picture.SetPixel(x, y, new Color(v, v, v));
			}
		}
		string mapPath = _shotPath.Replace(".png", "_cascade0.png");
		picture.SavePng(mapPath);
		GD.Print($"[level-viewer] shadow probe: cascade 0 written to {mapPath}");
		// The cascade camera's nodes: which top-level groups have geometry it draws.
		uint mask = shadows.CascadeCamera(0).CullMask;
		var counts = new Dictionary<string, int>();
		foreach (Node n in GetTree().Root.FindChildren("*", "VisualInstance3D", true, false))
		{
			if (n is VisualInstance3D vi && vi.IsVisibleInTree() && (vi.Layers & mask) != 0 && vi is GeometryInstance3D gi && gi.CastShadow != GeometryInstance3D.ShadowCastingSetting.ShadowsOnly)
			{
				Node top = vi;
				while (top.GetParent() is Node parent && parent != _region && parent != GetTree().Root)
				{
					top = parent;
				}
				string key = top.Name + " (" + vi.GetClass() + ")";
				counts[key] = counts.GetValueOrDefault(key) + 1;
			}
		}
		foreach (var pair in counts.OrderByDescending(p => p.Value).Take(25))
		{
			GD.Print($"[level-viewer] shadow probe: cascade draws {pair.Value} x {pair.Key}");
		}
		float[] rows = Refii.Models.Engine_ClassicConstants.ReadFrame(true, 36, 4);
		Vector3 focus = _camera.GlobalPosition + (-_camera.GlobalBasis.Z) * 12f;
		foreach (Vector3 offset in new[] { Vector3.Zero, new Vector3(0, -2, 0), new Vector3(3, -2, 0), new Vector3(0, -2, 3), new Vector3(-3, -2, -3) })
		{
			Vector3 g = focus + offset;
			var p = new Vector4(-g.X, g.Z, g.Y, 1f);
			float Dot(int r) => rows[r * 4] * p.X + rows[r * 4 + 1] * p.Y + rows[r * 4 + 2] * p.Z + rows[r * 4 + 3] * p.W;
			float cx = Dot(0), cy = Dot(1), cz = Dot(2), cw = Dot(3);
			float u = cx / cw, v = cy / cw, z = cz / cw;
			int tx = (int)(u * w), ty = (int)(v * h);
			float stored = tx >= 0 && ty >= 0 && tx < w && ty < h ? At(tx, ty) : float.NaN;
			Vector2 screen = shadows.CascadeCamera(0).UnprojectPosition(g) / w;
			float godotDepth = shadows.CascadeCamera(0).GlobalTransform.AffineInverse() * g is var local ? -local.Z : 0f;
			string maskText = "";
			if (Refii.Models.Engine_ClassicScreenShadow.Mask.TextureRdRid is { IsValid: true } probeRid &&
				Refii.Models.Engine_ClassicScreenShadow.Size is { X: > 0 } probeSize)
			{
				_maskBytes ??= device.TextureGetData(probeRid, 0);
				Vector2 px = _camera.UnprojectPosition(g) * ((float)probeSize.X / GetViewport().GetVisibleRect().Size.X);
				int mx = Math.Clamp((int)px.X, 0, probeSize.X - 1), my = Math.Clamp((int)px.Y, 0, probeSize.Y - 1);
				if (_maskBytes.Length >= probeSize.X * probeSize.Y)
				{
					maskText = $" mask {_maskBytes[my * probeSize.X + mx] / 255f:0.00} at ({mx}, {my})";
				}
			}
			GD.Print($"[level-viewer] shadow probe at godot {g}: uv ({u:0.000}, {v:0.000}) godot cam uv ({screen.X:0.000}, {screen.Y:0.000}) " +
				$"view depth {godotDepth:0.00} 1-z {1f - z:0.0000} map {stored:0.0000} -> {(1f - z > stored ? "lit" : "SHADOWED")}{maskText}");
		}
	}
}
