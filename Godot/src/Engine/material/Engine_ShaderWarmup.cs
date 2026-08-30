using System.Collections.Generic;
using Godot;

namespace Refii.Models;

/// <summary>
/// The shader-preparation screen: compiles every pipeline BEFORE the guest boots.
///
/// Editing any .gdshader invalidates the Vulkan pipeline cache, and Godot compiles a
/// pipeline synchronously on the main thread the first time a material is drawn. The host's
/// 30 Hz step pump lives on that thread, so a first run after a shader edit stalls it for
/// seconds at a time — during exactly the boot window where the guest loses wake races it
/// normally wins, which is how "shader edit" became "cannot get in game" five runs straight.
///
/// So the storm is paid up front, behind a visible screen, while the asset mount runs on its
/// worker: every shader in the tree is drawn for real — spatial ones on a full-attribute mesh
/// under a shadow-casting light, canvas ones as a quad, sky ones as the warm viewport's sky —
/// and only when the last one has rendered does <see cref="Done"/> release the guest boot.
/// The pipeline cache still only persists on clean exit, but a killed run no longer matters:
/// the next one warms again behind the same screen instead of starving the pump.
/// </summary>
public partial class Engine_ShaderWarmup : Node
{
	public const string ShaderRoot = "res://src/Engine";

	/// <summary>Shaders drawn per frame. Each first draw compiles; more per frame means a
	/// longer hitch between progress updates, fewer means a longer total wall time.</summary>
	public const int PerFrame = 3;

	/// <summary>Frames the finished set stays alive so every queued draw actually lands.</summary>
	public const int SettleFrames = 3;

	public static bool Done { get; private set; }

	private static bool _begun;

	private static Engine_ShaderWarmup? _instance;

	private readonly List<string> _paths = new();
	private int _next;
	private bool _batchShown;
	private int _settle = SettleFrames;
	private ulong _startedMs;

	private SubViewport _viewport = null!;
	private Node3D _spatialRoot = null!;
	private Camera3D _camera = null!;
	private CanvasLayer _screen = null!;
	private Control _canvasRoot = null!;
	private Label _label = null!;
	private SpinnerArc? _spinner;
	private ArrayMesh _mesh = null!;

	/// <summary>Fable's gold — the loading screen's lettering and seal tone.</summary>
	private static readonly Color Gold = new(0.78f, 0.63f, 0.35f);

	private static readonly Color GoldDim = new(0.45f, 0.35f, 0.18f);

	public static void Begin(Node parent)
	{
		if (Done || _begun)
		{
			return;
		}

		_begun = true;

		bool enabled = true;
		try
		{
			enabled = Refii.Core.Engine_RefiiConfig.Bool(
				Refii.Core.Engine_RefiiConfig.Read(out _), "shader_warmup_enabled", true);
		}
		catch (System.Exception)
		{
		}

		if (!enabled)
		{
			Done = true;
			return;
		}

		_instance = new Engine_ShaderWarmup { Name = "ShaderWarmup" };
		parent.AddChild(_instance);
	}

	/// <summary>Status line while the guest itself boots, after the compiles are done.</summary>
	public static void SetStatus(string text)
	{
		if (_instance is { } warm && IsInstanceValid(warm) && warm._label is not null)
		{
			warm._label.Text = text;
		}
	}

	/// <summary>Takes the screen down. The caller decides when the boot is far enough along.</summary>
	public static void ReleaseScreen()
	{
		if (_instance is { } warm && IsInstanceValid(warm))
		{
			warm.QueueFree();
		}

		_instance = null;
	}

	public override void _Ready()
	{
		_startedMs = Time.GetTicksMsec();
		Collect(ShaderRoot);
		_paths.Sort();

		if (_paths.Count == 0)
		{
			GD.Print("[shader-warmup] no shaders found; skipping");
			Done = true;
			QueueFree();
			return;
		}

		BuildScreen();
		BuildViewport();
		GD.Print($"[shader-warmup] compiling {_paths.Count} shader(s) before the guest starts");
	}

