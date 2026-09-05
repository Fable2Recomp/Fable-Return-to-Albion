using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Refii.Achievements;

/// <summary>
/// The unlock set and its file. One record per (user, achievement), stamped with a monotonic
/// sequence and the time it was earned.
///
/// THE FILE IS THE NATIVE RUNTIME'S `REFIIACHV1`, byte for byte, so every unlock earned before
/// the move reads back: four text lines — the header, `salt=` (16 hex), `payload=` (hex) and
/// `tag=` (64 hex). The payload is XOR'd with a SplitMix64 keystream seeded by
/// FNV-1a(key ‖ salt) and authenticated by four FNV-1a tags over (seed ‖ key ‖ salt ‖ payload);
/// decoded it is `refii-achievements-v1` then one `user id sequence [unixSeconds]` line per
/// unlock. The fourth field is new and optional — the native writer never had a time.
/// </summary>
public sealed class Engine_AchievementStore
{
	public readonly record struct Unlock(
		uint UserIndex, uint AchievementId, ulong Sequence, long UnlockedUnixSeconds);

	private const string FileHeader = "REFIIACHV1";
	private const string PayloadHeader = "refii-achievements-v1";

	private static readonly ulong[] TagSeeds =
	{
		0x6a9d2f94b4f2a145ul, 0xd44f4f4a6d5a3f9bul, 0x928f7d9e91c3bb23ul, 0x3f38e2f5bd789573ul,
	};

	private static readonly byte[] Key =
	{
		0x8a, 0x38, 0xe6, 0x61, 0x47, 0x2c, 0x9f, 0x19, 0x31, 0xb4, 0x67,
		0xd2, 0x5b, 0x08, 0xf0, 0xae, 0xca, 0x73, 0x14, 0x92, 0xef, 0x5d,
		0x20, 0x6b, 0x7c, 0xd9, 0x03, 0xa6, 0x41, 0xbe, 0x88, 0x55,
	};

	private readonly Dictionary<ulong, Unlock> _unlocks = new();

	private ulong _nextSequence = 1;

	public IReadOnlyCollection<Unlock> Unlocks => _unlocks.Values;

	public int Count => _unlocks.Count;

