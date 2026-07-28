namespace VDict.Core.Policies;

public static class DownloadFileNamePolicy
{
    private const int MaximumLength = 180;
    private static readonly HashSet<string> ReservedNames = new(
        [
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static string Sanitize(string? value)
    {
        string name;
        try
        {
            name = string.IsNullOrWhiteSpace(value) ? "download" : Path.GetFileName(value);
        }
        catch
        {
            name = "download";
        }

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        name = name.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "download";
        }

        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(name)))
        {
            name = "_" + name;
        }

        if (name.Length <= MaximumLength)
        {
            return name;
        }

        string extension = Path.GetExtension(name);
        if (extension.Length > 20)
        {
            extension = string.Empty;
        }

        int prefixLength = Math.Max(1, MaximumLength - extension.Length);
        return name[..prefixLength] + extension;
    }
}
