using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VDict.Core.Models;
using VDict.Core.Policies;

namespace VDict.Core.Text;

public sealed record ReaderDarkModeAssets(string Script, string Overrides);

internal sealed record ReaderFrameBuildResult(
    string Html,
    DictionaryHtmlNormalizationDiagnostics Diagnostics);

public static partial class ReaderHtmlRenderer
{
    public const string ProductionMarker = "core-reader-html-v1";
    private const int DefaultDictionaryFontSize = 18;
    private const double DefaultDictionaryLineHeight = 1.65;

    [GeneratedRegex(
        @"<script\b[^>]*\bsrc\s*=\s*(?:""(?<double>[^""]+)""|'(?<single>[^']+)'|(?<bare>[^\s>]+))",
        RegexOptions.IgnoreCase)]
    private static partial Regex ScriptSourceAttribute();

    [GeneratedRegex(
        @"<script\b[^>]*\bnonce\s*=\s*[""']([^""']+)[""']",
        RegexOptions.IgnoreCase)]
    private static partial Regex CspNonce();

    public static string BuildShell(
        ReaderDocument document,
        bool? effectiveApplicationDark = null)
    {
        string nonce = NewNonce();
        string tokenJson = JsonSerializer.Serialize(document.RenderToken);
        bool initialDark =
            effectiveApplicationDark ??
            document.Appearance.Theme == ApplicationTheme.Dark;
        string initialDarkJson = JsonSerializer.Serialize(initialDark);
        string body = document.Frames.IsDefaultOrEmpty
            ? document.Html
            : string.Join(
                System.Environment.NewLine,
                document.Frames.Select(frame => BuildFrameElement(frame, document)));
        if (!document.Notices.IsDefaultOrEmpty)
        {
            body += "<aside class=\"notices\"><ul>" +
                    string.Join(
                        string.Empty,
                        document.Notices.Select(notice =>
                            $"<li>{WebUtility.HtmlEncode(notice)}</li>")) +
                    "</ul></aside>";
        }

        string fontFamily = CssFontFamily(
            document.Appearance.LatinFontFamily,
            document.Appearance.CjkFontFamily);
        int contentWidthPercent = Math.Clamp(
            document.Appearance.ContentWidthPercent,
            30,
            100);
        string themeClass = initialDark ? "dark" : "light";
        return $$"""
            <!doctype html>
            <html class="{{themeClass}}">
            <head>
              <meta charset="utf-8">
              <meta name="vdict-renderer" content="{{ProductionMarker}}">
              <meta http-equiv="Content-Security-Policy"
                    content="default-src 'none'; style-src 'unsafe-inline'; script-src 'nonce-{{nonce}}'; frame-src vdict-doc://*.vdict.invalid; img-src 'self' data:; base-uri 'none'; form-action 'none'; object-src 'none'">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <style>
                :root { color-scheme: light dark; font-family: {{fontFamily}};
                        --vdict-content-width:{{contentWidthPercent}}%;
                        --vdict-accent:#0f6cbd; --vdict-muted:#5f6368;
                        --vdict-card:#f7f9fc; --vdict-card-border:#dce3ed;
                        --vdict-mark:#e8f2fc; }
                html.light { color-scheme: light; }
                html.dark { color-scheme: dark; --vdict-accent:#78b7f0;
                            --vdict-muted:#b2b8c2; --vdict-card:#25282d;
                            --vdict-card-border:#414750; --vdict-mark:#173b5c; }
                body { margin:0; padding:24px 28px 60px; font-size:{{document.Appearance.FontSize}}px;
                       line-height:{{Invariant(document.Appearance.LineHeight)}}; overflow-x:hidden;
                       overflow-y:auto; overflow-wrap:anywhere; }
                .entry,.welcome,.not-found,.no-dictionaries,.notices {
                  box-sizing:border-box; width:var(--vdict-content-width); max-width:100%;
                  margin-left:auto; margin-right:auto;
                }
                .entry { display:block; min-height:1px; margin-top:0; margin-bottom:0;
                         border:0; background:transparent; color:inherit; }
                .no-dictionaries,.notices {
                  margin-top:32px; margin-bottom:32px; padding:24px;
                  border:1px solid color-mix(in srgb,CanvasText 18%,transparent);
                  border-radius:12px;
                }
                .not-found {
                  max-width:min(560px,var(--vdict-content-width));
                  margin-top:clamp(52px,12vh,136px); margin-bottom:32px;
                  padding:32px 28px; text-align:center; color:CanvasText;
                  background:var(--vdict-card); border:1px solid var(--vdict-card-border);
                  border-radius:18px; box-shadow:0 8px 28px rgb(0 0 0 / 7%);
                }
                .not-found-mark {
                  position:relative; box-sizing:border-box; width:48px; height:48px;
                  margin:0 auto 18px; color:var(--vdict-accent);
                  background:var(--vdict-mark); border-radius:50%;
                }
                .not-found-mark::before {
                  content:""; position:absolute; left:13px; top:12px;
                  width:15px; height:15px; border:2px solid currentColor;
                  border-radius:50%;
                }
                .not-found-mark::after {
                  content:""; position:absolute; left:28px; top:29px;
                  width:9px; height:2px; background:currentColor;
                  border-radius:2px; transform:rotate(45deg);
                  transform-origin:left center;
                }
                .not-found h1 {
                  margin:0; color:var(--vdict-accent); font-size:20px;
                  font-weight:500; line-height:1.35;
                }
                .not-found p {
                  margin:10px 0 0; color:var(--vdict-muted);
                  font-size:.94em; line-height:1.7;
                }
                .not-found-query { color:var(--vdict-accent); font-weight:500; }
                .welcome {
                  position:fixed; inset:0; box-sizing:border-box; width:auto; max-width:none;
                  margin:0; padding:clamp(28px,8vw,96px); border:0;
                  display:grid; place-items:center; overflow:hidden;
                }
                .welcome-logo {
                  display:block; width:min(420px,58vw); max-width:100%; height:auto;
                  max-height:44vh; object-fit:contain;
                }
              </style>
            </head>
            <body>
              {{body}}
              <script nonce="{{nonce}}">
                const renderToken = {{tokenJson}};
                let effectiveDark = {{initialDarkJson}};
                const frames = Array.from(document.querySelectorAll("iframe.entry"));
                const envelope = (kind, frame, extra = {}) => ({
                  kind, renderToken, frameId: frame?.dataset.frameId || "",
                  dictionaryId: frame?.dataset.dictionaryId || "", ...extra
                });
                const postZoomDelta = (frame, delta) => {
                  if (!frame || !Number.isFinite(delta)) return;
                  chrome.webview.postMessage(envelope("zoomDelta", frame, {
                    delta: Math.max(-1000, Math.min(1000, delta))
                  }));
                };
                const postZoomKey = (frame, event) => {
                  if (!frame || !event.ctrlKey || event.altKey || event.metaKey) return false;
                  if (event.key === "0") {
                    chrome.webview.postMessage(envelope("zoomReset", frame));
                    return true;
                  }
                  const step = ["+", "=", "Add"].includes(event.key) ? 1 :
                    ["-", "_", "Subtract"].includes(event.key) ? -1 : 0;
                  if (!step) return false;
                  chrome.webview.postMessage(envelope("zoomStep", frame, { step }));
                  return true;
                };
                addEventListener("wheel", event => {
                  if (!event.ctrlKey) return;
                  event.preventDefault();
                  postZoomDelta(frames[0], event.deltaY);
                }, { passive:false, capture:true });
                addEventListener("keydown", event => {
                  if (!postZoomKey(frames[0], event)) return;
                  event.preventDefault();
                }, { capture:true });
                window.vdictRequestFrameSizes = () => frames.forEach(frame =>
                  frame.contentWindow?.postMessage({
                    channel:"vdict-host", kind:"reportSize", renderToken
                  }, new URL(frame.src).origin));
                const postTheme = frame => frame.contentWindow?.postMessage({
                  channel:"vdict-host",
                  kind:"applyTheme",
                  renderToken,
                  frameId:frame.dataset.frameId || "",
                  dark:effectiveDark
                }, new URL(frame.src).origin);
                frames.forEach(frame =>
                  frame.addEventListener("load", () => postTheme(frame)));
                window.vdictApplyTheme = (dark, token) => {
                  if (token !== renderToken) return false;
                  effectiveDark = Boolean(dark);
                  document.documentElement.classList.toggle("dark", effectiveDark);
                  document.documentElement.classList.toggle("light", !effectiveDark);
                  frames.forEach(postTheme);
                  return true;
                };
                addEventListener("message", event => {
                  const frame = frames.find(candidate => candidate.contentWindow === event.source);
                  const data = event.data;
                  if (!frame || !data || data.channel !== "vdict-frame") return;
                  if (event.origin !== new URL(frame.src).origin) return;
                  if (data.kind === "resize") {
                    frame.style.height = `${Math.max(1, Math.min(250000, Number(data.height) || 1))}px`;
                    return;
                  }
                  if (data.kind === "entry") {
                    chrome.webview.postMessage(envelope("entry", frame, {
                      word: String(data.word || "").slice(0, 512)
                    }));
                    return;
                  }
                  if (data.kind === "selection") {
                    chrome.webview.postMessage(envelope("selection", frame, {
                      text: String(data.text || "").slice(0, 256)
                    }));
                    return;
                  }
                  if (data.kind === "userActivation") {
                    chrome.webview.postMessage(envelope("userActivation", frame, {
                      source: String(data.source || "").slice(0, 16)
                    }));
                    return;
                  }
                  if (data.kind === "inputEvent") {
                    chrome.webview.postMessage(envelope("inputEvent", frame, {
                      eventName: String(data.eventName || "").slice(0, 20),
                      timestamp: Math.max(0, Number(data.timestamp) || 0),
                      trusted: Boolean(data.trusted),
                      targetTag: String(data.targetTag || "").slice(0, 32),
                      targetId: String(data.targetId || "").slice(0, 128),
                      targetClass: String(data.targetClass || "").slice(0, 256),
                      triggerTag: String(data.triggerTag || "").slice(0, 32),
                      triggerClass: String(data.triggerClass || "").slice(0, 256),
                      triggerHref: String(data.triggerHref || "").slice(0, 4096)
                    }));
                    return;
                  }
                  if (data.kind === "imageError") {
                    chrome.webview.postMessage(envelope("imageError", frame, {
                      src: String(data.src || "").slice(0, 4096),
                      originalSrc: String(data.originalSrc || "").slice(0, 4096),
                      complete: Boolean(data.complete),
                      naturalWidth: Math.max(0, Number(data.naturalWidth) || 0),
                      naturalHeight: Math.max(0, Number(data.naturalHeight) || 0),
                      duration: Math.max(0, Number(data.duration) || 0),
                      transferSize: Math.max(0, Number(data.transferSize) || 0),
                      encodedBodySize: Math.max(0, Number(data.encodedBodySize) || 0),
                      decodedBodySize: Math.max(0, Number(data.decodedBodySize) || 0)
                    }));
                    return;
                  }
                  if (data.kind === "mediaEvent") {
                    chrome.webview.postMessage(envelope("mediaEvent", frame, {
                      eventName: String(data.eventName || "").slice(0, 32),
                      readyState: Math.max(0, Math.min(9, Number(data.readyState) || 0)),
                      networkState: Math.max(0, Math.min(9, Number(data.networkState) || 0)),
                      currentSrc: String(data.currentSrc || "").slice(0, 4096),
                      preload: String(data.preload || "").slice(0, 16),
                      paused: Boolean(data.paused),
                      ended: Boolean(data.ended),
                      currentTime: Math.max(0, Number(data.currentTime) || 0),
                      duration: Number.isFinite(Number(data.duration)) ? Number(data.duration) : -1,
                      errorCode: Math.max(0, Math.min(9, Number(data.errorCode) || 0)),
                      errorMessage: String(data.errorMessage || "").slice(0, 512),
                      userActivationActive: Boolean(data.userActivationActive),
                      promiseOutcome: String(data.promiseOutcome || "").slice(0, 32),
                      playLatencyMs: Math.max(0, Number(data.playLatencyMs) || 0)
                    }));
                    return;
                  }
                  if (data.kind === "mediaError") {
                    chrome.webview.postMessage(envelope("mediaError", frame, {
                      code: Math.max(0, Math.min(9, Number(data.code) || 0)),
                      readyState: Math.max(0, Math.min(9, Number(data.readyState) || 0)),
                      networkState: Math.max(0, Math.min(9, Number(data.networkState) || 0)),
                      extension: String(data.extension || "").slice(0, 16),
                      errorName: String(data.errorName || "").slice(0, 64),
                      errorMessage: String(data.errorMessage || "").slice(0, 512)
                    }));
                    return;
                  }
                  if (data.kind === "mediaMetadata") {
                    chrome.webview.postMessage(envelope("mediaMetadata", frame, {
                      duration: Number.isFinite(Number(data.duration)) ? Number(data.duration) : -1
                    }));
                    return;
                  }
                  if (data.kind === "mediaWarmup") {
                    chrome.webview.postMessage(envelope("mediaWarmup", frame, {
                      eventName: String(data.eventName || "").slice(0, 32),
                      src: String(data.src || "").slice(0, 4096),
                      sourceCount: Math.max(0, Math.min(4, Number(data.sourceCount) || 0)),
                      pendingCount: Math.max(0, Math.min(4, Number(data.pendingCount) || 0)),
                      elapsedMs: Math.max(0, Number(data.elapsedMs) || 0),
                      errorName: String(data.errorName || "").slice(0, 64)
                    }));
                    return;
                  }
                  if (data.kind === "zoomDelta") {
                    postZoomDelta(frame, Number(data.delta));
                    return;
                  }
                  if (data.kind === "zoomStep" || data.kind === "zoomReset") {
                    chrome.webview.postMessage(envelope(data.kind, frame, {
                      step: data.kind === "zoomStep" ? Number(data.step) : undefined
                    }));
                    return;
                  }
                  if (data.kind === "frameReady") {
                    chrome.webview.postMessage(envelope("frameReady", frame, {
                      hasContent: Boolean(data.hasContent)
                    }));
                    return;
                  }
                  if (data.kind === "frameLoad") {
                    chrome.webview.postMessage(envelope("frameLoad", frame, {}));
                    return;
                  }
                  if (data.kind === "browserDiagnostic") {
                    chrome.webview.postMessage(envelope("browserDiagnostic", frame, {
                      category: String(data.category || "").slice(0, 32),
                      message: String(data.message || "").slice(0, 1024),
                      source: String(data.source || "").slice(0, 4096),
                      directive: String(data.directive || "").slice(0, 128)
                    }));
                  }
                });
              </script>
            </body>
            </html>
            """;
    }

