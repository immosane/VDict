using System.Security.Cryptography;
using System.Text;

namespace VDict.Infrastructure.Indexing;

public static class DictionaryIndexPath
{
    public static bool TryGet(string indexesDirectory, string? mdxPath, out string indexPath)
    {
        indexPath = string.Empty;
        if (string.IsNullOrWhiteSpace(indexesDirectory) || string.IsNullOrWhiteSpace(mdxPath))
        {
            return false;
        }

        try
        {
            string resolved = Path.GetFullPath(mdxPath).ToUpperInvariant();
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(resolved));
            string hex = Convert.ToHexString(bytes).ToLowerInvariant()[..24];
            indexPath = Path.Combine(indexesDirectory, $"{hex}.sqlite3");
            return true;
        }
        catch (Exception exception)
            when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
