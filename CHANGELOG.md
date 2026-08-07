# Changelog

VDict follows [Semantic Versioning](https://semver.org/). User-visible changes
are recorded here.

## Unreleased

## 0.1.1 - 2026-08-07

- Settings text inputs now detect edits immediately and update the save state.
- Pressing Enter or clicking non-interactive background space now commits pending
  settings input and moves focus out of the active editor.
- Updated the bundled SQLite native dependency to `SQLitePCLRaw 2.1.12`.
- Simplified release output to the self-contained Windows x64 portable ZIP.

## 0.1.0 - 2026-07-27

- Initial public preview of the Windows x64 MDX/MDD dictionary reader.
- Local dictionary indexing, suggestions, lookup history, redirects, and
  multi-dictionary results.
- Controlled WebView2 rendering for dictionary styles, scripts, images, audio,
  and relative resources.
- Chinese, English, and system-language UI with configurable reader appearance.
- Portable self-contained Windows x64 distribution.
