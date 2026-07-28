using VDict.Core.Policies;

namespace VDict.Core.Tests.Policies;

public sealed class ZoomShortcutPolicyTests
{
    [Theory]
    [InlineData(0x6B, 1, false)]
    [InlineData(0xBB, 1, false)]
    [InlineData(0x6D, -1, false)]
    [InlineData(0xBD, -1, false)]
    [InlineData(0x30, 0, true)]
    [InlineData(0x60, 0, true)]
    public void Resolve_WithControl_MapsSupportedKeys(uint key, int step, bool reset)
    {
        Assert.Equal(new ZoomShortcut(step, reset), ZoomShortcutPolicy.Resolve(key, true));
    }

    [Fact]
    public void Resolve_WithoutControl_DoesNotHandleShortcut()
    {
        Assert.Equal(ZoomShortcut.None, ZoomShortcutPolicy.Resolve(0xBB, false));
    }

    [Fact]
    public void Resolve_UnknownKey_DoesNotHandleShortcut()
    {
        Assert.Equal(ZoomShortcut.None, ZoomShortcutPolicy.Resolve(0x41, true));
    }
}
