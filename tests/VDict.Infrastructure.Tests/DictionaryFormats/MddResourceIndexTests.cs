using VDict.Infrastructure.DictionaryFormats;
using System.Reflection;
using System.Text;

namespace VDict.Infrastructure.Tests.DictionaryFormats;

public sealed class MddResourceIndexTests
{
    [Fact]
    public void LookupUsesCaseInsensitiveResourceIdentity()
    {
        MddResourceIndex index = new();
        index.Add(@"\Images\Speaker Icon.PNG", 123, 456);

        Assert.True(index.TryGet("images/speaker icon.png", out var record));
        Assert.Equal((123L, 456L), record);
    }

    [Fact]
    public void CaseInsensitiveHashChangeInvalidatesVersionTwoPersistence()
    {
        FieldInfo? magicField = typeof(MddResourceIndex).GetField(
            "Magic",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(magicField);
        byte[] magic = Assert.IsType<byte[]>(magicField.GetValue(null));

        Assert.Equal("VDMRI03", Encoding.ASCII.GetString(magic));
    }

}
