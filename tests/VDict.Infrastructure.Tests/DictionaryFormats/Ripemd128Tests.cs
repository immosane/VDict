using VDict.Infrastructure.DictionaryFormats;

namespace VDict.Infrastructure.Tests.DictionaryFormats;

public sealed class Ripemd128Tests
{
    [Theory]
    [MemberData(nameof(StandardTestVectors))]
    public void Hash_ProducesExpectedDigest(byte[] input, string expectedHex)
    {
        byte[] digest = Ripemd128.Hash(input);
        Assert.Equal(expectedHex, Convert.ToHexString(digest).ToLowerInvariant());
    }

    public static TheoryData<byte[], string> StandardTestVectors()
    {
        return new TheoryData<byte[], string>
        {
            { [], "cdf26213a150dc3ecb610f18f6b38b46" },
            { "a"u8.ToArray(), "86be7afa339d0fc7cfc785e72f578d33" },
            { "abc"u8.ToArray(), "c14a12199c66e4ba84636b0f69144c77" },
            { "message digest"u8.ToArray(), "9e327b3d6e523062afc1132d7df9d1b8" },
            { "abcdefghijklmnopqrstuvwxyz"u8.ToArray(), "fd2aa607f71dc8f510714922b371834e" },
        };
    }
}
