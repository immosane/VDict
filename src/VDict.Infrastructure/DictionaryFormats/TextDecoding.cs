using System.Text;

namespace VDict.Infrastructure.DictionaryFormats;

public static class TextDecoding
{
    static TextDecoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string Decode(byte[]? bytes, string? declaredEncoding = null)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return "";
        }

        if (!string.IsNullOrWhiteSpace(declaredEncoding) && TryDecode(bytes, declaredEncoding, out var declared))
        {
            return declared;
        }

        if (HasUtf8Bom(bytes) && TryDecode(bytes[3..], "utf-8", out var utf8Bom))
        {
            return utf8Bom;
        }

        if (HasUtf32LeBom(bytes) && TryDecode(bytes[4..], "utf-32", out var utf32Le))
        {
            return utf32Le;
        }

        if (HasUtf32BeBom(bytes) && TryDecode(bytes[4..], "utf-32BE", out var utf32Be))
        {
            return utf32Be;
        }

        if (HasUtf16LeBom(bytes) && TryDecode(bytes[2..], "utf-16", out var utf16Le))
        {
            return utf16Le;
        }

        if (HasUtf16BeBom(bytes) && TryDecode(bytes[2..], "utf-16BE", out var utf16Be))
        {
            return utf16Be;
        }

        foreach (var encoding in new[]
                 {
                     "utf-8", "gb18030", "big5", "shift_jis", "euc-jp", "euc-kr", "windows-1252"
                 })
        {
            if (TryDecode(bytes, encoding, out var decoded))
            {
                return decoded;
            }
        }

        return Encoding.Latin1.GetString(bytes);
    }

    private static bool TryDecode(byte[] bytes, string encodingName, out string decoded)
    {
        decoded = "";
        try
        {
            var encoding = Encoding.GetEncoding(
                encodingName,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            decoded = encoding.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool HasUtf16LeBom(byte[] bytes) => bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe;

    private static bool HasUtf16BeBom(byte[] bytes) => bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff;

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;

    private static bool HasUtf32LeBom(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 0xff && bytes[1] == 0xfe && bytes[2] == 0 && bytes[3] == 0;

    private static bool HasUtf32BeBom(byte[] bytes) =>
        bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xfe && bytes[3] == 0xff;
}
