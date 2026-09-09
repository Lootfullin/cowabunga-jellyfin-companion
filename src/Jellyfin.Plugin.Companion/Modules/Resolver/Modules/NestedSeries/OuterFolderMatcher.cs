using System.Text.RegularExpressions;
using Jellyfin.Plugin.Companion.Configuration;
using Jellyfin.Plugin.SmartResolver.Core;

namespace Jellyfin.Plugin.SmartResolver.Modules.NestedSeries;

public sealed partial class OuterFolderMatcher
{
    private readonly object _sync = new();
    private string? _cachedPattern;
    private Regex? _cachedRegex;

    public OuterFolderMatchResult Match(
        string outerName,
        ResolverMode mode,
        string customRegex)
    {
        switch (mode)
        {
            case ResolverMode.YearPrefix:
                return YearPrefixRegex().IsMatch(outerName)
                    ? OuterFolderMatchResult.Accepted
                    : OuterFolderMatchResult.Rejected(ResolverReasonCode.OuterNameRejected);

            case ResolverMode.AnySingleNestedSeries:
                return OuterFolderMatchResult.Accepted;

            case ResolverMode.CustomRegex:
                var regex = GetCustomRegex(customRegex);
                if (regex is null)
                {
                    return OuterFolderMatchResult.Rejected(ResolverReasonCode.InvalidCustomRegex);
                }

                return regex.IsMatch(outerName)
                    ? OuterFolderMatchResult.Accepted
                    : OuterFolderMatchResult.Rejected(ResolverReasonCode.OuterNameRejected);

            default:
                return OuterFolderMatchResult.Rejected(ResolverReasonCode.OuterNameRejected);
        }
    }

    private Regex? GetCustomRegex(string pattern)
    {
        lock (_sync)
        {
            if (string.Equals(pattern, _cachedPattern, StringComparison.Ordinal))
            {
                return _cachedRegex;
            }

            _cachedPattern = pattern;
            try
            {
                _cachedRegex = string.IsNullOrWhiteSpace(pattern)
                    ? null
                    : new Regex(
                        pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException)
            {
                _cachedRegex = null;
            }

            return _cachedRegex;
        }
    }

    [GeneratedRegex(@"^\(\d{4}\)\s+.+$", RegexOptions.CultureInvariant)]
    private static partial Regex YearPrefixRegex();
}

public readonly record struct OuterFolderMatchResult(
    bool IsMatch,
    ResolverReasonCode ReasonCode)
{
    public static OuterFolderMatchResult Accepted =>
        new(true, ResolverReasonCode.Accepted);

    public static OuterFolderMatchResult Rejected(ResolverReasonCode reasonCode) =>
        new(false, reasonCode);
}
