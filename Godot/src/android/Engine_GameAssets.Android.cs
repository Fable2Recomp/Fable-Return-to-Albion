using Refii.Bnk;

namespace Refii.Assets;

/// <summary>What the Android diagnostics report reads from the mounted game.</summary>
public partial class Engine_GameAssets
{
	/// <summary>The nested-bank cache counters, for the Android diagnostics report; null before mounting.</summary>
	public BnkNestedCacheSnapshot? NestedBankCache => IsMounted ? BnkNestedCacheSnapshot.Current() : null;

	/// <summary>The file a read of this path would serve, and the mount it is in, from one snapshot.</summary>
	internal bool TryGetMountedFile(string virtualPath, out BnkFileSystem? mount, out BnkFile? file)
	{
		mount = Snapshot();
		file = null;
		return mount is not null && mount.TryGetFile(Strip(virtualPath), out file!);
	}
}
