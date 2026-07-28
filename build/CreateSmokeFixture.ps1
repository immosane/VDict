[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PortableDirectory,

    [Parameter()]
    [ValidateRange(30, 100)]
    [int] $ContentWidthPercent = 80,

    [Parameter()]
    [switch] $TypographyPhase4,

    [Parameter()]
    [ValidateSet('system', 'light', 'dark')]
    [string] $Theme = 'system',

    [Parameter()]
    [switch] $AudioPhase5,

    [Parameter()]
    [switch] $Phase6,

    [Parameter()]
    [ValidateSet(2, 3)]
    [int] $SchemaVersion = 3,

    [Parameter()]
    [switch] $OmitContentWidth
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-UInt32BigEndian {
    param(
        [Parameter(Mandatory)]
        [System.IO.Stream] $Stream,

        [Parameter(Mandatory)]
        [uint32] $Value
    )

    $bytes = [BitConverter]::GetBytes($Value)
    if ([BitConverter]::IsLittleEndian) {
        [Array]::Reverse($bytes)
    }

    $Stream.Write($bytes, 0, $bytes.Length)
}

function Write-Bytes {
    param(
        [Parameter(Mandatory)]
        [System.IO.Stream] $Stream,

        [Parameter(Mandatory)]
        [byte[]] $Bytes
    )

    $Stream.Write($Bytes, 0, $Bytes.Length)
}

function Add-UncompressedHeader {
    param([Parameter(Mandatory)][byte[]] $Bytes)

    $output = [byte[]]::new(8 + $Bytes.Length)
    [Array]::Copy($Bytes, 0, $output, 8, $Bytes.Length)
    return $output
}

$portableRoot = (Resolve-Path -LiteralPath $PortableDirectory).Path
$fixtureDirectory = Join-Path $portableRoot 'SmokeFixture'
$settingsDirectory = Join-Path $portableRoot 'Data\Settings'
[System.IO.Directory]::CreateDirectory($fixtureDirectory) | Out-Null
[System.IO.Directory]::CreateDirectory($settingsDirectory) | Out-Null

$mdxPath = Join-Path $fixtureDirectory 'smoke.mdx'
$word = 'smoke'
$entry = @'
<!doctype html>
<html lang="en">
<head>
<style>
html, body { min-height: 100%; overflow-y: auto; }
.fixture-shell { min-height: 1px; }
</style>
</head>
<body class="dictionary-document-shell">
<div class="fixture-shell">
<p id="top">fixture-entry-marker</p>
<a id="internal-entry-link" href="entry://linked">internal-entry</a>
<a id="fragment-link" href="#details">fragment-link</a>
<img src="mdd-image.png" alt="mdd-image-marker">
<a id="audio-range-link" href="voice.mp3">audio-range-marker</a>
<a id="external-link" href="https://example.test/page">external-link-marker</a>
<button id="inline-action" onclick="window.vdictInlineClicks=(window.vdictInlineClicks||0)+1">inline-onclick-marker</button>
<script src="fixture.js"></script>
<script type="module" src="module.js"></script>
<button id="fold-toggle" class="toggle" data-target="#folded">fold-toggle</button>
<div id="folded" hidden>folded-script-marker</div>
<p id="details">fragment-target-marker</p>
</div>
</body>
</html>
'@
if ($TypographyPhase4) {
    $typographyFixture = @'
<section id="typography-fixture" class="fixture-parent">
  <h1>typography-title-marker</h1>
  <p>definition <span id="nested-text" class="fixture-child">nested-example-marker</span></p>
  <table><tr><td>typography-table-marker</td></tr></table>
  <span id="protected-icon" class="icon" style="font-family:'Segoe Fluent Icons';font-size:16px;line-height:16px">&#xE11A;</span>
  <button id="protected-audio" class="sound" style="font-size:16px;line-height:16px">audio-icon-marker</button>
</section>
<script>
setTimeout(() => {
  const parent = document.getElementById('typography-fixture');
  const nested = document.getElementById('nested-text');
  const icon = document.getElementById('protected-icon');
  const audio = document.getElementById('protected-audio');
  const dynamic = document.createElement('span');
  dynamic.id = 'dynamic-text';
  dynamic.textContent = 'dynamic-typography-marker';
  dynamic.style.fontSize = '0.5em';
  dynamic.style.lineHeight = '1.5';
  parent.appendChild(dynamic);
  setTimeout(() => {
    const close = (actual, expected) => Math.abs(Number.parseFloat(actual) - expected) < 0.2;
    const parentStyle = getComputedStyle(parent);
    const nestedStyle = getComputedStyle(nested);
    const dynamicStyle = getComputedStyle(dynamic);
    const iconStyle = getComputedStyle(icon);
    const audioStyle = getComputedStyle(audio);
    const passed =
      close(parentStyle.fontSize, 33) && close(parentStyle.lineHeight, 57.6) &&
      close(nestedStyle.fontSize, 16.5) && close(nestedStyle.lineHeight, 29.7) &&
      close(dynamicStyle.fontSize, 16.5) && close(dynamicStyle.lineHeight, 29.7) &&
      close(iconStyle.fontSize, 16) && close(iconStyle.lineHeight, 16) &&
      close(audioStyle.fontSize, 16) && close(audioStyle.lineHeight, 16) &&
      parentStyle.fontFamily.includes('Arial') &&
      !iconStyle.fontFamily.includes('Arial');
    const result = document.createElement('p');
    result.id = 'typography-result';
    result.textContent = passed ? 'typography-check-pass' :
      `typography-check-fail:${parentStyle.fontSize}/${nestedStyle.fontSize}/${dynamicStyle.fontSize}/${iconStyle.fontSize}`;
    document.body.appendChild(result);
  }, 160);
}, 80);
</script>
'@
    $entry = $entry.Replace(
        '<p id="top">fixture-entry-marker</p>',
        "<p id=`"top`">fixture-entry-marker</p>`n$typographyFixture")
}
if ($Phase6) {
    $phase6Fixture = @'
<section id="phase6-complex-fixture"
         style="width:760px;max-width:100%;min-height:220px;overflow:visible">
  <h1>phase6-complex-title</h1>
  <p id="phase6-important-text"
     style="font-size:11px !important;line-height:12px !important">
    phase6-important-typography
  </p>
  <div style="min-height:120px">phase6-min-height-content</div>
</section>
<script>
(() => {
  const appendResult = (id, text, anchor = null) => {
    if (document.getElementById(id)) return;
    const result = document.createElement('p');
    result.id = id;
    result.textContent = text;
    if (anchor?.parentElement) anchor.insertAdjacentElement('afterend', result);
    else document.body.appendChild(result);
  };
  const deadline = performance.now() + 6000;
  const verify = () => {
    const fixture = document.getElementById('phase6-complex-fixture');
    const important = document.getElementById('phase6-important-text');
    const image = document.querySelector('img[alt="mdd-image-marker"]');
    const fixtureStyle = getComputedStyle(fixture);
    const importantStyle = getComputedStyle(important);
    const width = Number.parseFloat(fixtureStyle.width);
    const passed =
      document.documentElement.tagName === 'HTML' &&
      document.body.classList.contains('dictionary-document-shell') &&
      width <= document.body.clientWidth + 1 &&
      Number.parseFloat(fixtureStyle.minHeight) >= 220 &&
      fixtureStyle.overflowY === 'visible' &&
      Math.abs(Number.parseFloat(importantStyle.fontSize) - 11) < 0.2 &&
      Math.abs(Number.parseFloat(importantStyle.lineHeight) - 12) < 0.2 &&
      image?.complete && image.naturalWidth > 0 &&
      window.vdictFixture?.classicLoaded === true &&
      window.vdictModuleResult === 42 &&
      window.vdictFetchResult === 'offline' &&
      localStorage.getItem('vdict-smoke-storage') === 'offline' &&
      document.getElementById('folded')?.hidden === true;
    if (passed) {
      appendResult('phase6-complex-result', 'phase6-complex-check-pass');
      return;
    }
    if (performance.now() >= deadline) {
      appendResult(
        'phase6-complex-result',
        `phase6-complex-check-fail:${width}/${fixtureStyle.minHeight}/` +
        `${fixtureStyle.overflowY}/${importantStyle.fontSize}/` +
        `${importantStyle.lineHeight}`);
      return;
    }
    setTimeout(verify, 80);
  };
  document.addEventListener('click', event => {
    if (!event.isTrusted) return;
    if (event.target.closest('#fragment-link')) {
      setTimeout(() => appendResult(
        'phase6-fragment-result',
        'phase6-fragment-click-pass',
        document.getElementById('details')), 120);
    }
    if (event.target.closest('#fold-toggle')) {
      setTimeout(() => {
        const folded = document.getElementById('folded');
        if (!folded.hidden && getComputedStyle(folded).display !== 'none') {
          appendResult(
            'phase6-fold-result',
            'phase6-fold-click-pass',
            document.getElementById('fold-toggle'));
        }
      }, 200);
    }
    if (event.target.closest('#external-link')) {
      setTimeout(() => appendResult(
        'phase6-external-result',
        'phase6-external-click-pass',
        document.getElementById('external-link')), 120);
    }
  }, true);
  document.addEventListener('contextmenu', event => {
    if (event.isTrusted) {
      appendResult(
        'phase6-context-result',
        'phase6-contextmenu-pass',
        event.target instanceof Element ? event.target : null);
    }
  }, true);
  setTimeout(verify, 160);
})();
</script>
'@
    $entry = $entry.Replace(
        '<p id="top">fixture-entry-marker</p>',
        "<p id=`"top`">fixture-entry-marker</p>`n$phase6Fixture")
}
if ($AudioPhase5 -or $Phase6) {
    $audioFixture = @'
<section id="audio-phase5-fixture">
  <a href="mdd-voice.mp3">audio-phase5-play</a>
  <a href="voice.mp3">audio-phase5-second</a>
  <a href="extra.mp3">audio-phase5-third-must-not-warm</a>
  <a href="voice.mp3">audio-phase5-duplicate</a>
  <a href="https://example.test/remote.mp3">audio-phase5-remote-must-not-warm</a>
  <a href="data:audio/mpeg;base64,AA==">audio-phase5-data-must-not-warm</a>
</section>
<script>
(() => {
  const deadline = performance.now() + 6000;
  const verify = () => {
    const state = window.__vdictAudioWarmupDiagnostics;
    if (state?.started >= 1 && state.completed >= 2 &&
        state.sources?.length === 2 &&
        state.sources.every(source =>
          !source.includes('example.test') && !source.startsWith('data:')) &&
        state.sources.some(source => source.endsWith('/mdd-voice.mp3')) &&
        state.sources.some(source => source.endsWith('/voice.mp3'))) {
      const result = document.createElement('p');
      result.id = 'audio-warmup-result';
      result.textContent = 'audio-warmup-check-pass';
      document.body.appendChild(result);
      return;
    }
    if (performance.now() >= deadline) {
      const result = document.createElement('p');
      result.id = 'audio-warmup-result';
      result.textContent = `audio-warmup-check-fail:${JSON.stringify(state || {})}`;
      document.body.appendChild(result);
      return;
    }
    setTimeout(verify, 80);
  };
  setTimeout(verify, 160);
})();
</script>
'@
    $entry = $entry.Replace(
        '<p id="top">fixture-entry-marker</p>',
        "<p id=`"top`">fixture-entry-marker</p>`n$audioFixture")
}
$wordBytes = [System.Text.Encoding]::UTF8.GetBytes($word)
$entryBytes = [System.Text.Encoding]::UTF8.GetBytes($entry)

$keyData = [System.IO.MemoryStream]::new()
try {
    Write-UInt32BigEndian -Stream $keyData -Value 0
    Write-Bytes -Stream $keyData -Bytes $wordBytes
    $keyData.WriteByte(0)
    $keyDataBytes = $keyData.ToArray()
}
finally {
    $keyData.Dispose()
}

$keyBlock = Add-UncompressedHeader -Bytes $keyDataBytes
$keyInfo = [System.IO.MemoryStream]::new()
try {
    Write-UInt32BigEndian -Stream $keyInfo -Value 1
    $keyInfo.WriteByte([byte]$word.Length)
    Write-Bytes -Stream $keyInfo -Bytes $wordBytes
    $keyInfo.WriteByte([byte]$word.Length)
    Write-Bytes -Stream $keyInfo -Bytes $wordBytes
    Write-UInt32BigEndian -Stream $keyInfo -Value ([uint32]$keyBlock.Length)
    Write-UInt32BigEndian -Stream $keyInfo -Value ([uint32]$keyDataBytes.Length)
    $keyInfoBytes = $keyInfo.ToArray()
}
finally {
    $keyInfo.Dispose()
}

$recordBlock = Add-UncompressedHeader -Bytes $entryBytes
$headerText = '<Dictionary GeneratedByEngineVersion="1.2" Encoding="UTF-8" />' + [char]0
$headerBytes = [System.Text.Encoding]::Unicode.GetBytes($headerText)
$output = [System.IO.File]::Create($mdxPath)
try {
    Write-UInt32BigEndian -Stream $output -Value ([uint32]$headerBytes.Length)
    Write-Bytes -Stream $output -Bytes $headerBytes
    Write-UInt32BigEndian -Stream $output -Value 0

    Write-UInt32BigEndian -Stream $output -Value 1
    Write-UInt32BigEndian -Stream $output -Value 1
    Write-UInt32BigEndian -Stream $output -Value ([uint32]$keyInfoBytes.Length)
    Write-UInt32BigEndian -Stream $output -Value ([uint32]$keyBlock.Length)
    Write-Bytes -Stream $output -Bytes $keyInfoBytes
    Write-Bytes -Stream $output -Bytes $keyBlock

    Write-UInt32BigEndian -Stream $output -Value 1
    Write-UInt32BigEndian -Stream $output -Value 1
    Write-UInt32BigEndian -Stream $output -Value 8
    Write-UInt32BigEndian -Stream $output -Value ([uint32]$recordBlock.Length)
    Write-UInt32BigEndian -Stream $output -Value ([uint32]$recordBlock.Length)
    Write-UInt32BigEndian -Stream $output -Value ([uint32]$entryBytes.Length)
    Write-Bytes -Stream $output -Bytes $recordBlock
}
finally {
    $output.Dispose()
}

$allowedMdxPath = Join-Path $fixtureDirectory 'allowed.mdx'
$allowedBytes = [System.IO.File]::ReadAllBytes($mdxPath)
$from = [System.Text.Encoding]::UTF8.GetBytes('fixture-entry-marker')
$to = [System.Text.Encoding]::UTF8.GetBytes('allowed-entry-marker')
for ($index = 0; $index -le $allowedBytes.Length - $from.Length; $index++) {
    $matches = $true
    for ($offset = 0; $offset -lt $from.Length; $offset++) {
        if ($allowedBytes[$index + $offset] -ne $from[$offset]) {
            $matches = $false
            break
        }
    }

    if ($matches) {
        [Array]::Copy($to, 0, $allowedBytes, $index, $to.Length)
        break
    }
}
[System.IO.File]::WriteAllBytes($allowedMdxPath, $allowedBytes)

function Write-SingleMdd {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $ResourceKey,
        [Parameter(Mandatory)][byte[]] $Body
    )

    $keyEncoding = [System.Text.Encoding]::Unicode
    $keyBytes = $keyEncoding.GetBytes($ResourceKey)
    $keyData = [System.IO.MemoryStream]::new()
    try {
        Write-UInt32BigEndian -Stream $keyData -Value 0
        Write-Bytes -Stream $keyData -Bytes $keyBytes
        $keyData.WriteByte(0)
        $keyData.WriteByte(0)
        $keyDataBytes = $keyData.ToArray()
    }
    finally {
        $keyData.Dispose()
    }

    $keyBlock = Add-UncompressedHeader -Bytes $keyDataBytes
    $keyInfo = [System.IO.MemoryStream]::new()
    try {
        Write-UInt32BigEndian -Stream $keyInfo -Value 1
        $keyInfo.WriteByte([byte]$ResourceKey.Length)
        Write-Bytes -Stream $keyInfo -Bytes $keyBytes
        $keyInfo.WriteByte([byte]$ResourceKey.Length)
        Write-Bytes -Stream $keyInfo -Bytes $keyBytes
        Write-UInt32BigEndian -Stream $keyInfo -Value ([uint32]$keyBlock.Length)
        Write-UInt32BigEndian -Stream $keyInfo -Value ([uint32]$keyDataBytes.Length)
        $keyInfoBytes = $keyInfo.ToArray()
    }
    finally {
        $keyInfo.Dispose()
    }

    $recordBlock = Add-UncompressedHeader -Bytes $Body
    $header = [System.Text.Encoding]::Unicode.GetBytes(
        '<Dictionary GeneratedByEngineVersion="1.2" Encoding="UTF-16" />' + [char]0)
    $stream = [System.IO.File]::Create($Path)
    try {
        Write-UInt32BigEndian -Stream $stream -Value ([uint32]$header.Length)
        Write-Bytes -Stream $stream -Bytes $header
        Write-UInt32BigEndian -Stream $stream -Value 0
        Write-UInt32BigEndian -Stream $stream -Value 1
        Write-UInt32BigEndian -Stream $stream -Value 1
        Write-UInt32BigEndian -Stream $stream -Value ([uint32]$keyInfoBytes.Length)
        Write-UInt32BigEndian -Stream $stream -Value ([uint32]$keyBlock.Length)
        Write-Bytes -Stream $stream -Bytes $keyInfoBytes
        Write-Bytes -Stream $stream -Bytes $keyBlock
        Write-UInt32BigEndian -Stream $stream -Value 1
        Write-UInt32BigEndian -Stream $stream -Value 1
        Write-UInt32BigEndian -Stream $stream -Value 8
        Write-UInt32BigEndian -Stream $stream -Value ([uint32]$recordBlock.Length)
        Write-UInt32BigEndian -Stream $stream -Value ([uint32]$recordBlock.Length)
        Write-UInt32BigEndian -Stream $stream -Value ([uint32]$Body.Length)
        Write-Bytes -Stream $stream -Bytes $recordBlock
    }
    finally {
        $stream.Dispose()
    }
}

$mddPath = Join-Path $fixtureDirectory 'smoke.mdd'
Write-SingleMdd `
    -Path $mddPath `
    -ResourceKey '\mdd-image.png' `
    -Body ([Convert]::FromBase64String(
        'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII='))
$fixtureSourcePath = Join-Path $PSScriptRoot '..\tests\VDict.Infrastructure.Tests\Fixtures\BrowserAssetFixtures.cs'
$fixtureSource = Get-Content -LiteralPath $fixtureSourcePath -Raw
function Read-FixtureBase64([string] $constantName) {
    $pattern = 'private const string ' + [regex]::Escape($constantName) +
        '\s*=\s*"([^"]+)";'
    $match = [regex]::Match($fixtureSource, $pattern)
    if (-not $match.Success) {
        throw "Browser fixture constant '$constantName' was not found."
    }
    return [Convert]::FromBase64String($match.Groups[1].Value)
}
$fontPath = Join-Path $fixtureDirectory 'smoke.woff2'
[System.IO.File]::WriteAllBytes($fontPath, (Read-FixtureBase64 'Woff2Base64'))
$audioBytes = Read-FixtureBase64 'Mp3Base64'
$audioPath = Join-Path $fixtureDirectory 'voice.mp3'
[System.IO.File]::WriteAllBytes($audioPath, $audioBytes)
$extraAudioPath = Join-Path $fixtureDirectory 'extra.mp3'
[System.IO.File]::WriteAllBytes($extraAudioPath, $audioBytes)
$audioMddPath = Join-Path $fixtureDirectory 'smoke.1.mdd'
Write-SingleMdd `
    -Path $audioMddPath `
    -ResourceKey '\mdd-voice.mp3' `
    -Body $audioBytes
$scriptPath = Join-Path $fixtureDirectory 'fixture.js'
[System.IO.File]::WriteAllText(
    $scriptPath,
    'window.vdictFixture={classicLoaded:true};try{window.vdictLegacyEval=Function("return 42")();}catch{window.vdictLegacyEval="blocked";}',
    [System.Text.UTF8Encoding]::new($false))
$dependencyPath = Join-Path $fixtureDirectory 'dependency.js'
[System.IO.File]::WriteAllText(
    $dependencyPath,
    'export const value=41;',
    [System.Text.UTF8Encoding]::new($false))
$modulePath = Join-Path $fixtureDirectory 'module.js'
[System.IO.File]::WriteAllText(
    $modulePath,
    @'
import { value } from './dependency.js';
const response = await fetch('./data.json');
const data = await response.json();
localStorage.setItem('vdict-smoke-storage', data.mode);
window.vdictModuleResult = value + 1;
window.vdictFetchResult = data.mode;
'@,
    [System.Text.UTF8Encoding]::new($false))
$jsonPath = Join-Path $fixtureDirectory 'data.json'
[System.IO.File]::WriteAllText(
    $jsonPath,
    '{"mode":"offline"}',
    [System.Text.UTF8Encoding]::new($false))
$cssPath = Join-Path $fixtureDirectory 'smoke.css'
@'
@font-face { font-family: SmokeFont; src: url(smoke.woff2); }
.dictionary-content { font-family: SmokeFont, sans-serif; }
'@ | Set-Content -LiteralPath $cssPath -Encoding UTF8
if ($TypographyPhase4) {
    @'
.fixture-parent { font-size:22px; line-height:32px; }
.fixture-child { font-size:0.5em; line-height:1.5; }
'@ | Add-Content -LiteralPath $cssPath -Encoding UTF8
}

$settings = [ordered]@{
    schema_version = $SchemaVersion
    revision = 1
    dictionaries = @(
        [ordered]@{
            id = 'smoke-fixture'
            name = 'Smoke Fixture'
            display_name = 'Smoke Fixture'
            mdx_path = $mdxPath
            enabled = $true
            block_network = $true
            local_script_mode = 'standard'
            mdd_paths = @(
                if ($AudioPhase5 -or $Phase6) {
                    $mddPath
                    $audioMddPath
                } else {
                    $mddPath
                }
            )
            css_paths = @($cssPath)
            font_size = 18
            line_height = 1.65
            latin_font_family = if ($TypographyPhase4) { 'Arial' } else { 'Segoe UI' }
            cjk_font_family = 'Microsoft YaHei'
            override_font = [bool]$TypographyPhase4
            override_typography = $false
            interaction_mode = 'auto'
            dark_mode_strategy = if ($Theme -eq 'dark') { 'dark' } else { 'original' }
        }
        [ordered]@{
            id = 'smoke-legacy'
            name = 'Smoke Legacy'
            display_name = 'Smoke Legacy'
            mdx_path = $allowedMdxPath
            enabled = $true
            block_network = $true
            local_script_mode = 'legacy'
            mdd_paths = @()
            css_paths = @($cssPath)
            font_size = 18
            line_height = 1.65
            latin_font_family = 'Segoe UI'
            cjk_font_family = 'Microsoft YaHei'
            override_font = $false
            override_typography = $false
            interaction_mode = 'fallback'
            dark_mode_strategy = 'dark'
        }
    )
    style = [ordered]@{
        font_size = if ($TypographyPhase4) { 27 } else { 18 }
        line_height = if ($TypographyPhase4) { 1.98 } else { 1.65 }
        content_zoom = 1.0
        content_width_percent = $ContentWidthPercent
        theme = $Theme
        app_font_family = 'Microsoft YaHei UI'
        latin_font_family = 'Segoe UI'
        cjk_font_family = 'Microsoft YaHei'
    }
    reduce_dictionary_brightness_in_dark_mode = $true
    external_link_policy = 'block'
    compatibility_diagnostics_enabled = $true
}
if ($OmitContentWidth) {
    $settings.style.Remove('content_width_percent')
}

$settingsPath = Join-Path $settingsDirectory 'settings.json'
$settings |
    ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $settingsPath -Encoding UTF8

[pscustomobject]@{
    MdxPath = $mdxPath
    SettingsPath = $settingsPath
    Query = $word
    ExpectedReaderText = if ($Phase6) {
        'phase6-complex-check-pass'
    } elseif ($AudioPhase5) {
        'audio-warmup-check-pass'
    } elseif ($TypographyPhase4) {
        'typography-check-pass'
    } else {
        'fixture-entry-marker'
    }
}
