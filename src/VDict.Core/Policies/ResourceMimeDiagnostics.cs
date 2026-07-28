namespace VDict.Core.Policies;

public static class ResourceMimeDiagnostics
{
    public static string? DescribeMismatch(
        string path,
        string declaredMime,
        ReadOnlySpan<byte> body)
    {
        string? detected = Detect(body);
        if (detected is null ||
            detected.Equals(declaredMime, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string extension = Path.GetExtension(path);
        return
            $"MIME mismatch for {extension}: declared {declaredMime}, signature indicates {detected}.";
    }

    private static string? Detect(ReadOnlySpan<byte> body)
    {
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        if (body.Length >= 8 && body[..8].SequenceEqual(png))
        {
            return "image/png";
        }

        if (body.Length >= 3 &&
            body[0] == 0xff &&
            body[1] == 0xd8 &&
            body[2] == 0xff)
        {
            return "image/jpeg";
        }

        if (body.Length >= 4 && body[..4].SequenceEqual("wOF2"u8))
        {
            return "font/woff2";
        }

        if (body.Length >= 3 && body[..3].SequenceEqual("ID3"u8))
        {
            return "audio/mpeg";
        }

        return null;
    }
}
