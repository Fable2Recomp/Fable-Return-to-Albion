using Refii.Bnk;

namespace Refii.Assets;

/// <summary>What the Android diagnostics report reads from the mounted game.</summary>
public partial class Engine_GameAssets
{
	/// <summary>The nested-bank cache counters, for the Android diagnostics report; null before mounting.</summary>
	public BnkNestedCacheSnapshot? NestedBankCache => IsMounted ? BnkNestedCacheSnapshot.Current() : null;
}
