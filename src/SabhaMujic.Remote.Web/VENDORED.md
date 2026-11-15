# Vendored third-party files

| File | Source | Version | License | SHA-256 |
|---|---|---|---|---|
| `wwwroot/vendor/signalr/signalr.min.js` | npm `@microsoft/signalr`, `dist/browser/signalr.min.js` (UMD build, defines the global `signalR`) | 10.0.11 | MIT (`wwwroot/vendor/signalr/LICENSE.txt`, the dotnet/aspnetcore license; the npm package ships none) | `97e9b97e642a72e5a470917147a2bf79f86cad829a5c6786adb10614d248bb95` |

The server is ASP.NET Core 10.0.15. The JavaScript client is versioned separately, and 10.0.11 is the latest 10.x release on npm. Both use SignalR hub protocol 1 (JSON), so they are compatible.

To update it, change the `@microsoft/signalr` version in `package.json`, then run `npm install && npm run vendor`. Update the version and the SHA-256 that the script prints in this file, then rebuild `SabhaMujic.Server`. The web assets are embedded resources, so the rebuild is what picks them up.

## Development tools (not shipped)
| Package | Version | Use |
|---|---|---|
| `typescript` | 5.9.3 | `npm run typecheck` (JSDoc types, `checkJs`) |
| `playwright` | 1.63.0 | `npm test` (Chromium with iPhone 13 emulation). Browsers come from `PLAYWRIGHT_BROWSERS_PATH`. |
