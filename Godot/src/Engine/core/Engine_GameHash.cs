using System;

namespace Refii.Core;

/// <summary>
/// The name hashes Fable 2 uses. There are several, they are NOT interchangeable,
/// and mixing them produces silent garbage rather than an error — so each one here
/// documents exactly which engine function it mirrors.
///
/// <list type="bullet">
/// <item><see cref="Fnv1"/> — Babel text keys, GDB record ids, Lua permanents.
///   Binary: Fnv1HashWithBasis @ 0x821F3D58.</item>
/// <item><see cref="Fnv1Lower"/> — the LHUtil/GUI variant, same algorithm but case-folded.
///   Binary: LHUtil_FNVHashLower @ 0x825D6B60.</item>
/// <item><see cref="ResourcePath"/> — texture and resource paths. FNV-1 over the
///   lowercased path. Binary: sub_822CA588.</item>
/// <item><see cref="Sdbm"/> — Autodesk/Anark Gameface element and attribute names.
///   A different family entirely. Binary: sub_82189140 @ 0x82189140.</item>
/// </list>
///
/// The two traps worth stating plainly, because both are silent:
/// FNV-1 does NOT hash the terminating NUL; sdbm DOES. And FNV-1 loads bytes
/// zero-extended while sdbm sign-extends them, so any byte >= 0x80 diverges.
/// </summary>
public static class Engine_GameHash
{
	public const uint Fnv1Basis = 0x811C9DC5u;
	public const uint Fnv1Prime = 0x01000193u;

	/// <summary>sdbm ids are masked to 27 bits before use as a Gameface attribute id.</summary>
	public const uint AttributeIdMask = 0x07FFFFFFu;

	/// <summary>Gameface's hash gives up after this many characters.</summary>
	private const int SdbmMaxChars = 100;

	/// <summary>
	/// FNV-1 (multiply THEN xor), 32-bit, basis 0x811C9DC5. Case sensitive.
	/// Bytes are zero-extended and the terminating NUL is not hashed.
	/// </summary>
	public static uint Fnv1(ReadOnlySpan<byte> bytes)
	{
		uint h = Fnv1Basis;
		foreach (byte b in bytes)
			h = (h * Fnv1Prime) ^ b;
		return h;
	}

	/// <summary>
	/// FNV-1 over the UTF-8 bytes of <paramref name="value"/>. Babel keys are
	/// UPPER_SNAKE_CASE by convention only — this does not fold case, so
	/// "gui_back" and "GUI_BACK" are different keys.
	/// </summary>
	/// <remarks>
	/// Encoded into the stack for a short value (the HUD hashes a few element names every
	/// frame); a long one still gets a byte array.
	/// </remarks>
	public static uint Fnv1(string value)
	{
		const int StackBytes = 256;
		if (value.Length <= StackBytes / 3)
		{
			Span<byte> buffer = stackalloc byte[StackBytes];
			int written = System.Text.Encoding.UTF8.GetBytes(value, buffer);
			return Fnv1(buffer[..written]);
		}

		return Fnv1(System.Text.Encoding.UTF8.GetBytes(value));
	}

	/// <summary>FNV-1 with ASCII case folding, mirroring LHUtil_FNVHashLower.</summary>
	public static uint Fnv1Lower(string value) => Fnv1(value.ToLowerInvariant());

	/// <summary>
	/// Hash of a resource path such as <c>art\gui\frames\frames_04.tex</c>.
	/// The engine lowercases first and keeps backslashes and the extension.
	/// </summary>
	/// <remarks>
	/// The engine also special-cases a 40-character hex basename, parsing its last
	/// eight characters as the id directly. That path is not reproduced here because
	/// no shipped UI asset uses it; add it if GUID-named resources ever turn up.
	/// </remarks>
	public static uint ResourcePath(string path) => Fnv1Lower(path.Replace('/', '\\'));

	/// <summary>
	/// sdbm, seed 0, prime 65599, over sign-extended bytes INCLUDING the terminating
	/// NUL, capped at 100 characters. This is the full 32-bit result; call
	/// <see cref="AttributeId"/> for the masked form the element tables use.
	/// </summary>
	public static uint Sdbm(string value)
	{
		uint h = 0;
		int n = 0;
		foreach (char c in value)
		{
			if (n >= SdbmMaxChars)
				return h;
			// The engine reads bytes with a sign-extending load, so 0x80..0xFF
			// contribute a negative value. Shipped names are ASCII, but be faithful.
			h = unchecked(h * 65599u + (uint)(sbyte)(byte)c);
			n++;
		}
		// The NUL is folded in. Omitting it changes every hash.
		return unchecked(h * 65599u);
	}

	/// <summary>The 27-bit id a Gameface element stores for an attribute name.</summary>
	public static uint AttributeId(string name) => Sdbm(name) & AttributeIdMask;
}