	public bool IsUnlocked(uint achievementId)
	{
		foreach (Unlock unlock in _unlocks.Values)
		{
			if (unlock.AchievementId == achievementId)
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>The earliest record for an id across users, or null while locked.</summary>
	public Unlock? Get(uint achievementId)
	{
		Unlock? found = null;
		foreach (Unlock unlock in _unlocks.Values)
		{
			if (unlock.AchievementId == achievementId &&
				(found is null || unlock.Sequence < found.Value.Sequence))
			{
				found = unlock;
			}
		}

		return found;
	}

	/// <summary>Records an unlock. False when the pair was already in the set.</summary>
	public bool Add(uint userIndex, uint achievementId, long unlockedUnixSeconds)
	{
		if (achievementId == 0)
		{
			return false;
		}

		ulong key = ((ulong)userIndex << 32) | achievementId;
		if (_unlocks.ContainsKey(key))
		{
			return false;
		}

		_unlocks[key] = new Unlock(userIndex, achievementId, _nextSequence++, unlockedUnixSeconds);
		return true;
	}

	public static Engine_AchievementStore Load(string path)
	{
		var store = new Engine_AchievementStore();
		string[] lines;
		try
		{
			if (!File.Exists(path))
			{
				return store;
			}

			lines = File.ReadAllLines(path);
		}
		catch (Exception)
		{
			return store;
		}

		if (lines.Length < 4 || lines[0] != FileHeader ||
			!lines[1].StartsWith("salt=", StringComparison.Ordinal) ||
			!lines[2].StartsWith("payload=", StringComparison.Ordinal) ||
			!lines[3].StartsWith("tag=", StringComparison.Ordinal))
		{
			return store;
		}

		// The salt is the integer's own hex (`HexEncode64`, big-endian digits); the stream and
		// the tags take its little-endian BYTES.
		string saltHex = lines[1][5..];
		if (saltHex.Length != 16 ||
			!ulong.TryParse(saltHex, System.Globalization.NumberStyles.HexNumber, null, out ulong salt) ||
			HexDecode(lines[2][8..]) is not { } encoded)
		{
			return store;
		}

		if (TagHex(salt, encoded) != lines[3][4..])
		{
			return store;
		}

		store.ParsePayload(XorWithStream(encoded, salt));
		return store;
	}

	public void Save(string path)
	{
		byte[] payload = BuildPayload();
		ulong salt = MakeSalt();
		byte[] encoded = XorWithStream(payload, salt);

		var text = new StringBuilder();
		text.Append(FileHeader).Append('\n');
		text.Append("salt=").Append(salt.ToString("x16")).Append('\n');
		text.Append("payload=").Append(HexEncode(encoded)).Append('\n');
		text.Append("tag=").Append(TagHex(salt, encoded)).Append('\n');

		Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
		File.WriteAllText(path, text.ToString());
	}

	private byte[] BuildPayload()
	{
		var text = new StringBuilder();
		text.Append(PayloadHeader).Append('\n');
		foreach (Unlock unlock in _unlocks.Values.OrderBy(u => u.Sequence))
		{
			text.Append(unlock.UserIndex).Append(' ')
				.Append(unlock.AchievementId).Append(' ')
				.Append(unlock.Sequence);
			if (unlock.UnlockedUnixSeconds != 0)
			{
				text.Append(' ').Append(unlock.UnlockedUnixSeconds);
			}

			text.Append('\n');
		}

		return Encoding.ASCII.GetBytes(text.ToString());
	}

	private void ParsePayload(byte[] payload)
	{
		string[] lines = Encoding.ASCII.GetString(payload).Split('\n');
		if (lines.Length == 0 || lines[0] != PayloadHeader)
		{
			return;
		}

		for (int i = 1; i < lines.Length; i++)
		{
			string[] fields = lines[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (fields.Length < 3 ||
				!uint.TryParse(fields[0], out uint user) ||
				!uint.TryParse(fields[1], out uint id) ||
				!ulong.TryParse(fields[2], out ulong sequence) || id == 0)
			{
				continue;
			}

			long when = fields.Length >= 4 && long.TryParse(fields[3], out long stamp) ? stamp : 0;
			if (sequence == 0)
			{
				sequence = _nextSequence++;
			}

			ulong key = ((ulong)user << 32) | id;
			if (_unlocks.ContainsKey(key))
			{
				continue;
			}

			_nextSequence = Math.Max(_nextSequence, sequence + 1);
			_unlocks[key] = new Unlock(user, id, sequence, when);
		}
	}

	private ulong MakeSalt()
	{
		ulong salt = (ulong)DateTime.UtcNow.Ticks;
		salt ^= (ulong)_unlocks.Count * 0x9e3779b97f4a7c15ul;
		return SplitMix64(ref salt);
	}

	private static ulong StreamSeed(ulong salt)
	{
		ulong hash = 14695981039346656037ul;
		hash = Fnv1a64(hash, Key);
		hash = Fnv1a64(hash, BitConverter.GetBytes(salt));
		return hash;
	}

	private static byte[] XorWithStream(byte[] input, ulong salt)
	{
		var output = (byte[])input.Clone();
		ulong state = StreamSeed(salt);
		ulong block = 0;
		int index = 8;
		for (int i = 0; i < output.Length; i++)
		{
			if (index >= 8)
			{
				block = SplitMix64(ref state);
				index = 0;
			}

			output[i] ^= (byte)((block >> (index * 8)) & 0xFF);
			index++;
		}

		return output;
	}

	private static string TagHex(ulong salt, byte[] encoded)
	{
		byte[] saltBytes = BitConverter.GetBytes(salt);
		var text = new StringBuilder(64);
		foreach (ulong seed in TagSeeds)
		{
			ulong hash = seed;
			hash = Fnv1a64(hash, Key);
			hash = Fnv1a64(hash, saltBytes);
			hash = Fnv1a64(hash, encoded);
			text.Append(hash.ToString("x16"));
		}

		return text.ToString();
	}

	private static ulong Fnv1a64(ulong hash, byte[] bytes)
	{
		const ulong Prime = 1099511628211ul;
		foreach (byte b in bytes)
		{
			hash ^= b;
			hash *= Prime;
		}

		return hash;
	}

	private static ulong SplitMix64(ref ulong state)
	{
		state += 0x9e3779b97f4a7c15ul;
		ulong value = state;
		value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9ul;
		value = (value ^ (value >> 27)) * 0x94d049bb133111ebul;
		return value ^ (value >> 31);
	}

	private static string HexEncode(byte[] bytes)
	{
		var text = new StringBuilder(bytes.Length * 2);
		foreach (byte b in bytes)
		{
			text.Append(b.ToString("x2"));
		}

		return text.ToString();
	}

	private static byte[]? HexDecode(string hex)
	{
		if (hex.Length % 2 != 0)
		{
			return null;
		}

		var bytes = new byte[hex.Length / 2];
		for (int i = 0; i < bytes.Length; i++)
		{
			int high = Nibble(hex[2 * i]);
			int low = Nibble(hex[(2 * i) + 1]);
			if (high < 0 || low < 0)
			{
				return null;
			}

			bytes[i] = (byte)((high << 4) | low);
		}

		return bytes;
	}

	private static int Nibble(char c) => c switch
	{
		>= '0' and <= '9' => c - '0',
		>= 'a' and <= 'f' => c - 'a' + 10,
		>= 'A' and <= 'F' => c - 'A' + 10,
		_ => -1,
	};
}
