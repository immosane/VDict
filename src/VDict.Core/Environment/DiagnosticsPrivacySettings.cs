using System.Text.RegularExpressions;

namespace VDict.Core.Environment;

public sealed class DiagnosticsPrivacySettings
{
    private int _detailedEnabled;

    public bool DetailedEnabled
    {
        get => Volatile.Read(ref _detailedEnabled) != 0;
        set => Volatile.Write(ref _detailedEnabled, value ? 1 : 0);
    }
}

public static partial class DiagnosticsLogPrivacy
{
    public static string Sanitize(string? value, bool detailed)
    {
        string text = value ?? string.Empty;
        if (detailed)
        {
            return text;
        }

        text = FileUri().Replace(text, "<local-uri>");
        text = UncPath().Replace(text, "<local-path>");
        text = WindowsPath().Replace(text, "<local-path>");
        text = SensitiveField().Replace(text, match =>
            match.Groups["name"].Value + "=<redacted>");
        return text;
    }

    [GeneratedRegex(
        @"(?i)\bfile:///[^\s;|]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex FileUri();

    [GeneratedRegex(
        @"\\\\[^\\\s;|]+\\[^\s;|]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex UncPath();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:[A-Za-z]:\\)[^\r\n;|]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex WindowsPath();

    [GeneratedRegex(
        @"(?i)(?<name>Query|Word|Entry|Selection|Path|Uri|Origin|Referer|TriggerHref|Metrics)=[^;|]*",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveField();
}