	public override void _Process(double delta)
	{
		if (Done)
		{
			_spinner?.QueueRedraw();
			return;
		}

		if (_next < _paths.Count)
		{
			// THE COUNT GOES UP FIRST AND COMPILES ON THE NEXT FRAME. A compile stalls
			// presentation, so a count written after its batch is always one stall stale —
			// the screen reads "18/23" while 19..23 compile and the whole thing looks like it
			// bailed early. Showing the batch, presenting a frame, THEN compiling it keeps the
			// number on screen true for the stall it labels.
			int until = System.Math.Min(_next + PerFrame, _paths.Count);
			if (!_batchShown)
			{
				_label.Text = $"Preparing shaders   {until} / {_paths.Count}";
				_batchShown = true;
				return;
			}

			for (; _next < until; _next++)
			{
				Warm(_paths[_next]);
			}

			_batchShown = false;
			_spinner?.QueueRedraw();
			return;
		}

		if (--_settle > 0)
		{
			return;
		}

		GD.Print($"[shader-warmup] {_paths.Count} shader(s) compiled in " +
			$"{Time.GetTicksMsec() - _startedMs} ms");
		_label.Text = "Preparing Albion...";
		Done = true;
	}

	/// <summary>
	/// One shader, drawn by its own processor type. The material carries default uniforms —
	/// pipelines key on the shader and pass state, never on uniform values, so defaults warm
	/// the same pipelines the real materials use. Instance-uniform branches (foliage, the
	/// debug views) are runtime branches inside one pipeline, not variants.
	/// </summary>
	private void Warm(string path)
	{
		if (ResourceLoader.Load(path, "Shader") is not Shader shader)
		{
			GD.PrintErr($"[shader-warmup] could not load {path}");
			return;
		}

		var material = new ShaderMaterial { Shader = shader };
		string code = shader.Code;
		int at = code.IndexOf("shader_type", System.StringComparison.Ordinal);
		string type = at >= 0 ? code.Substring(at, System.Math.Min(40, code.Length - at)) : "";

		if (type.Contains("canvas_item"))
		{
			_canvasRoot.AddChild(new ColorRect
			{
				Material = material,
				Size = new Vector2(8, 8),
			});
		}
		else if (type.Contains("sky"))
		{
			_camera.Environment = new Godot.Environment
			{
				BackgroundMode = Godot.Environment.BGMode.Sky,
				Sky = new Sky { SkyMaterial = material },
			};
		}
		else
		{
			_spatialRoot.AddChild(new MeshInstance3D
			{
				Mesh = _mesh,
				MaterialOverride = material,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
			});
		}
	}

	private void Collect(string dir)
	{
		using DirAccess access = DirAccess.Open(dir);
		if (access is null)
		{
			return;
		}

		access.ListDirBegin();
		for (string name = access.GetNext(); name.Length != 0; name = access.GetNext())
		{
			string full = $"{dir}/{name}";
			if (access.CurrentIsDir())
			{
				if (!name.StartsWith('.'))
				{
					Collect(full);
				}

				continue;
			}

			// Exported PCKs list text resources with a .remap suffix; the load path is the
			// original name.
			if (name.EndsWith(".remap", System.StringComparison.Ordinal))
			{
				full = full.Substring(0, full.Length - ".remap".Length);
			}

			if (full.EndsWith(".gdshader", System.StringComparison.Ordinal))
			{
				_paths.Add(full);
			}
		}

		access.ListDirEnd();
	}

