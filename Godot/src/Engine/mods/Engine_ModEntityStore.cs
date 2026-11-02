using System.Collections.Generic;

namespace Refii.Mods;

/// <summary>
/// A VALUE PER ENTITY HANDLE for Godot scripts (<c>RefiiHost.entity_value / entity_set</c>), by the
/// rule Lua's <c>Refii.entity_value / entity_set</c> keeps (<see cref="ModApi.EntityStoreMeaning"/>):
/// setting nothing clears the key, an entity's values are forgotten at its <c>entity_removed</c>
/// (<see cref="Forget"/>). Each surface holds its own values — a Lua value and a Godot Variant do not
/// cross; what both must read goes in the player store or a published value. Pure.
/// </summary>
/// <typeparam name="T">The value type: the host's Variant, a test's object.</typeparam>
public sealed class ModEntityStore<T>
{
	private readonly Dictionary<long, Dictionary<string, T>> _values = new();

	/// <summary>Entities holding a value: the host hears entity_removed while this is above 0.</summary>
	public int Count => _values.Count;

	/// <summary>The value under <paramref name="key"/> for <paramref name="handle"/>, or <paramref name="fallback"/>.</summary>
	public T Value(long handle, string key, T fallback) =>
		_values.TryGetValue(handle, out Dictionary<string, T>? values) && values.TryGetValue(key, out T? value) ? value : fallback;

	/// <summary>Sets the value, or clears the key when <paramref name="clear"/>; handle 0 and an empty key hold nothing.</summary>
	public void Set(long handle, string key, T value, bool clear)
	{
		if (handle == 0 || string.IsNullOrEmpty(key))
		{
			return;
		}

		if (!_values.TryGetValue(handle, out Dictionary<string, T>? values))
		{
			if (clear)
			{
				return;
			}

			values = new Dictionary<string, T>();
			_values[handle] = values;
		}

		if (clear)
		{
			values.Remove(key);
			if (values.Count == 0)
			{
				_values.Remove(handle);
			}

			return;
		}

		values[key] = value;
	}

	/// <summary>The entity is gone (entity_removed): its values with it.</summary>
	public void Forget(long handle) => _values.Remove(handle);

	public void Clear() => _values.Clear();
}
