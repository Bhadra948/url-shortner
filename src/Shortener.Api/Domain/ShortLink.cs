namespace Shortener.Api.Domain;

public sealed class ShortLink
{
    public int Id { get; set; }
    public required string Code { get; set; }
    public required string TargetUrl { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public bool IsActive { get; set; } = true;
    public ICollection<ClickEvent> Clicks { get; } = new List<ClickEvent>();
}

public sealed class ClickEvent
{
    public long Id { get; set; }
    public required string Code { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string? ReferrerHost { get; set; }
    public string? CountryCode { get; set; }
}