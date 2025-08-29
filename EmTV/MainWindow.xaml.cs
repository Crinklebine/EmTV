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
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media;

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

    public sealed partial class MainWindow : Window
    {
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0, SW_SHOW = 5;
        private const int SW_MINIMIZE = 6;
        private const int SW_RESTORE = 9; // restore from minimized
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;   // hide from taskbar/Alt-Tab
        private const int WS_EX_APPWINDOW = 0x00040000;   // forces taskbar icon
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;

        // --- Simple models ---
        public record Channel(string Name, string Group, string? Logo, string Url);
        public record PlaylistSlot(string Emoji, string? Url);

        // --- Fields/state ---
        private MediaPlayer _mp;
        private AppWindow? _appWindow;
        private bool _isFull;
        private Window? _fsWindow;
        private MediaPlayerElement? _fsElement;
        private AppWindow? _fsAppWindow;
        private Microsoft.UI.Xaml.DispatcherTimer? _fsOverlayTimer;
        private bool _hasPlaylist;
        private string? _activePlaylistName;
        private bool _isReattaching;
        private Windows.Graphics.RectInt32? _savedMainBounds;
        private List<Channel> _allChannels = new();   // master list for search/filter
        private bool _suppressSearch;
        private bool _initialSized;

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
            Root.Loaded += (_, __) => SetInitialWindowSize();

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

        private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressSearch) return;     // don't react when we clear programmatically
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
            // Version text
            string version;
            try
            {
                var v = Windows.ApplicationModel.Package.Current.Id.Version;
                version = $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
            }
            catch
            {
                var asmVer = System.Reflection.Assembly.GetExecutingAssembly()?.GetName()?.Version;
                version = asmVer?.ToString() ?? "0.0.0.0";
            }

            // Try your assets in order: emtv-icon-1024 → StoreLogo → EMTV-Load
            var img = await TryLoadAboutImageAsync(new[]
            {
                "ms-appx:///Assets/emtv-icon-1024.png",
                "ms-appx:///Assets/StoreLogo.png",
                "ms-appx:///Assets/EMTV-Load.png"
            });

            // Text
            var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = "EmTV", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = $"Version {version}" });
            text.Children.Add(new TextBlock { Text = "© Crinklebine" });

            // Layout: image (if any) + text
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
                    return new Image
                    {
                        Source = bmp,
                        Width = 64,
                        Height = 64,
                        Margin = new Thickness(0, 0, 12, 0)
                    };
                }
                catch { /* try next */ }
            }
            return null;
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
            if (_isFull) ExitFullscreen();
            else EnterFullscreen();
        }

        private void EnterFullscreen()
        {
            if (_isFull) return;
            if (_isPip) ExitPip(); // ensure only one secondary window

            // 1) Resolve target display BEFORE touching the main window
            var mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var mainId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(mainHwnd);
            var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                               mainId, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            var target = display.OuterBounds; // full-monitor bounds

            // 2) Detach from main surface
            Player.SetMediaPlayer(null);

            // 3) Build fullscreen window (video + tiny exit overlay)
            _fsWindow = new Window();
            _fsWindow.Title = "EmTV Fullscreen";

            var root = new Grid
            {
                Background = new SolidColorBrush(Colors.Black),
                KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden
            };

            _fsElement = new MediaPlayerElement
            {
                AreTransportControlsEnabled = false, // video-only
                Stretch = Stretch.Uniform,
                IsTabStop = true
            };
            _fsElement.SetMediaPlayer(_mp);
            _fsElement.DoubleTapped += (_, __) => ExitFullscreen();

            // Exit FS overlay (top-right)
            var overlay = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x80, 0x00, 0x00, 0x00)),
                Padding = new Thickness(6),
                CornerRadius = new CornerRadius(8),
                Spacing = 6
            };
            var exitBtn = new Button();
            ToolTipService.SetToolTip(exitBtn, "Exit Fullscreen (F / Esc)");
            exitBtn.Content = new FontIcon { Glyph = "\uE73F", FontFamily = new FontFamily("Segoe MDL2 Assets") };
            exitBtn.Click += (_, __) => ExitFullscreen();
            overlay.Children.Add(exitBtn);

            overlay.Visibility = Visibility.Visible;
            _fsOverlayTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _fsOverlayTimer.Tick += (_, __) => overlay.Visibility = Visibility.Collapsed;
            root.PointerMoved += (_, __) =>
            {
                overlay.Visibility = Visibility.Visible;
                _fsOverlayTimer?.Stop();
                _fsOverlayTimer?.Start();
            };

            // Keyboard shortcuts: Esc/F exit
            var kaEsc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
            kaEsc.Invoked += (_, __) => ExitFullscreen();
            var kaF = new KeyboardAccelerator { Key = Windows.System.VirtualKey.F };
            kaF.Invoked += (_, __) => ExitFullscreen();
            root.KeyboardAccelerators.Add(kaEsc);
            root.KeyboardAccelerators.Add(kaF);

            root.Children.Add(_fsElement);
            root.Children.Add(overlay);
            _fsWindow.Content = root;

            // 4) Show FS window so we can get its AppWindow, then place it on the anchor display
            _fsWindow.Activate();
            var fsHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_fsWindow);
            var fsId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(fsHwnd);
            _fsAppWindow = AppWindow.GetFromWindowId(fsId);
            if (_fsAppWindow is not null)
            {
                try { _fsAppWindow.SetIcon("Assets/emtv.ico"); } catch { }
                _fsAppWindow.MoveAndResize(target); // put FS on the same monitor
                try { _fsAppWindow.SetPresenter(AppWindowPresenterKind.FullScreen); } catch { }
            }

            // 5) Hand taskbar/Alt-Tab ownership to FS, then minimize main (no Win32 style flips on main)
            SaveMainBounds();                                   // snapshot main’s normal rect
            SetTaskbarVisibility(_fsWindow, _fsAppWindow, true); // FS visible in taskbar/switchers
            SetMainShownInSwitchers(false);                      // main hidden from taskbar/switchers
            MinimizeMainWindow();                                // Win32 fallback: SW_MINIMIZE

            // 6) Foreground FS & focus the video so Esc/F work immediately
            try { SetForegroundWindow(fsHwnd); } catch { }
            _fsWindow.Activate();
            _fsElement.Focus(FocusState.Programmatic);

            // 7) If user closes via X/Alt+F4, route through our exit path
            _fsWindow.Closed += (_, __) =>
            {
                DispatcherQueue.TryEnqueue(() => { if (_isFull) ExitFullscreen(); });
            };

            _isFull = true;
            if (FullIcon is not null) FullIcon.Glyph = "\uE73F"; // back-to-window glyph
        }


        private async void ExitFullscreen()
        {
            if (!_isFull) return;
            _isFull = false; // prevent Closed handler double-run

            // Detach FS surface, reattach to main, and nudge playback if needed
            try { _fsElement?.SetMediaPlayer(null); } catch { }
            await AttachPlayerToMainAndResumeAsync();

            // Tear down FS window
            var win = _fsWindow;
            _fsWindow = null; _fsElement = null; _fsAppWindow = null;
            try { win?.Close(); } catch { /* ignore */ }

            _fsOverlayTimer?.Stop();
            _fsOverlayTimer = null;

            // Bring main back as the ONLY taskbar/Alt-Tab entry
            SetMainShownInSwitchers(true);  // make main visible in switchers/taskbar first
            RestoreMainWindow();            // unminimize + Activate + SetForegroundWindow (in helper)
            RestoreMainBounds();            // put back saved rect from EnterFullscreen()

            if (FullIcon is not null) FullIcon.Glyph = "\uE740"; // restore glyph
        }



        private void OnToggleFullscreen(object sender, RoutedEventArgs e) => ToggleFullscreen();

        private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => ToggleFullscreen();

        // KeyboardAccelerators handler (hooked in XAML on Root)
        private void OnAccel(object sender, KeyboardAcceleratorInvokedEventArgs e)
        {
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

        // ====================== PiP ======================

        private void EnterPip()
        {
            if (_isPip) return;
            if (_isFull) ExitFullscreen(); // ensure only one secondary window

            // 1) Resolve target display BEFORE touching the main window
            var mainHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var mainId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(mainHwnd);
            var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                               mainId, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            var wa = display.WorkArea; // work area (excludes taskbar)

            // 2) Detach from main surface
            Player.SetMediaPlayer(null);

            // 3) Build minimal PiP window (video only)
            _pipWindow = new Window();
            _pipWindow.Title = "EmTV PiP";

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
            _pipElement.SetMediaPlayer(_mp);
            _pipElement.DoubleTapped += (_, __) => ExitPip();

            grid.Children.Add(_pipElement);
            _pipWindow.Content = grid;

            // Esc / P exit
            var kaEsc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
            kaEsc.Invoked += (_, __) => ExitPip();
            var kaP = new KeyboardAccelerator { Key = Windows.System.VirtualKey.P };
            kaP.Invoked += (_, __) => ExitPip();
            grid.KeyboardAccelerators.Add(kaEsc);
            grid.KeyboardAccelerators.Add(kaP);

            // 4) Show PiP so we can get its AppWindow, then place it on the anchor display
            _pipWindow.Activate();
            var pipHwnd = WinRT.Interop.WindowNative.GetWindowHandle(_pipWindow);
            var pipId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(pipHwnd);
            _pipAppWindow = AppWindow.GetFromWindowId(pipId);
            if (_pipAppWindow is not null)
            {
                try { _pipAppWindow.SetIcon("Assets/emtv.ico"); } catch { }

                // Bottom-right placement in the work area (true 16:9)
                const int W = 480, H = 270, M = 12;
                int x = Math.Max(wa.X + M, wa.X + wa.Width - W - M);
                int y = Math.Max(wa.Y + M, wa.Y + wa.Height - H - M);

                _pipAppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, W, H));
                try { _pipAppWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay); } catch { }
            }

            // 5) Hand taskbar/Alt-Tab ownership to PiP, then minimize main
            SaveMainBounds();
            SetTaskbarVisibility(_pipWindow, _pipAppWindow, true); // PiP visible in switchers
            SetMainShownInSwitchers(false);                        // main hidden (no style flips)
            MinimizeMainWindow();                                  // Win32 fallback: SW_MINIMIZE

            // 6) Foreground PiP & focus video so Esc/P work immediately
            try { SetForegroundWindow(pipHwnd); } catch { }
            _pipWindow.Activate();
            _pipElement.Focus(FocusState.Programmatic);

            // 7) If user closes via X/Alt+F4, route through our exit path
            _pipWindow.Closed += (_, __) =>
            {
                DispatcherQueue.TryEnqueue(() => { if (_isPip) ExitPip(); });
            };

            _isPip = true;
        }

        private async void ExitPip()
        {
            if (!_isPip) return;
            _isPip = false; // prevent Closed handler double-run

            // Detach from PiP surface and reattach+resume on main
            try { _pipElement?.SetMediaPlayer(null); } catch { }
            await AttachPlayerToMainAndResumeAsync();

            // Tear down PiP window
            var win = _pipWindow;
            _pipWindow = null; _pipElement = null; _pipAppWindow = null;
            try { win?.Close(); } catch { /* ignore */ }

            // Bring main back as the only taskbar/Alt-Tab entry
            RestoreMainWindow();            // unminimize / show (SW_RESTORE + Activate + SetForegroundWindow)
            RestoreMainBounds();            // put back saved rect from EnterPip()
            SetMainShownInSwitchers(true);  // main visible in switchers/taskbar again
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

        // inside MainWindow

        private async Task AttachPlayerToMainAndResumeAsync()
        {
            if (_isReattaching) return;
            _isReattaching = true;
            try
            {
                // Re-attach to the main surface
                Player.SetMediaPlayer(_mp);

                // Let XAML bind the surface
                await Task.Delay(30);

                // If we landed in Paused/None during the handoff, nudge playback
                var ps = _mp.PlaybackSession;
                for (int i = 0; i < 3; i++)
                {
                    var st = ps?.PlaybackState ?? MediaPlaybackState.None;
                    if (st == MediaPlaybackState.Playing) break;
                    _mp.Play();
                    await Task.Delay(60);
                }

                // Clean UI so main window doesn't look idle
                LoadingOverlay.Visibility = Visibility.Collapsed;
                HideErrorOverlay();
                Player.AreTransportControlsEnabled =
                    ps?.PlaybackState is MediaPlaybackState.Playing or MediaPlaybackState.Paused;

                // If you want to suppress a brief flicker, you can force-hide here:
                IdleOverlay.Visibility = Visibility.Collapsed;
                UpdateIdleOverlay();
            }
            finally { _isReattaching = false; }
        }

        /// <summary>Show or hide a Window in taskbar/Alt-Tab.</summary>
        private void SetTaskbarVisibility(Window w, AppWindow? aw, bool show)
        {
            // WinAppSDK API (Alt-Tab / Switchers)
            try { if (aw is not null) aw.IsShownInSwitchers = show; } catch { }

            // Win32 fallback (taskbar icon)
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
                long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                if (show)
                {
                    ex &= ~WS_EX_TOOLWINDOW;
                    ex |= WS_EX_APPWINDOW;
                }
                else
                {
                    ex &= ~WS_EX_APPWINDOW;
                    ex |= WS_EX_TOOLWINDOW;
                }
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
            catch { /* ignore */ }
        }

        // Force taskbar to re-evaluate this window’s icon
        private void RefreshTaskbarIcon(Window w)
        {
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
                ShowWindow(hwnd, SW_HIDE);   // bounce visibility
                ShowWindow(hwnd, SW_SHOW);
            }
            catch { /* ignore */ }
        }

        private void SaveMainBounds()
        {
            if (_appWindow is null) return;
            _savedMainBounds = new Windows.Graphics.RectInt32(
                _appWindow.Position.X, _appWindow.Position.Y, _appWindow.Size.Width, _appWindow.Size.Height);
        }

        private void RestoreMainBounds()
        {
            if (_appWindow is null || _savedMainBounds is null) return;
            _appWindow.MoveAndResize(_savedMainBounds.Value);
            _savedMainBounds = null;
        }

        // Show/hide MAIN window in Alt+Tab/taskbar WITHOUT Win32 style flips (prevents the tiny caption)
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
            try { SetForegroundWindow(hwnd); } catch { /* best effort */ }
        }

        private void ClearSearchAndFilter()
        {
            if (SearchBox is null) return;
            _suppressSearch = true;
            SearchBox.Text = "";      // clears the box visually
            _suppressSearch = false;
            ApplyChannelFilter();     // shows the full list
        }

        private void SetInitialWindowSize()
        {
            if (_initialSized) return;
            _initialSized = true;

            try
            {
                // Ensure _appWindow exists
                if (_appWindow is null)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
                    _appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
                }

                // Keep current position, force 1200x800
                var pos = _appWindow.Position;
                _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(pos.X, pos.Y, 1200, 800));
            }
            catch
            {
                // Fallback if AppWindow API isn’t available
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                const uint SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 1200, 460, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
        }
    }
}
