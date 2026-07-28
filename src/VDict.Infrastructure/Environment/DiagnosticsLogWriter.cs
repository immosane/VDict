using VDict.Core.Environment;

namespace VDict.Infrastructure.Environment;

public sealed class DiagnosticsLogWriter : IAppLogger, IDisposable
{
    private readonly SessionLog _sessionLog;
    private readonly DiagnosticsPrivacySettings _privacy;

    public DiagnosticsLogWriter(SessionLog sessionLog)
        : this(sessionLog, new DiagnosticsPrivacySettings())
    {
    }

    public DiagnosticsLogWriter(
        SessionLog sessionLog,
        DiagnosticsPrivacySettings privacy)
    {
        _sessionLog = sessionLog;
        _privacy = privacy;
    }

    public void Write(
        AppLogLevel level,
        string source,
        string message,
        Exception? exception = null)
    {
        bool detailed = _privacy.DetailedEnabled;
        string safeSource = SingleLine(
            DiagnosticsLogPrivacy.Sanitize(source, detailed),
            128);
        string safeMessage = SingleLine(
            DiagnosticsLogPrivacy.Sanitize(message, detailed),
            8192);
        string exceptionText = exception is null
            ? string.Empty
            : " | " + SingleLine(
                DiagnosticsLogPrivacy.Sanitize(exception.ToString(), detailed),
                24 * 1024);
        _sessionLog.Append(
            $"{DateTimeOffset.UtcNow:O} [{level}] [{safeSource}] {safeMessage}{exceptionText}");
    }

    public void Dispose() => _sessionLog.Dispose();

    private static string SingleLine(string? value, int maxLength)
    {
        string result = (value ?? string.Empty)
            .Replace("\r\n", " ↩ ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
        return result.Length <= maxLength ? result : result[..maxLength];
    }
}
