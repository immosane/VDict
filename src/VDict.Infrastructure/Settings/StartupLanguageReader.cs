using System.Text.Json;
using VDict.Core.Environment;
using VDict.Core.Models;

namespace VDict.Infrastructure.Settings;

public static class StartupLanguageReader
{
    public static ApplicationLanguage Read(IAppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return TryRead(paths.SettingsFile, out ApplicationLanguage primary)
            ? primary
            : TryRead(paths.SettingsBackupFile, out ApplicationLanguage backup)
                ? backup
                : ApplicationLanguage.System;
    }

    private static bool TryRead(string path, out ApplicationLanguage language)
    {
        language = ApplicationLanguage.System;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using JsonDocument document = JsonDocument.Parse(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!document.RootElement.TryGetProperty("ui_language", out JsonElement value))
            {
                return true;
            }

            language = value.ValueKind == JsonValueKind.String
                ? SettingsSchema.ParseLanguage(value.GetString())
                : ApplicationLanguage.System;
            return true;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }
}
