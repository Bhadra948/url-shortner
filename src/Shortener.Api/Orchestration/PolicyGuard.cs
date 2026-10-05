using System.Text.RegularExpressions;

namespace Shortener.Api.Orchestration;

public static partial class PolicyGuard
{
    public static void ValidateRequirement(string requirement)
    {
        ValidateSafeText(requirement, "Requirement", 4000);
    }

    public static void ValidateActor(string actor) => ValidateSafeText(actor, "Actor", 128);

    public static void ValidateSafeText(string value, string field, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            throw new ArgumentException($"{field} must contain 1-{maxLength} characters.");
        }

        if (CredentialPattern().IsMatch(value))
        {
            throw new ArgumentException($"{field} must not contain credentials or secret values.");
        }
    }

    [GeneratedRegex(@"(?i)\b(?:api[_-]?key|client[_-]?secret|password|token)\b\s*[:=]\s*\S+|\bbearer\s+[A-Za-z0-9._~+/=-]{12,}", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPattern();
}