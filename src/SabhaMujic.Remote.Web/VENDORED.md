# Vendored third-party files

| File | Source | Version | License | SHA-256 |
|---|---|---|---|---|
| `wwwroot/vendor/signalr/signalr.min.js` | npm `@microsoft/signalr`, `dist/browser/signalr.min.js` (UMD build, defines the global `signalR`) | 10.0.11 | MIT (`wwwroot/vendor/signalr/LICENSE.txt`, the dotnet/aspnetcore license; the npm package ships none) | `97e9b97e642a72e5a470917147a2bf79f86cad829a5c6786adb10614d248bb95` |
| `wwwroot/fonts/Inter-Regular.woff2` | Inter woff2, as staged by the lead (the font tables report revision 4.066) | 4.1 | SIL OFL 1.1 (`wwwroot/fonts/LICENSE.txt`) | `e06f6b1bc553aaea4e4668023ed0ab0a147129c3107f511bc7d03d361b0ae085` |
| `wwwroot/fonts/Inter-Medium.woff2` | same | 4.1 | SIL OFL 1.1 | `0ff3e94614e1493eb556314fd247ae6c4a85a7783b4cc86be539940cf83f2a48` |
| `wwwroot/fonts/Inter-SemiBold.woff2` | same | 4.1 | SIL OFL 1.1 | `5cb7103e4e605989afebc03d989c79201e54b21b5183db33981f70db9178a301` |
| `wwwroot/fonts/Inter-Bold.woff2` | same | 4.1 | SIL OFL 1.1 | `fa888127b6da015b65569f0351f3b5c391ad928904951f1c20e9f8462a8d95ea` |

The server is ASP.NET Core 10.0.15. The JavaScript client is versioned separately, and 10.0.11 is the latest 10.x release on npm. Both use SignalR hub protocol 1 (JSON), so they are compatible.

The fonts are served by the remote itself as `font/woff2` (`WebAssets.cs`); no CDN. The existing CSP (`font-src 'self'`) covers them.

To update the SignalR client, change the `@microsoft/signalr` version in `package.json`, then run `npm install && npm run vendor`. Update the version and the SHA-256 that the script prints in this file, then rebuild `SabhaMujic.Server`. The web assets are embedded resources, so the rebuild is what picks them up.

## Development tools (not shipped)
| Package | Version | Use |
|---|---|---|
| `typescript` | 5.9.3 | `npm run typecheck` (JSDoc types, `checkJs`) |
| `playwright` | 1.63.0 | `npm test` (Chromium with iPhone 13 emulation). Browsers come from `PLAYWRIGHT_BROWSERS_PATH`. |
