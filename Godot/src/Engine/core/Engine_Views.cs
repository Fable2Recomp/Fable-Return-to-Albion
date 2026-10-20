using System.Collections.Generic;
using Godot;

namespace Refii.Core;

/// <summary>
/// Every camera the world is drawn through this frame: the window's, and on split screen the
/// second player's view.
/// </summary>
/// <remarks>
/// <para>ONE WORLD, ONE OR TWO VIEWS. Local co-op on split screen draws the window's world through
/// two cameras (Refii.Game.GameSplitView): player one's sits exactly where the window camera is —
/// it copies it every frame — and player two's is <see cref="Second"/>. So "the window's camera
/// plus <see cref="Second"/>" is the whole set, and a system that culls, ranks or budgets by what
/// is in view asks for every view rather than <c>GetViewport().GetCamera3D()</c>: a thing in
/// either player's view is in view.</para>
/// <para>A system that MUST draw per view (an overlay that mirrors a camera into its own
/// viewport, a per-view shader constant) is told which views exist through
/// <see cref="ViewsChanged"/> and builds an instance per view.</para>
/// </remarks>
public static class Engine_Views
{
	/// <summary>Player two's camera while the screen is split, else null.</summary>
	public static Camera3D? Second { get; private set; }

	/// <summary>Player two's view while the screen is split, else null.</summary>
	public static SubViewport? SecondViewport { get; private set; }

	/// <summary>Player one's view while the screen is split, else null (the window draws).</summary>
	public static SubViewport? FirstViewport { get; private set; }

	/// <summary>Whether the world is drawn through two views right now.</summary>
	public static bool Split => Second is { } camera && GodotObject.IsInstanceValid(camera);

	/// <summary>
	/// How the split view divides the window: false side by side (player one left, player two
	/// right), true stacked (player one top, player two bottom). Stated by the split view as it
	/// lays the views out; every half of the UI (Refii.Ui.Engine_UiSeats) follows it.
	/// </summary>
	public static bool Stacked { get; private set; }

	/// <summary>Raised when the screen splits or rejoins.</summary>
	public static event System.Action? ViewsChanged;

	private static readonly List<Camera3D> Scratch = new(2);

	/// <summary>Set by the split view when the screen splits (both non-null) or rejoins (nulls).</summary>
	public static void SetSplit(SubViewport? first, SubViewport? second, Camera3D? secondCamera)
	{
		FirstViewport = first;
		SecondViewport = second;
		Second = secondCamera;
		ViewsChanged?.Invoke();
	}

	/// <summary>Set by the split view when it lays its views out side by side or stacked.</summary>
	public static void SetStacked(bool stacked) => Stacked = stacked;

	/// <summary>
	/// The cameras drawing the world: <paramref name="primary"/> (the window's), or on split
	/// screen player one's view camera in its place followed by player two's. The list is reused;
	/// read it before the next call.
	/// </summary>
	/// <remarks>
	/// While split, the window camera is NOT what player one is drawn through: player one's view
	/// shares its eye but renders a half of the window at the split view's angle rule, so its
	/// frustum differs. A frustum test against the window camera would call
	/// things on player one's screen out of view.
	/// </remarks>
	public static IReadOnlyList<Camera3D> Cameras(Camera3D? primary)
	{
		Scratch.Clear();
		if (Split)
		{
			if (FirstViewport?.GetCamera3D() is { } first && GodotObject.IsInstanceValid(first))
			{
				Scratch.Add(first);
			}
			else if (primary is not null && GodotObject.IsInstanceValid(primary))
			{
				Scratch.Add(primary);
			}

			Scratch.Add(Second!);
			return Scratch;
		}

		if (primary is not null && GodotObject.IsInstanceValid(primary))
		{
			Scratch.Add(primary);
		}

		return Scratch;
	}

	/// <summary>
	/// The camera player one is drawn through: their view's camera while split (same eye as the
	/// window camera, but a half of the window at the split view's angle rule), else the window's.
	/// Anything that culls by a frustum or projects through player one's camera uses this.
	/// </summary>
	public static Camera3D? Primary(Camera3D? window) =>
		Split && FirstViewport?.GetCamera3D() is { } first && GodotObject.IsInstanceValid(first) ? first : window;

	/// <summary>The squared distance from <paramref name="point"/> to the nearest view's eye.</summary>
	public static float NearestEyeDistanceSquared(Camera3D? primary, Vector3 point)
	{
		float best = float.MaxValue;
		foreach (Camera3D camera in Cameras(primary))
		{
			float d = camera.GlobalPosition.DistanceSquaredTo(point);
			if (d < best)
			{
				best = d;
			}
		}

		return best;
	}

	/// <summary>Whether a sphere is inside any view's frustum.</summary>
	public static bool SphereInAnyView(Camera3D? primary, Vector3 centre, float radius)
	{
		foreach (Camera3D camera in Cameras(primary))
		{
			if (SphereInView(camera, centre, radius))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>Whether a sphere is inside one camera's frustum (its planes face outward).</summary>
	public static bool SphereInView(Camera3D camera, Vector3 centre, float radius)
	{
		foreach (Plane plane in camera.GetFrustum())
		{
			if (plane.DistanceTo(centre) > radius)
			{
				return false;
			}
		}

		return true;
	}
}
