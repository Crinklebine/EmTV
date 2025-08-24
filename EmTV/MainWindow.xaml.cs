using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Storage.Pickers;
using Windows.Web.Http;
using Windows.Web.Http.Filters;
using WinRT;

namespace EmTV
{
    public sealed partial class MainWindow : Window
    {
        // --- Models ---
        public record Sample(string Name, string Url);
        public record Channel(string Name, string Group, string? Logo, string Url);
        public record PlaylistSlot(string Emoji, string? Url);

        // --- Fields ---
        private readonly MediaPlayer _mp = new();
        private AppWindow? _appWindow;
        private bool _isFull;
        private GridLength _savedLeftWidth = new GridLength(360);

        // 6 playlist slots; slot 1 (🇹🇭) preconfigured, others placeholders for future config
        private List<PlaylistSlot> _playlistSlots = new()
        {
            new("🇹🇭", "https://raw.githubusercontent.com/akkradet/IPTV-THAI/refs/heads/master/FREETV.m3u"),
            new("⭐",  null),
            new("📺",  null),
            new("🎬",  null),
            new("🌍",  null),
            new("🧪",  null),
        };

        public MainWindow()
        {
            InitializeComponent();

            // Hide timeline for live TV
            Player.TransportControls.IsSeekBarVisible = false;
            Player.TransportControls.IsSeekEnabled = false;

            // Attach the MediaPlayer to XAML element
            Player.SetMediaPlayer(_mp);
            _mp.AutoPlay = true;
            _mp.MediaFailed += (s, e) =>
                System.Diagnostics.Debug.WriteLine($"MediaFailed: {e.Error} {e.ErrorMessage}");

            // Focus video host so key events work (F/Esc/Arrows)
            Activated += (_, __) => VideoHost.Focus(FocusState.Programmatic);

            // AppWindow for true fullscreen
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var winId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(winId);

            // Quick sample channels (you can remove later)
            Samples.ItemsSource = new List<Sample>
            {
                new("7HD",    "https://lb1-live-mv.v2h-cdn.com/hls/ffac/gohg/gohg.m3u8"),
                new("Amarin", "https://lb1-live-mv.v2h-cdn.com/hls/ffad/vibomi/vibomi.m3u8"),
                new("Test (BBB)", "https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8"),
            };

            // Initialize playlist buttons (apply emoji + URLs; optional JSON override)
            _ = InitPlaylistsAsync();

            // Start first sample
            _ = PlayUrlAsync(((Sample)Samples.Items[0]).Url);
        }

        // =========================
        // Playback (with headers)
        // =========================
        private async Task PlayUrlAsync(string url, IDictionary<string, string>? headers = null)
        {
            var uri = new Uri(url);
            AdaptiveMediaSourceCreationResult createResult;

            if (headers is not null && headers.Count > 0)
            {
                var filter = new HttpBaseProtocolFilter();
                var client = new HttpClient(filter);
                foreach (var kv in headers)
                    client.DefaultRequestHeaders.TryAppendWithoutValidation(kv.Key, kv.Value);

                createResult = await AdaptiveMediaSource.CreateFromUriAsync(uri, client);
            }
            else
            {
                createResult = await AdaptiveMediaSource.CreateFromUriAsync(uri);
            }

            if (createResult.Status == AdaptiveMediaSourceCreationStatus.Success)
            {
                var ams = createResult.MediaSource;
                ams.DesiredLiveOffset = TimeSpan.FromSeconds(3); // tweak for smoother live playback
                var mediaSource = MediaSource.CreateFromAdaptiveMediaSource(ams);
                _mp.Source = mediaSource; // or new MediaPlaybackItem(mediaSource)
                _mp.Play();
            }
            else
            {
                _mp.Source = MediaSource.CreateFromUri(uri);
                _mp.Play();
            }
        }

        // =========================
        // Left pane actions
        // =========================
        private void OnPlayClick(object sender, RoutedEventArgs e)
        {
            var url = UrlBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(url))
                _ = PlayUrlAsync(url);
        }

        private void OnSampleClick(object sender, ItemClickEventArgs e)
        {
            switch (e.ClickedItem)
            {
                case Sample s:
                    UrlBox.Text = s.Url;
                    _ = PlayUrlAsync(s.Url);
                    break;
                case Channel ch:
                    UrlBox.Text = ch.Url;
                    _ = PlayUrlAsync(ch.Url);
                    break;
            }
        }

        private async void OnLoadM3U(object sender, RoutedEventArgs e)
        {
            var picker = new FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            picker.FileTypeFilter.Add(".m3u");
            picker.FileTypeFilter.Add(".m3u8");

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var chs = ParseM3UFromFile(file.Path).ToList();
            Samples.ItemsSource = chs;
            Samples.SelectedIndex = chs.Count > 0 ? 0 : -1;
            if (chs.Count > 0) _ = PlayUrlAsync(chs[0].Url);
        }

