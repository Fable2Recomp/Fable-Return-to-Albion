using System;

namespace Refii.Mods;

/// <summary>
/// A set of event kinds as the host states it to the payload: <see cref="ModApi.HostMaskWords"/>
/// words of 64, word w holding kinds 64w..64w+63 (bit = kind − 64w), the layout the payload keeps
/// (mod_values.cpp). Words 0 and 1 cross through the two mask exports, the words past them
/// through the tick's <c>mask_word</c> command; <see cref="Refii.Host.HostBridge.SetModEventMask"/>
/// and <see cref="Refii.Host.HostBridge.SetModVerdictMask"/> send every word. A kind at or past
/// <see cref="Capacity"/> is never in the set.
/// </summary>
public readonly struct ModEventMask : IEquatable<ModEventMask>
{
	public const int Words = ModApi.HostMaskWords;
	public const int Capacity = ModApi.HostMaskCapacity;

	private readonly ulong _w0;
	private readonly ulong _w1;
	private readonly ulong _w2;
	private readonly ulong _w3;

	private ModEventMask(ulong w0, ulong w1, ulong w2, ulong w3)
	{
		_w0 = w0;
		_w1 = w1;
		_w2 = w2;
		_w3 = w3;
	}

	/// <summary>True when no kind is in the set.</summary>
	public bool IsZero => (_w0 | _w1 | _w2 | _w3) == 0;

	/// <summary>Word <paramref name="index"/> (0..<see cref="Words"/>−1); 0 outside.</summary>
	public ulong Word(int index) => index switch
	{
		0 => _w0,
		1 => _w1,
		2 => _w2,
		3 => _w3,
		_ => 0,
	};

	/// <summary>Whether <paramref name="kind"/> is in the set.</summary>
	public bool Has(int kind) => kind >= 0 && kind < Capacity && ((Word(kind / 64) >> (kind % 64)) & 1) != 0;

	/// <summary>The set with <paramref name="kind"/> added; unchanged for a kind outside 0..<see cref="Capacity"/>−1.</summary>
	public ModEventMask With(int kind)
	{
		if (kind < 0 || kind >= Capacity)
		{
			return this;
		}

		ulong bit = 1ul << (kind % 64);
		return (kind / 64) switch
		{
			0 => new ModEventMask(_w0 | bit, _w1, _w2, _w3),
			1 => new ModEventMask(_w0, _w1 | bit, _w2, _w3),
			2 => new ModEventMask(_w0, _w1, _w2 | bit, _w3),
			_ => new ModEventMask(_w0, _w1, _w2, _w3 | bit),
		};
	}

	public bool Equals(ModEventMask other) => _w0 == other._w0 && _w1 == other._w1 && _w2 == other._w2 && _w3 == other._w3;

	public override bool Equals(object? obj) => obj is ModEventMask other && Equals(other);

	public override int GetHashCode() => HashCode.Combine(_w0, _w1, _w2, _w3);

	public static bool operator ==(ModEventMask a, ModEventMask b) => a.Equals(b);

	public static bool operator !=(ModEventMask a, ModEventMask b) => !a.Equals(b);
}
