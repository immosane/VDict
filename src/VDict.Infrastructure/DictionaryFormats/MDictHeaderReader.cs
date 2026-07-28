using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VDict.Infrastructure.DictionaryFormats;

internal static class MDictHeaderReader
{
    private const int MaxHeaderLength = 4 * 1024 * 1024;

    public static Dictionary<string, string> Read(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Read(stream);
    }

    public static Dictionary<string, string> Read(Stream stream)
    {
        stream.Position = 0;
        var lengthBytes = ReadExactly(stream, sizeof(uint));
        var headerLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
        if (headerLength == 0 || headerLength > MaxHeaderLength)
        {
            throw new InvalidDataException($"MDict header length is invalid: {headerLength} bytes.");
        }

        var headerBytes = ReadExactly(stream, checked((int)headerLength));
        _ = ReadExactly(stream, sizeof(uint));
        var headerText = headerBytes.Length >= 2 && headerBytes[^1] == 0 && headerBytes[^2] == 0
            ? Encoding.Unicode.GetString(headerBytes, 0, headerBytes.Length - 2)
            : Encoding.UTF8.GetString(headerBytes).TrimEnd('\0');

        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(headerText, "(\\w+)=\"(.*?)\"", RegexOptions.Singleline))
        {
            output[match.Groups[1].Value] = System.Net.WebUtility.HtmlDecode(match.Groups[2].Value);
        }

        if (output.Count == 0)
        {
            throw new InvalidDataException("The MDict header does not contain readable metadata.");
        }

        return output;
    }

    private static byte[] ReadExactly(Stream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read == 0)
            {
                throw new EndOfStreamException("Unexpected end of MDict header.");
            }

            offset += read;
        }

        return buffer;
    }
}
