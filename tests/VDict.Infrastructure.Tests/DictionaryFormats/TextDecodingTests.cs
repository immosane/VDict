using VDict.Infrastructure.DictionaryFormats;

namespace VDict.Infrastructure.Tests.DictionaryFormats;

public sealed class TextDecodingTests
{
    [Fact]
    public void LegacyChineseCodePage_IsAvailableWithoutCallerRegistration()
    {
        byte[] gb18030 = [0xD6, 0xD0, 0xCE, 0xC4];

        string decoded = TextDecoding.Decode(gb18030, "gb18030");

        Assert.Equal("中文", decoded);
    }
}
