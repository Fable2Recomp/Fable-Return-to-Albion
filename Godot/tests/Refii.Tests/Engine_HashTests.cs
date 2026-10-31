using System;
using System.IO;
using System.Security.Cryptography;
using GdUnit4;
using Refii.Core;
using static GdUnit4.Assertions;

namespace Refii.Tests;

/// <summary>
/// The managed hashes the NativeAOT Android export uses (it has no usable OpenSSL) must give
/// the platform's digests, or every recorded module, asset and cache identity breaks.
/// </summary>
[TestSuite]
public sealed class Engine_HashTests
{
	private static byte[] Data(int length, int seed)
	{
		var data = new byte[length];
		new Random(seed).NextBytes(data);
		return data;
	}

	/// <summary>No game or launcher source hashes through System.Security.Cryptography directly: on the
	/// NativeAOT Android build that loads OpenSSL, which the APK lacks, and the process aborts
	/// ("libssl.so.10" not found). It happened twice: the first NativeAOT build, and the phone
	/// launcher's compiler-bundle identity (2026-10-29), which aborted the app at every start.</summary>
	[TestCase]
	public void NoSourceHashesThroughThePlatform()
	{
		string src = Path.GetFullPath(Path.Combine(SourceDirectory(), "..", "..", "src"));
		var calls = new System.Text.RegularExpressions.Regex(@"\b(SHA1|SHA256|SHA384|SHA512|MD5|IncrementalHash)\.(HashData|Create|CreateHash)\b");
		var found = new System.Collections.Generic.List<string>();
		foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
		{
			// Engine_Hash itself, and the desktop-only modding editor download (SHA-512, which
			// Engine_Hash does not carry; the Android launcher never installs the editor).
			if (Path.GetFileName(file) is "Engine_Hash.cs" or "EditorInstall.cs") continue;
			string[] lines = File.ReadAllLines(file);
			for (int i = 0; i < lines.Length; i++)
				if (!lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) && calls.IsMatch(lines[i]))
					found.Add(Path.GetRelativePath(src, file) + ":" + (i + 1));
		}
		AssertThat(string.Join("\n", found)).IsEqual("");
	}

	private static string SourceDirectory([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
		Path.GetDirectoryName(path)!;

	/// <summary>Every padding boundary (55/56/64 bytes and their neighbors) and large inputs, one shot.</summary>
	[TestCase]
	public void OneShotMatchesThePlatform()
	{
		foreach (int length in new[] { 0, 1, 3, 55, 56, 57, 63, 64, 65, 119, 120, 127, 128, 129, 1000, 65536, 1_000_003 })
		{
			byte[] data = Data(length, length);
			var sha256 = new Engine_Hash.ManagedSha256();
			sha256.Append(data);
			AssertThat(Convert.ToHexString(sha256.Finish())).IsEqual(Convert.ToHexString(SHA256.HashData(data)));
			var sha1 = new Engine_Hash.ManagedSha1();
			sha1.Append(data);
			AssertThat(Convert.ToHexString(sha1.Finish())).IsEqual(Convert.ToHexString(SHA1.HashData(data)));
		}

		var abc = new Engine_Hash.ManagedSha256();
		abc.Append("abc"u8);
		AssertThat(Convert.ToHexString(abc.Finish()))
			.IsEqual("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD");
	}

	/// <summary>Uneven appends, reuse after Finish, the HashAlgorithm surface and streams.</summary>
	[TestCase]
	public void IncrementalAndStreamUsesMatchThePlatform()
	{
		byte[] data = Data(10_007, 7);
		var managed = new Engine_Hash.ManagedSha256();
		for (int round = 0; round < 2; round++)
		{
			int at = 0;
			foreach (int size in new[] { 1, 63, 64, 65, 0, 3, 1000, 7, 4096 })
			{
				int take = Math.Min(size, data.Length - at);
				managed.Append(data.AsSpan(at, take));
				at += take;
			}

			managed.Append(data.AsSpan(at));
			AssertThat(Convert.ToHexString(managed.Finish())).IsEqual(Convert.ToHexString(SHA256.HashData(data)));
		}

		using HashAlgorithm algorithm = new Engine_Hash.ManagedSha256();
		algorithm.TransformBlock(data, 0, 100, null, 0);
		algorithm.TransformBlock(data, 100, data.Length - 100, null, 0);
		algorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
		AssertThat(Convert.ToHexString(algorithm.Hash!)).IsEqual(Convert.ToHexString(SHA256.HashData(data)));

		using HashAlgorithm sha1 = new Engine_Hash.ManagedSha1();
		AssertThat(Convert.ToHexString(sha1.ComputeHash(new MemoryStream(data)))).IsEqual(Convert.ToHexString(SHA1.HashData(data)));

		// The desktop takes the platform's path; the answers are the same.
		AssertThat(Engine_Hash.UsesManaged).IsFalse();
		AssertThat(Convert.ToHexString(Engine_Hash.Sha256(new MemoryStream(data)))).IsEqual(Convert.ToHexString(SHA256.HashData(data)));
		using Engine_Hash.Incremental incremental = Engine_Hash.CreateIncrementalSha256();
		incremental.AppendData(data.AsSpan(0, 5));
		incremental.AppendData(data.AsSpan(5));
		AssertThat(Convert.ToHexString(incremental.GetHashAndReset())).IsEqual(Convert.ToHexString(SHA256.HashData(data)));
	}
}
