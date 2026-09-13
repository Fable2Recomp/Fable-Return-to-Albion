using System;
using System.IO;
using Refii.Launcher.Stfs;

namespace Refii.Launcher.Core;

/// <summary>
/// The game's title update, resolved from whatever the player handed over: the bare
/// <c>default.xexp</c>, or the LIVE package it ships in under any name.
/// </summary>
/// <remarks>
/// Whether the update fits the disc is decided by the patch itself, never by the package
/// header or the file name: the patch's 16-byte media id must equal the executable's, and its
/// source version must be the executable's version. A patch for another pressing carries the
/// same title id and the same file name and is rejected by the loader at run time, long after
/// the install, so it is rejected here first.
/// </remarks>
public sealed class TitleUpdate
{
	private const int HeadLength = 64 * 1024;
	private const string PatchExtension = ".xexp";

	public string SourcePath = string.Empty;

	/// <summary>"patch file" or "title update package", for messages.</summary>
	public string SourceKind = string.Empty;

	/// <summary>The patch bytes, whole.</summary>
	public byte[] Patch = Array.Empty<byte>();

	public XexIdentity Identity = new();

	public string Describe() =>
		"title update " + Identity.SourceVersionText + " to " + Identity.VersionText +
		" for media ID " + Identity.ShortMediaIdText;

	/// <summary>
	/// Loads the patch from a path that is either a .xexp or an STFS package holding one.
	/// </summary>
	public static bool TryLoad(string path, out TitleUpdate update, out string error)
	{
		update = new TitleUpdate { SourcePath = path };
		error = string.Empty;

		if (!File.Exists(path))
		{
			error = "The selected title update does not exist.";
			return false;
		}

		byte[] head;
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			head = new byte[Math.Min(HeadLength, stream.Length)];
			int total = 0;
			while (total < head.Length)
			{
				int read = stream.Read(head, total, head.Length - total);
				if (read <= 0)
				{
					break;
				}

				total += read;
			}
		}
		catch (Exception exception)
		{
			error = exception.Message;
			return false;
		}

		if (XexIdentity.IsXex2(head))
		{
			update.SourceKind = "patch file";
			try
			{
				update.Patch = File.ReadAllBytes(path);
			}
			catch (Exception exception)
			{
				error = exception.Message;
				return false;
			}
		}
		else if (StfsPackage.IsPackage(head))
		{
			update.SourceKind = "title update package";
			if (!StfsPackage.TryReadFile(path, IsPatchEntry, out StfsPackage.Entry? entry,
					out update.Patch, out string packageError))
			{
				error = "The package holds no .xexp: " + packageError;
				return false;
			}
		}
		else
		{
			error = "The selected file is neither a .xexp nor a title update package (LIVE / PIRS / CON).";
			return false;
		}

		if (!XexIdentity.TryRead(update.Patch, out update.Identity, out string xexError))
		{
			error = "The " + update.SourceKind + " is not a usable patch: " + xexError;
			return false;
		}

		if (!update.Identity.IsPatch)
		{
			error = "The selected file is a full executable, not a title update patch.";
			return false;
		}

		return true;
	}

	/// <summary>
	/// The patch inside a package is <c>default.xexp</c>; a package with a single .xexp under
	/// another name is accepted too. Directories and sibling banks are not.
	/// </summary>
	private static bool IsPatchEntry(StfsPackage.Entry entry) =>
		entry.Name.EndsWith(PatchExtension, StringComparison.OrdinalIgnoreCase);

	/// <summary>Whether this patch applies to the given base executable.</summary>
	public bool AppliesTo(XexIdentity executable, out string error)
	{
		error = string.Empty;

		if (!Identity.SamePressingAs(executable))
		{
			error = "The title update is for a different pressing of the game: it names media ID " +
					Identity.ShortMediaIdText + " but the disc image is media ID " +
					executable.ShortMediaIdText + ". Fetch the title update for media ID " +
					executable.ShortMediaIdText + ".";
			return false;
		}

		if (Identity.SourceVersion != executable.Version)
		{
			error = "The title update patches version " + Identity.SourceVersionText +
					" but the disc image is version " + executable.VersionText + ".";
			return false;
		}

		return true;
	}

	/// <summary>Reads the identity of a base executable from a file on disk.</summary>
	public static bool TryReadExecutable(string path, out XexIdentity identity, out string error)
	{
		identity = new XexIdentity();
		error = string.Empty;

		byte[] head;
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			head = new byte[Math.Min(HeadLength, stream.Length)];
			int total = 0;
			while (total < head.Length)
			{
				int read = stream.Read(head, total, head.Length - total);
				if (read <= 0)
				{
					break;
				}

				total += read;
			}
		}
		catch (Exception exception)
		{
			error = exception.Message;
			return false;
		}

		return TryReadExecutable(head, out identity, out error);
	}

	/// <summary>Reads the identity of a base executable from its leading bytes.</summary>
	public static bool TryReadExecutable(ReadOnlySpan<byte> head, out XexIdentity identity, out string error)
	{
		if (!XexIdentity.TryRead(head, out identity, out error))
		{
			return false;
		}

		if (identity.IsPatch)
		{
			error = "The executable is a patch, not the game's base image.";
			return false;
		}

		return true;
	}
}