    public static string BuildFrameDocument(
        ReaderFrame frame,
        ReaderDocument document,
        ReaderDarkModeAssets? darkModeAssets = null,
        CancellationToken cancellationToken = default,
        bool? effectiveApplicationDark = null) =>
        BuildFrame(
            frame,
            document,
            darkModeAssets,
            cancellationToken,
            effectiveApplicationDark).Html;

    internal static ReaderFrameBuildResult BuildFrame(
        ReaderFrame frame,
        ReaderDocument document,
        ReaderDarkModeAssets? darkModeAssets = null,
        CancellationToken cancellationToken = default,
        bool? effectiveApplicationDark = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string nonce = NewNonce();
        DictionaryHtmlNormalizationResult normalized =
            DictionaryHtmlNormalizer.Normalize(frame.Html, cancellationToken);
        string safeHead = normalized.HeadContent;
        string safeContent = normalized.BodyContent;
        string bodyAttributes = normalized.BodyAttributes;
        string bodyAttributeSuffix = string.IsNullOrWhiteSpace(bodyAttributes)
            ? string.Empty
            : " " + bodyAttributes.Trim();
        safeHead = PrepareDictionaryScripts(safeHead, nonce, cancellationToken);
        safeContent = PrepareDictionaryScripts(safeContent, nonce, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        string origin = DictionaryNetworkPolicy.FrameOriginForDictionary(frame.DictionaryId.Value);
        DictionaryAppearance appearance = frame.Appearance;
        bool applicationDark =
            effectiveApplicationDark ??
            document.Appearance.Theme == ApplicationTheme.Dark;
        bool followsApplication =
            appearance.DarkModeStrategy == DictionaryDarkModeStrategy.Global;
        bool darkRequested =
            appearance.DarkModeStrategy == DictionaryDarkModeStrategy.Dark ||
            (followsApplication && applicationDark);
        bool hasDarkReader = darkModeAssets is { Script.Length: > 0 };
        bool injectDarkReader =
            hasDarkReader &&
            appearance.DarkModeStrategy is
                DictionaryDarkModeStrategy.Global or
                DictionaryDarkModeStrategy.Dark;
        string darkClass = darkRequested ? "dark" : "original";
        string customCss = string.Join(
            System.Environment.NewLine,
            frame.CustomCssResourcePaths.Select(path =>
                $"<link rel=\"stylesheet\" href=\"{WebUtility.HtmlEncode(path)}\">"));
        string customScripts = string.Join(
            System.Environment.NewLine,
            frame.CustomScriptResourcePaths
                .Where(path => !ReferencesScriptFile(safeHead + safeContent, path))
                .Select(path =>
                $"<script defer src=\"{WebUtility.HtmlEncode(path)}\"></script>"));
        int effectiveFontSize = Math.Clamp(
            appearance.OverrideTypography
                ? appearance.FontSize
                : document.Appearance.FontSize,
            10,
            48);
        double effectiveLineHeight = Math.Clamp(
            appearance.OverrideTypography
                ? appearance.LineHeight
                : document.Appearance.LineHeight,
            1.0,
            3.0);
        double fontScale = effectiveFontSize / (double)DefaultDictionaryFontSize;
        double lineHeightScale = effectiveLineHeight / DefaultDictionaryLineHeight;
        bool scaleTypography =
            appearance.OverrideTypography ||
            Math.Abs(fontScale - 1.0) > 0.000001 ||
            Math.Abs(lineHeightScale - 1.0) > 0.000001;
        bool typographyEnabled =
            appearance.OverrideFont ||
            scaleTypography;
        string typographyPendingClass = typographyEnabled
            ? " vdict-typography-pending"
            : string.Empty;
        string fallbackFontFamily = CssFontFamily(
            document.Appearance.LatinFontFamily,
            document.Appearance.CjkFontFamily);
        string readerFontFamilyJson = JsonSerializer.Serialize(
            CssFontFamily(appearance.LatinFontFamily, appearance.CjkFontFamily));
        string forceReaderFontJson = JsonSerializer.Serialize(appearance.OverrideFont);
        string scaleTypographyJson = JsonSerializer.Serialize(scaleTypography);
        string typographyEnabledJson = JsonSerializer.Serialize(typographyEnabled);
#if DEBUG
        const string debugTimingEnabledJson = "true";
#else
        const string debugTimingEnabledJson = "false";
#endif
        string preferenceCss = BuildDictionaryPreferenceCss(typographyEnabled);
        const string compatibilityCss =
            "<style data-vdict-layer=\"compatibility\">" +
            "body{min-width:0;max-width:100%;overflow-x:clip;}" +
            "</style>";
        string themeCss = darkRequested && !hasDarkReader
            ? "<style data-vdict-layer=\"theme\">:root{color-scheme:dark;background:#111418;color:#d3d8e0;}</style>"
            : string.Empty;
        string interaction = appearance.InteractionMode.ToString().ToLowerInvariant();
        string fragmentJson = JsonSerializer.Serialize(document.TargetFragment);
        string renderTokenJson = JsonSerializer.Serialize(document.RenderToken);
        string frameIdJson = JsonSerializer.Serialize(frame.FrameId);
        string blockExternalLinksJson = JsonSerializer.Serialize(
            document.ExternalLinkPolicy == ExternalLinkPolicy.Block);
        bool useDarkReader = darkRequested && hasDarkReader;
        string darkEngine = useDarkReader
            ? "darkreader"
            : darkRequested
                ? "palette"
                : "original";
        string darkPendingClass = useDarkReader ? " vdict-await-dark" : string.Empty;
        string darkModeBootstrap = injectDarkReader
            ? BuildDarkModeBootstrap(
                darkModeAssets!,
                nonce,
                document.ReduceDictionaryBrightnessInDarkMode,
                darkRequested,
                followsApplication,
                document.RenderToken,
                frame.FrameId)
            : string.Empty;
        string scriptSources = frame.LocalScriptMode switch
        {
            LocalScriptMode.Legacy => "'self' 'unsafe-inline' 'unsafe-eval' data: blob:",
            LocalScriptMode.Strict => $"'self' 'nonce-{nonce}'",
            _ => "'self' 'unsafe-inline' data: blob:"
        };
        string resourceBase = DictionaryNetworkPolicy.ResourceBaseUriForDictionary(
            frame.DictionaryId.Value,
            frame.CatalogGeneration,
            frame.ResourceCapability);
        string html = $$"""
            <!doctype html>
            <html class="{{darkClass}}{{darkPendingClass}}{{typographyPendingClass}}" data-vdict-dark-engine="{{darkEngine}}"
                  data-interaction-mode="{{interaction}}" data-resource-origin="{{origin}}">
            <head>
              <meta charset="utf-8">
              <meta name="vdict-renderer" content="{{ProductionMarker}}">
              <meta http-equiv="Content-Security-Policy"
                    content="default-src 'none'; img-src 'self' data: blob:; media-src 'self' data: blob:; font-src 'self' data:; style-src 'self' 'unsafe-inline' data:; script-src {{scriptSources}}; connect-src 'self'; frame-src 'none'; object-src 'none'; base-uri 'self'; form-action 'none'">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <base href="{{WebUtility.HtmlEncode(resourceBase)}}">
              <style data-vdict-layer="foundation">
                html { box-sizing:border-box; width:100%; min-height:0; overflow:hidden; }
                body { min-height:0; font-family:{{fallbackFontFamily}}; }
              </style>
              <script nonce="{{nonce}}">
                (() => {
                const interactionMode = "{{interaction}}";
                const targetFragment = {{fragmentJson}};
                const renderToken = {{renderTokenJson}};
                const frameId = {{frameIdJson}};
                const blockExternalLinks = {{blockExternalLinksJson}};
                const typographyEnabled = {{typographyEnabledJson}};
                const debugTimingEnabled = {{debugTimingEnabledJson}};
                const fontScale = {{Invariant(fontScale)}};
                const lineHeightScale = {{Invariant(lineHeightScale)}};
                const forceReaderFont = {{forceReaderFontJson}};
                const scaleTypography = {{scaleTypographyJson}};
                const readerFontFamily = {{readerFontFamilyJson}};
                const protectedTypographySelector = [
                  "img", "picture", "video", "audio", "source", "track", "canvas",
                  "object", "embed", "iframe", "svg", "symbol",
                  ".sym", ".icon", ".audio", ".sound", ".audio_play_button",
                  ".vdict-audio-trigger", "[data-audio-src]",
                  "[class*='symbol' i]", "[class*='icon' i]",
                  "[class*='audio' i]", "[class*='sound' i]"
                ].join(",");
                const nonTextSelector = "script,style,link,meta,noscript,template";
                const iconFontPattern =
                  /(?:segoe (?:fluent|mdl2)|material icons?|fontawesome|glyphicons?|icomoon|iconfont)/i;
                const typographyState = new WeakMap();
                const revealTypography = () =>
                  document.documentElement.classList.remove("vdict-typography-pending");
                const typographyFallback = setTimeout(revealTypography, 100);
                const restoreInlineProperty = (element, name, value, priority) => {
                  if (value) element.style.setProperty(name, value, priority);
                  else element.style.removeProperty(name);
                };
                const restoreTypography = state => {
                  if (state.appliedFontSize) restoreInlineProperty(
                    state.element, "font-size", state.inlineFontSize, state.fontSizePriority);
                  if (state.appliedLineHeight) restoreInlineProperty(
                    state.element, "line-height", state.inlineLineHeight, state.lineHeightPriority);
                  if (state.appliedFontFamily) restoreInlineProperty(
                    state.element, "font-family", state.inlineFontFamily, state.fontFamilyPriority);
                };
                const writeTypography = state => {
                  if (state.appliedFontSize) {
                    state.element.style.setProperty(
                      "font-size", `${(state.size * fontScale).toFixed(3)}px`, "important");
                  }
                  if (state.appliedLineHeight) {
                    state.element.style.setProperty(
                      "line-height",
                      `${(state.line * fontScale * lineHeightScale).toFixed(3)}px`,
                      "important");
                  }
                  if (state.appliedFontFamily) {
                    state.element.style.setProperty(
                      "font-family", readerFontFamily, "important");
                  }
                };
                const protectedTypography = (element, computed) =>
                  element.matches(protectedTypographySelector) ||
                  Boolean(element.closest(protectedTypographySelector)) ||
                  iconFontPattern.test(computed.fontFamily);
                const readTypography = element => {
                  if (!(element instanceof HTMLElement) ||
                      element.matches(nonTextSelector) ||
                      typographyState.has(element)) return null;
                  const computed = getComputedStyle(element);
                  if (protectedTypography(element, computed)) return null;
                  const size = Number.parseFloat(computed.fontSize);
                  const parsedLineHeight = Number.parseFloat(computed.lineHeight);
                  const line = Number.isFinite(parsedLineHeight)
                    ? parsedLineHeight
                    : size * 1.2;
                  if (!Number.isFinite(size) || !Number.isFinite(line)) return null;
                  return {
                    element, size, line,
                    inlineFontSize:element.style.getPropertyValue("font-size"),
                    fontSizePriority:element.style.getPropertyPriority("font-size"),
                    inlineLineHeight:element.style.getPropertyValue("line-height"),
                    lineHeightPriority:element.style.getPropertyPriority("line-height"),
                    inlineFontFamily:element.style.getPropertyValue("font-family"),
                    fontFamilyPriority:element.style.getPropertyPriority("font-family"),
                    appliedFontSize:scaleTypography,
                    appliedLineHeight:scaleTypography,
                    appliedFontFamily:forceReaderFont
                  };
                };
                const collectTypographyTargets = roots => {
                  const targets = [];
                  const seen = new Set();
                  const add = element => {
                    if (!(element instanceof HTMLElement) || seen.has(element)) return;
                    seen.add(element);
                    if (!typographyState.has(element)) targets.push(element);
                  };
                  for (const root of roots) {
                    if (!(root instanceof Element)) continue;
                    add(root);
                    root.querySelectorAll("*").forEach(add);
                  }
                  return targets;
                };
                const applyTypographyBatch = roots => {
                  if (!typographyEnabled) return;
                  const targets = collectTypographyTargets(roots);
                  if (!targets.length) return;
                  const ancestors = new Set();
                  for (const target of targets) {
                    for (let parent = target.parentElement; parent; parent = parent.parentElement) {
                      const state = typographyState.get(parent);
                      if (state) ancestors.add(state);
                    }
                  }
                  // Restore processed ancestors before reading newly inserted descendants.
                  // This keeps relative and inherited sizes from receiving the scale twice.
                  ancestors.forEach(restoreTypography);
                  // Read the complete batch before any write, including nested children.
                  const metrics = targets.map(readTypography).filter(Boolean);
                  ancestors.forEach(writeTypography);
                  for (const metric of metrics) {
                    typographyState.set(metric.element, metric);
                    writeTypography(metric);
                  }
                };
                const pendingTypographyRoots = new Set();
                let typographyScheduled = false;
                const queueTypography = roots => {
                  if (!typographyEnabled) return;
                  roots.forEach(root => {
                    if (root instanceof Element) pendingTypographyRoots.add(root);
                  });
                  if (typographyScheduled || !pendingTypographyRoots.size) return;
                  typographyScheduled = true;
                  requestAnimationFrame(() => {
                    typographyScheduled = false;
                    const batch = [...pendingTypographyRoots];
                    pendingTypographyRoots.clear();
                    applyTypographyBatch(batch);
                    reportSize();
                  });
                };
                const send = (kind, extra = {}) =>
                  parent.postMessage({ channel:"vdict-frame", kind, ...extra }, "*");
                addEventListener("error", event => send("browserDiagnostic", {
                  category:"error",
                  message:event.message || "Script or resource error",
                  source:event.filename || event.target?.currentSrc || event.target?.src || ""
                }), true);
                addEventListener("unhandledrejection", event => send("browserDiagnostic", {
                  category:"unhandledrejection",
                  message:String(event.reason?.message || event.reason || "Unhandled promise rejection")
                }));
                addEventListener("securitypolicyviolation", event => send("browserDiagnostic", {
                  category:"csp",
                  message:event.blockedURI || "",
                  source:event.sourceFile || "",
                  directive:event.effectiveDirective || event.violatedDirective || ""
                }));
                let currentAudio = null;
                const audioPool = new Map();
                const audioTiming = new WeakMap();
                const warmupPending = new Map();
                const warmupDiagnostics = {
                  scheduled:0, started:0, completed:0, cancelled:0, failed:0, sources:[]
                };
                window.__vdictAudioWarmupDiagnostics = warmupDiagnostics;
                let warmupDelayId = 0;
                let warmupIdleId = 0;
                let warmupFallbackId = 0;
                let warmupCancelled = false;
                const resourceLike = href => /\.(?:avif|bmp|gif|ico|jpe?g|png|svg|webp|css|js|ttf|otf|woff2?|mp3|wav|ogg|aac|m4a|flac|opus|webm|wma|mp4)(?:[?#]|$)/i.test(href);
                const audioLike = href => /\.(?:mp3|wav|ogg|aac|m4a|flac|opus|webm|wma)(?:[?#]|$)/i.test(href);
                const audioTriggerSelector =
                  "[data-audio-src],.vdict-audio-trigger,.sound,.audio_play_button,a[href]";
                const resourceBaseUrl = new URL(document.baseURI);
                const mediaState = (audio, eventName, extra = {}) => send("mediaEvent", {
                  eventName,
                  readyState:audio.readyState,
                  networkState:audio.networkState,
                  currentSrc:audio.currentSrc || audio.src || "",
                  preload:audio.preload,
                  paused:audio.paused,
                  ended:audio.ended,
                  currentTime:Number.isFinite(audio.currentTime) ? audio.currentTime : 0,
                  duration:Number.isFinite(audio.duration) ? audio.duration : -1,
                  errorCode:audio.error?.code || 0,
                  errorMessage:String(audio.error?.message || ""),
                  userActivationActive:Boolean(navigator.userActivation?.isActive),
                  ...extra
                });
                const warmupState = (eventName, extra = {}) =>
                  send("mediaWarmup", { eventName, ...extra });
                const touchAudio = (src, audio) => {
                  audioPool.delete(src);
                  audioPool.set(src, audio);
                };
                const releaseAudio = (src, audio, eventName) => {
                  audio.pause();
                  try { audio.currentTime = 0; } catch {}
                  audio.removeAttribute("src");
                  try { audio.load(); } catch {}
                  mediaState(audio, eventName);
                  audioPool.delete(src);
                };
                const settleWarmup = (src, eventName, errorName = "") => {
                  const pending = warmupPending.get(src);
                  if (!pending) return;
                  warmupPending.delete(src);
                  const elapsedMs = performance.now() - pending.startedAt;
                  if (eventName === "completed") warmupDiagnostics.completed++;
                  else warmupDiagnostics.failed++;
                  warmupState(eventName, {
                    src, pendingCount:warmupPending.size, elapsedMs, errorName
                  });
                };
                function audioFor(src, preload = "metadata") {
                  const normalized = new URL(src, document.baseURI).href;
                  let audio = audioPool.get(normalized);
                  if (!audio) {
                    audio = new Audio();
                    audio.preload = preload;
                    audio.src = normalized;
                    for (const eventName of [
                      "loadedmetadata", "loadeddata", "canplay", "canplaythrough",
                      "play", "playing", "pause", "waiting", "stalled", "suspend",
                      "abort", "emptied", "ended"
                    ]) {
                      audio.addEventListener(eventName, () => {
                        const timing = audioTiming.get(audio);
                        const playLatencyMs = eventName === "playing" && timing?.playRequestedAt
                          ? performance.now() - timing.playRequestedAt
                          : 0;
                        mediaState(audio, eventName, { playLatencyMs });
                        if (eventName === "loadeddata") settleWarmup(normalized, "completed");
                      });
                    }
                    audio.addEventListener("loadedmetadata", () => send("mediaMetadata", {
                      duration: Number.isFinite(audio.duration) ? audio.duration : -1
                    }));
                    audio.addEventListener("error", () => {
                      settleWarmup(
                        normalized,
                        "failed",
                        String(audio.error?.message || "MediaError"));
                      send("mediaError", {
                        code: audio.error?.code || 0,
                        readyState: audio.readyState,
                        networkState: audio.networkState,
                        extension: (new URL(audio.currentSrc || audio.src, document.baseURI)
                          .pathname.match(/\.[^.\/]+$/)?.[0] || "").slice(0, 16)
                      });
                    });
                    mediaState(audio, "created");
                    touchAudio(normalized, audio);
                    while (audioPool.size > 4) {
                      const oldest = audioPool.keys().next().value;
                      if (oldest === normalized) break;
                      const evicted = audioPool.get(oldest);
                      if (evicted) releaseAudio(oldest, evicted, "evicted");
                    }
                  } else {
                    if (preload === "auto") audio.preload = "auto";
                    touchAudio(normalized, audio);
                  }
                  return audio;
                }
                const localAudioSources = () => {
                  const sources = [];
                  const seen = new Set();
                  for (const node of document.querySelectorAll(audioTriggerSelector)) {
                    const link = node.matches("a[href]") ? node : node.querySelector?.("a[href]");
                    const value = node.dataset.audioSrc ||
                      node.getAttribute("href") ||
                      link?.dataset.audioSrc || link?.getAttribute("href") || "";
                    if (!value) continue;
                    let candidate;
                    try { candidate = new URL(value, document.baseURI); } catch { continue; }
                    if (candidate.protocol !== resourceBaseUrl.protocol ||
                        candidate.origin !== resourceBaseUrl.origin ||
                        !candidate.pathname.startsWith(resourceBaseUrl.pathname) ||
                        !audioLike(candidate.pathname) ||
                        seen.has(candidate.href)) continue;
                    seen.add(candidate.href);
                    sources.push(candidate.href);
                    if (sources.length === 2) break;
                  }
                  return sources;
                };
                const beginAudioWarmup = () => {
                  warmupIdleId = 0;
                  warmupFallbackId = 0;
                  if (warmupCancelled) return;
                  const sources = localAudioSources();
                  warmupDiagnostics.started++;
                  warmupDiagnostics.sources = [...sources];
                  warmupState("started", { sourceCount:sources.length });
                  if (!sources.length) {
                    warmupState("completed", { sourceCount:0, pendingCount:0 });
                    return;
                  }
                  for (const src of sources) {
                    try {
                      const audio = audioFor(src, "auto");
                      warmupPending.set(src, {
                        audio,
                        startedAt:performance.now()
                      });
                      audio.preload = "auto";
                      audio.load();
                      warmupState("requested", {
                        src, sourceCount:sources.length, pendingCount:warmupPending.size
                      });
                    } catch (error) {
                      warmupDiagnostics.failed++;
                      warmupState("failed", {
                        src,
                        pendingCount:warmupPending.size,
                        errorName:String(error?.name || "Error")
                      });
                    }
                  }
                };
                const scheduleAudioWarmup = () => {
                  if (warmupCancelled || warmupDelayId) return;
                  warmupDiagnostics.scheduled++;
                  warmupState("scheduled", { elapsedMs:120 });
                  warmupDelayId = setTimeout(() => {
                    warmupDelayId = 0;
                    if (warmupCancelled) return;
                    if ("requestIdleCallback" in window) {
                      warmupIdleId = requestIdleCallback(beginAudioWarmup, { timeout:600 });
                    } else {
                      warmupFallbackId = setTimeout(beginAudioWarmup, 0);
                    }
                  }, 120);
                };
                const cancelAudioWarmup = () => {
                  const wasActive =
                    Boolean(warmupDelayId || warmupIdleId || warmupFallbackId) ||
                    warmupPending.size > 0;
                  warmupCancelled = true;
                  if (warmupDelayId) clearTimeout(warmupDelayId);
                  if (warmupFallbackId) clearTimeout(warmupFallbackId);
                  if (warmupIdleId && "cancelIdleCallback" in window) {
                    cancelIdleCallback(warmupIdleId);
                  }
                  warmupDelayId = 0;
                  warmupIdleId = 0;
                  warmupFallbackId = 0;
                  if (wasActive) {
                    warmupDiagnostics.cancelled++;
                    warmupState("cancelled", { pendingCount:warmupPending.size });
                  }
                  warmupPending.clear();
                };
                function play(src) {
                  if (!src) return;
                  const audio = audioFor(src);
                  if (currentAudio && currentAudio !== audio) currentAudio.pause();
                  currentAudio = audio;
                  touchAudio(audio.src, audio);
                  try { audio.currentTime = 0; } catch {}
                  audioTiming.set(audio, { playRequestedAt:performance.now() });
                  mediaState(audio, "playAttempt");
                  const promise = audio.play();
                  promise.then(
                    () => mediaState(audio, "playPromise", { promiseOutcome:"resolved" }),
                    error => {
                      mediaState(audio, "playPromise", {
                        promiseOutcome:String(error?.name || "rejected"),
                        errorMessage:String(error?.message || "")
                      });
                      send("mediaError", {
                        code: audio.error?.code || 0,
                        readyState: audio.readyState || 0,
                        networkState: audio.networkState || 0,
                        extension: "playback",
                        errorName: String(error?.name || ""),
                        errorMessage: String(error?.message || "")
                      });
                    });
                }
                function stopMedia() {
                  cancelAudioWarmup();
                  for (const [src, audio] of [...audioPool]) {
                    releaseAudio(src, audio, "released");
                  }
                  currentAudio = null;
                }
                window.vdictStopMedia = stopMedia;
                addEventListener("wheel", event => {
                  if (event.isTrusted) send("inputEvent", { eventName:event.type });
                  if (!event.ctrlKey) return;
                  event.preventDefault();
                  send("zoomDelta", { delta:Math.max(-1000, Math.min(1000, event.deltaY)) });
                }, { passive:false, capture:true });
                addEventListener("keydown", event => {
                  if (!event.ctrlKey || event.altKey || event.metaKey) return;
                  if (event.key === "0") {
                    event.preventDefault();
                    send("zoomReset");
                    return;
                  }
                  const step = ["+", "=", "Add"].includes(event.key) ? 1 :
                    ["-", "_", "Subtract"].includes(event.key) ? -1 : 0;
                  if (!step) return;
                  event.preventDefault();
                  send("zoomStep", { step });
                }, { capture:true });
                addEventListener("message", event => {
                  if (event.source !== parent ||
                      event.origin !== "{{DictionaryNetworkPolicy.ShellOrigin}}" ||
                      event.data?.channel !== "vdict-host" ||
                      event.data?.renderToken !== renderToken) return;
                  if (event.data.kind === "reportSize") requestAnimationFrame(reportSize);
                  if (event.data.kind === "applyTheme" &&
                      event.data.frameId === frameId) {
                    window.vdictApplyTheme?.(
                      Boolean(event.data.dark),
                      renderToken,
                      frameId);
                  }
                });
                const contentRoot = document.documentElement;
                const explicitFallbackSelector =
                  "[aria-controls],[aria-expanded],[data-target],[data-bs-target]," +
                  "[data-toggle],[data-bs-toggle]";
                const uniqueElements = values => [...new Set(values.filter(value =>
                  value instanceof Element && value !== contentRoot))];
                const rendered = element => {
                  const style = getComputedStyle(element);
                  const rect = element.getBoundingClientRect();
                  return !element.hidden && style.display !== "none" &&
                    style.visibility !== "hidden" && (rect.width > 0 || rect.height > 0);
                };
                function targetFromSelector(selector) {
                  if (!selector || !selector.startsWith("#")) return null;
                  let id = selector.slice(1);
                  try { id = decodeURIComponent(id); } catch {}
                  return document.getElementById(id);
                }
                function explicitFallbackTargets(trigger) {
                  const controlled = trigger.getAttribute("aria-controls");
                  if (controlled) {
                    const targets = controlled.split(/\s+/)
                      .map(id => document.getElementById(id));
                    if (targets.some(Boolean)) return uniqueElements(targets);
                  }
                  const selector = trigger.getAttribute("data-target") ||
                    trigger.getAttribute("data-bs-target") || trigger.getAttribute("href") || "";
                  const byId = targetFromSelector(selector);
                  if (byId) return [byId];
                  if (selector) {
                    try {
                      const selected = document.querySelector(selector);
                      if (selected) return [selected];
                    } catch {}
                  }
                  return [];
                }
                function fallbackInteractionFor(trigger, explicit = false) {
                  if (!(trigger instanceof Element) || trigger === contentRoot ||
                      trigger.closest("details") || !contentRoot?.contains(trigger)) return null;
                  const targets = explicitFallbackTargets(trigger);
                  const structuralTargets = targets;
                  if (structuralTargets.length === 0 ||
                      structuralTargets.some(target => target === trigger || trigger.contains(target))) return null;
                  return {
                    trigger,
                    targets:structuralTargets,
                    container:trigger.parentElement,
                    explicit:explicit || targets.length > 0
                  };
                }
                function findFallbackInteraction(start) {
                  if (!(start instanceof Element)) return null;
                  const explicitTrigger = start.closest(explicitFallbackSelector);
                  const explicit = fallbackInteractionFor(explicitTrigger, true);
                  if (explicit) return explicit;
                  return null;
                }
                function applyFallbackInteraction(interaction) {
                  const { trigger, targets, container } = interaction;
                  if (!targets.length) return false;
                  const ariaExpanded = trigger.getAttribute("aria-expanded");
                  const expanded = ariaExpanded === "false" ||
                    (ariaExpanded !== "true" && targets.every(target => !rendered(target)));
                  for (const target of targets) {
                    if (expanded) {
                      target.hidden = false;
                      target.removeAttribute("aria-hidden");
                      target.style.removeProperty("display");
                      if (!rendered(target)) {
                        const display = target.dataset.vdictDisplay ||
                          (target.tagName === "TABLE" ? "table" :
                           target.tagName === "LI" ? "list-item" : "block");
                        target.style.setProperty("display", display, "important");
                      }
                    } else {
                      target.dataset.vdictDisplay = target.style.getPropertyValue("display") || "";
                      target.hidden = true;
                      target.setAttribute("aria-hidden", "true");
                      target.style.setProperty("display", "none", "important");
                    }
                  }
                  trigger.setAttribute("aria-expanded", String(expanded));
                  container?.setAttribute("data-vdict-expanded", String(expanded));
                  return true;
                }
                function activationSource(event) {
                  if (event.type === "keydown") return "keyboard";
                  return event.pointerType === "touch" ? "touch" :
                    event.pointerType === "pen" ? "pen" : "mouse";
                }
                function reportUserActivation(event) {
                  if (!event.isTrusted) return;
                  if (event.type === "keydown") {
                    if (event.key !== "Enter" && event.key !== " ") return;
                  } else if (event.pointerType === "mouse" && event.button !== 0) {
                    return;
                  }
                  send("userActivation", { source:activationSource(event) });
                  sendInputEvent(event);
                }
                function sendInputEvent(event) {
                  const target = event.target instanceof Element ? event.target : null;
                  const trigger = target?.closest(audioTriggerSelector);
                  send("inputEvent", {
                    eventName:event.type,
                    timestamp:performance.now(),
                    trusted:event.isTrusted,
                    targetTag:target?.tagName || "",
                    targetId:target?.id || "",
                    targetClass:String(target?.className || ""),
                    triggerTag:trigger?.tagName || "",
                    triggerClass:String(trigger?.className || ""),
                    triggerHref:trigger?.dataset.audioSrc || trigger?.getAttribute("href") || ""
                  });
                }
                addEventListener("pointerdown", reportUserActivation, true);
                addEventListener("keydown", reportUserActivation, true);
                addEventListener("pointerup", event => {
                  if (event.isTrusted) sendInputEvent(event);
                }, true);
                addEventListener("click", event => {
                  if (event.isTrusted) sendInputEvent(event);
                }, true);
                addEventListener("contextmenu", event => {
                  if (event.isTrusted) send("inputEvent", { eventName:event.type });
                }, true);
                const interactionSignature = interaction => JSON.stringify({
                  trigger:{
                    expanded:interaction.trigger.getAttribute("aria-expanded"),
                    hidden:interaction.trigger.hidden,
                    display:interaction.trigger.style.getPropertyValue("display"),
                    classes:String(interaction.trigger.className || "")
                  },
                  container:{
                    hidden:interaction.container?.hidden || false,
                    display:interaction.container?.style.getPropertyValue("display") || "",
                    classes:String(interaction.container?.className || "")
                  },
                  targets:interaction.targets.map(target => {
                    const style = getComputedStyle(target);
                    const rect = target.getBoundingClientRect();
                    return {
                      hidden:target.hidden,
                      ariaHidden:target.getAttribute("aria-hidden"),
                      display:target.style.getPropertyValue("display"),
                      computedDisplay:style.display,
                      visibility:style.visibility,
                      width:Math.round(rect.width * 100) / 100,
                      height:Math.round(rect.height * 100) / 100
                    };
                  })
                });
                const reportFallbackSize = () => requestAnimationFrame(() => {
                  reportSize();
                  setTimeout(reportSize, 80);
                });
                const finishFallback = (interaction, signature) => {
                  if (interactionSignature(interaction) !== signature) return;
                  if (applyFallbackInteraction(interaction)) reportFallbackSize();
                };
                const fallbackSignatures = new WeakMap();
                addEventListener("click", event => {
                  if (!event.isTrusted || interactionMode === "native") return;
                  const target = event.target instanceof Element ? event.target : null;
                  const interaction = target ? findFallbackInteraction(target) : null;
                  if (interaction) {
                    fallbackSignatures.set(event, {
                      interaction,
                      signature:interactionSignature(interaction)
                    });
                  }
                }, true);
                function activate(event) {
                  if (!event.isTrusted) return;
                  if (event.type === "keydown" && event.key !== "Enter" && event.key !== " ") return;
                  const target = event.target instanceof Element ? event.target : null;
                  if (!target) return;
                  if (event.defaultPrevented) return;
                  const capturedFallback = event.type === "click"
                    ? fallbackSignatures.get(event)
                    : null;
                  const fallbackInteraction = capturedFallback?.interaction || null;
                  const clicked = target.closest(
                    "[data-audio-src],.vdict-audio-trigger,.sound,.audio_play_button," +
                    "a[href],button,[role=button],summary,img");
                  const audioTrigger = target.closest(audioTriggerSelector);
                  const audioLink = audioTrigger?.closest("a[href]") ||
                    audioTrigger?.querySelector?.("a[href]");
                  const audioSource = audioTrigger?.dataset.audioSrc ||
                    audioTrigger?.getAttribute("href") ||
                    audioLink?.dataset.audioSrc || audioLink?.getAttribute("href") || "";
                  if (audioTrigger && audioSource &&
                      (audioLike(audioSource) ||
                       audioSource.toLowerCase().startsWith("sound://") ||
                       audioTrigger.matches("[data-audio-src],.vdict-audio-trigger,.sound,.audio_play_button"))) {
                    event.preventDefault();
                    play(new URL(audioSource, document.baseURI).href);
                    return;
                  }
                  const link = clicked?.closest("a[href]");
                  const href = link?.getAttribute("href") || "";
                  if (target.closest("img") || link?.querySelector("img")) return;
                  if (link && (href.toLowerCase().startsWith("sound://") || audioLike(href))) {
                    event.preventDefault(); play(link.dataset.audioSrc || href); return;
                  }
                  if (link && href.toLowerCase().startsWith("entry://")) {
                    event.preventDefault();
                    let word = href.slice(8); try { word = decodeURIComponent(word); } catch {}
                    send("entry", { word }); return;
                  }
                  if (link && href.startsWith("#") && !fallbackInteraction) {
                    event.preventDefault();
                    let id = href.slice(1); try { id = decodeURIComponent(id); } catch {}
                    (document.getElementById(id) || document.getElementsByName(id)[0])
                      ?.scrollIntoView({ block:"start" });
                    return;
                  }
                  if (link && /^(?:https?|mailto):/i.test(href)) {
                    if (blockExternalLinks) {
                      event.preventDefault();
                      send("browserDiagnostic", {
                        category:"external-blocked",
                        message:href,
                        source:""
                      });
                    }
                    // System-browser mode lets WebView2 validate native user initiation.
                    return;
                  }
                  if (link && !resourceLike(href) && !fallbackInteraction) {
                    event.preventDefault(); send("entry", { word:href }); return;
                  }
                  if (!fallbackInteraction || interactionMode === "native") return;
                  const signature = capturedFallback.signature;
                  if (interactionMode === "fallback") {
                    if (interactionSignature(fallbackInteraction) !== signature) return;
                    event.preventDefault();
                    if (applyFallbackInteraction(fallbackInteraction)) reportFallbackSize();
                  } else if (interactionMode === "auto") {
                    setTimeout(() => setTimeout(() => requestAnimationFrame(() =>
                      finishFallback(fallbackInteraction, signature)), 0), 0);
                  }
                }
                addEventListener("click", activate);
                addEventListener("keydown", activate);
                let lastHeight = 0;
                const finite = value => Number.isFinite(value) ? value : 0;
                const renderedForSize = element => {
                  const style = getComputedStyle(element);
                  if (style.display === "none" || style.visibility === "hidden" ||
                      style.position === "fixed" || element.hidden) return false;
                  return element.getClientRects().length > 0;
                };
                const reportSize = () => {
                  if (!document.body) return;
                  let bottom = 0;
                  const elements = [document.body, ...document.body.querySelectorAll("*")];
                  for (const element of elements) {
                    if (!renderedForSize(element)) continue;
                    const rect = element.getBoundingClientRect();
                    const style = getComputedStyle(element);
                    bottom = Math.max(
                      bottom,
                      rect.bottom + scrollY + finite(parseFloat(style.marginBottom)));
                  }
                  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
                  const range = document.createRange();
                  while (walker.nextNode()) {
                    if (!walker.currentNode.textContent?.trim()) continue;
                    range.selectNodeContents(walker.currentNode);
                    for (const rect of range.getClientRects()) {
                      bottom = Math.max(bottom, rect.bottom + scrollY);
                    }
                  }
                  range.detach();
                  const height = Math.ceil(Math.max(1, bottom));
                  if (height === lastHeight) return;
                  lastHeight = height;
                  send("resize", { height });
                };
                document.addEventListener("transitionend", reportSize, true);
                document.addEventListener("animationend", reportSize, true);
                document.addEventListener("toggle", reportSize, true);
                addEventListener("resize", reportSize);
                addEventListener("beforeunload", stopMedia, { once:true });
                addEventListener("load", () => {
                  if (targetFragment) {
                    (document.getElementById(targetFragment) || document.getElementsByName(targetFragment)[0])
                      ?.scrollIntoView({ block:"start" });
                  }
                  reportSize();
                  send("frameLoad");
                });
                const observedImages = new WeakSet();
                const observeImages = () => document.querySelectorAll("img").forEach(img => {
                  if (observedImages.has(img)) return;
                  observedImages.add(img);
                  img.addEventListener("load", () => requestAnimationFrame(reportSize), { once:true });
                  img.addEventListener("error", () => {
                    const src = img.currentSrc || img.src;
                    const timing = performance.getEntriesByName(src).at(-1);
                    send("imageError", {
                      src,
                      originalSrc:img.getAttribute("data-vdict-original-src") || img.getAttribute("src") || "",
                      complete:img.complete,
                      naturalWidth:img.naturalWidth,
                      naturalHeight:img.naturalHeight,
                      duration:timing?.duration || 0,
                      transferSize:timing?.transferSize || 0,
                      encodedBodySize:timing?.encodedBodySize || 0,
                      decodedBodySize:timing?.decodedBodySize || 0
                    });
                    requestAnimationFrame(reportSize);
                  }, { once:true });
                });
                const initialize = () => {
                  const initializeStarted = debugTimingEnabled ? performance.now() : 0;
                  const typographyStarted = debugTimingEnabled ? performance.now() : 0;
                  try {
                    applyTypographyBatch([document.body]);
                  } catch {
                    // Malformed dictionary DOM must not disable media, navigation, or sizing.
                  } finally {
                    clearTimeout(typographyFallback);
                    revealTypography();
                  }
                  const typographyMilliseconds = debugTimingEnabled
                    ? performance.now() - typographyStarted
                    : 0;
                  if (typographyEnabled) {
                    const typographyObserver = new MutationObserver(records => {
                      const roots = records.flatMap(record =>
                        [...record.addedNodes].filter(node => node instanceof Element));
                      queueTypography(roots);
                    });
                    typographyObserver.observe(document.body, {
                      childList:true, subtree:true
                    });
                  }
                  const observer = new ResizeObserver(reportSize);
                  observer.observe(document.body);
                  for (const child of document.body.children) observer.observe(child);
                  const mutationObserver = new MutationObserver(() => {
                    observeImages();
                    requestAnimationFrame(reportSize);
                  });
                  mutationObserver.observe(document.body, {
                    attributes:true, childList:true, subtree:true,
                    attributeFilter:["class","style","hidden","open","aria-expanded"]
                  });
                  observeImages();
                  document.fonts?.ready.then(reportSize).catch(() => {});
                  const initialSizeStarted = debugTimingEnabled ? performance.now() : 0;
                  reportSize();
                  const initialSizeMilliseconds = debugTimingEnabled
                    ? performance.now() - initialSizeStarted
                    : 0;
                  window.__vdictRuntimeReady = true;
                  if (debugTimingEnabled) {
                    send("browserDiagnostic", {
                      category:"debug-frame-timing",
                      message:JSON.stringify({
                        typographyMilliseconds,
                        initialSizeMilliseconds,
                        initializeMilliseconds:performance.now() - initializeStarted,
                        elementCount:document.body.querySelectorAll("*").length,
                        textNodeCount:(() => {
                          let count = 0;
                          const walker = document.createTreeWalker(
                            document.body, NodeFilter.SHOW_TEXT);
                          while (walker.nextNode()) count++;
                          return count;
                        })()
                      }),
                      source:""
                    });
                  }
                  send("frameReady", { hasContent:Boolean(document.body.textContent?.trim()) });
                  scheduleAudioWarmup();
                };
                if (document.readyState === "loading") {
                  document.addEventListener("DOMContentLoaded", initialize, { once:true });
                } else {
                  initialize();
                }
                })();
              </script>
              {{safeHead}}
              {{customCss}}
              {{customScripts}}
              {{compatibilityCss}}
              {{preferenceCss}}
              {{themeCss}}
              {{darkModeBootstrap}}
            </head>
            <body{{bodyAttributeSuffix}}>{{safeContent}}</body>
            </html>
            """;
        return new ReaderFrameBuildResult(html, normalized.Diagnostics);
    }

    public static string ExtractCspNonce(string html)
    {
        Match match = CspNonce().Match(html);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    private static string BuildFrameElement(ReaderFrame frame, ReaderDocument document)
    {
        string uri = DictionaryNetworkPolicy.FrameUriForDictionary(
            frame.DictionaryId.Value,
            document.RenderToken,
            frame.FrameId);
        return
            $"<iframe class=\"entry\" sandbox=\"allow-scripts allow-same-origin\" " +
            $"title=\"Dictionary entry {WebUtility.HtmlEncode(frame.DictionaryId.Value)}\" " +
            $"data-frame-id=\"{WebUtility.HtmlEncode(frame.FrameId)}\" " +
            $"data-dictionary-id=\"{WebUtility.HtmlEncode(frame.DictionaryId.Value)}\" " +
            $"src=\"{WebUtility.HtmlEncode(uri)}\"></iframe>";
    }

    private static string NewNonce() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));

