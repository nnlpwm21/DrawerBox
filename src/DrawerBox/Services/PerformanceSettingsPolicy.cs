namespace DeskBoxWhite.Services;

/// <summary>
/// DrawerBox-only subset of the performance policy: the icon-cache budget
/// constants the copied IconHelper reads. The performance editor surface stays
/// in the full app.
/// </summary>
public static class PerformanceSettingsPolicy
{
    public const string CacheBudgetSmall = "Small";
    public const string CacheBudgetLarge = "Large";
    public const string CacheBudgetBalanced = "Balanced";

    public static string NormalizeCacheBudget(string? cacheBudget) =>
        string.Equals(cacheBudget, CacheBudgetSmall, StringComparison.OrdinalIgnoreCase)
            ? CacheBudgetSmall
            : string.Equals(cacheBudget, CacheBudgetLarge, StringComparison.OrdinalIgnoreCase)
                ? CacheBudgetLarge
                : CacheBudgetBalanced;
}