	/// <summary>
	/// STYLED LIKE THE GAME'S OWN LOAD SCREENS, but code-drawn: the real one draws bank art
	/// with the guest's fonts, and during warm-up the banks are still mounting and the guest
	/// does not exist. Black ground, the gold the loading seal uses, a drawn spinner arc in
	/// the lower right where the game puts its own, and the text beside it.
	/// </summary>
	private void BuildScreen()
	{
		_screen = new CanvasLayer { Layer = 120 };
		AddChild(_screen);

		// The canvas warm quads draw first, the opaque backdrop covers them.
		_canvasRoot = new Control();
		_screen.AddChild(_canvasRoot);

		var backdrop = new ColorRect
		{
			Color = Colors.Black,
			AnchorRight = 1.0f,
			AnchorBottom = 1.0f,
		};
		_screen.AddChild(backdrop);

		_spinner = new SpinnerArc
		{
			AnchorLeft = 1.0f,
			AnchorTop = 1.0f,
			AnchorRight = 1.0f,
			AnchorBottom = 1.0f,
			OffsetLeft = -96,
			OffsetTop = -96,
			OffsetRight = -40,
			OffsetBottom = -40,
		};
		_screen.AddChild(_spinner);

		_label = new Label
		{
			Text = "Preparing shaders",
			HorizontalAlignment = HorizontalAlignment.Right,
			VerticalAlignment = VerticalAlignment.Center,
			AnchorLeft = 0.0f,
			AnchorTop = 1.0f,
			AnchorRight = 1.0f,
			AnchorBottom = 1.0f,
			OffsetLeft = 40,
			OffsetTop = -96,
			OffsetRight = -112,
			OffsetBottom = -40,
		};
		_label.AddThemeColorOverride("font_color", Gold);
		_label.AddThemeFontSizeOverride("font_size", 22);
		_screen.AddChild(_label);
	}

	/// <summary>
	/// The loading spinner's shape without its art: a golden arc sweeping a dim ring. The
	/// game's own is a bank texture the mount has not produced yet.
	/// </summary>
	private sealed partial class SpinnerArc : Control
	{
		private float _angle;

		public override void _Draw()
		{
			_angle += 0.35f;
			Vector2 centre = Size * 0.5f;
			float radius = Mathf.Min(centre.X, centre.Y) - 4.0f;
			DrawArc(centre, radius, 0, Mathf.Tau, 48, GoldDim, 3.0f, true);
			DrawArc(centre, radius, _angle, _angle + Mathf.Tau * 0.28f, 24, Gold, 3.0f, true);
		}
	}

	private void BuildViewport()
	{
		// Its own world so nothing leaks into the game's; UpdateAlways so it renders during
		// the warm-up even though nothing else looks at it.
		_viewport = new SubViewport
		{
			Size = new Vector2I(128, 128),
			OwnWorld3D = true,
			World3D = new World3D(),
			RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
		};
		AddChild(_viewport);

		_spatialRoot = new Node3D();
		_viewport.AddChild(_spatialRoot);

		_camera = new Camera3D { Position = new Vector3(0, 0, 4) };
		_viewport.AddChild(_camera);

		// Shadows on, so the depth and shadow pass pipelines warm alongside the colour ones.
		_viewport.AddChild(new DirectionalLight3D
		{
			ShadowEnabled = true,
			Rotation = new Vector3(-0.9f, 0.4f, 0.0f),
		});

		_mesh = FullAttributeMesh();
	}

	/// <summary>
	/// A triangle carrying every attribute our real meshes use — position, normal, tangent,
	/// colour, two UV channels. Pipelines key on the vertex format, so warming with a plain
	/// quad would leave the formats the .mdl meshes actually use cold.
	/// </summary>
	private static ArrayMesh FullAttributeMesh()
	{
		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = new Vector3[]
		{
			new(-1, -1, 0), new(1, -1, 0), new(0, 1, 0),
		};
		arrays[(int)Mesh.ArrayType.Normal] = new Vector3[]
		{
			new(0, 0, 1), new(0, 0, 1), new(0, 0, 1),
		};
		arrays[(int)Mesh.ArrayType.Tangent] = new float[]
		{
			1, 0, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1,
		};
		arrays[(int)Mesh.ArrayType.Color] = new Color[]
		{
			new(1, 1, 1), new(1, 1, 1), new(1, 1, 1),
		};
		arrays[(int)Mesh.ArrayType.TexUV] = new Vector2[]
		{
			new(0, 0), new(1, 0), new(0.5f, 1),
		};
		arrays[(int)Mesh.ArrayType.TexUV2] = new Vector2[]
		{
			new(0, 0), new(1, 0), new(0.5f, 1),
		};

		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		return mesh;
	}
}
