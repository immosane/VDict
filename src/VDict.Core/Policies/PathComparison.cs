namespace VDict.Core.Policies;

public static class PathComparison
{
    public static bool AreEquivalent(string? left, string? right)
    {
        try
        {
            return Path.GetFullPath(left ?? string.Empty).Equals(
                Path.GetFullPath(right ?? string.Empty),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