        // =========================
        // Emoji playlist buttons
        // =========================
        private async Task InitPlaylistsAsync()
        {
            // Optional: look for a user-provided JSON to override slots:
            //   playlists.json in LocalFolder
            //   Format: [ {"Emoji":"🇹🇭","Url":"https://..."}, {"Emoji":"⭐","Url":null}, ... ]
            try
            {
                var local = Windows.Storage.ApplicationData.Current.LocalFolder;
                var file = await local.TryGetItemAsync("playlists.json") as Windows.Storage.StorageFile;
                if (file is not null)
                {
                    var json = await Windows.Storage.FileIO.ReadTextAsync(file);
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<List<PlaylistSlot>>(json);
                    if (parsed is { Count: > 0 })
                        _playlistSlots = parsed.Take(6).ToList();
                }
            }
            catch
            {
                // ignore config errors silently
            }

            // Apply to UI buttons (store URL into Tag; Content is emoji)
            ApplyPlaylistSlotToButton(PlaylistBtn1, 0);
            ApplyPlaylistSlotToButton(PlaylistBtn2, 1);
            ApplyPlaylistSlotToButton(PlaylistBtn3, 2);
            ApplyPlaylistSlotToButton(PlaylistBtn4, 3);
            ApplyPlaylistSlotToButton(PlaylistBtn5, 4);
            ApplyPlaylistSlotToButton(PlaylistBtn6, 5);
        }

        private void ApplyPlaylistSlotToButton(Button btn, int index)
        {
            if (index >= _playlistSlots.Count) return;
            var slot = _playlistSlots[index];
            btn.Content = slot.Emoji;
            btn.Tag = slot.Url; // keep URL hidden in Tag; null means not configured yet
        }

        private async void OnPlaylistButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var url = btn.Tag as string;

            if (string.IsNullOrWhiteSpace(url))
            {
                System.Diagnostics.Debug.WriteLine("Playlist slot not configured yet.");
                return;
            }

            await LoadM3UFromUriAsync(url);
        }

        private async Task LoadM3UFromUriAsync(string url)
        {
            try
            {
                var client = new HttpClient(new HttpBaseProtocolFilter());
                var text = await client.GetStringAsync(new Uri(url));
                var chs = ParseM3UFromString(text).ToList();
                Samples.ItemsSource = chs;
                Samples.SelectedIndex = chs.Count > 0 ? 0 : -1;
                if (chs.Count > 0) _ = PlayUrlAsync(chs[0].Url);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load M3U: {ex.Message}");
            }
        }

        // =========================
        // Parsing helpers
        // =========================
        private static IEnumerable<Channel> ParseM3UFromFile(string path)
        {
            string? name = null, group = "", logo = null;
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    name = line.Split(',').LastOrDefault()?.Trim();
                    group = GetAttrFromExtinf(line, "group-title") ?? "";
                    logo = GetAttrFromExtinf(line, "tvg-logo");
                }
                else if (!line.StartsWith("#") && !string.IsNullOrWhiteSpace(line) && name is not null)
                {
                    yield return new Channel(name, group, logo, line.Trim());
                    name = null; group = ""; logo = null;
                }
            }
        }

        private static IEnumerable<Channel> ParseM3UFromString(string content)
        {
            string? name = null, group = "", logo = null;
            foreach (var raw in content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var line = raw.Trim();
                if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    name = line.Split(',').LastOrDefault()?.Trim();
                    group = GetAttrFromExtinf(line, "group-title") ?? "";
                    logo = GetAttrFromExtinf(line, "tvg-logo");
                }
                else if (!line.StartsWith("#") && !string.IsNullOrWhiteSpace(line) && name is not null)
                {
                    yield return new Channel(name, group, logo, line);
                    name = null; group = ""; logo = null;
                }
            }
        }

        private static string? GetAttrFromExtinf(string s, string key)
        {
            var k = key + "=\"";
            var i = s.IndexOf(k, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            var j = s.IndexOf('"', i + k.Length);
            return j > i ? s.Substring(i + k.Length, j - (i + k.Length)) : null;
        }

        // =========================
        // Fullscreen controls
        // =========================
        private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();
        private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ToggleFullscreen();

        private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.F) { ToggleFullscreen(); e.Handled = true; return; }
            if (e.Key == Windows.System.VirtualKey.Escape && _isFull) { ToggleFullscreen(); e.Handled = true; return; }
        }

        private void ToggleFullscreen()
        {
            if (_isFull)
            {
                try { _appWindow?.SetPresenter(AppWindowPresenterKind.Default); } catch { }
                LeftCol.Width = _savedLeftWidth;
                LeftPane.Visibility = Visibility.Visible;
                _isFull = false;
                SetFullIcon(false);
            }
            else
            {
                _savedLeftWidth = LeftCol.Width;
                LeftPane.Visibility = Visibility.Collapsed;
                LeftCol.Width = new GridLength(0);
                try { _appWindow?.SetPresenter(AppWindowPresenterKind.FullScreen); } catch { }
                _isFull = true;
                SetFullIcon(true);
            }
        }

        private void SetFullIcon(bool full)
        {
            // Segoe MDL2: E740 = Fullscreen, E73F = BackToWindow
            FullIcon.Glyph = full ? "\uE73F" : "\uE740";
        }
    }
}

