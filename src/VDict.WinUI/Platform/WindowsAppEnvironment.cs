using System.Runtime.InteropServices;
using VDict.Core.Environment;
using VDict.Infrastructure.Environment;

namespace VDict.WinUI.Platform;

public static partial class WindowsAppEnvironment
{
    private const int AppModelErrorNoPackage = 15700;

    public static IAppEnvironment Create()
    {
        bool packaged = HasPackageIdentity();
#if PORTABLE
        const bool portable = true;
#else
        bool portable = !packaged;
#endif
        return new ProcessAppEnvironment(packaged, portable);
    }

    public static IAppDataPaths CreatePaths(IAppEnvironment environment)
    {
        if (!environment.IsPackaged)
        {
            return new PortableAppDataPaths(environment);
        }

        Windows.Storage.ApplicationData data = Windows.Storage.ApplicationData.Current;
        return new PackagedAppDataPaths(
            data.LocalFolder.Path,
            data.LocalCacheFolder.Path);
    }

    private static bool HasPackageIdentity()
    {
        uint length = 0;
        int result = GetCurrentPackageFullName(ref length, null);
        return result != AppModelErrorNoPackage;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentPackageFullName", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
}
