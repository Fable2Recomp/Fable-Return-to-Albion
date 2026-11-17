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
		Refii.Models.Engine_ShaderFamily.StandardCulledPath,
		Refii.Models.Engine_ShaderFamily.StandardTranslucentPath,
		Refii.Models.Engine_ShaderFamily.StandardWindPath,
		Refii.Models.Engine_ShaderFamily.StandardTreePath,
		"res://src/Engine/material/fur_shell.gdshader",
		"res://src/Engine/material/fur_shell_depth.gdshader",

		// The draw-path families. Each is a DIFFERENT LIGHTING MODEL rather than a variant, so
		// each can break on its own — and a family with no caller yet (the canopy, until the
		// per-surface trunk/canopy discriminator is settled) would otherwise not be compiled by
		// anything at all and could sit broken indefinitely.
		Refii.Models.Engine_ShaderFamily.TreeCanopyPath,
		"res://src/Engine/level/terrain/terrain_splat.gdshader",
		"res://src/Engine/level/water/water.gdshader",
		"res://src/Engine/level/environment/sky.gdshader",
		"res://src/Engine/level/environment/clouds.gdshader",
		"res://src/Engine/fx/fx_particle.gdshader",
		"res://src/Engine/entities/entity_highlight_silhouette.gdshader",
		"res://src/Engine/entities/entity_saturation_mask.gdshader",
		"res://src/Engine/entities/entity_fade_depth.gdshader",
		"res://src/Engine/entities/entity_fade_depth_mask.gdshader",
		"res://src/Engine/entities/shadow_caster.gdshader",
		"res://src/Engine/entities/shadow_caster_solid.gdshader",
		"res://src/Engine/entities/shadow_caster_cutout.gdshader",
	};

	public override void _Ready()
	{
		int bad = 0;
		// The mobile lite builds too: the same bodies under other defines, so each can break alone.
		foreach (string path in System.Linq.Enumerable.Concat(Shaders, Refii.Models.Engine_ShaderVariant.AllVariantPaths))
		{
			var shader = Refii.Models.Engine_ShaderSpecialisation.LoadFile(path);
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
			// A shader that declares no material uniform (e.g. `instance uniform`-only or empty
			// overlays) has nothing for GetShaderUniformList() to count; parse errors still print.
			int uniforms = shader.GetShaderUniformList().Count;
			bool hasMaterialUniform = System.Text.RegularExpressions.Regex.IsMatch(
				shader.Code,
				@"(?m)^\s*uniform\b",
				System.Text.RegularExpressions.RegexOptions.CultureInvariant);
			if (uniforms == 0 && hasMaterialUniform)
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
