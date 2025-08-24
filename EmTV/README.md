# EmTV (WinUI 3, Windows App SDK)

A simple IPTV player for Windows with:
- M3U playlist loading (file or URL)
- Emoji quick-select playlist buttons
- Fullscreen toggle
- Loading & error overlays (no sticky built-in error banner)

## Build
- Windows 11
- Visual Studio 2022
- .NET 8, Windows App SDK (WinUI 3)

## Run
1. Open `EmTV.sln` in Visual Studio
2. Set configuration: `Release | x64`
3. Build & run

## Notes
- Place your splash image at `Assets/EmTV-Load.png` (Build Action: **Content**)
- Optional: put a `playlists.json` in LocalState to configure the 6 emoji buttons