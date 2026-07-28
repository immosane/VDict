namespace VDict.Core.Policies;

public static class HttpByteRangeParser
{
    public static bool TryParse(string? range, int totalLength, out int start, out int end)
    {
        start = 0;
        end = totalLength - 1;
        if (totalLength <= 0 ||
            range is null ||
            !range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string value = range["bytes=".Length..].Trim();
        if (value.Contains(','))
        {
            return false;
        }

        string[] parts = value.Split('-', 2);
        if (parts.Length != 2)
        {
            return false;
        }

        if (parts[0].Length == 0)
        {
            if (!int.TryParse(parts[1], out int suffix) || suffix <= 0)
            {
                return false;
            }

            suffix = Math.Min(suffix, totalLength);
            start = totalLength - suffix;
            end = totalLength - 1;
            return true;
        }

        if (!int.TryParse(parts[0], out start) ||
            start < 0 ||
            start >= totalLength)
        {
            start = 0;
            end = totalLength - 1;
            return false;
        }

        if (parts[1].Length > 0 &&
            (!int.TryParse(parts[1], out end) || end < start))
        {
            start = 0;
            end = totalLength - 1;
            return false;
        }

        end = parts[1].Length == 0 ? totalLength - 1 : Math.Min(end, totalLength - 1);
        return true;
    }
}
