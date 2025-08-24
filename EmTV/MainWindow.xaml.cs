using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Text;

using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Storage.Pickers;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

using Microsoft.UI.Xaml.Controls;  // TextBlock
using Microsoft.UI.Xaml.Media;     // FontFamily

using WinRT;

namespace EmTV
{
    public sealed partial class MainWindow : Window
    {
        // Models
        public record Channel(string Name, string Group, string? Logo, string Url);
        public record PlaylistSlot(string Emoji, string? Url);

        // Fields
        private readonly MediaPlayer _mp = new();
        private AppWindow? _appWindow;
        private bool _isFull;
        private GridLength _savedLeftWidth = new GridLength(360);

        // 6 quick playlist buttons (slot 1 pre-configured for Thailand)
        private List<PlaylistSlot> _playlistSlots = new()
        {
            new("🛕", "https://raw.githubusercontent.com/akkradet/IPTV-THAI/refs/heads/master/FREETV.m3u"),
            new("⭐",  null),
            new("📺",  null),
            new("🎬",  null),
            new("🌍",  null),
            new("🧪",  null),
        };

        public MainWindow()
        {
            InitializeComponent();

            // Wire player and start idle (show poster)
            Player.SetMediaPlayer(_mp);
            _mp.AutoPlay = false;
            _mp.Source = null;
            Player.AreTransportControlsEnabled = false; // off by default

            // Hide seek bar for live TV
            Player.TransportControls.IsSeekBarVisible = false;
            Player.TransportControls.IsSeekEnabled = false;

            // Keyboard shortcuts ready
            Activated += (_, __) => VideoHost.Focus(FocusState.Programmatic);

            // AppWindow for fullscreen
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var winId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(winId);

            // Start with NO channels loaded
            Samples.ItemsSource = Array.Empty<Channel>();

            // Show/hide controls + overlay based on playback state
            var session = _mp.PlaybackSession;
            session.PlaybackStateChanged += (_, __) => DispatcherQueue.TryEnqueue(() =>
            {
                var st = session.PlaybackState;

                // Controls only when ready
                Player.AreTransportControlsEnabled =
                    st == MediaPlaybackState.Playing || st == MediaPlaybackState.Paused;

                // Overlay during Opening/Buffering
                LoadingOverlay.Visibility =
                    (st == MediaPlaybackState.Opening || st == MediaPlaybackState.Buffering)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            });

            // On failure: hide overlay, show controls so the user can act
            _mp.MediaFailed += (_, __) => DispatcherQueue.TryEnqueue(() =>
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                Player.AreTransportControlsEnabled = true;
            });

            // Init emoji playlist buttons (doesn't auto-load anything)
            _ = InitPlaylistsAsync();
        }

        // -------- Playback (headers supported) --------
        private async Task PlayUrlAsync(string url, IDictionary<string, string>? headers = null)
        {
            // Hide controls + show overlay immediately
            DispatcherQueue.TryEnqueue(() =>
            {
                Player.AreTransportControlsEnabled = false;
                LoadingOverlay.Visibility = Visibility.Visible;
            });

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
                ams.DesiredLiveOffset = TimeSpan.FromSeconds(3); // tweak for smoother live
                _mp.Source = MediaSource.CreateFromAdaptiveMediaSource(ams);
                _mp.Play();
            }
            else
            {
                _mp.Source = MediaSource.CreateFromUri(uri);
                _mp.Play();
            }
        }

        // -------- Channel list click --------
        private void OnSampleClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Channel ch)
                _ = PlayUrlAsync(ch.Url);
        }

        // -------- Advanced Controls dialog --------
        private async void OnAdvancedControlsClick(object sender, RoutedEventArgs e)
        {
            var urlBox = new TextBox
            {
                PlaceholderText = "https://...m3u8",
                MinWidth = 340
            };

            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock { Text = "Quick play URL", FontWeight = FontWeights.SemiBold });
            panel.Children.Add(urlBox);

            var dlg = new ContentDialog
            {
                Title = "Advanced Controls",
                Content = panel,
                PrimaryButtonText = "Play",
                SecondaryButtonText = "Load .m3u",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };

            var result = await dlg.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                var url = urlBox.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(url))
                    await PlayUrlAsync(url);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                await PickAndLoadM3UAsync();
            }
        }

        private async Task PickAndLoadM3UAsync()
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

        // -------- Emoji playlist buttons --------
        private async Task InitPlaylistsAsync()
        {
            // Optional override via playlists.json in LocalFolder
            try
            {
                var local = Windows.Storage.ApplicationData.Current.LocalFolder;
                var file = await local.TryGetItemAsync("playlists.json") as Windows.Storage.StorageFile;
                if (file is not null)
                {
                    var json = await Windows.Storage.FileIO.ReadTextAsync(file);
                    var parsed = System.Text.Json.JsonSerializer.Deserialize<List<PlaylistSlot>>(json);
                    if (parsed is { Count: > 0 }) _playlistSlots = parsed.Take(6).ToList();
                }
            }
            catch { /* ignore config errors */ }

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

            // Use a TextBlock with Segoe UI Emoji so flags render as flags
            btn.Content = EmojiBlock(slot.Emoji);
            btn.Tag = slot.Url; // keep the URL for click handler
        }


        private async void OnPlaylistButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var url = btn.Tag as string;
            if (string.IsNullOrWhiteSpace(url))
            {
                System.Diagnostics.Debug.WriteLine("Playlist slot not configured.");
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

        // -------- M3U parsing helpers --------
        private static IEnumerable<Channel> ParseM3UFromFile(string path)
        {
            string? name = null, group = "", logo = null;
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    name = line.Split(',').LastOrDefault()?.Trim();
                    group = GetAttr(line, "group-title") ?? "";
                    logo = GetAttr(line, "tvg-logo");
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
                    group = GetAttr(line, "group-title") ?? "";
                    logo = GetAttr(line, "tvg-logo");
                }
                else if (!line.StartsWith("#") && !string.IsNullOrWhiteSpace(line) && name is not null)
                {
                    yield return new Channel(name, group, logo, line);
                    name = null; group = ""; logo = null;
                }
            }
        }

        private static string? GetAttr(string s, string key)
        {
            var k = key + "=\"";
            var i = s.IndexOf(k, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            var j = s.IndexOf('"', i + k.Length);
            return j > i ? s.Substring(i + k.Length, j - (i + k.Length)) : null;
        }

        // -------- Fullscreen controls --------
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

        private static TextBlock EmojiBlock(string emoji) => new TextBlock
        {
            Text = emoji,
            FontFamily = new FontFamily("Segoe UI Emoji"),
            FontSize = 28,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center,
            LineStackingStrategy = Microsoft.UI.Xaml.LineStackingStrategy.BlockLineHeight,
            LineHeight = 36
        };

    }
}



