namespace Refii.Bnk;

/// <summary>Measurements of the nested-bank cache in <see cref="BnkFileSystem"/>, for diagnostics reports.</summary>
/// <param name="BudgetBytes">The budget that bounds inflated (compressed-in-outer-bank) containers.</param>
/// <param name="ResidentBytes">Inflated container bytes currently retained.</param>
/// <param name="MappedBytes">Container bytes served from the outer banks' memory maps; outside the budget.</param>
/// <param name="Loads">Container loads, including reloads after eviction.</param>
/// <param name="Evictions">Inflated containers dropped to enforce the budget.</param>
public sealed record BnkNestedCacheSnapshot(long BudgetBytes, long ResidentBytes, long MappedBytes,
    long Loads, long Evictions)
{
    /// <summary>Reads the process-wide counters <see cref="BnkFileSystem"/> keeps for its nested cache.</summary>
    public static BnkNestedCacheSnapshot Current() => new(
        BnkNestedCachePolicy.DefaultBytes(Refii.Core.Engine_Platform.IsAndroid),
        System.Threading.Interlocked.Read(ref BnkFileSystem.NestedResidentBytes),
        System.Threading.Interlocked.Read(ref BnkFileSystem.NestedMappedBytes),
        System.Threading.Interlocked.Read(ref BnkFileSystem.NestedLoads),
        System.Threading.Interlocked.Read(ref BnkFileSystem.NestedEvictions));
}

/// <summary>Platform defaults for managed nested-bank storage, independent of GPU and decoded asset caches.</summary>
public static class BnkNestedCachePolicy
{
    /// <summary>Android trades some repeated bank inflation for a smaller managed live heap.</summary>
    public const long AndroidBudgetBytes = 256L << 20;
    /// <summary>Preserves the existing desktop budget.</summary>
    public const long DesktopBudgetBytes = 1024L << 20;

    /// <summary>Selects a platform default without querying native Godot APIs.</summary>
    /// <param name="android">Whether the host is Android.</param>
    /// <returns>The retained nested-container budget in bytes.</returns>
    public static long DefaultBytes(bool android) => android ? AndroidBudgetBytes : DesktopBudgetBytes;
}
