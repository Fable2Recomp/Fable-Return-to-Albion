using Godot;

namespace Refii.Fx;

/// <summary>
/// The halo publisher on a phone whose post chain does not composite (<see cref="Refii.Lighting.Engine_PostFxUnseen"/>):
/// no sampling, no sprite decode and no array rebuild, since only the chain draws halos.
/// </summary>
public partial class Engine_PostFxHalos
{
	/// <summary>
	/// True when this frame's halos go unbuilt. The registry is still pruned of freed lights, so it
	/// does not grow while skipped, and nothing stays published for a chain that may come back.
	/// </summary>
	private static bool SkipUnseen()
	{
		if (!Refii.Lighting.Engine_PostFxUnseen.Now)
		{
			return false;
		}

		using (Gate.Hold())
		{
			for (int i = Entries.Count - 1; i >= 0; i--)
			{
				if (!GodotObject.IsInstanceValid(Entries[i].Light))
				{
					Entries.RemoveAt(i);
				}
			}

			if (Published.Count != 0)
			{
				Published.Clear();
			}
		}

		return true;
	}
}
