using System;
using Godot;

namespace Refii.Lighting;

/// <summary>
/// A global shader parameter that only one place writes: remembers the last value it gave the
/// renderer and sends a new one only when it differs. Godot's setter has no equality check, so
/// a repeat write still queues a renderer update.
/// </summary>
/// <typeparam name="T">The parameter's value type; compared exactly.</typeparam>
public sealed class Engine_LastWritten<T> where T : struct, IEquatable<T>
{
	private T _value;
	private bool _has;

	/// <summary>True when <paramref name="value"/> is not what was last taken (and now is).</summary>
	public bool Take(T value)
	{
		if (_has && _value.Equals(value))
		{
			return false;
		}

		_value = value;
		_has = true;
		return true;
	}

	/// <summary>Writes <paramref name="name"/> unless it already holds <paramref name="value"/>.</summary>
	public void Set(StringName name, T value)
	{
		if (Take(value))
		{
			RenderingServer.GlobalShaderParameterSet(name, Variant.From(value));
		}
	}
}
