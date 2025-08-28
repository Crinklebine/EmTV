using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Windows.ApplicationModel;               // Package.Current.Id.Version
using Windows.Graphics;                      // RectInt32
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Storage;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace EmTV
{
    public sealed partial class MainWindow : Window
    {
        // --- Simple models ---
        public record Channel(string Name, string Group, string? Logo, string Url);
        public record PlaylistSlot(string Emoji, string? Url);

        // --- Fields/state ---
        private MediaPlayer _mp;
        private AppWindow? _appWindow;
        private bool _isFull;

        private bool _hasPlaylist;
        private string? _activePlaylistName;

        private List<Channel> _allChannels = new();   // master list for search/filter

        // PiP
        private Window? _pipWindow;
        private MediaPlayerElement? _pipElement;
        private AppWindow? _pipAppWindow;
        private bool _isPip;

        // ======= HARD-CODED PLAYLIST SLOTS (edit these) =======
        // Slot 1: Thai playlist; slots 2–6: fill in later.
        private readonly List<PlaylistSlot> _playlistSlots = new()
        {
            new("🛕", "https://raw.githubusercontent.com/akkradet/IPTV-THAI/refs/heads/master/FREETV.m3u"),  // Thailand
            new("💂", "https://raw.githubusercontent.com/iptv-org/iptv/refs/heads/master/streams/uk.m3u"),   // UK
            new("🍁", "https://raw.githubusercontent.com/iptv-org/iptv/refs/heads/master/streams/ca.m3u"),   // Canada
            new("🗽", "https://raw.githubusercontent.com/iptv-org/iptv/refs/heads/master/streams/us.m3u"),   // USA            
            new("🦘", "https://raw.githubusercontent.com/iptv-org/iptv/refs/heads/master/streams/au.m3u"),   // Australia
            new("🌏", "https://iptv-org.github.io/iptv/index.m3u"),                                          // World
        };
        // =======================================================

        public MainWindow()
        {
            InitializeComponent();

            // AppWindow + optional caption icon
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var winId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(winId);
            try { _appWindow?.SetIcon("Assets/emtv.ico"); } catch { }

            // MediaPlayer
            _mp = new MediaPlayer();
            _mp.MediaFailed += OnMediaFailed;
            _mp.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;

            Player.SetMediaPlayer(_mp);
            Player.AreTransportControlsEnabled = false;

            // Optional: hide seek bar (live TV UX)
            try
            {
                Player.TransportControls.IsSeekBarVisible = false;
                Player.TransportControls.IsSeekEnabled = false;
            }
            catch { }

            // Initial state
            _hasPlaylist = false;
            SetActivePlaylistName(null);
            UpdateIdleOverlay();

            // Wire the six emoji buttons from the hard-coded slots
            ApplyPlaylistSlotsToButtons();
        }

        // ====================== UI helpers ======================

        private void SetActivePlaylistName(string? name)
        {
            _activePlaylistName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            ChannelHeader.Text = _activePlaylistName is null ? "Channels" : $"Channels: {_activePlaylistName}";
        }

        private static string FriendlyNameFromUrl(string url)
        {
            try
            {
                var u = new Uri(url);
                var last = u.Segments.LastOrDefault()?.Trim('/') ?? "";
                if (last.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) ||
                    last.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileNameWithoutExtension(last);
                return u.Host;
            }
            catch { return "Playlist"; }
        }

        private void UpdateIdleOverlay()
        {
            var st = _mp.PlaybackSession?.PlaybackState;
            bool isPlayingOrPaused = st == MediaPlaybackState.Playing || st == MediaPlaybackState.Paused;

            bool show = _hasPlaylist
                        && LoadingOverlay.Visibility != Visibility.Visible
                        && ErrorOverlay.Visibility != Visibility.Visible
                        && !isPlayingOrPaused;

            IdleOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        // Error overlay
        private void ShowErrorOverlay(string message)
        {
            ErrorDetail.Text = message;
            ErrorOverlay.Visibility = Visibility.Visible;
            Player.AreTransportControlsEnabled = false;
            UpdateIdleOverlay();
        }
        private void HideErrorOverlay()
        {
            ErrorOverlay.Visibility = Visibility.Collapsed;
            ErrorDetail.Text = string.Empty;
            UpdateIdleOverlay();
        }
        private void OnDismissError(object sender, RoutedEventArgs e) => HideErrorOverlay();

        // ====================== Search/filter ======================

        private void ApplyChannelFilter(string? query = null)
        {
            var q = (query ?? (SearchBox?.Text ?? string.Empty)).Trim();

            IEnumerable<Channel> src = _allChannels;
            if (!string.IsNullOrEmpty(q))
            {
                src = _allChannels.Where(c =>
                    (!string.IsNullOrEmpty(c.Name) && c.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)) ||
                    (!string.IsNullOrEmpty(c.Group) && c.Group.Contains(q, StringComparison.CurrentCultureIgnoreCase)));
            }

            var view = src.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            Samples.ItemsSource = view;
            Samples.SelectedIndex = -1;
            UpdateIdleOverlay();
        }

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => ApplyChannelFilter();

        private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter || e.Key == Windows.System.VirtualKey.Down)
            {
                if (Samples.Items.Count > 0)
                {
                    Samples.SelectedIndex = 0;
                    Samples.Focus(FocusState.Programmatic);
                }
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                SearchBox.Text = "";
                e.Handled = true;
            }
        }

        // ====================== Playlist slots → buttons ======================

        private void ApplyPlaylistSlotsToButtons()
        {
            ApplyPlaylistSlotToButton(PlaylistBtn1, 0);
            ApplyPlaylistSlotToButton(PlaylistBtn2, 1);
            ApplyPlaylistSlotToButton(PlaylistBtn3, 2);
            ApplyPlaylistSlotToButton(PlaylistBtn4, 3);
            ApplyPlaylistSlotToButton(PlaylistBtn5, 4);
            ApplyPlaylistSlotToButton(PlaylistBtn6, 5);
        }

        private void ApplyPlaylistSlotToButton(Button btn, int index)
        {
            if (index < 0 || index >= _playlistSlots.Count) return;
            var slot = _playlistSlots[index];

            // Emoji content
            btn.Content = slot.Emoji;

            // URL sits in Tag (null = not configured yet)
            btn.Tag = slot.Url;

            // If you want explicit tooltips for header naming, set them per index:
            if (index == 0 && ToolTipService.GetToolTip(btn) is null)
                ToolTipService.SetToolTip(btn, "Thai playlist");
            // (leave others as-is or set later)
        }

        private async void OnPlaylistButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            var url = btn.Tag as string;

            if (string.IsNullOrWhiteSpace(url))
            {
                ShowErrorOverlay("This playlist button isn’t configured yet.\nUse Advanced Controls → Load URL to set one.");
                return;
            }

            // Friendly name: prefer URL filename/host
            await LoadM3UFromUriAsync(url, FriendlyNameFromUrl(url));
        }

        // ====================== Playlist load (URL/File/Dialog) ======================

        private async Task LoadM3UFromUriAsync(string url, string? friendlyName = null)
        {
            try
            {
                var client = new HttpClient(new HttpBaseProtocolFilter());
                client.DefaultRequestHeaders.UserAgent.TryParseAdd("EmTV/1.0");

                var text = await client.GetStringAsync(new Uri(url));
                var chs = ParseM3UFromString(text).ToList();

                _allChannels = chs;
                _hasPlaylist = _allChannels.Count > 0;
                SetActivePlaylistName(friendlyName ?? FriendlyNameFromUrl(url));
                ApplyChannelFilter();
                UpdateIdleOverlay();
            }
            catch (Exception ex)
            {
                ShowErrorOverlay($"Failed to load playlist.\n{ex.Message}");
            }
        }

        private async Task PickAndLoadM3UAsync()
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            picker.FileTypeFilter.Add(".m3u");
            picker.FileTypeFilter.Add(".m3u8");

            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            var chs = ParseM3UFromFile(file.Path).ToList();
            _allChannels = chs;
            _hasPlaylist = _allChannels.Count > 0;
            SetActivePlaylistName(file.DisplayName);
            ApplyChannelFilter();
            UpdateIdleOverlay();
        }

        private async Task LoadM3UFromUrlOrPathAsync(string input)
        {
            if (Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                await LoadM3UFromUriAsync(uri.ToString(), FriendlyNameFromUrl(uri.ToString()));
                return;
            }
            if (File.Exists(input))
            {
                var chs = ParseM3UFromFile(input).ToList();
                _allChannels = chs;
                _hasPlaylist = _allChannels.Count > 0;
                SetActivePlaylistName(Path.GetFileNameWithoutExtension(input));
                ApplyChannelFilter();
                UpdateIdleOverlay();
                return;
            }
            ShowErrorOverlay("Not a valid URL or file path.");
        }

        private void OnSampleClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Channel ch)
                _ = PlayUrlAsync(ch.Url);
        }

        private async void OnAdvancedControlsClick(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var box = new Microsoft.UI.Xaml.Controls.TextBox
            {
                // cleaned-up label (no more “leave empty…”)
                PlaceholderText = "Paste an http(s) .m3u or .m3u8 URL",
                MinWidth = 420
            };

            var dlg = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                Title = "Advanced Controls",
                Content = box,
                PrimaryButtonText = "Load URL",
                SecondaryButtonText = "Open .m3u file…",
                CloseButtonText = "Cancel",
                DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };

            // (Optional but nice) Disable “Load URL” until a valid http(s) URL is typed
            dlg.IsPrimaryButtonEnabled = false;
            box.TextChanged += (_, __) =>
            {
                var txt = (box.Text ?? "").Trim();
                dlg.IsPrimaryButtonEnabled =
                    Uri.TryCreate(txt, UriKind.Absolute, out var u) &&
                    (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) &&
                    (u.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) ||
                     u.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase));
            };

            var res = await dlg.ShowAsync();
            if (res == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary)
            {
                var txt = box.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(txt))
                    await LoadM3UFromUrlOrPathAsync(txt);
                else
                    ShowErrorOverlay("Please paste an http(s) .m3u/.m3u8 URL, or click “Open .m3u file…”.");
            }
            else if (res == Microsoft.UI.Xaml.Controls.ContentDialogResult.Secondary)
            {
                await PickAndLoadM3UAsync();
            }
        }


        private async void OnAboutClick(object sender, RoutedEventArgs e)
        {
            string version;
            try
            {
                var v = Package.Current.Id.Version;
                version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch
            {
                var asmVer = System.Reflection.Assembly.GetExecutingAssembly()?.GetName()?.Version;
                version = asmVer?.ToString() ?? "0.0.0.0";
            }

            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = "EmTV", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = $"Version {version}" });
            panel.Children.Add(new TextBlock { Text = "© Crinklebine" });

            var dlg = new ContentDialog
            {
                Title = "About",
                Content = panel,
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };

            await dlg.ShowAsync();
        }

        // ====================== Playback ======================

        private async Task PlayUrlAsync(string url)
        {
            HideErrorOverlay();
            IdleOverlay.Visibility = Visibility.Collapsed;
            LoadingOverlay.Visibility = Visibility.Visible;
            Player.AreTransportControlsEnabled = false;

            try
            {
                MediaSource? src = null;

                try
                {
                    var amsResult = await AdaptiveMediaSource.CreateFromUriAsync(new Uri(url));
                    if (amsResult.Status == AdaptiveMediaSourceCreationStatus.Success)
                    {
                        var ams = amsResult.MediaSource;
                        try { ams.DesiredLiveOffset = TimeSpan.FromSeconds(2); } catch { }
                        src = MediaSource.CreateFromAdaptiveMediaSource(ams);
                    }
                }
                catch { /* ignore and fall back */ }

                src ??= MediaSource.CreateFromUri(new Uri(url));

                _mp.Source = src;
                _mp.Play();
            }
            catch (Exception ex)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    ShowErrorOverlay($"Play failed.\n{ex.Message}");
                    Player.AreTransportControlsEnabled = false;
                    UpdateIdleOverlay();
                });
            }
        }

        private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var st = sender.PlaybackState;

                LoadingOverlay.Visibility =
                    (st == MediaPlaybackState.Opening || st == MediaPlaybackState.Buffering)
                    ? Visibility.Visible
                    : Visibility.Collapsed;

                Player.AreTransportControlsEnabled =
                    st == MediaPlaybackState.Playing || st == MediaPlaybackState.Paused;

                UpdateIdleOverlay();
            });
        }

        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                ShowErrorOverlay($"{e.Error} (0x{e.ExtendedErrorCode.HResult:X8})");
                try { sender.Pause(); } catch { }
                sender.Source = null;
                Player.Source = null;
                Player.AreTransportControlsEnabled = false;
                UpdateIdleOverlay();
            });
        }

        // ====================== Fullscreen + Accelerators ======================

        private void ToggleFullscreen()
        {
            if (_appWindow is null) return;

            try
            {
                if (!_isFull)
                    _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                else
                    _appWindow.SetPresenter(AppWindowPresenterKind.Default);

                _isFull = !_isFull;

                // Update the glyph on your Fullscreen button (named FullIcon in XAML)
                if (FullIcon is not null)
                    FullIcon.Glyph = _isFull ? "\uE73F" /* back-to-window */ : "\uE740" /* fullscreen */;
            }
            catch { }
        }

        private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

        private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ToggleFullscreen();

        // KeyboardAccelerators handler (hooked in XAML on Root)
        private void OnAccel(object sender, KeyboardAcceleratorInvokedEventArgs e)
        {
            switch (e.KeyboardAccelerator.Key)
            {
                case Windows.System.VirtualKey.Space:
                    if (_mp.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing) _mp.Pause();
                    else _mp.Play();
                    e.Handled = true; break;

                case Windows.System.VirtualKey.Up:
                    _mp.Volume = Math.Min(1.0, _mp.Volume + 0.05);
                    e.Handled = true; break;

                case Windows.System.VirtualKey.Down:
                    _mp.Volume = Math.Max(0.0, _mp.Volume - 0.05);
                    e.Handled = true; break;

                case Windows.System.VirtualKey.M:
                    _mp.IsMuted = !_mp.IsMuted;
                    e.Handled = true; break;

                case Windows.System.VirtualKey.F:
                    ToggleFullscreen();
                    e.Handled = true; break;

                case Windows.System.VirtualKey.P:
                    OnTogglePip(this, new RoutedEventArgs());
                    e.Handled = true; break;

                case Windows.System.VirtualKey.Escape:
                    if (_isFull) { ToggleFullscreen(); e.Handled = true; }
                    if (_isPip) { ExitPip(); e.Handled = true; }
                    break;
            }
        }

        // ====================== PiP ======================

        private void EnterPip()
        {
            if (_isPip) return;

            // Detach from main
            Player.SetMediaPlayer(null);

            _pipWindow = new Window();
            _pipWindow.Title = "EmTV PiP";
            var grid = new Grid { Background = new SolidColorBrush(Colors.Black) };
            grid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;

            _pipElement = new MediaPlayerElement
            {
                AreTransportControlsEnabled = false,
                Stretch = Stretch.UniformToFill
            };
            _pipElement.SetMediaPlayer(_mp);
            _pipElement.DoubleTapped += (_, __) => ExitPip();
            _pipWindow.Content = grid;
            grid.Children.Add(_pipElement);

            // Esc/P accelerators in PiP window
            var kaEsc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
            kaEsc.Invoked += (_, __) => { ExitPip(); };
            var kaP = new KeyboardAccelerator { Key = Windows.System.VirtualKey.P };
            kaP.Invoked += (_, __) => { ExitPip(); };
            grid.KeyboardAccelerators.Add(kaEsc);
            grid.KeyboardAccelerators.Add(kaP);

            _pipWindow.Activate();

            var pipHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_pipWindow);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(pipHwnd);
            _pipAppWindow = AppWindow.GetFromWindowId(id);
            if (_pipAppWindow is not null)
            {
                _pipAppWindow.Title = "EmTV PiP";
                try { _pipAppWindow.SetIcon("Assets/emtv.ico"); } catch { /* optional */ }
            }
            try { _pipAppWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay); } catch { }
            _pipAppWindow.MoveAndResize(new RectInt32(100, 100, 426, 240));

            _pipWindow.Closed += (_, __) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_isPip)
                    {
                        try { _pipElement?.SetMediaPlayer(null); } catch { }
                        Player.SetMediaPlayer(_mp);
                        _isPip = false;
                        _pipWindow = null; _pipElement = null; _pipAppWindow = null;
                    }
                });
            };

            _isPip = true;
        }

        private void ExitPip()
        {
            if (!_isPip) return;

            try { _pipElement?.SetMediaPlayer(null); } catch { }
            Player.SetMediaPlayer(_mp);
            try { _pipWindow?.Close(); } catch { }

            _pipWindow = null; _pipElement = null; _pipAppWindow = null;
            _isPip = false;
        }

        private void OnTogglePip(object sender, RoutedEventArgs e)
        {
            if (_isPip) ExitPip(); else EnterPip();
        }

        // ====================== M3U parsing ======================

        private static IEnumerable<Channel> ParseM3UFromFile(string path)
            => ParseM3UFromString(File.ReadAllText(path));

        private static IEnumerable<Channel> ParseM3UFromString(string content)
        {
            var list = new List<Channel>();
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
                    list.Add(new Channel(name, group, logo, line));
                    name = null; group = ""; logo = null;
                }
            }
            return list;
        }

        private static string? GetAttr(string s, string key)
        {
            var k = key + "=\"";
            var i = s.IndexOf(k, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            var j = s.IndexOf('"', i + k.Length);
            return j > i ? s.Substring(i + k.Length, j - (i + k.Length)) : null;
        }
    }
}
