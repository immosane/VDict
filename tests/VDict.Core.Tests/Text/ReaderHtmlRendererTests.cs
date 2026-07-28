using System.Text.RegularExpressions;
using VDict.Core.Models;
using VDict.Core.Policies;
using VDict.Core.Text;

namespace VDict.Core.Tests.Text;

public sealed class ReaderHtmlRendererTests
{
    [Fact]
    public void FrameBuild_ObservesCancellation()
    {
        ReaderFrame frame = new(
            "frame",
            new DictionaryId("dict"),
            new string('x', 64 * 1024));
        ReaderDocument document = new("word", "", "token", [frame], []);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => ReaderHtmlRenderer.BuildFrameDocument(
                frame,
                document,
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public void Frame_CompositeBodiesPreserveFirstMiddleAndLastPayload()
    {
        ReaderFrame frame = new(
            "frame",
            new DictionaryId("dict"),
            "<p>first</p><body><p>middle</p></body><p>last</p>");
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);

        Assert.Contains("first", html);
        Assert.Contains("middle", html);
        Assert.Contains("last", html);
    }

    [Fact]
    public void ShellAndFrame_ExposeTheSingleProductionRendererMarker()
    {
        ReaderFrame frame = new("frame", new DictionaryId("dict"), "<p>body</p>");
        ReaderDocument document = new("word", "", "token", [frame], []);

        Assert.Contains(ReaderHtmlRenderer.ProductionMarker, ReaderHtmlRenderer.BuildShell(document));
        Assert.Contains(
            ReaderHtmlRenderer.ProductionMarker,
            ReaderHtmlRenderer.BuildFrameDocument(frame, document));
    }

    [Fact]
    public void FrameSandboxAndCsp_UseSameOriginCapabilityArchitecture()
    {
        string capability = DictionaryNetworkPolicy.ResourceCapabilityForDictionary("dict", 4);
        ReaderFrame frame = new("frame", new DictionaryId("dict"), "<p>body</p>")
        {
            CatalogGeneration = 4,
            ResourceCapability = capability
        };
        ReaderDocument document = new("word", "", "token", [frame], []);

        string shell = ReaderHtmlRenderer.BuildShell(document);
        string frameHtml = ReaderHtmlRenderer.BuildFrameDocument(frame, document);

        Assert.Contains("sandbox=\"allow-scripts allow-same-origin\"", shell);
        Assert.Contains("frame-src vdict-doc://*.vdict.invalid", shell);
        Assert.DoesNotContain("frame-src https://*.vdict.invalid", shell);
        Assert.Contains("default-src 'none'", frameHtml);
        Assert.Contains("img-src 'self' data: blob:", frameHtml);
        Assert.DoesNotContain("img-src http:", frameHtml);
        Assert.DoesNotContain("img-src https:", frameHtml);
    }

    [Fact]
    public void Frame_UsesFreshNonceAndReauthorizesDictionaryScripts()
    {
        ReaderFrame frame = new(
            "frame",
            new DictionaryId("dict"),
            """<p nonce="attacker">body</p><script nonce="attacker">steal()</script>""");
        ReaderDocument document = new("word", "", "token", [frame], []);

        string first = ReaderHtmlRenderer.BuildFrameDocument(frame, document);
        string second = ReaderHtmlRenderer.BuildFrameDocument(frame, document);

        Assert.DoesNotContain("nonce=\"attacker\"", first);
        Assert.Contains("steal()", first);
        Assert.NotEqual(
            ReaderHtmlRenderer.ExtractCspNonce(first),
            ReaderHtmlRenderer.ExtractCspNonce(second));
    }

    [Fact]
    public void LegacyNetworkFlag_DoesNotChangeOfflineCsp()
    {
        const string content =
            """<script src="https://example.test/dict.js"></script><script>local()</script>""";
        ReaderFrame blocked = new("blocked", new DictionaryId("dict"), content)
        {
            BlockNetwork = true
        };
        ReaderFrame allowed = blocked with
        {
            FrameId = "allowed",
            BlockNetwork = false
        };
        ReaderDocument document = new("word", "", "token", [blocked, allowed], []);

        string blockedHtml = ReaderHtmlRenderer.BuildFrameDocument(blocked, document);
        string allowedHtml = ReaderHtmlRenderer.BuildFrameDocument(allowed, document);

        Assert.Contains("https://example.test/dict.js", blockedHtml);
        Assert.Contains("https://example.test/dict.js", allowedHtml);
        Assert.DoesNotContain("'unsafe-eval'", blockedHtml);
        Assert.DoesNotContain("'unsafe-eval'", allowedHtml);
        Assert.Contains("connect-src 'self'", blockedHtml);
        Assert.Contains("connect-src 'self'", allowedHtml);
    }

    [Fact]
    public void LocalScriptModes_DifferOnlyByLegacyEvalCompatibility()
    {
        ReaderFrame standard = new("standard", new DictionaryId("dict"),
            "<button onclick=\"local()\">x</button><script type=\"module\">fetch('data.json')</script>");
        ReaderFrame legacy = standard with
        {
            FrameId = "legacy",
            LocalScriptMode = LocalScriptMode.Legacy
        };
        ReaderDocument document = new("word", "", "token", [standard, legacy], []);

        string standardHtml = ReaderHtmlRenderer.BuildFrameDocument(standard, document);
        string legacyHtml = ReaderHtmlRenderer.BuildFrameDocument(legacy, document);

        Assert.Contains("script-src 'self' 'unsafe-inline'", standardHtml);
        Assert.DoesNotContain("'unsafe-eval'", standardHtml);
        Assert.Contains("'unsafe-eval'", legacyHtml);
        Assert.Contains("connect-src 'self'", standardHtml);
        Assert.Contains("connect-src 'self'", legacyHtml);
        Assert.DoesNotContain("https:", CspContent(standardHtml));
        Assert.DoesNotContain("https:", CspContent(legacyHtml));
    }

    [Fact]
    public void StrictScriptMode_RemovesInlineAndEvalCompatibilityWithoutChangingNetworkBoundary()
    {
        ReaderFrame strict = new(
            "strict",
            new DictionaryId("dict"),
            "<button onclick=\"run()\">x</button><script>window.run=()=>{};</script>")
        {
            LocalScriptMode = LocalScriptMode.Strict
        };
        ReaderDocument document = new("word", "", "token", [strict], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(strict, document);
        string csp = CspContent(html);

        Assert.Contains("script-src 'self' 'nonce-", csp);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp);
        Assert.DoesNotContain("script-src 'self' 'unsafe-eval'", csp);
        Assert.DoesNotContain("https:", csp);
    }

    [Fact]
    public void Frame_UsesCapabilityResourceBaseForInlineModulesAndFetch()
    {
        string capability = DictionaryNetworkPolicy.ResourceCapabilityForDictionary("dict", 12);
        ReaderFrame frame = new("frame", new DictionaryId("dict"), "<script type=\"module\">import './module.js'</script>")
        {
            CatalogGeneration = 12,
            ResourceCapability = capability
        };
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);

        Assert.Contains(
            $"<base href=\"{DictionaryNetworkPolicy.FrameOriginForDictionary("dict")}/resource/12/{capability}/\">",
            html);
        Assert.Contains("base-uri 'self'", html);
    }

