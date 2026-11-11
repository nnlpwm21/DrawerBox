namespace DeskBoxWhite.Services;

/// <summary>
/// DrawerBox-only stand-in for the full performance logger: the drawer build
/// keeps the same call surface (so copied files compile unchanged) while the
/// timings stay off and the counters are simple totals the icon cache updates.
/// </summary>
public static class PerformanceLogger
{
    public static bool IsEnabled => false;

    public static IDisposable Measure(string name, string? detail = null) => NullDisposable.Instance;

    public static void Mark(string name, string? detail = null)
    {
    }

    public static long ThumbnailCacheCount { get; set; }

    public static long ThumbnailEstimatedBytes { get; set; }

    public static int IconCacheCount { get; set; }

    public static int DecodedBitmapCacheCount { get; set; }

    public static long DecodedBitmapEstimatedBytes { get; set; }

    public static void RecordDecodedBitmapCacheLookup(bool hit)
    {
    }

    public static void RecordThumbnailCacheLookup(bool hit)
    {
    }

    public static void RecordDecodedBitmapCacheLoad(TimeSpan elapsed, bool succeeded)
    {
    }

    public static void RecordThumbnailCacheLoad(TimeSpan elapsed, bool succeeded)
    {
    }

    private sealed class NullDisposable : IDisposable
    {
        public static readonly NullDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
