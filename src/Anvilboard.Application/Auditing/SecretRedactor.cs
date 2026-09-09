using System.Text.RegularExpressions;

namespace Anvilboard.Application.Auditing;

public static partial class SecretRedactor
{
    private const string Replacement = "***REDACTED***";

    [GeneratedRegex(
        "[\\\"']?(?<key>secret|token|password|apiKey|credential)[\\\"']?\\s*[:=]\\s*(?:(?<quote>[\\\"'])(?<value>.+?)\\k<quote>|(?<value_unquoted>[^,;\\s}\\\"']+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveFieldRegex();

    [GeneratedRegex("(?<![A-Za-z0-9])[A-Za-z0-9+/]{64,}={0,2}(?![A-Za-z0-9])|(?<![A-Fa-f0-9])[A-Fa-f0-9]{64,}(?![A-Fa-f0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex OpaqueValueRegex();

    public static string Scrub(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var scrubbed = SensitiveFieldRegex().Replace(text, match =>
            $"{match.Groups["key"].Value}: {Replacement}");
        return OpaqueValueRegex().Replace(scrubbed, Replacement);
    }
}

