using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.UI.Text;
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
        // -------- Models --------
        public record Channel(string Name, string Group, string? Logo, string Url);
        public record PlaylistSlot(string Emoji, string? Url);

        // -------- Fields --------
        private readonly MediaPlayer _mp = new();   // single player, no detach/rebuild
        private AppWindow? _appWindow;
        private bool _isFull;
        private GridLength _savedLeftWidth = new GridLength(360);

        // Six quick playlist buttons (slot 1 preconfigured)
        private List<PlaylistSlot> _playlistSlots = new()
        {
            new("🛕", "https://raw.githubusercontent.com/akkradet/IPTV-THAI/refs/heads/master/FREETV.m3u"),
            new("🍁", "https://raw.githubusercontent.com/iptv-org/iptv/refs/heads/master/streams/ca.m3u"),
            new("💂", "https://raw.githubusercontent.com/iptv-org/iptv/refs/heads/master/streams/uk.m3u"),
            new("🗽", "https://raw.githubusercontent.com/iptv-org/iptv/refs/heads/master/streams/us.m3u"),
            new("🍀", "https://raw.githubusercontent.com/iptv-org/iptv/refs/heads/master/streams/ie.m3u"),
            new("🦘", "https://raw.githubusercontent.com/iptv-org/iptv/refs/heads/master/streams/au.m3u"),
        };

        public MainWindow()
        {
            InitializeComponent();

            // Wire a single MediaPlayer to the element
            Player.SetMediaPlayer(_mp);
            _mp.AutoPlay = false;       // poster on first launch
            Player.Source = null;
            Player.AreTransportControlsEnabled = false;

            // Live TV: hide seekbar
            Player.TransportControls.IsSeekBarVisible = false;
            Player.TransportControls.IsSeekEnabled = false;

            // State -> overlays + when to show controls
            _mp.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
            _mp.MediaFailed += OnMediaFailed;

            // Keyboard shortcuts focus
            Activated += (_, __) => VideoHost.Focus(FocusState.Programmatic);

            // AppWindow for fullscreen
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var winId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(winId);

            // Set the small caption/task-switcher icon
            try { _appWindow.SetIcon("Assets/emtv.ico"); } catch { /* ignore if not supported */ }

            // Start empty
            Samples.ItemsSource = Array.Empty<Channel>();

            // Initialize emoji buttons (optional override via playlists.json)
            _ = InitPlaylistsAsync();
        }

        // =========================================================
        // TransportControls reset (clears any old built-in error banner)
        // =========================================================
        private void ResetTransportControls()
        {
            // Replace the transport controls instance to flush internal state/banner
            var mtc = new MediaTransportControls();
            Player.TransportControls = mtc;

            // Re-apply our settings
            mtc.IsSeekBarVisible = false;
            mtc.IsSeekEnabled = false;

            // Keep them hidden until the stream is ready
            Player.AreTransportControlsEnabled = false;
        }

        // =========================================================
        // Playback state & failure
        // =========================================================
        private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var st = sender.PlaybackState;

                // Loading overlay while Opening/Buffering
                LoadingOverlay.Visibility =
                    (st == MediaPlaybackState.Opening || st == MediaPlaybackState.Buffering)
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                // Show built-in controls only when actually ready
                Player.AreTransportControlsEnabled =
                    st == MediaPlaybackState.Playing || st == MediaPlaybackState.Paused;
            });
        }

        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                // Stop spinner, show our error (not the built-in banner)
                LoadingOverlay.Visibility = Visibility.Collapsed;
                ShowErrorOverlay($"{e.Error} (0x{e.ExtendedErrorCode.HResult:X8})");

                // Return to poster
                try { sender.Pause(); } catch { }
                sender.Source = null;
                Player.Source = null;

                // Keep controls off on failure
                Player.AreTransportControlsEnabled = false;
            });
        }

        // Error overlay helpers
        private void ShowErrorOverlay(string message)
        {
            ErrorDetail.Text = string.IsNullOrWhiteSpace(message) ? "Playback failed." : message;
            ErrorOverlay.Visibility = Visibility.Visible;
        }

        private void HideErrorOverlay()
        {
            ErrorOverlay.Visibility = Visibility.Collapsed;
            ErrorDetail.Text = string.Empty;
        }

        private void OnDismissError(object sender, RoutedEventArgs e) => HideErrorOverlay();

        // =========================================================
        // Start playback (headers optional) — simple, reliable
        // =========================================================
        private async Task PlayUrlAsync(string url, IDictionary<string, string>? headers = null)
        {
            // UI to loading and clear any prior overlay/banner
            HideErrorOverlay();
            LoadingOverlay.Visibility = Visibility.Visible;

            // Reset transport controls to flush any old built-in error banner
            ResetTransportControls();

            var uri = new Uri(url);
            AdaptiveMediaSourceCreationResult createResult;

            try
            {
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
                    ams.DesiredLiveOffset = TimeSpan.FromSeconds(3); // smoother live
                    _mp.Source = MediaSource.CreateFromAdaptiveMediaSource(ams);
                }
                else
                {
                    _mp.Source = MediaSource.CreateFromUri(uri);
                }

                _mp.Play();
            }
            catch (Exception ex)
            {
                // Surface as our own error (network/parse exceptions, etc.)
                LoadingOverlay.Visibility = Visibility.Collapsed;
                ShowErrorOverlay(ex.Message);
                _mp.Source = null;
                Player.Source = null;
            }
        }

        // =========================================================
        // Left pane interactions
        // =========================================================
        private void OnSampleClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Channel ch)
                _ = PlayUrlAsync(ch.Url);
        }

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

        // Quick playlist buttons (emoji row)
        private async Task InitPlaylistsAsync()
        {
            // Optional override via playlists.json in LocalState
            try
            {
                var local = Windows.Storage.ApplicationData.Current.LocalFolder;
                var sf = await local.TryGetItemAsync("playlists.json") as Windows.Storage.StorageFile;
                if (sf is not null)
                {
                    var json = await Windows.Storage.FileIO.ReadTextAsync(sf);
                    var parsed = JsonSerializer.Deserialize<List<PlaylistSlot>>(json);
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
            btn.Content = slot.Emoji;  // text; style in XAML handles font/size
            btn.Tag = slot.Url;        // URL for click handler (null = not configured)
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

        // =========================================================
        // M3U parsing helpers
        // =========================================================
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

        // =========================================================
        // Fullscreen controls
        // =========================================================
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




