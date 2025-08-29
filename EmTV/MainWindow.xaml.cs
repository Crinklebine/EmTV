using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using Windows.ApplicationModel;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace EmTV
{
    /// <summary>
    /// EmTV — single-player, multi-view IPTV player.
    /// Design:
    ///  - Create a FRESH MediaPlayer for each new channel (failures can’t poison the next play).
    ///  - FS/PiP only reattach the SAME MediaPlayer to a different surface (no source/stop/restart).
    ///  - Overlays are simple: Welcome before first ever play, Loading while opening/buffering, Error overlay on fail.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        // ========= Win32 interop =========
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const int SW_MINIMIZE = 6, SW_RESTORE = 9;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080, WS_EX_APPWINDOW = 0x00040000;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

        // ========= Data models =========
        public record Channel(string Name, string Group, string? Logo, string Url);
        public record PlaylistSlot(string Emoji, string? Url);

        // ========= State =========
        private AppWindow? _appWindow;
        private RectInt32? _savedMainBounds;
        private bool _initialSized;

        // Playback
        private MediaPlayer _mp = default!;
        private string? _currentUrl;
        private double _lastVolume = 0.5;
        private bool _lastMuted = false;
        private bool _everPlayed;                 // after first success, never show welcome again

        // UI collections
        private List<Channel> _allChannels = new();
        private bool _suppressSearch;

        // Window modes
        private bool _isFull;
        private Window? _fsWindow;
        private MediaPlayerElement? _fsElement;
        private AppWindow? _fsAppWindow;
        private Microsoft.UI.Xaml.DispatcherTimer? _fsOverlayTimer;

        private bool _isPip;
        private Window? _pipWindow;
        private MediaPlayerElement? _pipElement;
        private AppWindow? _pipAppWindow;

        // Hard-coded playlist slots (editable)
        private readonly List<PlaylistSlot> _playlistSlots = new()
        {
            new("🛕", "https://raw.githubusercontent.com/akkradet/IPTV-THAI/refs/heads/master/FREETV.m3u"),
            new("💂", "https://raw.githubusercontent.com/iptv-org/iptv/refs/heads/master/streams/uk.m3u"),
            new("🍁", "https://raw.githubusercontent.com/iptv-org/iptv/refs/heads/master/streams/ca.m3u"),
            new("🗽", "https://raw.githubusercontent.com/iptv-org/iptv/refs/heads/master/streams/us.m3u"),
            new("🦘", "https://raw.githubusercontent.com/iptv-org/iptv/refs/heads/master/streams/au.m3u"),
            new("🌏", "https://iptv-org.github.io/iptv/index.m3u"),
        };

        // ========= Ctor / init =========
        public MainWindow()
        {
            InitializeComponent();

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(id);
            try { _appWindow?.SetIcon("Assets/emtv.ico"); } catch { }

            Root.Loaded += (_, __) => SetInitialWindowSize();   // always 1200x460

            // Create an initial player (quiet) and attach to main surface
            CreateFreshPlayer();
            Player.AreTransportControlsEnabled = false; // always off (we surface our own UI)
            try
            {
                Player.TransportControls.IsSeekBarVisible = false;
                Player.TransportControls.IsSeekEnabled = false;
            }
            catch { }

            SetActivePlaylistName(null);
            UpdateOverlays();

            ApplyPlaylistSlotsToButtons();
        }

        // ========= Overlay logic (Welcome / Loading / Error) =========
        private void UpdateOverlays()
        {
            // Error overlay is explicit
            if (ErrorOverlay.Visibility == Visibility.Visible) { IdleOverlay.Visibility = Visibility.Collapsed; LoadingOverlay.Visibility = Visibility.Collapsed; return; }

            // Loading shows if we are Opening/Buffering
            var st = _mp?.PlaybackSession?.PlaybackState;
            bool loading = st is MediaPlaybackState.Opening or MediaPlaybackState.Buffering;
            LoadingOverlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;

            // Welcome shows only before the FIRST successful play this session and only if a playlist exists but nothing is playing yet
            bool playingOrPaused = st is MediaPlaybackState.Playing or MediaPlaybackState.Paused;
            bool showWelcome = !_everPlayed && _allChannels.Count > 0 && !playingOrPaused && !loading;
            IdleOverlay.Visibility = showWelcome ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowErrorOverlay(string message)
        {
            ErrorDetail.Text = message;
            ErrorOverlay.Visibility = Visibility.Visible;
            UpdateOverlays();
        }

        private void HideErrorOverlay()
        {
            ErrorOverlay.Visibility = Visibility.Collapsed;
            ErrorDetail.Text = string.Empty;
            UpdateOverlays();
        }

        private void OnDismissError(object sender, RoutedEventArgs e) => HideErrorOverlay();

        private void ClearErrorBannerInControls()
        {
            // If you ever enable TransportControls, this forces their banner to clear
            var was = Player.AreTransportControlsEnabled;
            Player.AreTransportControlsEnabled = false;
            Player.AreTransportControlsEnabled = was;
        }

        // ========= Player lifecycle =========

        /// <summary>Create a brand-new MediaPlayer, wire events, restore volume/mute, attach to the active surface.</summary>
        private void CreateFreshPlayer()
        {
            // Snapshot user prefs from old player if any
            if (_mp is not null)
            {
                try { _lastVolume = _mp.Volume; _lastMuted = _mp.IsMuted; } catch { }
                try { _mp.Dispose(); } catch { }
            }

            _mp = new MediaPlayer
            {
                Volume = _lastVolume,
                IsMuted = _lastMuted
            };

            _mp.MediaOpened += (_, __) => DispatcherQueue.TryEnqueue(() =>
            {
                _everPlayed = true;                       // after first success, never show Welcome again
                HideErrorOverlay();
                ClearErrorBannerInControls();
                UpdateOverlays();
            });

            _mp.MediaFailed += (sender, e) => DispatcherQueue.TryEnqueue(() =>
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                ShowErrorOverlay($"{e.Error} (0x{e.ExtendedErrorCode.HResult:X8})");
                try { sender.Pause(); } catch { }
                ClearErrorBannerInControls();
                UpdateOverlays();
            });

            _mp.PlaybackSession.PlaybackStateChanged += (s, a) =>
                DispatcherQueue.TryEnqueue(UpdateOverlays);

            AttachToActiveSurface(); // attach this new player to Main/FS/PiP depending on where we are
        }

        /// <summary>Attach the current MediaPlayer to whichever view is active.</summary>
        private void AttachToActiveSurface()
        {
            if (_isFull && _fsElement is not null) _fsElement.SetMediaPlayer(_mp);
            else if (_isPip && _pipElement is not null) _pipElement.SetMediaPlayer(_mp);
            else Player.SetMediaPlayer(_mp);
        }

        // ========= Channel loading & play =========

        private async Task PlayUrlAsync(string url)
        {
            _currentUrl = url;
            HideErrorOverlay();
            ClearErrorBannerInControls();

            IdleOverlay.Visibility = Visibility.Collapsed;
            LoadingOverlay.Visibility = Visibility.Visible;

            // Fresh player per stream (prevents “poisoned” state leakage)
            CreateFreshPlayer();
            AttachToActiveSurface();

            try
            {
                MediaSource? src = null;

                // Prefer Adaptive (HLS/DASH)
                try
                {
                    var ams = await AdaptiveMediaSource.CreateFromUriAsync(new Uri(url));
                    if (ams.Status == AdaptiveMediaSourceCreationStatus.Success)
                    {
                        try { ams.MediaSource.DesiredLiveOffset = TimeSpan.FromSeconds(2); } catch { }
                        src = MediaSource.CreateFromAdaptiveMediaSource(ams.MediaSource);
                    }
                }
                catch { /* fall back below */ }

                src ??= MediaSource.CreateFromUri(new Uri(url));

                _mp.Source = src;
                _mp.Play(); // no nudging loops; this instance is fresh and clean
            }
            catch (Exception ex)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                ShowErrorOverlay($"Play failed.\n{ex.Message}");
            }
        }

        // ========= Search & filter =========
        private void ApplyChannelFilter()
        {
            var q = (SearchBox?.Text ?? string.Empty).Trim();
            IEnumerable<Channel> src = _allChannels;

            if (!string.IsNullOrEmpty(q))
            {
                src = src.Where(c =>
                    c.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                    (!string.IsNullOrEmpty(c.Group) && c.Group.Contains(q, StringComparison.CurrentCultureIgnoreCase)));
            }

            Samples.ItemsSource = src.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            Samples.SelectedIndex = -1;
            UpdateOverlays();
        }

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressSearch) return;
            ApplyChannelFilter();
        }

        private void ClearSearchAndFilter()
        {
            if (SearchBox is null) return;
            _suppressSearch = true;
            SearchBox.Text = "";
            _suppressSearch = false;
            ApplyChannelFilter();
        }

        private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter || e.Key == Windows.System.VirtualKey.Down)
            {
                if (Samples.Items.Count > 0) { Samples.SelectedIndex = 0; Samples.Focus(FocusState.Programmatic); }
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                SearchBox.Text = "";
                e.Handled = true;
            }
        }

        // ========= Playlist UI =========
        private void ApplyPlaylistSlotsToButtons()
        {
            ApplyPlaylistSlot(PlaylistBtn1, 0, "Thai playlist");
            ApplyPlaylistSlot(PlaylistBtn2, 1, "UK playlist");
            ApplyPlaylistSlot(PlaylistBtn3, 2, "Canada playlist");
            ApplyPlaylistSlot(PlaylistBtn4, 3, "USA playlist");
            ApplyPlaylistSlot(PlaylistBtn5, 4, "Australia playlist");
            ApplyPlaylistSlot(PlaylistBtn6, 5, "Global playlist");
        }

        private void ApplyPlaylistSlot(Button btn, int index, string tooltip)
        {
            if (index < 0 || index >= _playlistSlots.Count) return;
            var slot = _playlistSlots[index];
            btn.Content = slot.Emoji;
            btn.Tag = slot.Url;
            ToolTipService.SetToolTip(btn, tooltip);
        }

        private async void OnPlaylistButtonClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b) return;
            var url = b.Tag as string;
            if (string.IsNullOrWhiteSpace(url))
            {
                ShowErrorOverlay("This playlist button isn’t configured yet.\nUse Advanced Controls to load a list.");
                return;
            }
            await LoadM3UFromUriAsync(url, FriendlyNameFromUrl(url));
        }

        private void OnSampleClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Channel ch)
                _ = PlayUrlAsync(ch.Url);
        }

        // ========= Playlist loading =========
        private async Task LoadM3UFromUriAsync(string url, string? friendlyName = null)
        {
            try
            {
                var http = new HttpClient(new HttpBaseProtocolFilter());
                http.DefaultRequestHeaders.UserAgent.TryParseAdd("EmTV/1.0");
                var text = await http.GetStringAsync(new Uri(url));

                _allChannels = ParseM3UFromString(text).ToList();
                SetActivePlaylistName(friendlyName ?? FriendlyNameFromUrl(url));

                ClearSearchAndFilter();
                HideErrorOverlay();
                UpdateOverlays();
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

            _allChannels = ParseM3UFromFile(file.Path).ToList();
            SetActivePlaylistName(file.DisplayName);

            ClearSearchAndFilter();
            HideErrorOverlay();
            UpdateOverlays();
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
                _allChannels = ParseM3UFromFile(input).ToList();
                SetActivePlaylistName(Path.GetFileNameWithoutExtension(input));

                ClearSearchAndFilter();
                HideErrorOverlay();
                UpdateOverlays();
                return;
            }

            ShowErrorOverlay("Not a valid URL or path to a .m3u/.m3u8 file.");
        }

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

        private static string FriendlyNameFromUrl(string url)
        {
            try
            {
                var u = new Uri(url);
                var last = u.Segments.LastOrDefault()?.Trim('/') ?? "";
                if (last.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase) || last.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                    return Path.GetFileNameWithoutExtension(last);
                return u.Host;
            }
            catch { return "Playlist"; }
        }

        private void SetActivePlaylistName(string? name)
        {
            var clean = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            ChannelHeader.Text = clean is null ? "Channels" : $"Channels: {clean}";
        }

        // ========= Advanced Controls (dialog) =========
        private async void OnAdvancedControlsClick(object sender, RoutedEventArgs e)
        {
            var box = new TextBox { PlaceholderText = "Paste an http(s) .m3u or .m3u8 URL", MinWidth = 420 };

            var dlg = new ContentDialog
            {
                Title = "Advanced Controls",
                Content = box,
                PrimaryButtonText = "Load URL",
                SecondaryButtonText = "Open .m3u file…",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.Content.XamlRoot
            };

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
            if (res == ContentDialogResult.Primary)
            {
                var txt = box.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(txt)) await LoadM3UFromUrlOrPathAsync(txt);
            }
            else if (res == ContentDialogResult.Secondary)
            {
                await PickAndLoadM3UAsync();
            }
        }

        private async void OnAboutClick(object sender, RoutedEventArgs e)
        {
            string version;
            try
            {
                var v = Package.Current.Id.Version;               // MSIX manifest version
                version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch
            {
                // Unpackaged fallback (when running the app project directly)
                version = System.Reflection.Assembly
                            .GetExecutingAssembly()
                            .GetName()
                            .Version?.ToString() ?? "1.0.0.0";
            }

            var img = await TryLoadImageAsync(new[]
            {
                "ms-appx:///Assets/emtv-icon-1024.png",
                "ms-appx:///Assets/StoreLogo.png",
                "ms-appx:///Assets/EMTV-Load.png"
            });

            var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = "EmTV", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = $"Version {version}" });
            text.Children.Add(new TextBlock { Text = "© Crinklebine" });

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            if (img is not null) row.Children.Add(img);
            row.Children.Add(text);

            var dlg = new ContentDialog { Title = "About", Content = row, CloseButtonText = "OK", XamlRoot = this.Content.XamlRoot };
            await dlg.ShowAsync();
        }

        private static async Task<Image?> TryLoadImageAsync(IEnumerable<string> candidateUris)
        {
            foreach (var u in candidateUris)
            {
                try
                {
                    StorageFile file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(u));
                    using IRandomAccessStream s = await file.OpenAsync(FileAccessMode.Read);
                    var bmp = new BitmapImage();
                    await bmp.SetSourceAsync(s);
                    return new Image { Source = bmp, Width = 64, Height = 64, Margin = new Thickness(0, 0, 12, 0) };
                }
                catch { }
            }
            return null;
        }

        // ========= Fullscreen =========
        private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();
        private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ToggleFullscreen();

        private void ToggleFullscreen()
        {
            if (_isFull) ExitFullscreen();
            else EnterFullscreen();
        }

        private void EnterFullscreen()
        {
            if (_isFull) return;
            if (_isPip) ExitPip(); // one secondary at a time

            // Anchor monitor BEFORE touching main
            var mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var mainId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(mainHwnd);
            var display = DisplayArea.GetFromWindowId(mainId, DisplayAreaFallback.Nearest);
            var target = display.OuterBounds;

            _fsWindow = new Window { Title = "EmTV Fullscreen" };
            var root = new Grid
            {
                Background = new SolidColorBrush(Colors.Black),
                KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden
            };

            _fsElement = new MediaPlayerElement
            {
                AreTransportControlsEnabled = false,
                Stretch = Stretch.Uniform, // change to UniformToFill if you prefer fill/crop
                IsTabStop = true
            };
            _fsElement.SetMediaPlayer(_mp);            // attach FIRST for seamless video
            _fsElement.DoubleTapped += (_, __) => ExitFullscreen();

            // Minimal exit overlay
            var overlay = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x80, 0, 0, 0)),
                Padding = new Thickness(6),
                CornerRadius = new CornerRadius(8),
                Spacing = 6,
                Visibility = Visibility.Visible
            };
            var exitBtn = new Button { Content = new FontIcon { Glyph = "\uE73F", FontFamily = new FontFamily("Segoe MDL2 Assets") } };
            ToolTipService.SetToolTip(exitBtn, "Exit Fullscreen (F / Esc)");
            exitBtn.Click += (_, __) => ExitFullscreen();
            overlay.Children.Add(exitBtn);

            _fsOverlayTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _fsOverlayTimer.Tick += (_, __) => overlay.Visibility = Visibility.Collapsed;
            root.PointerMoved += (_, __) => { overlay.Visibility = Visibility.Visible; _fsOverlayTimer?.Stop(); _fsOverlayTimer?.Start(); };

            var kaEsc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape }; kaEsc.Invoked += (_, __) => ExitFullscreen();
            var kaF = new KeyboardAccelerator { Key = Windows.System.VirtualKey.F }; kaF.Invoked += (_, __) => ExitFullscreen();
            root.KeyboardAccelerators.Add(kaEsc); root.KeyboardAccelerators.Add(kaF);

            root.Children.Add(_fsElement);
            root.Children.Add(overlay);
            _fsWindow.Content = root;

            _fsWindow.Activate();
            var fsHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_fsWindow);
            var fsId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(fsHwnd);
            _fsAppWindow = AppWindow.GetFromWindowId(fsId);
            if (_fsAppWindow is not null)
            {
                try { _fsAppWindow.SetIcon("Assets/emtv.ico"); } catch { }
                _fsAppWindow.MoveAndResize(target);
                try { _fsAppWindow.SetPresenter(AppWindowPresenterKind.FullScreen); } catch { }
            }

            // Taskbar ownership → FS
            SaveMainBounds();
            SetTaskbarVisibility(_fsWindow, _fsAppWindow, true);
            SetMainShownInSwitchers(false);
            MinimizeMainWindow();

            try { SetForegroundWindow(fsHwnd); } catch { }
            _fsWindow.Activate();
            _fsElement.Focus(FocusState.Programmatic);

            _fsWindow.Closed += (_, __) => DispatcherQueue.TryEnqueue(() => { if (_isFull) ExitFullscreen(); });

            _isFull = true;
            if (FullIcon is not null) FullIcon.Glyph = "\uE73F";
        }

        private void ExitFullscreen()
        {
            if (!_isFull) return;
            _isFull = false;

            // Reattach to main FIRST (no restart)
            Player.SetMediaPlayer(_mp);

            var win = _fsWindow;
            _fsWindow = null; _fsElement = null; _fsAppWindow = null;
            try { win?.Close(); } catch { }

            _fsOverlayTimer?.Stop(); _fsOverlayTimer = null;

            SetMainShownInSwitchers(true);
            RestoreMainWindow();
            RestoreMainBounds();

            if (FullIcon is not null) FullIcon.Glyph = "\uE740";
            ClearErrorBannerInControls();
            UpdateOverlays();
        }

        // ========= PiP =========
        private void OnTogglePip(object sender, RoutedEventArgs e)
            => (_isPip ? (Action)ExitPip : EnterPip)();

        private void EnterPip()
        {
            if (_isPip) return;
            if (_isFull) ExitFullscreen();

            // Anchor to main’s monitor
            var mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var mainId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(mainHwnd);
            var display = DisplayArea.GetFromWindowId(mainId, DisplayAreaFallback.Nearest);
            var wa = display.WorkArea;

            _pipWindow = new Window { Title = "EmTV PiP" };
            var grid = new Grid
            {
                Background = new SolidColorBrush(Colors.Black),
                KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden
            };

            _pipElement = new MediaPlayerElement
            {
                AreTransportControlsEnabled = false,
                Stretch = Stretch.Uniform,
                IsTabStop = true
            };
            _pipElement.SetMediaPlayer(_mp);     // attach FIRST
            _pipElement.DoubleTapped += (_, __) => ExitPip();

            var kaEsc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape }; kaEsc.Invoked += (_, __) => ExitPip();
            var kaP = new KeyboardAccelerator { Key = Windows.System.VirtualKey.P }; kaP.Invoked += (_, __) => ExitPip();
            grid.KeyboardAccelerators.Add(kaEsc); grid.KeyboardAccelerators.Add(kaP);

            grid.Children.Add(_pipElement);
            _pipWindow.Content = grid;

            _pipWindow.Activate();
            var pipHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_pipWindow);
            var pipId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(pipHwnd);
            _pipAppWindow = AppWindow.GetFromWindowId(pipId);
            if (_pipAppWindow is not null)
            {
                try { _pipAppWindow.SetIcon("Assets/emtv.ico"); } catch { }

                const int W = 480, H = 270, M = 12; // 16:9 bottom-right
                int x = Math.Max(wa.X + M, wa.X + wa.Width - W - M);
                int y = Math.Max(wa.Y + M, wa.Y + wa.Height - H - M);

                _pipAppWindow.MoveAndResize(new RectInt32(x, y, W, H));
                try { _pipAppWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay); } catch { }
            }

            SaveMainBounds();
            SetTaskbarVisibility(_pipWindow, _pipAppWindow, true);
            SetMainShownInSwitchers(false);
            MinimizeMainWindow();

            try { SetForegroundWindow(pipHwnd); } catch { }
            _pipWindow.Activate();
            _pipElement.Focus(FocusState.Programmatic);

            _pipWindow.Closed += (_, __) => DispatcherQueue.TryEnqueue(() => { if (_isPip) ExitPip(); });

            _isPip = true;
        }

        private void ExitPip()
        {
            if (!_isPip) return;
            _isPip = false;

            // Reattach to main FIRST (no restart)
            Player.SetMediaPlayer(_mp);

            var win = _pipWindow;
            _pipWindow = null; _pipElement = null; _pipAppWindow = null;
            try { win?.Close(); } catch { }

            SetMainShownInSwitchers(true);
            RestoreMainWindow();
            RestoreMainBounds();

            ClearErrorBannerInControls();
            UpdateOverlays();
        }

        // ========= Keyboard accelerators (declared on Root in XAML) =========
        private void OnAccel(object sender, KeyboardAcceleratorInvokedEventArgs e)
        {
            // Don’t fire hotkeys while typing
            var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(this.Content.XamlRoot);
            if (focused is TextBox or AutoSuggestBox or RichEditBox) return;

            switch (e.KeyboardAccelerator.Key)
            {
                case Windows.System.VirtualKey.Space:
                    var ps = _mp.PlaybackSession;
                    if (ps?.PlaybackState == MediaPlaybackState.Playing) _mp.Pause();
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
                    if (_isFull) { ExitFullscreen(); e.Handled = true; }
                    else if (_isPip) { ExitPip(); e.Handled = true; }
                    break;
            }
        }

        // ========= Taskbar / window helpers =========
        private void SaveMainBounds()
        {
            if (_appWindow is null) return;
            _savedMainBounds = new RectInt32(_appWindow.Position.X, _appWindow.Position.Y, _appWindow.Size.Width, _appWindow.Size.Height);
        }

        private void RestoreMainBounds()
        {
            if (_appWindow is null || _savedMainBounds is null) return;
            _appWindow.MoveAndResize(_savedMainBounds.Value);
            _savedMainBounds = null;
        }

        private void MinimizeMainWindow()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SW_MINIMIZE);
        }

        private void RestoreMainWindow()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            ShowWindow(hwnd, SW_RESTORE);
            this.Activate();
            try { SetForegroundWindow(hwnd); } catch { }
        }

        private void SetMainShownInSwitchers(bool show)
        {
            try { if (_appWindow is not null) _appWindow.IsShownInSwitchers = show; } catch { }
        }

        private void SetTaskbarVisibility(Window w, AppWindow? aw, bool show)
        {
            // WinAppSDK switchers
            try { if (aw is not null) aw.IsShownInSwitchers = show; } catch { }

            // Win32 taskbar
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
                long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                if (show) { ex &= ~WS_EX_TOOLWINDOW; ex |= WS_EX_APPWINDOW; }
                else { ex &= ~WS_EX_APPWINDOW; ex |= WS_EX_TOOLWINDOW; }
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
            catch { }
        }

        private void SetInitialWindowSize()
        {
            if (_initialSized) return;
            _initialSized = true;

            try
            {
                if (_appWindow is null)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                    _appWindow = AppWindow.GetFromWindowId(id);
                }
                var pos = _appWindow.Position;
                _appWindow.MoveAndResize(new RectInt32(pos.X, pos.Y, 1200, 800));
            }
            catch
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 1200, 800, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
        }
    }
}