    [Fact]
    public void ExplicitDarkStrategy_EmbedsDarkReaderAssetsWhenAvailable()
    {
        ReaderFrame frame = new("frame", new DictionaryId("dict"), "<p>x</p>")
        {
            Appearance = DictionaryAppearance.Default with
            {
                DarkModeStrategy = DictionaryDarkModeStrategy.Dark
            }
        };
        ReaderDocument document = new("word", "", "token", [frame], []);
        ReaderDarkModeAssets assets = new(
            "window.DarkReader={enable:()=>{window.darkEnabled=true}};",
            "body{outline:0}");

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document, assets);

        Assert.Contains("window.DarkReader", html);
        Assert.Contains("DarkReader.enable", html);
        Assert.Contains("app-dark-mode-overrides", html);
        Assert.Contains("vdict-await-dark", html);
        Assert.Contains("data-vdict-dark-engine=\"darkreader\"", html);
        Assert.DoesNotContain("filter:invert", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GlobalStrategy_UsesResolvedApplicationThemeAndKeepsDynamicController()
    {
        ReaderFrame frame = new("frame", new DictionaryId("dict"), "<p>x</p>")
        {
            Appearance = DictionaryAppearance.NewDictionaryDefault
        };
        ReaderDocument document = new("word", "", "token", [frame], [])
        {
            Appearance = ApplicationAppearance.Default with
            {
                Theme = ApplicationTheme.System
            }
        };
        ReaderDarkModeAssets assets = new(
            "window.DarkReader={enable:()=>{},disable:()=>{}};",
            "body{outline:0}");

        string dark = ReaderHtmlRenderer.BuildFrameDocument(
            frame,
            document,
            assets,
            effectiveApplicationDark: true);
        string light = ReaderHtmlRenderer.BuildFrameDocument(
            frame,
            document,
            assets,
            effectiveApplicationDark: false);

        Assert.Contains("data-vdict-dark-engine=\"darkreader\"", dark);
        Assert.Contains("vdict-await-dark", dark);
        Assert.Contains("const allowDynamic = true", dark);
        Assert.Contains("window.vdictApplyTheme", dark);
        Assert.Contains("data-vdict-dark-engine=\"original\"", light);
        Assert.DoesNotContain("<html class=\"original vdict-await-dark", light);
        Assert.Contains("window.DarkReader", light);
        Assert.Contains("const allowDynamic = true", light);
    }

    [Fact]
    public void OriginalStrategy_RemainsLightWhenApplicationIsDark()
    {
        ReaderFrame frame = new("frame", new DictionaryId("dict"), "<p>x</p>")
        {
            Appearance = DictionaryAppearance.Default
        };
        ReaderDocument document = new("word", "", "token", [frame], []);
        ReaderDarkModeAssets assets = new(
            "window.DarkReader={enable:()=>{},disable:()=>{}};",
            "body{outline:0}");

        string html = ReaderHtmlRenderer.BuildFrameDocument(
            frame,
            document,
            assets,
            effectiveApplicationDark: true);

        Assert.Contains("data-vdict-dark-engine=\"original\"", html);
        Assert.DoesNotContain("window.DarkReader", html);
        Assert.DoesNotContain("vdict-await-dark", html);
    }

    [Fact]
    public void Shell_DynamicThemeMessageIsBoundToRenderTokenAndFrameId()
    {
        ReaderFrame frame = new("frame", new DictionaryId("dict"), "<p>x</p>");
        ReaderDocument document = new("word", "", "token", [frame], []);

        string shell = ReaderHtmlRenderer.BuildShell(
            document,
            effectiveApplicationDark: true);

        Assert.Contains("<html class=\"dark\">", shell);
        Assert.Contains("window.vdictApplyTheme", shell);
        Assert.Contains("token !== renderToken", shell);
        Assert.Contains("frameId:frame.dataset.frameId", shell);
        Assert.Contains("kind:\"applyTheme\"", shell);
    }

    [Fact]
    public void Frame_HoistsDictionaryStylesBeforeCustomLayersAndMeasuresVisibleContent()
    {
        ReaderFrame frame = new(
            "frame",
            new DictionaryId("dict"),
            "<style>main{min-height:100%}</style><p>body</p>");
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);

        int dictionaryStyle = html.IndexOf("<style>main{min-height:100%}</style>", StringComparison.Ordinal);
        int body = html.IndexOf("<body><p>body</p></body>", StringComparison.Ordinal);
        Assert.True(dictionaryStyle >= 0 && dictionaryStyle < body);
        Assert.DoesNotContain("vdict-dictionary-content", html);
        Assert.DoesNotContain("display:flow-root", html);
        Assert.DoesNotContain("height:auto !important", html);
        Assert.Contains("document.body.querySelectorAll", html);
        Assert.Contains("style.position === \"fixed\"", html);
    }

