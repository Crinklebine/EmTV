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

using Windows.ApplicationModel;               // Package.Current.Id.Version
using Windows.Graphics;                      // RectInt32
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
    /// EmTV — single-player, multi-view IPTV player (Main, Fullscreen, PiP).
    /// Key design points:
    /// - ONE long-lived MediaPlayer (_mp). Windows (Main/FS/PiP) are just “views”.
    /// - FS/PiP transitions attach _mp to a new MediaPlayerElement FIRST, then hide/minimize main.
    /// - No source recreation during mode changes; only rebuild after actual MediaFailed.
    /// - Main is the only window shown in taskbar when in Main; FS/PiP own the icon while active.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        // ===================== P/Invoke & Win32 constants =====================
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0, SW_SHOW = 5;
        private const int SW_MINIMIZE = 6, SW_RESTORE = 9;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;   // hide from taskbar/Alt-Tab
        private const int WS_EX_APPWINDOW = 0x00040000;    // force taskbar icon
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

        // ===================== Simple models =====================
        public record Channel(string Name, string Group, string? Logo, string Url);
        public record PlaylistSlot(string Emoji, string? Url);

        // ===================== Fields / app state =====================
        private MediaPlayer _mp;
        private AppWindow? _appWindow;
        private bool _initialSized;

        // Playback & overlays
        private bool _hasPlaylist;
        private string? _activePlaylistName;
        private string? _currentUrl;                 // last requested URL (for rebuild after failure)
        private bool _isReattaching;

        // Channel list & search
        private List<Channel> _allChannels = new();
        private bool _suppressSearch;

        // Window mode: Fullscreen
        private bool _isFull;
        private Window? _fsWindow;
        private MediaPlayerElement? _fsElement;
        private AppWindow? _fsAppWindow;
        private Microsoft.UI.Xaml.DispatcherTimer? _fsOverlayTimer;

        // Window mode: PiP
        private bool _isPip;
        private Window? _pipWindow;
        private MediaPlayerElement? _pipElement;
        private AppWindow? _pipAppWindow;

        // Main window geometry persistence (for reliable restore)
        private RectInt32? _savedMainBounds;

        // ======= Hard-coded playlist buttons (edit friendly URLs here) =======
        private readonly List<PlaylistSlot> _playlistSlots = new()
        {
            new("🛕", "https://raw.githubusercontent.com/akkradet/IPTV-THAI/refs/heads/master/FREETV.m3u"),
            new("💂", "https://raw.githubusercontent.com/iptv-org/iptv/refs/heads/master/streams/uk.m3u"),
            new("🍁", "https://raw.githubusercontent.com/iptv-org/iptv/refs/heads/master/streams/ca.m3u"),
            new("🗽", "https://raw.githubusercontent.com/iptv-org/iptv/refs/heads/master/streams/us.m3u"),
            new("🦘", "https://raw.githubusercontent.com/iptv-org/iptv/refs/heads/master/streams/au.m3u"),
            new("🌏", "https://iptv-org.github.io/iptv/index.m3u"),
        };

        // ===================== Construction & initialization =====================
        public MainWindow()
        {
            InitializeComponent();

            // AppWindow + caption icon
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var winId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(winId);
            try { _appWindow?.SetIcon("Assets/emtv.ico"); } catch { }
            Root.Loaded += (_, __) => SetInitialWindowSize();   // always 1200x460 at launch

            // MediaPlayer (long-lived)
            _mp = new MediaPlayer();
            _mp.MediaFailed += OnMediaFailed;
            _mp.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
            _mp.MediaOpened += (_, __) => DispatcherQueue.TryEnqueue(() =>
            {
                ClearErrorUI();
                LoadingOverlay.Visibility = Visibility.Collapsed;
                IdleOverlay.Visibility = Visibility.Collapsed;
                UpdateIdleOverlay();
            });

            // Attach player to main surface; controls hidden until playing
            Player.SetMediaPlayer(_mp);
            Player.AreTransportControlsEnabled = false;
            try
            {
                Player.TransportControls.IsSeekBarVisible = false;
                Player.TransportControls.IsSeekEnabled = false;
            }
            catch { /* older builds */ }

            // Initial UI state
            _hasPlaylist = false;
            SetActivePlaylistName(null);
            UpdateIdleOverlay();

            // Configure 6 emoji buttons
            ApplyPlaylistSlotsToButtons();
        }

        // ===================== UI helpers =====================
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

        // ===================== Search & filter =====================
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

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressSearch) return;   // ignore programmatic clears
            ApplyChannelFilter();
        }

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

        private void ClearSearchAndFilter()
        {
            if (SearchBox is null) return;
            _suppressSearch = true;
            SearchBox.Text = "";          // visual clear
            _suppressSearch = false;
            ApplyChannelFilter();         // show full list
        }

        // ===================== Playlist buttons =====================
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

            btn.Content = slot.Emoji;       // emoji glyph
            btn.Tag = slot.Url;             // backing URL (null = unconfigured)

            if (index == 0 && ToolTipService.GetToolTip(btn) is null)
                ToolTipService.SetToolTip(btn, "Thai playlist");
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

            await LoadM3UFromUriAsync(url, FriendlyNameFromUrl(url));
        }

        // ===================== Playlist load (URL/File/Dialog) =====================
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
                HideErrorOverlay();
                ClearSearchAndFilter();
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
            HideErrorOverlay();
            ClearSearchAndFilter();
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
                HideErrorOverlay();
                ClearSearchAndFilter();
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

        private async void OnAdvancedControlsClick(object sender, RoutedEventArgs e)
        {
            var box = new TextBox
            {
                PlaceholderText = "Paste an http(s) .m3u or .m3u8 URL",
                MinWidth = 420
            };

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
                if (!string.IsNullOrWhiteSpace(txt))
                    await LoadM3UFromUrlOrPathAsync(txt);
                else
                    ShowErrorOverlay("Please paste an http(s) .m3u/.m3u8 URL, or click “Open .m3u file…”.");
            }
            else if (res == ContentDialogResult.Secondary)
            {
                await PickAndLoadM3UAsync();
            }
        }

        private async void OnAboutClick(object sender, RoutedEventArgs e)
        {
            // Version
            string version;
            try { var v = Package.Current.Id.Version; version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}"; }
            catch { version = System.Reflection.Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString() ?? "0.0.0.0"; }

            // Try these assets in order
            var img = await TryLoadAboutImageAsync(new[]
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

            var dlg = new ContentDialog
            {
                Title = "About",
                Content = row,
                CloseButtonText = "OK",
                XamlRoot = this.Content.XamlRoot
            };

            await dlg.ShowAsync();
        }

        private static async Task<Image?> TryLoadAboutImageAsync(IEnumerable<string> candidateUris)
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
                catch { /* try next */ }
            }
            return null;
        }

        // ===================== Playback =====================
        private async Task PlayUrlAsync(string url)
        {
            _currentUrl = url;                 // remember intent for rebuild if needed
            ClearErrorUI();
            HideErrorOverlay();
            IdleOverlay.Visibility = Visibility.Collapsed;
            LoadingOverlay.Visibility = Visibility.Visible;
            Player.AreTransportControlsEnabled = false;

            try
            {
                MediaSource? src = null;

                // Try Adaptive (HLS/DASH) first
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
                catch { /* fall back to simple URI */ }

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
                    ? Visibility.Visible : Visibility.Collapsed;

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
                sender.Source = null;      // ensures reattach can rebuild from _currentUrl
                Player.Source = null;
                Player.AreTransportControlsEnabled = false;
                UpdateIdleOverlay();
            });
        }

        // ===================== Fullscreen & accelerators =====================
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
            if (_isPip) ExitPip(); // only one secondary window

            // Resolve target display BEFORE touching main
            var mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var mainId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(mainHwnd);
            var display = DisplayArea.GetFromWindowId(mainId, DisplayAreaFallback.Nearest);
            var target = display.OuterBounds; // full monitor

            // Build FS window (attach player FIRST → no interruption)
            _fsWindow = new Window { Title = "EmTV Fullscreen" };

            var root = new Grid
            {
                Background = new SolidColorBrush(Colors.Black),
                KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden
            };

            _fsElement = new MediaPlayerElement
            {
                AreTransportControlsEnabled = false,
                Stretch = Stretch.Uniform, // change to UniformToFill if you want crop/fill
                IsTabStop = true
            };

            _fsElement.SetMediaPlayer(_mp);                // <— attach before hiding main
            _fsElement.DoubleTapped += (_, __) => ExitFullscreen();

            // Small exit overlay (top-right)
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
            var exitBtn = new Button();
            ToolTipService.SetToolTip(exitBtn, "Exit Fullscreen (F / Esc)");
            exitBtn.Content = new FontIcon { Glyph = "\uE73F", FontFamily = new FontFamily("Segoe MDL2 Assets") };
            exitBtn.Click += (_, __) => ExitFullscreen();
            overlay.Children.Add(exitBtn);

            _fsOverlayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _fsOverlayTimer.Tick += (_, __) => overlay.Visibility = Visibility.Collapsed;
            root.PointerMoved += (_, __) =>
            {
                overlay.Visibility = Visibility.Visible;
                _fsOverlayTimer?.Stop();
                _fsOverlayTimer?.Start();
            };

            // Esc/F exit
            var kaEsc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
            kaEsc.Invoked += (_, __) => ExitFullscreen();
            var kaF = new KeyboardAccelerator { Key = Windows.System.VirtualKey.F };
            kaF.Invoked += (_, __) => ExitFullscreen();
            root.KeyboardAccelerators.Add(kaEsc);
            root.KeyboardAccelerators.Add(kaF);

            root.Children.Add(_fsElement);
            root.Children.Add(overlay);
            _fsWindow.Content = root;

            // Show FS, move to target display, go fullscreen
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

            // Hand taskbar to FS, then minimize main
            SaveMainBounds();
            SetTaskbarVisibility(_fsWindow, _fsAppWindow, true);
            SetMainShownInSwitchers(false);
            MinimizeMainWindow();

            // Focus FS video
            try { SetForegroundWindow(fsHwnd); } catch { }
            _fsWindow.Activate();
            _fsElement.Focus(FocusState.Programmatic);

            // Close path
            _fsWindow.Closed += (_, __) =>
                DispatcherQueue.TryEnqueue(() => { if (_isFull) ExitFullscreen(); });

            _isFull = true;
            if (FullIcon is not null) FullIcon.Glyph = "\uE73F"; // back-to-window glyph
        }

        private async void ExitFullscreen()
        {
            if (!_isFull) return;
            _isFull = false;

            try { _fsElement?.SetMediaPlayer(null); } catch { }
            await AttachPlayerToMainAndResumeAsync();
            ClearErrorUI();

            var win = _fsWindow;
            _fsWindow = null; _fsElement = null; _fsAppWindow = null;
            try { win?.Close(); } catch { }

            _fsOverlayTimer?.Stop();
            _fsOverlayTimer = null;

            SetMainShownInSwitchers(true);
            RestoreMainWindow();
            RestoreMainBounds();

            if (FullIcon is not null) FullIcon.Glyph = "\uE740"; // fullscreen glyph
        }

        // Keyboard accelerators (declared on Root in XAML)
        private void OnAccel(object sender, KeyboardAcceleratorInvokedEventArgs e)
        {
            // Don’t fire hotkeys while typing in a text field
            var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(this.Content.XamlRoot);
            if (focused is TextBox or AutoSuggestBox or RichEditBox) return;

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

        // ===================== Picture-in-Picture =====================
        private void OnTogglePip(object sender, RoutedEventArgs e)
            => (_isPip ? (Action)ExitPip : EnterPip)();

        private void EnterPip()
        {
            if (_isPip) return;
            if (_isFull) ExitFullscreen();

            // Anchor to main’s display BEFORE touching main
            var mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var mainId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(mainHwnd);
            var display = DisplayArea.GetFromWindowId(mainId, DisplayAreaFallback.Nearest);
            var wa = display.WorkArea;    // excludes taskbar

            // Build PiP (attach player FIRST)
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
            _pipElement.SetMediaPlayer(_mp);        // <— attach before minimizing main
            _pipElement.DoubleTapped += (_, __) => ExitPip();

            // Esc / P exit
            var kaEsc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
            kaEsc.Invoked += (_, __) => ExitPip();
            var kaP = new KeyboardAccelerator { Key = Windows.System.VirtualKey.P };
            kaP.Invoked += (_, __) => ExitPip();
            grid.KeyboardAccelerators.Add(kaEsc);
            grid.KeyboardAccelerators.Add(kaP);

            grid.Children.Add(_pipElement);
            _pipWindow.Content = grid;

            // Show PiP, move to bottom-right, set CompactOverlay
            _pipWindow.Activate();
            var pipHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_pipWindow);
            var pipId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(pipHwnd);
            _pipAppWindow = AppWindow.GetFromWindowId(pipId);
            if (_pipAppWindow is not null)
            {
                try { _pipAppWindow.SetIcon("Assets/emtv.ico"); } catch { }

                const int W = 480, H = 270, M = 12;   // true 16:9 + margin
                int x = Math.Max(wa.X + M, wa.X + wa.Width - W - M);
                int y = Math.Max(wa.Y + M, wa.Y + wa.Height - H - M);

                _pipAppWindow.MoveAndResize(new RectInt32(x, y, W, H));
                try { _pipAppWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay); } catch { }
            }

            // Taskbar ownership → PiP; then minimize main
            SaveMainBounds();
            SetTaskbarVisibility(_pipWindow, _pipAppWindow, true);
            SetMainShownInSwitchers(false);
            MinimizeMainWindow();

            try { SetForegroundWindow(pipHwnd); } catch { }
            _pipWindow.Activate();
            _pipElement.Focus(FocusState.Programmatic);

            _pipWindow.Closed += (_, __) =>
                DispatcherQueue.TryEnqueue(() => { if (_isPip) ExitPip(); });

            _isPip = true;
        }

        private async void ExitPip()
        {
            if (!_isPip) return;
            _isPip = false;

            try { _pipElement?.SetMediaPlayer(null); } catch { }
            await AttachPlayerToMainAndResumeAsync();
            ClearErrorUI();

            var win = _pipWindow;
            _pipWindow = null; _pipElement = null; _pipAppWindow = null;
            try { win?.Close(); } catch { }

            SetMainShownInSwitchers(true);
            RestoreMainWindow();
            RestoreMainBounds();
        }

        // ===================== M3U parsing =====================
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

        // ===================== Attach back to main & resume =====================
        private async Task AttachPlayerToMainAndResumeAsync()
        {
            if (_isReattaching) return;
            _isReattaching = true;
            try
            {
                Player.SetMediaPlayer(_mp);
                await Task.Delay(30); // allow visual to bind

                // If prior MediaFailed cleared the source, rebuild from last intent
                if (_mp.Source is null && !string.IsNullOrWhiteSpace(_currentUrl))
                {
                    MediaSource? src = null;
                    try
                    {
                        var amsResult = await AdaptiveMediaSource.CreateFromUriAsync(new Uri(_currentUrl));
                        if (amsResult.Status == AdaptiveMediaSourceCreationStatus.Success)
                        {
                            var ams = amsResult.MediaSource;
                            try { ams.DesiredLiveOffset = TimeSpan.FromSeconds(2); } catch { }
                            src = MediaSource.CreateFromAdaptiveMediaSource(ams);
                        }
                    }
                    catch { /* fall through */ }

                    src ??= MediaSource.CreateFromUri(new Uri(_currentUrl));
                    _mp.Source = src;
                }

                // Nudge if not already playing
                var ps = _mp.PlaybackSession;
                if (ps?.PlaybackState != MediaPlaybackState.Playing)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        _mp.Play();
                        await Task.Delay(60);
                        if (ps?.PlaybackState == MediaPlaybackState.Playing) break;
                    }
                }

                LoadingOverlay.Visibility = Visibility.Collapsed;
                ClearErrorUI();
                Player.AreTransportControlsEnabled =
                    ps?.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Paused;

                IdleOverlay.Visibility = Visibility.Collapsed;
                UpdateIdleOverlay();
            }
            finally { _isReattaching = false; }
        }

        // ===================== Taskbar / window helpers =====================
        /// <summary>Show or hide a Window in Alt-Tab/taskbar.</summary>
        private void SetTaskbarVisibility(Window w, AppWindow? aw, bool show)
        {
            // WinAppSDK (Alt-Tab / Switchers)
            try { if (aw is not null) aw.IsShownInSwitchers = show; } catch { }

            // Win32 fallback (taskbar icon)
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
                long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                if (show) { ex &= ~WS_EX_TOOLWINDOW; ex |= WS_EX_APPWINDOW; }
                else { ex &= ~WS_EX_APPWINDOW; ex |= WS_EX_TOOLWINDOW; }
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
            catch { /* ignore */ }
        }

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

        /// <summary>Hide/show MAIN in Alt-Tab/taskbar without Win32 style flips (avoids tiny caption).</summary>
        private void SetMainShownInSwitchers(bool show)
        {
            try { if (_appWindow is not null) _appWindow.IsShownInSwitchers = show; } catch { }
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

        // ===================== Startup sizing & error UI reset =====================
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

                // Keep current position, force 1200x800 (effective px)
                var pos = _appWindow.Position;
                _appWindow.MoveAndResize(new RectInt32(pos.X, pos.Y, 1200, 800));
            }
            catch
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 1200, 800, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
        }

        private void ClearErrorUI()
        {
            HideErrorOverlay();   // custom overlay

            // reset built-in transport controls banner
            if (Player is not null)
            {
                var was = Player.AreTransportControlsEnabled;
                Player.AreTransportControlsEnabled = false;
                Player.AreTransportControlsEnabled = was;
            }
        }
    }
}
