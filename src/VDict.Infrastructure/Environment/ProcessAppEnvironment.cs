using System.Reflection;
using VDict.Core.Environment;

namespace VDict.Infrastructure.Environment;

public sealed class ProcessAppEnvironment(bool isPackaged, bool isPortable) : IAppEnvironment
{
    public bool IsPackaged { get; } = isPackaged;

    public bool IsPortable { get; } = isPortable;

    public string ExecutableDirectory
    {
        get
        {
            string? processPath = System.Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                throw new InvalidOperationException("无法确定实际可执行文件路径。");
            }

            return Path.GetDirectoryName(Path.GetFullPath(processPath))
                ?? throw new InvalidOperationException("无法确定实际可执行文件目录。");
        }
    }

    public string Version =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
}