    [Theory]
    [InlineData(DictionaryInteractionMode.Auto, "auto")]
    [InlineData(DictionaryInteractionMode.Native, "native")]
    [InlineData(DictionaryInteractionMode.Fallback, "fallback")]
    public void InteractionModes_ProduceDistinctTestableBehavior(
        DictionaryInteractionMode mode,
        string expected)
    {
        ReaderFrame frame = new("frame", new DictionaryId("dict"), "<details><summary>x</summary>y</details>")
        {
            Appearance = DictionaryAppearance.Default with { InteractionMode = mode }
        };
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);

        Assert.Contains($"const interactionMode = \"{expected}\"", html);
        Assert.Contains("applyFallbackInteraction", html);
    }

    [Fact]
    public void Frame_LeavesImageClicksAndDoubleClicksToDictionaryScripts()
    {
        ReaderFrame frame = new(
            "frame",
            new DictionaryId("dict"),
            "<a href=\"voice.mp3\"><img src=\"speaker.svg\"></a><img src=\"photo.png\">");
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);
        int audio = html.IndexOf("audioLike(href)", StringComparison.Ordinal);
        int entry = html.IndexOf("if (link && href.toLowerCase().startsWith(\"entry://\"))", StringComparison.Ordinal);
        int fragment = html.IndexOf(
            "if (link && href.startsWith(\"#\") && !fallbackInteraction)",
            StringComparison.Ordinal);
        int external = html.IndexOf("/^(?:https?|mailto):/i", StringComparison.Ordinal);

        Assert.True(audio >= 0 && audio < entry && entry < fragment);
        Assert.True(fragment < external);
        Assert.Contains("if (target.closest(\"img\") || link?.querySelector(\"img\")) return", html);
        Assert.DoesNotContain("dblclick", html);
        Assert.DoesNotContain("expandImage", html);
        Assert.DoesNotContain("vdict-image-overlay", html);
        Assert.DoesNotContain("zoom://", html);
        Assert.DoesNotContain("getSelection()?.toString()", html);
        Assert.DoesNotContain("send(\"selection\"", html);
        Assert.DoesNotContain("clicked.closest(\"a\")", html);
    }

    [Fact]
    public void Frame_MediaOverlayAndDynamicHeightHooksCoverLifecycleEvents()
    {
        ReaderFrame frame = new("frame", new DictionaryId("dict"), "<img src=\"a.png\">");
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);

        Assert.DoesNotContain("vdict-image-overlay", html);
        Assert.DoesNotContain("event.key === \"Escape\"", html);
        Assert.Contains("loadedmetadata", html);
        Assert.Contains("mediaError", html);
        Assert.Contains("errorName: String(error?.name", html);
        Assert.Contains("addEventListener(\"click\", activate);", html);
        Assert.DoesNotContain("addEventListener(\"pointerup\", activate, true)", html);
        Assert.Contains("scheduleAudioWarmup", html);
        Assert.Contains("animationend", html);
        Assert.Contains("document.fonts?.ready", html);
        Assert.Contains("send(\"frameLoad\")", html);
        Assert.Contains("securitypolicyviolation", html);
        Assert.Contains("unhandledrejection", html);
        Assert.Contains("MutationObserver", html);
        Assert.DoesNotContain("document.documentElement.scrollHeight", html);
        Assert.DoesNotContain("document.body.scrollHeight", html);
        Assert.DoesNotContain("document.body.offsetHeight", html);
        Assert.Contains("document.body.querySelectorAll", html);
        Assert.DoesNotContain("/^vdict:\\/\\/resource\\//i", html);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(50)]
    [InlineData(80)]
    [InlineData(100)]
    public void Shell_UsesResponsiveGlobalContentWidthForEveryReaderState(int percent)
    {
        ReaderDocument document = ReaderDocumentBuilder.Welcome(
            ApplicationSettings.Default with
            {
                Appearance = ApplicationAppearance.Default with
                {
                    ContentWidthPercent = percent
                }
            });

        string html = ReaderHtmlRenderer.BuildShell(document);

        Assert.Contains($"--vdict-content-width:{percent}%", html);
        Assert.Contains(".entry,.welcome,.not-found,.no-dictionaries,.notices", html);
        Assert.Contains("width:var(--vdict-content-width); max-width:100%", html);
        Assert.Contains("margin-left:auto; margin-right:auto", html);
    }

    [Fact]
    public void Frame_NormalizesFullHtmlShellAndPreservesBodyBehaviorAttributes()
    {
        ReaderFrame frame = new(
            "frame",
            new DictionaryId("dict"),
            """
            <!doctype html><html lang="en"><head>
              <meta name="dictionary-meta" content="ignored">
              <style>.definition{color:red}</style>
              <script>window.dictionaryReady=true;</script>
            </head><body class="dictionary-body" data-dropdown-root="ready"
              data-interaction-mode="native" data-resource-origin="https://example.invalid"
              onload="window.dictionaryBodyLoaded=true">
              <main class="definition">body</main>
            </body></html>
            """);
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);

        Assert.Contains("dictionary-meta", html);
        Assert.Contains("<html class=\"original\"", html);
        Assert.Contains("data-interaction-mode=\"auto\" data-resource-origin=\"vdict-doc://d-", html);
        Assert.Contains("class=\"dictionary-body\" data-dropdown-root=\"ready\"", html);
        Assert.Contains("onload=\"window.dictionaryBodyLoaded=true\"", html);
        Assert.Contains("data-interaction-mode=\"native\"", html);
        Assert.Contains("data-resource-origin=\"https://example.invalid\"", html);
        Assert.Contains(".definition{color:red}", html);
        Assert.Contains("window.dictionaryReady=true", html);
        Assert.Contains("<main class=\"definition\">body</main>", html);
    }

    [Fact]
    public void Frame_ContextMenuIsLeftToWebViewForNativeSelectionCommands()
    {
        ReaderFrame frame = new("frame", new DictionaryId("dict"), "<p>select me</p>");
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);
        int contextMenuHandler = html.IndexOf(
            "addEventListener(\"contextmenu\"",
            StringComparison.Ordinal);
        int nextHandler = html.IndexOf(
            "const interactionSignature",
            contextMenuHandler,
            StringComparison.Ordinal);
        string handler = html[contextMenuHandler..nextHandler];

        Assert.DoesNotContain("preventDefault", handler);
        Assert.Contains("send(\"inputEvent\"", handler);
    }

    [Fact]
    public void Frame_FirstTrustedAudioActivationRecognizesDictionaryTriggersAndReportsPlaybackState()
    {
        ReaderFrame frame = new(
            "frame",
            new DictionaryId("dict"),
            "<span class=\"sound\" data-audio-src=\"voice.mp3\"><img src=\"speaker.svg\"></span>");
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);

        Assert.Contains("[data-audio-src],.vdict-audio-trigger,.sound,.audio_play_button,a[href]", html);
        Assert.Contains("function audioFor(src, preload = \"metadata\")", html);
        Assert.Contains("mediaState(audio, \"playAttempt\")", html);
        Assert.Contains("\"playing\"", html);
        Assert.Contains("userActivationActive", html);
        Assert.Contains("const promise = audio.play();", html);
        Assert.Contains("audioTiming.set(audio, { playRequestedAt:performance.now() })", html);
        Assert.Contains("playLatencyMs", html);
        Assert.DoesNotContain("setTimeout(() => play", html);
    }

    [Fact]
    public void Frame_AudioWarmupIsIdleBoundedLocalOnlyAndLruCapped()
    {
        ReaderFrame frame = new(
            "frame",
            new DictionaryId("dict"),
            "<a href=\"one.mp3\">one</a><a href=\"two.wav\">two</a>" +
            "<a href=\"three.ogg\">three</a><a href=\"https://example.test/remote.mp3\">remote</a>");
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);

        Assert.Contains("warmupDelayId = setTimeout(() =>", html);
        Assert.Contains("}, 120)", html);
        Assert.Contains("\"requestIdleCallback\" in window", html);
        Assert.Contains("requestIdleCallback(beginAudioWarmup, { timeout:600 })", html);
        Assert.Contains("warmupFallbackId = setTimeout(beginAudioWarmup, 0)", html);
        Assert.Contains("candidate.origin !== resourceBaseUrl.origin", html);
        Assert.Contains("!candidate.pathname.startsWith(resourceBaseUrl.pathname)", html);
        Assert.Contains("if (sources.length === 2) break", html);
        Assert.Contains("audio.preload = \"auto\"", html);
        Assert.Contains("audio.load()", html);
        Assert.Contains("while (audioPool.size > 4)", html);
        Assert.DoesNotContain("fetch(candidate", html);
    }

    [Fact]
    public void Frame_AudioWarmupReusesLruAndCancelsOnDocumentTeardown()
    {
        ReaderFrame frame = new("frame", new DictionaryId("dict"), "<a href=\"voice.mp3\">voice</a>");
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);

        Assert.Contains("touchAudio(normalized, audio)", html);
        Assert.Contains("const audio = audioFor(src)", html);
        Assert.Contains("cancelIdleCallback(warmupIdleId)", html);
        Assert.Contains("cancelAudioWarmup()", html);
        Assert.Contains("warmupPending.clear()", html);
        Assert.Contains("audio.removeAttribute(\"src\")", html);
        Assert.Contains("addEventListener(\"beforeunload\", stopMedia", html);
        Assert.Contains("window.vdictStopMedia = stopMedia", html);
        Assert.Contains("window.__vdictAudioWarmupDiagnostics", html);
        Assert.Contains("warmupState(\"cancelled\"", html);
        Assert.Contains("warmupState(\"failed\"", html);
        Assert.Contains("settleWarmup(normalized, \"completed\")", html);
    }

    [Fact]
    public void AutoInteraction_WaitsForNativeConsumptionBeforeFallback()
    {
        ReaderFrame frame = new("frame", new DictionaryId("dict"), "<button class=\"toggle\">x</button>")
        {
            Appearance = DictionaryAppearance.Default with
            {
                InteractionMode = DictionaryInteractionMode.Auto
            }
        };
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);

        Assert.Contains(
            "setTimeout(() => setTimeout(() => requestAnimationFrame(() =>",
            html);
        Assert.Contains(
            "interactionSignature(interaction) !== signature",
            html);
        Assert.Contains(
            "classes:String(interaction.container?.className || \"\")",
            html);
        Assert.DoesNotContain(
            "queueMicrotask(() => finishFallback",
            html);
    }

    [Fact]
    public void FallbackInteraction_UsesOnlyExplicitDeclarativeRelationships()
    {
        ReaderFrame frame = new(
            "frame",
            new DictionaryId("dict"),
            "<button aria-controls=\"panel\">title</button><div id=\"panel\" hidden>body</div>")
        {
            Appearance = DictionaryAppearance.Default with
            {
                InteractionMode = DictionaryInteractionMode.Fallback
            }
        };
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);

        Assert.Contains("[aria-controls],[aria-expanded]", html);
        Assert.Contains("explicitFallbackTargets(trigger)", html);
        Assert.DoesNotContain("followingTargets(trigger)", html);
        Assert.DoesNotContain("getComputedStyle(element).cursor", html);
        Assert.DoesNotContain("event.stopImmediatePropagation();", html);
        Assert.DoesNotContain("if (!clicked) return", html);
        Assert.DoesNotContain(".box_title", html);
        Assert.DoesNotContain(".sense >", html);
    }

    [Fact]
    public void ShellAndFrame_BridgeOnlyCtrlWheelAndCanRecheckEveryFrameHeight()
    {
        ReaderFrame frame = new("frame", new DictionaryId("dict"), "<p>x</p>");
        ReaderDocument document = new("word", "", "render-token", [frame], []);

        string shell = ReaderHtmlRenderer.BuildShell(document);
        string frameHtml = ReaderHtmlRenderer.BuildFrameDocument(frame, document);

        Assert.Contains("if (!event.ctrlKey) return", shell);
        Assert.Contains("if (!event.ctrlKey) return", frameHtml);
        Assert.Contains("event.preventDefault()", shell);
        Assert.Contains("event.preventDefault()", frameHtml);
        Assert.Contains("zoomDelta", shell);
        Assert.Contains("zoomDelta", frameHtml);
        Assert.Contains("zoomStep", shell);
        Assert.Contains("zoomReset", shell);
        Assert.Contains("zoomStep", frameHtml);
        Assert.Contains("zoomReset", frameHtml);
        Assert.Contains("vdictRequestFrameSizes", shell);
        Assert.Contains("reportSize", frameHtml);
        Assert.Contains("render-token", frameHtml);
    }

    [Fact]
    public void ExternalLinks_BlockInDocumentOrUseWebView2UserInitiatedNavigation()
    {
        ReaderFrame frame = new(
            "frame",
            new DictionaryId("dict"),
            "<a href=\"https://example.test\">x</a>");
        ReaderDocument blocked = new("word", "", "render-token", [frame], []);
        ReaderDocument systemBrowser = blocked with
        {
            ExternalLinkPolicy = ExternalLinkPolicy.SystemBrowser
        };

        string shell = ReaderHtmlRenderer.BuildShell(blocked);
        string blockedHtml = ReaderHtmlRenderer.BuildFrameDocument(frame, blocked);
        string systemBrowserHtml = ReaderHtmlRenderer.BuildFrameDocument(frame, systemBrowser);

        Assert.Contains("userActivation", shell);
        Assert.Contains("event.isTrusted", blockedHtml);
        Assert.Contains("pointerdown", blockedHtml);
        Assert.Contains("const blockExternalLinks = true", blockedHtml);
        Assert.Contains("category:\"external-blocked\"", blockedHtml);
        Assert.Contains("event.preventDefault()", blockedHtml);
        Assert.Contains("const blockExternalLinks = false", systemBrowserHtml);
        Assert.DoesNotContain("send(\"externalLink\"", blockedHtml);
        Assert.DoesNotContain("send(\"externalLink\"", systemBrowserHtml);
        Assert.Contains("lets WebView2 validate native user initiation", systemBrowserHtml);
    }

    [Fact]
    public void ExplicitDarkStrategy_UsesExactlyOneNonInvertingFallbackWithoutAssets()
    {
        ReaderFrame frame = new("frame", new DictionaryId("dict"), "<p style=\"color:#111;background:#fff\">x</p>")
        {
            Appearance = DictionaryAppearance.Default with
            {
                DarkModeStrategy = DictionaryDarkModeStrategy.Dark
            }
        };
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);

        Assert.Contains("data-vdict-dark-engine=\"palette\"", html);
        Assert.DoesNotContain("filter:invert", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hue-rotate", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("color-scheme:dark", html);
    }

    [Fact]
    public void Frame_CascadeOrdersDictionaryCustomCompatibilityPreferencesAndTheme()
    {
        ReaderFrame frame = new(
            "frame",
            new DictionaryId("dict"),
            "<html><head><style>.entry{color:red}</style></head><body><p class=\"entry\">x</p></body></html>")
        {
            CustomCssResourcePaths = ["https://d-test.vdict.invalid/resource/1/c/custom.css"],
            Appearance = DictionaryAppearance.Default with
            {
                OverrideTypography = true,
                DarkModeStrategy = DictionaryDarkModeStrategy.Dark
            }
        };
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);
        int foundation = html.IndexOf("data-vdict-layer=\"foundation\"", StringComparison.Ordinal);
        int dictionary = html.IndexOf(".entry{color:red}", StringComparison.Ordinal);
        int custom = html.IndexOf("custom.css", StringComparison.Ordinal);
        int compatibility = html.IndexOf("data-vdict-layer=\"compatibility\"", StringComparison.Ordinal);
        int preferences = html.IndexOf("data-vdict-layer=\"preferences\"", StringComparison.Ordinal);
        int theme = html.IndexOf("data-vdict-layer=\"theme\"", StringComparison.Ordinal);

        Assert.True(foundation >= 0 && foundation < dictionary);
        Assert.True(dictionary < custom);
        Assert.True(custom < compatibility);
        Assert.True(compatibility < preferences);
        Assert.True(preferences < theme);
        Assert.DoesNotContain("* { font-family", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("filter:invert", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Frame_DefaultAppearancePreservesDictionaryTypographyColorsAndMediaSizing()
    {
        ReaderFrame frame = new(
            "frame",
            new DictionaryId("dict"),
            "<html><head><style>body{margin:12px;font-size:15px}img{width:640px}</style></head>" +
            "<body><img src=\"figure.png\"></body></html>");
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);

        Assert.Contains("body{margin:12px;font-size:15px}", html);
        Assert.DoesNotContain("data-vdict-layer=\"preferences\"", html);
        Assert.DoesNotContain("data-vdict-layer=\"theme\"", html);
        Assert.DoesNotContain("overflow-wrap:anywhere", html);
        Assert.DoesNotContain("img,video", html);
        Assert.DoesNotContain("margin:0 !important", html);
        Assert.DoesNotContain("padding:0 !important", html);
    }

    [Fact]
    public void Frame_ExplicitTypographyAndFontOverridesAreOptIn()
    {
        ReaderFrame frame = new("frame", new DictionaryId("dict"), "<p>x</p>")
        {
            Appearance = DictionaryAppearance.Default with
            {
                OverrideFont = true,
                OverrideTypography = true,
                FontSize = 24,
                LineHeight = 1.8,
                LatinFontFamily = "Fixture Latin",
                CjkFontFamily = "Fixture CJK"
            }
        };
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);

        Assert.Contains("data-vdict-layer=\"preferences\"", html);
        Assert.Contains("const fontScale = 1.3333333333333333", html);
        Assert.Contains("const lineHeightScale = 1.090909090909091", html);
        Assert.Contains("const scaleTypography = true", html);
        Assert.Contains("const forceReaderFont = true", html);
        Assert.Contains("\"font-size\", `${(state.size * fontScale).toFixed(3)}px`, \"important\"", html);
        Assert.Contains("state.line * fontScale * lineHeightScale", html);
        Assert.Contains("Fixture Latin", html);
        Assert.Contains("Fixture CJK", html);
    }

    [Fact]
    public void Frame_GlobalTypographyScalesComputedMetricsAndProvidesOnlyFontFallback()
    {
        ReaderFrame frame = new(
            "frame",
            new DictionaryId("dict"),
            "<style>.definition{font-family:Dictionary Face;font-size:20px}</style>" +
            "<h1>title</h1><p class=\"definition\"><span>nested</span></p><table><tr><td>cell</td></tr></table>");
        ReaderDocument document = new("word", "", "token", [frame], [])
        {
            Appearance = ApplicationAppearance.Default with
            {
                FontSize = 27,
                LineHeight = 1.98,
                LatinFontFamily = "Global Latin",
                CjkFontFamily = "Global CJK"
            }
        };

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);

        Assert.Contains("const fontScale = 1.5", html);
        Assert.Contains("const lineHeightScale = 1.2", html);
        Assert.Contains("const scaleTypography = true", html);
        Assert.Contains("const forceReaderFont = false", html);
        Assert.Contains("body { min-height:0; font-family:\"Global Latin\",\"Global CJK\",sans-serif; }", html);
        Assert.Contains(".definition{font-family:Dictionary Face;font-size:20px}", html);

        int batch = html.IndexOf("const applyTypographyBatch", StringComparison.Ordinal);
        int reads = html.IndexOf(
            "const metrics = targets.map(readTypography).filter(Boolean)",
            batch,
            StringComparison.Ordinal);
        int firstWrite = html.IndexOf(
            "ancestors.forEach(writeTypography)",
            reads,
            StringComparison.Ordinal);
        Assert.True(batch >= 0 && reads > batch && firstWrite > reads);
    }

    [Fact]
    public void Frame_TypographyProtectsMediaSymbolsAudioControlsAndIconFonts()
    {
        ReaderFrame frame = new("frame", new DictionaryId("dict"), "<p>x</p>")
        {
            Appearance = DictionaryAppearance.Default with
            {
                OverrideTypography = true,
                FontSize = 22
            }
        };
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);

        Assert.Contains("\"img\", \"picture\", \"video\", \"audio\"", html);
        Assert.Contains("\"object\", \"embed\", \"iframe\", \"svg\", \"symbol\"", html);
        Assert.Contains("\".vdict-audio-trigger\", \"[data-audio-src]\"", html);
        Assert.Contains("\"[class*='symbol' i]\", \"[class*='icon' i]\"", html);
        Assert.Contains("iconFontPattern.test(computed.fontFamily)", html);
        Assert.Contains("Boolean(element.closest(protectedTypographySelector))", html);
    }

    [Fact]
    public void Frame_TypographyProcessesMutationBatchesIncrementally()
    {
        ReaderFrame frame = new("frame", new DictionaryId("dict"), "<p>x</p>")
        {
            Appearance = DictionaryAppearance.Default with
            {
                OverrideTypography = true
            }
        };
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);

        Assert.Contains("const pendingTypographyRoots = new Set()", html);
        Assert.Contains("[...record.addedNodes].filter(node => node instanceof Element)", html);
        Assert.Contains("queueTypography(roots)", html);
        Assert.Contains("typographyObserver.observe(document.body", html);
        Assert.Contains("childList:true, subtree:true", html);
        Assert.Contains("ancestors.forEach(restoreTypography)", html);
        Assert.Contains("Read the complete batch before any write", html);
    }

    [Fact]
    public void Frame_FragmentStylesheetNodesPrecedeUserCustomCss()
    {
        ReaderFrame frame = new(
            "frame",
            new DictionaryId("dict"),
            "<link rel=\"stylesheet\" href=\"dictionary.css\">" +
            "<style>.entry{color:red}</style><p class=\"entry\">x</p>")
        {
            CustomCssResourcePaths = ["https://d-test.vdict.invalid/resource/custom.css"]
        };
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);
        int dictionaryLink = html.IndexOf("dictionary.css", StringComparison.Ordinal);
        int dictionaryStyle = html.IndexOf(".entry{color:red}", StringComparison.Ordinal);
        int custom = html.IndexOf("custom.css", StringComparison.Ordinal);
        int body = html.IndexOf("<body><p class=\"entry\">x</p></body>", StringComparison.Ordinal);

        Assert.True(dictionaryLink >= 0 && dictionaryLink < custom);
        Assert.True(dictionaryStyle >= 0 && dictionaryStyle < custom);
        Assert.True(custom < body);
    }

    [Fact]
    public void Frame_LoadsOrderedSidecarScriptsInHeadAndDeduplicatesEntryReferenceByFile()
    {
        ReaderFrame frame = new(
            "frame",
            new DictionaryId("dict"),
            "<html><head><script src=\"https://d-test.vdict.invalid/resource/native.js\"></script></head>" +
            "<body><div class=\"entry\">x</div></body></html>")
        {
            CustomScriptResourcePaths =
            [
                "https://d-test.vdict.invalid/resource/vdict-custom-script/0/dependency.js",
                "https://d-test.vdict.invalid/resource/vdict-custom-script/1/native.js"
            ]
        };
        ReaderDocument document = new("word", "", "token", [frame], []);

        string html = ReaderHtmlRenderer.BuildFrameDocument(frame, document);
        int dependency = html.IndexOf("dependency.js", StringComparison.Ordinal);
        int native = html.IndexOf("native.js", StringComparison.Ordinal);
        int body = html.IndexOf("<body>", StringComparison.Ordinal);

        Assert.True(dependency >= 0 && dependency < body);
        Assert.True(native >= 0 && native < dependency);
        Assert.Single(Regex.Matches(html, "native\\.js", RegexOptions.IgnoreCase).Cast<Match>());
        Assert.Contains("<script defer src=", html);
        Assert.Contains("<body><div class=\"entry\">x</div></body>", html);
    }

    [Fact]
    public void EmptyDocument_RendersWelcomeOrNotFoundContent()
    {
        ReaderDocument welcome = ReaderDocumentBuilder.Welcome(ApplicationSettings.Default);
        ReaderDocument missing = ReaderDocumentBuilder.Build("absent", [], ApplicationSettings.Default);
        string welcomeHtml = ReaderHtmlRenderer.BuildShell(welcome);

        Assert.Contains("welcome", welcomeHtml);
        Assert.Contains(DictionaryNetworkPolicy.WelcomeLogoUri, welcomeHtml);
        Assert.Contains(".welcome-logo", welcomeHtml);
        Assert.Contains("position:fixed; inset:0", welcomeHtml);
        Assert.Contains("width:min(420px,58vw)", welcomeHtml);
        Assert.Contains("img-src 'self' data:", CspContent(welcomeHtml));
        string missingHtml = ReaderHtmlRenderer.BuildShell(missing);
        Assert.Contains("not-found", missingHtml);
        Assert.Contains("Entry not found", missingHtml);
        Assert.Contains("not-found-mark", missingHtml);
        Assert.Contains("color:var(--vdict-accent); font-size:20px", missingHtml);
        Assert.Contains("font-weight:500", missingHtml);
        Assert.Contains("<span class=\"not-found-query\">“absent”</span>", missingHtml);
        Assert.DoesNotContain("<h1>absent</h1>", missingHtml);

        string noDictionariesHtml = ReaderHtmlRenderer.BuildShell(
            ReaderDocumentBuilder.NoDictionaries(ApplicationSettings.Default));
        Assert.Contains("no-dictionaries", noDictionariesHtml);
        Assert.Contains(DictionaryNetworkPolicy.WelcomeLogoUri, noDictionariesHtml);
        Assert.Contains("position:fixed; inset:0", noDictionariesHtml);
    }

    private static string CspContent(string html)
    {
        const string marker = "content=\"default-src";
        int start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        start += "content=\"".Length;
        int end = html.IndexOf('"', start);
        return html[start..end];
    }
}
