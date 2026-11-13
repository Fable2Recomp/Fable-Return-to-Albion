using Godot;

namespace Refii.Titles.Fable2;

using Refii.Ui;

/// <summary>
/// The title screen's logo flash, driven by Godot.
///
/// WHY THIS EXISTS. `frontendstartupscreen.bgf` carries an element named `White`, a sibling of
/// `fe_f2logo` sharing its material (`Material578`), sitting inside the logo's own effect group:
///
///     logo / fe_f2logo / Material440      Component  → FXGUI_Logomain
///     White            / Material578      Component2 → FXGUI_Logomain_Fadein
///                                         Component3 → FXGUI_Logomain_Ambient
///
/// It is the white flash of the logo fade-in, authored at full opacity and animated down. Its
/// slide command is a tween START — the same thing the gender card's nine `choosecard` slides
/// turned out to be — so a builder that applies slide values statically leaves it at 1.0
/// forever. The result is a full-canvas white plate at z0, in front of the panorama at z-990,
/// with the real logo painted on top of it in white-on-white. That is the entire "the title
/// screen is a solid white plane" symptom.
///
/// NOTHING IN THE SHIPPED LUA DRIVES IT. `frontend.lua` handles the PressStart/Title slides and
/// moves `IndicatorButton`; it never names `White`, the logo, or an opacity on either. So there
/// is no script to port here — the animation lived in the Anark scene, and with Godot owning the
/// UI the fade is ours to run.
///
/// FOUND BY NAME EVERY FRAME, deliberately. The title screen is REBUILT whenever its slide
/// changes — the log shows its quad count moving 11 → 12 → 11 as the logos play — and a cached
/// node reference would point at a freed tree after the first of those. The screen is about
/// sixteen quads, so a lookup is cheaper than the bookkeeping to avoid it.
/// </summary>
public sealed class Engine_FrontEndTitle
{
	/// <summary>The flash element, as `frontendstartupscreen.bgf` names it.</summary>
	public const string FlashName = "White";

	/// <summary>
	/// How long the flash takes to clear.
	///
	/// OURS, AND SAID SO. The tween's own duration lives in the Anark scene's animation data,
	/// which our reader surfaces as the post-command value rather than a curve — so this is
	/// authored here rather than recovered, and it is the one number in this file that is not
	/// the game's. It is short because the flash is a fade-IN accent on a logo that is already
	/// on screen, not a transition the player waits through.
	/// </summary>
	public const float FadeSeconds = 0.6f;

	private float _elapsed;

	/// <summary>What the last <see cref="Advance"/> did, for the log.</summary>
	public string LastReport { get; private set; } = "not driven yet";

	/// <summary>True once the flash has been found and taken to zero.</summary>
	public bool Finished { get; private set; }

	/// <summary>Current normalized flash alpha (0..1), synced to the owned book's HostOpacity.</summary>
	public float CurrentAlpha { get; private set; } = 1f;

	/// <summary>Restarts the fade — the title screen has come up again.</summary>
	public void Reset()
	{
		_elapsed = 0f;
		Finished = false;
		CurrentAlpha = 1f;
		LastReport = "restarted";
	}

	/// <summary>
	/// One frame of the fade.
	///
	/// Writes the material's albedo alpha directly, which is where
	/// <see cref="Engine_UiSceneBuilder"/> puts accumulated opacity — so this drives the same
	/// value the slide would have driven, by the same route, and nothing else on the screen is
	/// touched.
	/// </summary>
	/// <summary>
	/// One frame of the fade.
	///
	/// TWO ASSUMPTIONS WERE REMOVED HERE, both of which had gone untested. The first version
	/// required the catalogue to hand back a <see cref="MeshInstance3D"/> for the name; the
	/// catalogue is keyed by element name and may perfectly well hold the GROUP, whose meshes
	/// are children. It also ran only while a screen whose name matched a hardcoded constant was
	/// showing, which made a string comparison a silent precondition of the whole feature. Now
	/// it fades whatever meshes hang off a node called <see cref="FlashName"/>, whenever one
	/// exists — no screen-name test, and no assumption about the catalogue's node type.
	/// </summary>
	public void Advance(Engine_UiTheme? theme, double delta) =>
		Advance(theme?.ArtSourceNode(FlashName), delta);

	/// <summary>
	/// The flash node in hand, for a caller that OWNS the title screen rather than mirroring it.
	///
	/// With the owned book drawing the title, the catalogue above describes a tree nobody is
	/// looking at — fading it would fade the hidden mirror while the white plate stayed on screen
	/// in the clone. The book knows which of its nodes came from `White`; this takes that one.
	/// </summary>
	public void Advance(Node? flash, double delta)
	{
		if (flash is null)
		{
			LastReport = $"'{FlashName}' is not in this screen's catalogue";
			return;
		}

		_elapsed += (float)delta;

		// Clamped rather than wrapped, and applied every frame even once finished: the screen is
		// rebuilt on every slide change, and a rebuild restores the authored 1.0. Setting it
		// once would put the white plate straight back the next time the logos advance.
		float alpha = FadeSeconds <= 0f
			? 0f
			: Mathf.Clamp(1f - (_elapsed / FadeSeconds), 0f, 1f);
		CurrentAlpha = alpha;

		int faded = Fade(flash, alpha);

		// A STATE, NOT A PER-FRAME VALUE. The caller reports this whenever it changes, so
		// including the live alpha would make it change every frame and print a wall of lines.
		// Three states are all that matter: found nothing, fading, done.
		if (faded == 0)
		{
			LastReport = $"'{FlashName}' found but carries no mesh to fade";
			return;
		}

		if (alpha > 0f)
		{
			LastReport = $"'{FlashName}': fading {faded} quad(s) over {FadeSeconds:F2}s";
			return;
		}

		Finished = true;
		LastReport = $"'{FlashName}': {faded} quad(s) faded out";
	}

	/// <summary>Writes the alpha onto every mesh in the subtree. Returns how many it touched.</summary>
	private static int Fade(Node node, float alpha)
	{
		int faded = 0;

		// Each kind asked through its own door rather than by a type test: a quad carries its
		// alpha as `opacity`, a label as the alpha of `colour`, and a host-authored panel may
		// carry a plain material.
		if (node is MeshInstance3D mesh)
		{
			Color was = Engine_UiSceneBuilder.TextColour(mesh);

			if (Engine_UiSceneBuilder.SetQuadOpacity(mesh, alpha) ||
				Engine_UiSceneBuilder.SetTextColour(mesh, new Color(was.R, was.G, was.B, alpha)))
			{
				++faded;
			}
			else if ((mesh.MaterialOverride ?? mesh.GetActiveMaterial(0)) is BaseMaterial3D material)
			{
				uint wantLayers = alpha > 0.001f ? 1u : 0u;
				if (mesh.Layers != wantLayers)
				{
					mesh.Layers = wantLayers;
				}
				Color albedo = material.AlbedoColor;
				material.AlbedoColor = new Color(albedo.R, albedo.G, albedo.B, alpha);
				++faded;
			}
		}

		foreach (Node child in node.GetChildren())
		{
			faded += Fade(child, alpha);
		}

		return faded;
	}
}
