namespace Shortener.Api.Contracts;

public sealed record CreateShortLinkRequest(
    string TargetUrl,
    string? CustomAlias = null,
    DateTimeOffset? ExpiresAtUtc = null);

public sealed record ShortLinkResponse(
    string Code,
    string TargetUrl,
    string ShortUrl,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc);

public sealed record ShortLinkListItemResponse(
    string Code,
    string TargetUrl,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    long TotalClicks);

public sealed record DailyClickResponse(string Date, int Clicks);

public sealed record LinkAnalyticsResponse(
    string Code,
    long TotalClicks,
    long ClicksLast24Hours,
    IReadOnlyList<DailyClickResponse> DailyClicks,
    IReadOnlyDictionary<string, int> TopReferrers);