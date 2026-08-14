using Godot;

namespace Refii.Launcher.Tools;

/// <summary>
/// Loads every surface shader and reports whether it actually compiles.
///
/// A .gdshader is a resource, not C#, so `dotnet build` says nothing about it — a syntax error or
/// an illegal varying stage surfaces only when something instantiates the material, which in a
/// region build is thousands of frames after the mistake was made.
/// </summary>
public partial class ShaderCheck : Node
{
	private static readonly string[] Shaders =
	{
		"res://src/Engine/material/standard_material.gdshader",
		"res://src/Engine/material/standard_material_phong.gdshader",
		"res://src/Engine/level/terrain/terrain_splat.gdshader",
		"res://src/Engine/level/water/water.gdshader",
		"res://src/Engine/level/environment/sky.gdshader",
		"res://src/Engine/level/environment/clouds.gdshader",
		"res://src/Engine/fx/fx_particle.gdshader",
	};

	public override void _Ready()
	{
		int bad = 0;
		foreach (string path in Shaders)
		{
			var shader = GD.Load<Shader>(path);
			if (shader is null)
			{
				GD.Print($"[shader] LOAD FAILED {path}");
				++bad;
				continue;
			}

			// Instantiating the material is what forces the compile.
			var material = new ShaderMaterial { Shader = shader };

			// A FAILED COMPILE STILL RETURNS A Shader. Godot prints `SHADER ERROR:` to stderr and
			// hands back the resource anyway, so `!= null` reports success on a shader that will
			// never draw — this check said "ok" to a deliberately broken file before the count was
			// tested. What does change is the uniform list: compilation is what registers it, so a
			// shader that failed reports zero. Every shader here declares uniforms, so zero is
			// unambiguous rather than merely suspicious.
			int uniforms = shader.GetShaderUniformList().Count;
			if (uniforms == 0)
			{
				GD.Print($"[shader] COMPILE FAILED {path} — see the SHADER ERROR above");
				++bad;
				continue;
			}

			GD.Print($"[shader] ok {path} ({uniforms} uniforms)");
		}

		GD.Print($"[shader] done, {bad} failed to load");
		GetTree().Quit(bad == 0 ? 0 : 1);
	}
}
