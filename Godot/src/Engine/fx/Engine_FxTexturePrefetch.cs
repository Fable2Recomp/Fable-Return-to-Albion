using System;
using System.Collections.Generic;

namespace Refii.Fx;

/// <summary>
/// The hand-off between a background decode of the particle bank's textures and the main
/// thread's first use of each: what to prefetch, and a store that gives every decoded item out
/// exactly once. No Godot type, so the contract is testable without the runtime.
/// </summary>
/// <remarks>
/// WHY A STORE AND NOT A CACHE. The main thread's own load path is unchanged and still wins a
/// race: an effect that names a texture before the prefetch has reached it loads synchronously
/// as before, and <see cref="MarkLoaded"/> then drops the prefetched copy so it is neither
/// applied twice nor held for the rest of the session. A taken item leaves the store, so the
/// decoded bytes live only until the texture object exists.
/// </remarks>
public sealed class Engine_FxPrefetchStore<T> where T : class
{
	private readonly object _gate = new();
	private readonly Dictionary<string, T> _ready = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<string> _loaded = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>Whether the prefetch should still decode this name: not loaded, not already ready.</summary>
	public bool Wanted(string name)
	{
		lock (_gate)
		{
			return !_loaded.Contains(name) && !_ready.ContainsKey(name);
		}
	}

	/// <summary>
	/// Hands a decoded item in. False when the main thread loaded the name meanwhile (the item
	/// is dropped) or the name is already held.
	/// </summary>
	public bool Offer(string name, T item)
	{
		lock (_gate)
		{
			if (_loaded.Contains(name) || _ready.ContainsKey(name))
			{
				return false;
			}

			_ready[name] = item;
			return true;
		}
	}

	/// <summary>Takes the decoded item for a name, once; the name counts as loaded from here on.</summary>
	public bool TryTake(string name, out T item)
	{
		lock (_gate)
		{
			if (_ready.Remove(name, out T? held))
			{
				_loaded.Add(name);
				item = held;
				return true;
			}

			item = null!;
			return false;
		}
	}

	/// <summary>The main thread loaded (or failed) this name itself; any prefetched copy is dropped.</summary>
	public void MarkLoaded(string name)
	{
		lock (_gate)
		{
			_loaded.Add(name);
			_ready.Remove(name);
		}
	}

	/// <summary>Decoded items waiting to be taken.</summary>
	public int Pending
	{
		get
		{
			lock (_gate)
			{
				return _ready.Count;
			}
		}
	}
}

public static class Engine_FxTexturePrefetch
{
	/// <summary>
	/// Every texture the bank's visuals name, each once, in first-seen order — the keys
	/// <c>FableFx.LoadTexture</c> is called with (the material key's texture segment is the
	/// visual's <see cref="FxVisual.Texture"/> verbatim). Mesh-only visuals name none.
	/// </summary>
	public static List<string> Names(FxBank bank)
	{
		var names = new List<string>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (FxSystemDef system in bank.Systems)
		{
			foreach (FxVisual visual in system.Visuals)
			{
				string texture = visual.Texture;
				if (texture.Length != 0 && seen.Add(texture))
				{
					names.Add(texture);
				}
			}
		}

		return names;
	}
}
