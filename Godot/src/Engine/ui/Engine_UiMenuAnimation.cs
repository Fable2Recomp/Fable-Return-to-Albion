using System.Collections.Generic;

namespace Refii.Ui;

/// <summary>
/// `expandablemenuanimation.lua` translated: `g_AnimationList` and its nine calls
/// (`expandablemenuanimation.dis main` pc5..22). Each animation is one property of one element
/// lerped start→end over `ANIMATE_TIME`, clamped at 1, no easing. Every `start_val` is read from
/// the element via `getAttribute` when the set is built, never remembered. `titletextsize` and
/// `titletexttruncate` are routed to `Icon.Title` (`main.5` pc0..15) — see <see cref="Target"/>.
/// </summary>
public static class Engine_UiMenuAnimation
{
	/// <summary>
	/// The seven properties `AnimatePropertiesToIndex` moves on a slot change, in script order
	/// (`main.7` pc65, 86, 121, 141, 160, 185, 210). `fontsize` is 18 in all five style records
	/// (`expandablemenuformatting.dis main` pc21..41) so it animates 18→18; kept so the set is seven.
	/// </summary>
	public static readonly string[] SlotProperties =
	{
		"position.x", "position.y", "scale.x", "scale.y", "opacity", "fontsize", "truncate",
	};

	/// <summary>The label a `fontsize` or `truncate` animation actually writes on.</summary>
	public const string LabelPath = "Icon.Title";

	/// <summary>`fontsize` is renamed on the way to the label; the engine's own name is `size`.</summary>
	public const string FontSizeProperty = "fontsize";

	public const string LabelSizeAttribute = "size";

	public const string TruncateProperty = "truncate";

	/// <summary>
	/// Where one animated property is written: the component itself, or its label.
	/// `truncate` is the label's clip width and varies per slot (200, 210, 240, 305).
	/// </summary>
	public readonly record struct Target(bool OnLabel, string Attribute);

	/// <summary>Resolves a property name to where it is written and under what name.</summary>
	public static Target TargetFor(string property) => property switch
	{
		FontSizeProperty => new Target(true, LabelSizeAttribute),
		TruncateProperty => new Target(true, TruncateProperty),
		_ => new Target(false, property),
	};

	/// <summary>One property in flight — `CreateAnimation`'s record, field for field.</summary>
	public sealed class Animation
	{
		public required string Property { get; init; }
		public required float StartValue { get; init; }
		public required float EndValue { get; init; }
		public required double Duration { get; init; }
		public double StartTime { get; set; }

		/// <summary>`time_delay` — how long after the set starts this one begins.</summary>
		public double TimeDelay { get; init; }

		/// <summary>The element it writes on, as the caller identifies it.</summary>
		public object? Element { get; init; }

		/// <summary>`t`, clamped — the script clamps at 1 and never below 0.</summary>
		public float Progress(double now) => Duration <= 0.0
			? 1f
			: (float)System.Math.Clamp((now - StartTime) / Duration, 0.0, 1.0);

		/// <summary>The value at a point in time.</summary>
		public float ValueAt(double now) =>
			StartValue + ((EndValue - StartValue) * Progress(now));

		/// <summary>True once it has run its course.</summary>
		public bool Done(double now) => Progress(now) >= 1f;
	}

	/// <summary>`CreateAnimationSet` — the animations that move together, plus their callback.</summary>
	public sealed class AnimationSet
	{
		public List<Animation> Animations { get; } = new();

		/// <summary>`callback_func` — run when every animation in the set has finished. The
		/// shipped one is `FlagComponentAsAvailable`, which returns a row to the pool.</summary>
		public System.Action? Completed { get; init; }

		public bool Done(double now)
		{
			foreach (Animation animation in Animations)
			{
				if (!animation.Done(now))
				{
					return false;
				}
			}

			return true;
		}
	}

	/// <summary>
	/// `g_AnimationList` — every set currently running. `WaitForInput()` reads it and
	/// `SelectMenuItem` returns early while it is true, so it is what swallows a press mid-slide.
	/// </summary>
	public sealed class AnimationList
	{
		private readonly List<AnimationSet> _sets = new();

		public IReadOnlyList<AnimationSet> Sets => _sets;

		/// <summary>`PushAnimationSet`.</summary>
		public void Push(AnimationSet set) => _sets.Add(set);

		/// <summary>`StartAnimations` — stamps every animation with the moment it began.</summary>
		public void Start(double now)
		{
			foreach (AnimationSet set in _sets)
			{
				foreach (Animation animation in set.Animations)
				{
					animation.StartTime = now + animation.TimeDelay;
				}
			}
		}

		/// <summary>`WaitForInput()` — true while any animation is past its delay and not yet done.</summary>
		public bool WaitForInput(double now)
		{
			foreach (AnimationSet set in _sets)
			{
				foreach (Animation animation in set.Animations)
				{
					if (now >= animation.StartTime && !animation.Done(now))
					{
						return true;
					}
				}
			}

			return false;
		}

		/// <summary>
		/// `UpdateAnimations` — evaluates every animation and drops the sets that have finished,
		/// running each one's callback as it goes.
		/// </summary>
		public void Update(double now, System.Action<Animation, float> write)
		{
			for (int i = _sets.Count - 1; i >= 0; --i)
			{
				AnimationSet set = _sets[i];
				foreach (Animation animation in set.Animations)
				{
					if (now >= animation.StartTime)
					{
						write(animation, animation.ValueAt(now));
					}
				}

				if (set.Done(now))
				{
					_sets.RemoveAt(i);
					set.Completed?.Invoke();
				}
			}
		}

		/// <summary>`ClearAllAnimations` — a repopulation throws the whole list away.</summary>
		public void Clear() => _sets.Clear();

		public string Describe() => _sets.Count == 0
			? "nothing animating"
			: $"{_sets.Count} set(s) running";
	}
}
