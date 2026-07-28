using System.Text.Json.Serialization;

namespace VDict.Infrastructure.Settings;

public sealed class SettingsDocument
{
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; }

    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("ui_language")]
    public string UiLanguage { get; set; } = "system";

    [JsonPropertyName("dictionaries")]
    public List<DictionaryDocument> Dictionaries { get; set; } = [];

    [JsonPropertyName("style")]
    public StyleDocument Style { get; set; } = new();

    [JsonPropertyName("reduce_dictionary_brightness_in_dark_mode")]
    public bool ReduceDictionaryBrightnessInDarkMode { get; set; } = true;

    [JsonPropertyName("external_link_policy")]
    public string ExternalLinkPolicy { get; set; } = "block";

    [JsonPropertyName("compatibility_diagnostics_enabled")]
    public bool CompatibilityDiagnosticsEnabled { get; set; }

    [JsonPropertyName("compatibility_diagnostics_detailed")]
    public bool CompatibilityDiagnosticsDetailed { get; set; }
}

public sealed class DictionaryDocument
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("mdx_path")]
    public string MdxPath { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("block_network")]
    public bool BlockNetwork { get; set; } = true;

    [JsonPropertyName("local_script_mode")]
    public string LocalScriptMode { get; set; } = "standard";

    [JsonPropertyName("mdd_paths")]
    public List<string> MddPaths { get; set; } = [];

    [JsonPropertyName("css_paths")]
    public List<string> CssPaths { get; set; } = [];

    [JsonPropertyName("script_paths")]
    public List<string> ScriptPaths { get; set; } = [];

    [JsonPropertyName("font_size")]
    public int FontSize { get; set; } = 18;

    [JsonPropertyName("line_height")]
    public double LineHeight { get; set; } = 1.65;

    [JsonPropertyName("latin_font_family")]
    public string LatinFontFamily { get; set; } = "Segoe UI";

    [JsonPropertyName("cjk_font_family")]
    public string CjkFontFamily { get; set; } = "Microsoft YaHei";

    [JsonPropertyName("override_font")]
    public bool OverrideFont { get; set; }

    [JsonPropertyName("override_typography")]
    public bool OverrideTypography { get; set; }

    [JsonPropertyName("interaction_mode")]
    public string InteractionMode { get; set; } = "auto";

    [JsonPropertyName("dark_mode_strategy")]
    public string DarkModeStrategy { get; set; } = "original";
}

public sealed class StyleDocument
{
    [JsonPropertyName("font_size")]
    public int FontSize { get; set; } = 18;

    [JsonPropertyName("line_height")]
    public double LineHeight { get; set; } = 1.65;

    [JsonPropertyName("content_zoom")]
    public double ContentZoom { get; set; } = 1.0;

    [JsonPropertyName("content_width_percent")]
    public int ContentWidthPercent { get; set; } = 80;

    [JsonPropertyName("candidate_pane_width")]
    public double? CandidatePaneWidth { get; set; }

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    [JsonPropertyName("app_font_family")]
    public string AppFontFamily { get; set; } = "Microsoft YaHei UI";

    [JsonPropertyName("latin_font_family")]
    public string LatinFontFamily { get; set; } = "Segoe UI";

    [JsonPropertyName("cjk_font_family")]
    public string CjkFontFamily { get; set; } = "Microsoft YaHei";
}