    private static string PrepareDictionaryScripts(
        string html,
        string nonce,
        CancellationToken cancellationToken = default)
    {
        HtmlScanResult scan = HtmlLexicalScanner.Scan(html, cancellationToken);
        StringBuilder? output = null;
        int position = 0;
        int tokenIndex = 0;
        foreach (HtmlToken token in scan.Tokens)
        {
            if ((tokenIndex++ & 0xff) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (token.Kind != HtmlTokenKind.StartTag ||
                !token.NameEquals(html, "script"))
            {
                continue;
            }

            (int start, int length) =
                token.SourceRange.GetOffsetAndLength(html.Length);
            int insertion = start + length - (token.SelfClosing ? 2 : 1);
            output ??= new StringBuilder(html.Length + 64);
            output.Append(html, position, insertion - position);
            output.Append(" nonce=\"");
            output.Append(WebUtility.HtmlEncode(nonce));
            output.Append('"');
            position = insertion;
        }

        if (output is null)
        {
            return html;
        }

        output.Append(html, position, html.Length - position);
        return output.ToString();
    }

    private static string BuildDictionaryPreferenceCss(bool typographyEnabled) =>
        typographyEnabled
            ? "<style data-vdict-layer=\"preferences\">" +
              "html.vdict-typography-pending body{visibility:hidden!important;}" +
              "</style>"
            : string.Empty;

    private static bool ReferencesScriptFile(string html, string candidateUri)
    {
        string candidateName = FileNameFromUri(candidateUri);
        if (candidateName.Length == 0)
        {
            return false;
        }

        return ScriptSourceAttribute().Matches(html).Any(match =>
        {
            string source = match.Groups["double"].Success
                ? match.Groups["double"].Value
                : match.Groups["single"].Success
                    ? match.Groups["single"].Value
                    : match.Groups["bare"].Value;
            return FileNameFromUri(WebUtility.HtmlDecode(source)).Equals(
                candidateName,
                StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string FileNameFromUri(string value)
    {
        try
        {
            Uri uri = new(value, UriKind.RelativeOrAbsolute);
            string path = uri.IsAbsoluteUri ? uri.AbsolutePath : value.Split(['?', '#'], 2)[0];
            return Uri.UnescapeDataString(Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception exception)
            when (exception is UriFormatException or ArgumentException)
        {
            return string.Empty;
        }
    }

    private static string BuildDarkModeBootstrap(
        ReaderDarkModeAssets assets,
        string nonce,
        bool reduceBrightness,
        bool initialDark,
        bool allowDynamic,
        string renderToken,
        string frameId)
    {
        string script = Regex.Replace(
            assets.Script,
            "</script",
            "<\\/script",
            RegexOptions.IgnoreCase);
        string overrides = Regex.Replace(
            assets.Overrides,
            "</style",
            "<\\/style",
            RegexOptions.IgnoreCase);
        int brightness = reduceBrightness ? 88 : 100;
        string initialDarkJson = JsonSerializer.Serialize(initialDark);
        string allowDynamicJson = JsonSerializer.Serialize(allowDynamic);
        string renderTokenJson = JsonSerializer.Serialize(renderToken);
        string frameIdJson = JsonSerializer.Serialize(frameId);
        return $$"""
            <style id="app-dark-mode-overrides" data-vdict-layer="theme">{{overrides}}</style>
            <script nonce="{{nonce}}">{{script}}</script>
            <script nonce="{{nonce}}">
              (() => {
                const expectedRenderToken = {{renderTokenJson}};
                const expectedFrameId = {{frameIdJson}};
                const allowDynamic = {{allowDynamicJson}};
                let requestedDark = {{initialDarkJson}};
                let appliedDark = null;
                let scheduled = false;
                const reveal = () =>
                  document.documentElement.classList.remove("vdict-await-dark");
                const applyRequestedTheme = () => {
                  scheduled = false;
                  if (appliedDark === requestedDark) return;
                  try {
                    if (requestedDark) {
                      document.documentElement.classList.add("dark");
                      document.documentElement.classList.remove("original");
                      document.documentElement.dataset.vdictDarkEngine = "darkreader";
                      DarkReader.enable({
                        brightness: {{brightness}},
                        contrast: 90,
                        sepia: 0,
                        grayscale: 0,
                        darkSchemeBackgroundColor: "#111418",
                        darkSchemeTextColor: "#D3D8E0"
                      });
                    } else {
                      DarkReader.disable();
                      document.documentElement.classList.remove("dark");
                      document.documentElement.classList.add("original");
                      document.documentElement.dataset.vdictDarkEngine = "original";
                    }
                    appliedDark = requestedDark;
                    requestAnimationFrame(() => {
                      requestAnimationFrame(() => {
                        reveal();
                        window.dispatchEvent(new Event("resize"));
                      });
                    });
                  } catch (_) {
                    reveal();
                  }
                };
                window.vdictApplyTheme = (dark, token, targetFrameId) => {
                  if (!allowDynamic ||
                      token !== expectedRenderToken ||
                      targetFrameId !== expectedFrameId) return false;
                  requestedDark = Boolean(dark);
                  if (!scheduled) {
                    scheduled = true;
                    queueMicrotask(applyRequestedTheme);
                  }
                  return true;
                };
                applyRequestedTheme();
                setTimeout(reveal, 160);
              })();
            </script>
            """;
    }

    private static string Invariant(double value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string CssFontFamily(params string[] names) =>
        string.Join(
            ",",
            names.Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name =>
                    "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal)
                        .Replace("\"", "\\\"", StringComparison.Ordinal)
                        .Replace("\r", string.Empty, StringComparison.Ordinal)
                        .Replace("\n", string.Empty, StringComparison.Ordinal) + "\"")
                .Append("sans-serif"));
}
