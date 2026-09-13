using System;

namespace Refii.Launcher.Core;

/// <summary>
/// What a XEX2 image says about itself: which disc pressing it belongs to and which version
/// it is. Read from the header only, so a few kilobytes of the file are enough.
/// </summary>
/// <remarks>
/// A base executable carries an execution-info header (key 0x00040006: media id, version,
/// base version, title id). A patch (.xexp) has no execution info; it carries a delta
/// descriptor (key 0x000005FF: size, target version, source version) instead. Both carry a
/// security block whose 16-byte XGD2 media id at +0x140 names the pressing; the execution
/// info's 4-byte media id, and the id in a title update's name, is the last dword of it.
/// </remarks>
public sealed class XexIdentity
{
	private const int OptionalHeaderTableOffset = 0x18;
	private const uint KeyExecutionInfo = 0x00040006;
	private const uint KeyDeltaPatchDescriptor = 0x000005FF;
	private const int SecurityMediaIdOffset = 0x140;
	private const int MediaIdLength = 16;

	/// <summary>True for a delta patch, false for a base executable.</summary>
	public bool IsPatch;

	/// <summary>The 16-byte XGD2 media id from the security block.</summary>
	public byte[] MediaId = new byte[MediaIdLength];

	/// <summary>Title id from the execution info; zero on a patch.</summary>
	public uint TitleId;

	/// <summary>A base image's version, or the version a patch produces.</summary>
	public uint Version;

	/// <summary>The version a patch applies to; equals <see cref="Version"/> on a base image.</summary>
	public uint SourceVersion;

	/// <summary>The short media id, as title updates and disc databases print it.</summary>
	public uint ShortMediaId =>
		((uint)MediaId[12] << 24) | ((uint)MediaId[13] << 16) | ((uint)MediaId[14] << 8) | MediaId[15];

	public string ShortMediaIdText => ShortMediaId.ToString("X8");

	public string VersionText => FormatVersion(Version);

	public string SourceVersionText => FormatVersion(SourceVersion);

	/// <summary>xex2_version_t: major:4, minor:4, build:16, qfe:8.</summary>
	public static string FormatVersion(uint version) =>
		(version >> 28) + "." + ((version >> 24) & 0xF) + "." + ((version >> 8) & 0xFFFF) + "." + (version & 0xFF);

	public bool SamePressingAs(XexIdentity other) =>
		MediaId.AsSpan().SequenceEqual(other.MediaId);

	public static bool IsXex2(ReadOnlySpan<byte> head) =>
		head.Length >= 4 && head[0] == (byte)'X' && head[1] == (byte)'E' && head[2] == (byte)'X' && head[3] == (byte)'2';

	public static bool TryRead(ReadOnlySpan<byte> image, out XexIdentity identity, out string error)
	{
		identity = new XexIdentity();
		error = string.Empty;

		if (!IsXex2(image))
		{
			error = "The file is not a XEX2 image.";
			return false;
		}

		if (image.Length < OptionalHeaderTableOffset)
		{
			error = "The XEX header is truncated.";
			return false;
		}

		uint securityOffset = ReadUInt32BE(image, 0x10);
		uint headerCount = ReadUInt32BE(image, 0x14);
		if (headerCount > 0x100)
		{
			error = "The XEX header count is implausible.";
			return false;
		}

		if (securityOffset + SecurityMediaIdOffset + MediaIdLength > image.Length)
		{
			error = "The XEX security block is beyond the bytes read.";
			return false;
		}

		image.Slice((int)securityOffset + SecurityMediaIdOffset, MediaIdLength).CopyTo(identity.MediaId);

		bool sawExecutionInfo = false;
		bool sawDelta = false;
		for (uint i = 0; i < headerCount; i++)
		{
			int at = OptionalHeaderTableOffset + (int)i * 8;
			if (at + 8 > image.Length)
			{
				error = "The XEX optional header table is truncated.";
				return false;
			}

			uint key = ReadUInt32BE(image, at);
			uint value = ReadUInt32BE(image, at + 4);

			if (key == KeyExecutionInfo)
			{
				if (value + 16 > image.Length)
				{
					error = "The XEX execution info is beyond the bytes read.";
					return false;
				}

				identity.Version = ReadUInt32BE(image, (int)value + 4);
				identity.SourceVersion = identity.Version;
				identity.TitleId = ReadUInt32BE(image, (int)value + 12);
				sawExecutionInfo = true;
			}
			else if (key == KeyDeltaPatchDescriptor)
			{
				if (value + 12 > image.Length)
				{
					error = "The XEX delta descriptor is beyond the bytes read.";
					return false;
				}

				identity.Version = ReadUInt32BE(image, (int)value + 4);
				identity.SourceVersion = ReadUInt32BE(image, (int)value + 8);
				sawDelta = true;
			}
		}

		identity.IsPatch = sawDelta;
		if (!sawDelta && !sawExecutionInfo)
		{
			error = "The XEX carries neither execution info nor a delta descriptor.";
			return false;
		}

		return true;
	}

	private static uint ReadUInt32BE(ReadOnlySpan<byte> data, int at) =>
		((uint)data[at] << 24) | ((uint)data[at + 1] << 16) | ((uint)data[at + 2] << 8) | data[at + 3];
}
