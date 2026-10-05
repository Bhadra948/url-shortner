using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Shortener.Api.Contracts;
using Shortener.Api.Domain;
using Shortener.Api.Persistence;

namespace Shortener.Api.Services;

public sealed partial class ShortLinkService(AppDbContext db, TimeProvider clock)
{
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public async Task<ShortLink> CreateAsync(CreateShortLinkRequest request, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(request.TargetUrl, UriKind.Absolute, out var target)
            || (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps)
            || request.TargetUrl.Length > 2048)
        {
            throw new ArgumentException("TargetUrl must be an absolute HTTP or HTTPS URL of at most 2048 characters.");
        }

        var customAlias = request.CustomAlias?.Trim();
        if (customAlias is not null && !AliasPattern().IsMatch(customAlias))
        {
            throw new ArgumentException("CustomAlias must be 4-32 letters, numbers, underscores, or hyphens.");
        }

        if (request.ExpiresAtUtc is { } expiry && expiry <= clock.GetUtcNow())
        {
            throw new ArgumentException("ExpiresAtUtc must be in the future.");
        }

        var attempts = customAlias is null ? 5 : 1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var link = new ShortLink
            {
                Code = customAlias ?? GenerateCode(),
                TargetUrl = target.AbsoluteUri,
                CreatedAtUtc = clock.GetUtcNow(),
                ExpiresAtUtc = request.ExpiresAtUtc
            };

            db.Links.Add(link);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return link;
            }
            catch (DbUpdateException) when (customAlias is null && attempt < attempts - 1)
            {
                db.ChangeTracker.Clear();
            }
            catch (DbUpdateException) when (customAlias is not null)
            {
                db.ChangeTracker.Clear();
                throw new InvalidOperationException("That custom alias is already in use.");
            }
        }

        throw new InvalidOperationException("Could not allocate a unique short code. Retry the request.");
    }

    public async Task<ShortLink?> ResolveAsync(
        string code,
        string? referrer,
        string? countryCode,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var link = await db.Links.SingleOrDefaultAsync(item => item.Code == code, cancellationToken);
        if (link is null || !link.IsActive || (link.ExpiresAtUtc is { } expiry && expiry <= now))
        {
            return null;
        }

        db.Clicks.Add(new ClickEvent
        {
            Code = code,
            OccurredAtUtc = now,
            ReferrerHost = GetSafeReferrerHost(referrer),
            CountryCode = IsCountryCode(countryCode) ? countryCode!.ToUpperInvariant() : null
        });
        await db.SaveChangesAsync(cancellationToken);
        return link;
    }

    public Task<ShortLink?> GetByCodeAsync(string code, CancellationToken cancellationToken) =>
        db.Links.AsNoTracking().SingleOrDefaultAsync(item => item.Code == code, cancellationToken);

    public async Task<IReadOnlyList<ShortLinkListItemResponse>> GetRecentAsync(CancellationToken cancellationToken)
    {
        var links = await db.Links.AsNoTracking()
            .OrderByDescending(link => link.Id)
            .Take(100)
            .ToListAsync(cancellationToken);
        var codes = links.Select(link => link.Code).ToArray();
        var clickCounts = await db.Clicks.AsNoTracking()
            .Where(click => codes.Contains(click.Code))
            .GroupBy(click => click.Code)
            .Select(group => new { Code = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(item => item.Code, item => item.Count, cancellationToken);

        return links.Select(link => new ShortLinkListItemResponse(
            link.Code,
            link.TargetUrl,
            link.CreatedAtUtc,
            link.ExpiresAtUtc,
            clickCounts.GetValueOrDefault(link.Code)))
            .ToArray();
    }

    public async Task<LinkAnalyticsResponse?> GetAnalyticsAsync(string code, CancellationToken cancellationToken)
    {
        if (!await db.Links.AnyAsync(link => link.Code == code, cancellationToken))
        {
            return null;
        }

        var now = clock.GetUtcNow();
        var clicks = await db.Clicks.AsNoTracking()
            .Where(click => click.Code == code)
            .ToListAsync(cancellationToken);

        var daily = clicks
            .Where(click => click.OccurredAtUtc >= now.AddDays(-6))
            .GroupBy(click => click.OccurredAtUtc.UtcDateTime.ToString("yyyy-MM-dd"))
            .OrderBy(group => group.Key)
            .Select(group => new DailyClickResponse(group.Key, group.Count()))
            .ToArray();
        var referrers = clicks
            .Where(click => click.ReferrerHost is not null)
            .GroupBy(click => click.ReferrerHost!)
            .OrderByDescending(group => group.Count())
            .Take(5)
            .ToDictionary(group => group.Key, group => group.Count());

        return new LinkAnalyticsResponse(
            code,
            clicks.Count,
            clicks.LongCount(click => click.OccurredAtUtc >= now.AddHours(-24)),
            daily,
            referrers);
    }

    private static string GenerateCode()
    {
        Span<char> code = stackalloc char[7];
        for (var index = 0; index < code.Length; index++)
        {
            code[index] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(code);
    }

    private static string? GetSafeReferrerHost(string? referrer) =>
        Uri.TryCreate(referrer, UriKind.Absolute, out var uri) ? uri.Host : null;

    private static bool IsCountryCode(string? countryCode) =>
        countryCode is { Length: 2 } && countryCode.All(char.IsAsciiLetter);

    [GeneratedRegex("^[A-Za-z0-9_-]{4,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex AliasPattern();
}