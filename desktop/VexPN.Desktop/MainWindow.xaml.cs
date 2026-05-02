using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing2D = System.Drawing.Drawing2D;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;

namespace VexPN.Desktop;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _connectionWatch = new();
    private readonly DispatcherTimer _notificationTimer = new() { Interval = TimeSpan.FromSeconds(2.3) };
    private readonly DispatcherTimer _addKeySpinnerTimer = new() { Interval = TimeSpan.FromMilliseconds(42) };
    private readonly HttpClient _http = new();
    private readonly MihomoVpnService _vpn;
    private bool _vpnBusy;
    private Forms.NotifyIcon? _trayIcon;
    private BitmapImage? _connectedImage;
    private BitmapImage? _disconnectedImage;
    private readonly SolidColorBrush _statusBrush = new(MediaColor.FromRgb(160, 160, 160));
    private bool _lastConnectionState;
    private Guid? _activeKeyId;
    private readonly HashSet<Guid> _markedForDelete = [];
    private bool _allowClose;
    private bool _editMode;
    private bool _isResolvingKey;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _trayToggleVpnItem;
    private readonly List<BitmapSource> _addKeySpinnerFrames = [];
    private int _addKeySpinnerFrameIndex;

    private readonly string _storageDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VexPN");
    private string KeysStoragePath => Path.Combine(_storageDir, "keys.json");
    private const string BackendBaseUrl = "https://vex-gram.ru";

    public ObservableCollection<ProfileKeyVm> Keys { get; } = [];

    public ObservableCollection<SelectedVpnAppVm> VpnApps { get; } = [];

    public ObservableCollection<InstalledAppEntry> PickAppEntries { get; } = [];

    private List<InstalledAppEntry> _pickAppsAll = [];

    private InstalledAppEntry? _pendingVpnPick;

    private CancellationTokenSource? _pickScanCts;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _vpn = new MihomoVpnService(_storageDir);

        KeysList.ItemsSource = Keys;
        Keys.CollectionChanged += Keys_OnCollectionChanged;
        VpnApps.CollectionChanged += (_, _) =>
        {
            UpdateVpnAppsChrome();
            UpdateConnectionVisual();
            SaveKeys();
        };
        _notificationTimer.Tick += (_, _) =>
        {
            _notificationTimer.Stop();
            NotificationBanner.Visibility = Visibility.Collapsed;
        };
        _addKeySpinnerTimer.Tick += (_, _) => AdvanceAddKeySpinnerFrame();

        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
        };

        Loaded += async (_, _) =>
        {
            ApplyTitleBarBranding();
            LoadConnectionImages();
            LoadAddKeySpinnerFrames();
            LoadStoredKeys();
            ConnectionStatusText.Foreground = _statusBrush;
            ApplyRoundedClip();

            UpdateConnectionVisual();
            UpdateDeleteButtonState();
            UpdateKeysFadeOverlays();
            UpdateVpnAppsChrome();
            EnsureTrayIcon();

            try
            {
                await RefreshActiveKeyFromBackendAsync().ConfigureAwait(true);
            }
            catch
            {
                // ignore network/backend errors on startup
            }
        };
        SizeChanged += (_, _) => ApplyRoundedClip();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            if (!_vpn.IsRunning)
            {
                if (_connectionWatch.IsRunning)
                {
                    _connectionWatch.Reset();
                    _timer.Stop();
                    TimerText.Text = "00:00:00";
                    UpdateConnectionVisual();
                }

                return;
            }

            if (!_connectionWatch.IsRunning)
                return;
            var elapsed = _connectionWatch.Elapsed;
            TimerText.Text =
                $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        };
    }

    private void ApplyTitleBarBranding()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "IMG_8971.PNG");
            if (!File.Exists(path))
                return;

            var logo = LoadBitmap(path, decodeWidth: 44);
            if (logo is not null)
                TitleBarLogo.Source = logo;

            Icon = BitmapFrame.Create(
                new Uri(path),
                BitmapCreateOptions.IgnoreImageCache,
                BitmapCacheOption.OnLoad);
        }
        catch
        {
            // без лого окно всё равно работает
        }
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            return;

        var src = e.OriginalSource as DependencyObject;
        while (src is not null)
        {
            if (src is System.Windows.Controls.Button)
                return;
            src = VisualTreeHelper.GetParent(src);
        }

        try
        {
            DragMove();
        }
        catch
        {
            // игнорируем сбой перетаскивания
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        ShowCloseConfirmPanel();

    private void MinimizeToTrayButton_Click(object sender, RoutedEventArgs e) =>
        MinimizeToTray();

    private void LoadConnectionImages()
    {
        try
        {
            var dir = AppContext.BaseDirectory;
            var onPath = Path.Combine(dir, "Assets", "IMG_8970.PNG");
            var offPath = Path.Combine(dir, "Assets", "IMG_8971.PNG");

            _connectedImage = LoadBitmap(onPath);
            _disconnectedImage = LoadBitmap(offPath);

            ConnectionImage.Source = _disconnectedImage ?? _connectedImage;
        }
        catch
        {
            // без файлов изображение остаётся пустым
        }
    }

    private static BitmapImage? LoadBitmap(string path, int? decodeWidth = null)
    {
        if (!File.Exists(path))
            return null;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        if (decodeWidth is > 0)
            bmp.DecodePixelWidth = decodeWidth.Value;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void ApplyRoundedClip()
    {
        if (MainChromeBorder.ActualWidth <= 0 || MainChromeBorder.ActualHeight <= 0)
            return;

        MainChromeBorder.Clip = new RectangleGeometry(
            new Rect(0, 0, MainChromeBorder.ActualWidth, MainChromeBorder.ActualHeight),
            16,
            16);
    }

    private void SetActiveKey(Guid id)
    {
        _activeKeyId = id;
        foreach (var k in Keys)
            k.SetActive(k.Id == id);

        var match = Keys.FirstOrDefault(k => k.Id == id);
        if (KeysList.SelectedItem != match)
            KeysList.SelectedItem = match;
        SaveKeys();
        UpdateConnectionVisual();
    }

    private ProfileKeyVm? ActiveKey =>
        _activeKeyId is { } id ? Keys.FirstOrDefault(k => k.Id == id) : null;

    private void UpdateConnectionVisual()
    {
        EnsureActiveKeyConsistency();
        var connected = _vpn.IsRunning;
        ConnectionImage.Source =
            connected ? (_connectedImage ?? _disconnectedImage) : (_disconnectedImage ?? _connectedImage);

        AnimateConnectionStatus(connected);

        if (!connected)
            TimerText.Text = "00:00:00";

        var hasKey = ActiveKey is not null;
        var hasApps = VpnApps.Count > 0;
        ConnectionButton.IsEnabled = hasKey && hasApps && !_vpnBusy;
        ConnectionButton.Opacity = hasKey && hasApps && !_vpnBusy ? 1 : 0.55;
        EditButton.IsEnabled = !connected;
        AddKeyButton.IsEnabled = !connected;
        // Не используем IsEnabled=false, иначе WPF может "сбросить" внешний вид (фон становится белым).
        // Вместо этого отключаем интерактивность.
        KeysList.IsHitTestVisible = !connected;
        KeysScrollViewer.IsHitTestVisible = !connected;
        if (connected && _editMode)
        {
            _editMode = false;
            EditButton.Content = "Изменить";
            DeleteSelectedButton.Visibility = Visibility.Collapsed;
            foreach (var key in Keys)
                key.SetMarkedForDelete(false);
            _markedForDelete.Clear();
        }
        UpdateDeleteButtonState();
        UpdateTrayContextMenu();
    }

    private void EnsureActiveKeyConsistency()
    {
        if (ActiveKey is not null)
            return;
        var markedActive = Keys.FirstOrDefault(k => k.IsActive);
        if (markedActive is not null)
        {
            _activeKeyId = markedActive.Id;
            return;
        }
        if (Keys.Count > 0 && _activeKeyId is null)
            _activeKeyId = Keys[0].Id;
    }

    private async void ConnectionButton_OnClick(object sender, RoutedEventArgs e)
    {
        PlayConnectionButtonPressAnimation();
        await PerformVpnToggleAsync();
    }

    private async Task PerformVpnToggleAsync()
    {
        if (_vpnBusy)
        {
            ShowNotification("Подождите...", false);
            return;
        }

        if (ActiveKey is null)
        {
            ShowNotification("Не выбран активный ключ.", false);
            return;
        }

        if (VpnApps.Count == 0)
        {
            ShowNotification("Выберите хотя бы одно приложение для VPN.", false);
            return;
        }

        _vpnBusy = true;
        try
        {
            if (_vpn.IsRunning)
            {
                ShowNotification("Отключение...", true);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await _vpn.DisconnectAsync(cts.Token).ConfigureAwait(true);
                _connectionWatch.Reset();
                _timer.Stop();
                TimerText.Text = "00:00:00";
                ShowNotification("VPN отключён", true);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(ActiveKey.VlessUri))
                {
                    ShowNotification("Для ключа не получена VLESS ссылка. Добавьте ключ заново", false);
                    return;
                }

                ShowNotification("Подключение...", true);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var paths = VpnApps.Select(a => a.ExePath).ToList();
                var err = await _vpn.ConnectAsync(ActiveKey.VlessUri!, paths, cts.Token).ConfigureAwait(true);
                if (!string.IsNullOrEmpty(err))
                {
                    ShowNotification(err, false);
                    return;
                }

                _connectionWatch.Restart();
                _timer.Start();
                ShowNotification("VPN подключён", true);
            }

            UpdateConnectionVisual();
        }
        catch (OperationCanceledException)
        {
            ShowNotification("Таймаут операции. Попробуйте ещё раз.", false);
        }
        finally
        {
            _vpnBusy = false;
            UpdateConnectionVisual();
        }
    }

    private void PlayConnectionButtonPressAnimation()
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var downX = new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(85)) { EasingFunction = ease };
        var downY = new DoubleAnimation(0.94, TimeSpan.FromMilliseconds(85)) { EasingFunction = ease };
        var upX = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(160))
        {
            BeginTime = TimeSpan.FromMilliseconds(85),
            EasingFunction = ease
        };
        var upY = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(160))
        {
            BeginTime = TimeSpan.FromMilliseconds(85),
            EasingFunction = ease
        };

        ConnectionButtonScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ConnectionButtonScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ConnectionButtonScale.BeginAnimation(ScaleTransform.ScaleXProperty, downX);
        ConnectionButtonScale.BeginAnimation(ScaleTransform.ScaleYProperty, downY);
        ConnectionButtonScale.BeginAnimation(ScaleTransform.ScaleXProperty, upX);
        ConnectionButtonScale.BeginAnimation(ScaleTransform.ScaleYProperty, upY);
    }

    private void AnimateConnectionStatus(bool connected)
    {
        var newText = connected ? "ПОДКЛЮЧЕНО" : "НЕ ПОДКЛЮЧЕНО";
        var accent = (System.Windows.Application.Current.Resources["AccentPurpleBrush"] as SolidColorBrush)?.Color ?? MediaColor.FromRgb(93, 63, 211);
        var neutral = (System.Windows.Application.Current.Resources["SecondaryTextBrush"] as SolidColorBrush)?.Color ?? MediaColor.FromRgb(160, 160, 160);
        var targetColor = connected ? accent : neutral;

        if (_lastConnectionState == connected && ConnectionStatusText.Text == newText)
            return;

        _lastConnectionState = connected;

        var fadeOut = new DoubleAnimation(0.2, TimeSpan.FromMilliseconds(120));
        fadeOut.Completed += (_, _) =>
        {
            ConnectionStatusText.Text = newText;
            var fadeIn = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(180));
            ConnectionStatusText.BeginAnimation(OpacityProperty, fadeIn);
        };
        ConnectionStatusText.BeginAnimation(OpacityProperty, fadeOut);

        _statusBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _statusBrush.BeginAnimation(
            SolidColorBrush.ColorProperty,
            new ColorAnimation(targetColor, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            });
    }

    private void EditButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_vpn.IsRunning)
        {
            ShowNotification("Сначала отключите VPN, чтобы редактировать ключи.", false);
            return;
        }
        _editMode = !_editMode;
        EditButton.Content = _editMode ? "Отмена" : "Изменить";
        DeleteSelectedButton.Visibility = _editMode ? Visibility.Visible : Visibility.Collapsed;
        KeysList.SelectionMode = System.Windows.Controls.SelectionMode.Single;
        KeysList.SelectedItem = null;

        if (!_editMode)
        {
            foreach (var key in Keys)
                key.SetMarkedForDelete(false);
            _markedForDelete.Clear();
            if (_activeKeyId is { } id)
                KeysList.SelectedItem = Keys.FirstOrDefault(k => k.Id == id);
        }
        UpdateDeleteButtonState();
    }

    private void DeleteSelectedButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_vpn.IsRunning)
            return;
        if (_markedForDelete.Count == 0)
            return;
        var toRemove = Keys.Where(k => _markedForDelete.Contains(k.Id)).ToList();
        foreach (var k in toRemove)
            Keys.Remove(k);
        _markedForDelete.Clear();

        if (Keys.Count == 0)
        {
            _activeKeyId = null;
        }
        else if (_activeKeyId is { } active && Keys.All(k => k.Id != active))
        {
            SetActiveKey(Keys[0].Id);
        }

        UpdateConnectionVisual();
        UpdateDeleteButtonState();
        SaveKeys();
        ShowNotification("Ключи удалены", true);
    }

    private void KeysList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vpn.IsRunning)
            return;
        if (_editMode)
            return;
        if (KeysList.SelectedItem is ProfileKeyVm k && _activeKeyId != k.Id)
            SetActiveKey(k.Id);
    }

    private void KeysList_OnPreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_vpn.IsRunning)
        {
            e.Handled = true;
            return;
        }
        if (!_editMode)
            return;

        var src = e.OriginalSource as DependencyObject;
        while (src is not null && src is not ListBoxItem)
            src = VisualTreeHelper.GetParent(src);

        if (src is not ListBoxItem item || item.DataContext is not ProfileKeyVm vm)
            return;

        if (_markedForDelete.Contains(vm.Id))
        {
            _markedForDelete.Remove(vm.Id);
            vm.SetMarkedForDelete(false);
        }
        else
        {
            _markedForDelete.Add(vm.Id);
            vm.SetMarkedForDelete(true);
        }

        KeysList.SelectedItem = null;
        UpdateDeleteButtonState();
        e.Handled = true;
    }

    private void KeysScrollViewer_OnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        KeysScrollViewer.ScrollToVerticalOffset(KeysScrollViewer.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    private void KeysScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e) =>
        UpdateKeysFadeOverlays();

    private void Keys_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.BeginInvoke(UpdateKeysFadeOverlays, DispatcherPriority.Background);

    private void UpdateKeysFadeOverlays()
    {
        if (KeysScrollViewer is null)
            return;

        var scrollable = KeysScrollViewer.ExtentHeight - KeysScrollViewer.ViewportHeight > 1;
        if (!scrollable)
        {
            TopFadeOverlay.Visibility = Visibility.Collapsed;
            BottomFadeOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        TopFadeOverlay.Visibility = KeysScrollViewer.VerticalOffset > 0.5
            ? Visibility.Visible
            : Visibility.Collapsed;
        BottomFadeOverlay.Visibility =
            KeysScrollViewer.VerticalOffset < KeysScrollViewer.ScrollableHeight - 0.5
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void UpdateDeleteButtonState()
    {
        var hasSelection = _markedForDelete.Count > 0 && !_vpn.IsRunning;
        DeleteSelectedButton.IsEnabled = hasSelection;
        DeleteSelectedButton.Foreground = hasSelection
            ? new SolidColorBrush(MediaColor.FromRgb(248, 113, 113))
            : new SolidColorBrush(MediaColor.FromRgb(107, 114, 128));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            ShowCloseConfirmPanel();
            return;
        }

        _timer.Stop();
        try
        {
            _vpn.DisconnectAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch
        {
            // ignore
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }
        catch
        {
            // ignore
        }

        base.OnClosed(e);
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon is not null)
            return;

        var trayIcon = LoadTrayIconForNotifyArea();

        _trayMenu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = true,
            Font = new System.Drawing.Font("Segoe UI", 9.25f, System.Drawing.FontStyle.Regular,
                System.Drawing.GraphicsUnit.Point),
            AutoSize = true,
            DropShadowEnabled = true,
            Renderer = new Forms.ToolStripProfessionalRenderer(new VexPnTrayColorTable())
        };

        _trayToggleVpnItem = CreateTrayMenuItem(
            "Подключить",
            CreateTrayMenuGlyph(TrayMenuGlyphKind.ToggleOff),
            (_, _) => Dispatcher.InvokeAsync(PerformVpnToggleAsync));
        var showItem = CreateTrayMenuItem(
            "Показать",
            CreateTrayMenuGlyph(TrayMenuGlyphKind.Show),
            (_, _) => Dispatcher.Invoke(ShowFromTray));
        var exitItem = CreateTrayMenuItem(
            "Выход",
            CreateTrayMenuGlyph(TrayMenuGlyphKind.Exit),
            (_, _) =>
            {
                Dispatcher.Invoke(() =>
                {
                    _allowClose = true;
                    Close();
                });
            });

        _trayMenu.Items.Add(_trayToggleVpnItem);
        _trayMenu.Items.Add(showItem);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add(exitItem);
        _trayMenu.Opening += (_, _) => UpdateTrayContextMenu();

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "VexPN",
            Visible = true,
            Icon = trayIcon,
            ContextMenuStrip = _trayMenu
        };

        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);

        UpdateTrayContextMenu();
    }

    private static Forms.ToolStripMenuItem CreateTrayMenuItem(
        string text,
        System.Drawing.Image? image,
        EventHandler onClick)
    {
        var item = new Forms.ToolStripMenuItem(text, image, onClick)
        {
            ForeColor = DrawingColor.FromArgb(235, 238, 245),
            BackColor = DrawingColor.FromArgb(28, 30, 38)
        };
        return item;
    }

    private void UpdateTrayContextMenu()
    {
        if (_trayIcon is null)
            return;

        var connected = _vpn.IsRunning;
        var hasKey = ActiveKey is not null;
        _trayIcon.Text = connected ? "VexPN — подключено" : "VexPN — не подключено";

        if (_trayToggleVpnItem is null)
            return;

        _trayToggleVpnItem.Text = connected ? "Отключить" : "Подключить";
        var oldImg = _trayToggleVpnItem.Image;
        _trayToggleVpnItem.Image = CreateTrayMenuGlyph(connected ? TrayMenuGlyphKind.ToggleOn : TrayMenuGlyphKind.ToggleOff);
        oldImg?.Dispose();
        var hasApps = VpnApps.Count > 0;
        _trayToggleVpnItem.Enabled = hasKey && hasApps && !_vpnBusy;
        _trayToggleVpnItem.ForeColor = _trayToggleVpnItem.Enabled
            ? DrawingColor.FromArgb(235, 238, 245)
            : DrawingColor.FromArgb(120, 125, 140);
    }

    private System.Drawing.Icon LoadTrayIconForNotifyArea()
    {
        var fromLogo = TryIconFromPngLogo();
        if (fromLogo is not null)
            return fromLogo;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        try
        {
            if (File.Exists(iconPath))
                return new System.Drawing.Icon(iconPath);
        }
        catch
        {
            // ignore
        }

        return System.Drawing.SystemIcons.Application;
    }

    private static System.Drawing.Icon? TryIconFromPngLogo()
    {
        var pngPath = Path.Combine(AppContext.BaseDirectory, "Assets", "IMG_8971.PNG");
        if (!File.Exists(pngPath))
            return null;

        try
        {
            using var src = new System.Drawing.Bitmap(pngPath);
            using var square = new System.Drawing.Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(square))
            {
                g.InterpolationMode = Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = Drawing2D.SmoothingMode.HighQuality;
                g.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighQuality;
                g.CompositingQuality = Drawing2D.CompositingQuality.HighQuality;
                g.DrawImage(src, 0, 0, 16, 16);
            }

            var hIcon = square.GetHicon();
            try
            {
                using var tmp = System.Drawing.Icon.FromHandle(hIcon);
                return (System.Drawing.Icon)tmp.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private enum TrayMenuGlyphKind
    {
        Show,
        Exit,
        ToggleOff,
        ToggleOn
    }

    private static System.Drawing.Bitmap CreateTrayMenuGlyph(TrayMenuGlyphKind kind)
    {
        var bmp = new System.Drawing.Bitmap(16, 16, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(DrawingColor.Transparent);

        switch (kind)
        {
            case TrayMenuGlyphKind.Show:
            {
                using (var br = new System.Drawing.SolidBrush(DrawingColor.FromArgb(180, 200, 230)))
                {
                    g.FillRectangle(br, 2f, 3f, 11f, 9f);
                }

                using (var pen = new System.Drawing.Pen(DrawingColor.FromArgb(130, 170, 240), 1.2f))
                    g.DrawRectangle(pen, 2.5f, 3.5f, 10f, 8f);
                break;
            }
            case TrayMenuGlyphKind.Exit:
            {
                using (var door = new System.Drawing.SolidBrush(DrawingColor.FromArgb(230, 120, 120)))
                {
                    g.FillRectangle(door, 3f, 3f, 7f, 11f);
                }

                using (var arr = new System.Drawing.Pen(DrawingColor.FromArgb(245, 200, 120), 1.4f)
                       {
                           EndCap = Drawing2D.LineCap.Round
                       })
                    g.DrawLine(arr, 11f, 8f, 13.5f, 8f);
                break;
            }
            case TrayMenuGlyphKind.ToggleOff:
                using (var on = new System.Drawing.SolidBrush(DrawingColor.FromArgb(52, 211, 153)))
                {
                    var path = new Drawing2D.GraphicsPath();
                    path.AddPolygon(new[]
                    {
                        new System.Drawing.PointF(4f, 3f),
                        new System.Drawing.PointF(13f, 8f),
                        new System.Drawing.PointF(4f, 13f)
                    });
                    g.FillPath(on, path);
                }

                break;
            case TrayMenuGlyphKind.ToggleOn:
                using (var off = new System.Drawing.SolidBrush(DrawingColor.FromArgb(248, 113, 113)))
                {
                    g.FillRectangle(off, 4f, 4f, 8f, 8f);
                }

                break;
        }

        return bmp;
    }

    private sealed class VexPnTrayColorTable : Forms.ProfessionalColorTable
    {
        public override System.Drawing.Color MenuBorder => DrawingColor.FromArgb(55, 58, 72);
        public override System.Drawing.Color MenuItemBorder => DrawingColor.FromArgb(70, 74, 90);
        public override System.Drawing.Color MenuItemSelected => DrawingColor.FromArgb(93, 63, 211);
        public override System.Drawing.Color MenuItemPressedGradientBegin => DrawingColor.FromArgb(110, 80, 230);
        public override System.Drawing.Color MenuItemPressedGradientEnd => DrawingColor.FromArgb(85, 60, 200);
        public override System.Drawing.Color MenuItemSelectedGradientBegin => DrawingColor.FromArgb(93, 63, 211);
        public override System.Drawing.Color MenuItemSelectedGradientEnd => DrawingColor.FromArgb(75, 52, 185);
        public override System.Drawing.Color ToolStripDropDownBackground => DrawingColor.FromArgb(28, 30, 38);
        public override System.Drawing.Color ImageMarginGradientBegin => DrawingColor.FromArgb(22, 24, 30);
        public override System.Drawing.Color ImageMarginGradientMiddle => DrawingColor.FromArgb(22, 24, 30);
        public override System.Drawing.Color ImageMarginGradientEnd => DrawingColor.FromArgb(22, 24, 30);
        public override System.Drawing.Color SeparatorDark => DrawingColor.FromArgb(55, 58, 68);
        public override System.Drawing.Color SeparatorLight => DrawingColor.FromArgb(55, 58, 68);
    }

    private void MinimizeToTray()
    {
        EnsureTrayIcon();
        if (WindowState != WindowState.Minimized)
            WindowState = WindowState.Minimized;
        Hide();
        ShowInTaskbar = false;
        try
        {
            _trayIcon?.ShowBalloonTip(1200, "VexPN", "Приложение свернуто в трей.", Forms.ToolTipIcon.Info);
        }
        catch
        {
            // ignore
        }
    }

    internal void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState != WindowState.Normal)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Focus();
    }

    private void AddKeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_vpn.IsRunning)
        {
            ShowNotification("Сначала отключите VPN, чтобы менять ключи.", false);
            return;
        }
        ShowAddKeyPanel();
    }

    private void ShowAddKeyPanel()
    {
        CloseConfirmPanel.Visibility = Visibility.Collapsed;
        PickVpnAppPanel.Visibility = Visibility.Collapsed;
        ConfirmVpnAppPanel.Visibility = Visibility.Collapsed;
        AddKeyPanel.Visibility = Visibility.Visible;
        ModalOverlay.Visibility = Visibility.Visible;
        ManualKeyTextBox.Text = string.Empty;
        SetResolveUiState(false);
        ManualKeyTextBox.Focus();
    }

    private void HideModalPanels()
    {
        AddKeyPanel.Visibility = Visibility.Collapsed;
        CloseConfirmPanel.Visibility = Visibility.Collapsed;
        PickVpnAppPanel.Visibility = Visibility.Collapsed;
        ConfirmVpnAppPanel.Visibility = Visibility.Collapsed;
        ModalOverlay.Visibility = Visibility.Collapsed;
    }

    private void CancelAddKeyButton_OnClick(object sender, RoutedEventArgs e) =>
        HideModalPanels();

    private void ConfirmAddKeyButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = ResolveAndAddKeyAsync((ManualKeyTextBox.Text ?? string.Empty).Trim());
    }

    private void ShowCloseConfirmPanel()
    {
        AddKeyPanel.Visibility = Visibility.Collapsed;
        PickVpnAppPanel.Visibility = Visibility.Collapsed;
        ConfirmVpnAppPanel.Visibility = Visibility.Collapsed;
        CloseConfirmPanel.Visibility = Visibility.Visible;
        ModalOverlay.Visibility = Visibility.Visible;
    }

    private void CancelCloseButton_OnClick(object sender, RoutedEventArgs e) =>
        HideModalPanels();

    private void ConfirmCloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        _allowClose = true;
        Close();
    }

    private async Task ResolveAndAddKeyAsync(string rawKey)
    {
        if (_isResolvingKey)
            return;

        var key = NormalizeVexKeyInput(rawKey);
        if (!IsVexKey(key))
        {
            ShowNotification("Неверный формат ключа", false);
            return;
        }

        _isResolvingKey = true;
        SetResolveUiState(true);
        try
        {
            var url = $"{BackendBaseUrl}/api/vpn/key/resolve";
            var payload = JsonSerializer.Serialize(new { key });
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            using var res = await _http.SendAsync(req);

            if (!res.IsSuccessStatusCode)
            {
                ShowNotification("Ключ не найден или неактивен", false);
                return;
            }

            var body = await res.Content.ReadAsStringAsync();
            var resolved = JsonSerializer.Deserialize<ResolveKeyResponse>(body);
            if (resolved is null || !resolved.Ok)
            {
                ShowNotification("Ключ не прошёл проверку", false);
                return;
            }

            if (!resolved.Active)
            {
                ShowNotification("У этого ключа нет активного VPN тарифа", false);
                return;
            }

            var vm = new ProfileKeyVm(
                Guid.NewGuid(),
                resolved.Key,
                resolved.KeyName,
                Math.Max(0, resolved.RemainingDays),
                resolved.VlessUri);
            var activeId = AppendOrUpdateKey(vm);
            SetActiveKey(activeId);
            HideModalPanels();
            ShowNotification("Ключ успешно добавлен", true);
            SaveKeys();
        }
        catch
        {
            ShowNotification("Ошибка подключения к backend", false);
        }
        finally
        {
            _isResolvingKey = false;
            SetResolveUiState(false);
        }
    }

    private static string NormalizeVexKeyInput(string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
            return string.Empty;

        var match = Regex.Match(rawKey.ToUpperInvariant(), @"VEX[A-Z0-9]{9,32}");
        if (match.Success)
            return match.Value;

        // Позволяет вставлять ключ из бота с пробелами/дефисами/мусором.
        var compact = new string(rawKey
            .ToUpperInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
        return compact;
    }

    private static bool IsVexKey(string key) =>
        Regex.IsMatch(key, @"^VEX[A-Z0-9]{9,32}$", RegexOptions.CultureInvariant);

    private Guid AppendOrUpdateKey(ProfileKeyVm incoming)
    {
        var existing = Keys.FirstOrDefault(k => k.AccessKey.Equals(incoming.AccessKey, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            Keys.Add(incoming);
            return incoming.Id;
        }

        var idx = Keys.IndexOf(existing);
        var idToKeep = existing.Id;
        var selected = _activeKeyId == existing.Id;
        Keys[idx] = new ProfileKeyVm(
            idToKeep,
            incoming.AccessKey,
            incoming.Name,
            incoming.RemainingDays,
            incoming.VlessUri);
        if (selected)
            SetActiveKey(idToKeep);
        return idToKeep;
    }

    private void SetResolveUiState(bool loading)
    {
        ConfirmAddKeyButton.Content = loading ? string.Empty : "Добавить";
        ConfirmAddKeyButton.IsEnabled = !loading;
        ManualKeyTextBox.IsEnabled = !loading;
        CloseAddKeyPanelButton.IsEnabled = !loading;
        ModalOverlay.IsHitTestVisible = true;
        ConfirmAddKeySpinnerGif.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        if (loading)
            StartAddKeySpinner();
        else
            StopAddKeySpinner();
    }

    private void LoadAddKeySpinnerFrames()
    {
        try
        {
            _addKeySpinnerFrames.Clear();
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Lightness_rotate_36f_cw.gif");
            if (!File.Exists(path))
                return;

            using var fs = File.OpenRead(path);
            var decoder = new GifBitmapDecoder(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            foreach (var frame in decoder.Frames)
            {
                frame.Freeze();
                _addKeySpinnerFrames.Add(frame);
            }

            if (_addKeySpinnerFrames.Count > 0)
                ConfirmAddKeySpinnerGif.Source = _addKeySpinnerFrames[0];
        }
        catch
        {
            // ignore
        }
    }

    private void StartAddKeySpinner()
    {
        if (_addKeySpinnerFrames.Count == 0)
            return;
        _addKeySpinnerFrameIndex = 0;
        ConfirmAddKeySpinnerGif.Source = _addKeySpinnerFrames[0];
        _addKeySpinnerTimer.Start();
    }

    private void StopAddKeySpinner()
    {
        _addKeySpinnerTimer.Stop();
        if (_addKeySpinnerFrames.Count > 0)
            ConfirmAddKeySpinnerGif.Source = _addKeySpinnerFrames[0];
    }

    private void AdvanceAddKeySpinnerFrame()
    {
        if (_addKeySpinnerFrames.Count == 0)
            return;
        _addKeySpinnerFrameIndex = (_addKeySpinnerFrameIndex + 1) % _addKeySpinnerFrames.Count;
        ConfirmAddKeySpinnerGif.Source = _addKeySpinnerFrames[_addKeySpinnerFrameIndex];
    }

    private void ShowNotification(string message, bool success)
    {
        NotificationText.Text = message;
        NotificationBanner.Background = new SolidColorBrush(success
            ? MediaColor.FromArgb(230, 34, 68, 50)
            : MediaColor.FromArgb(230, 90, 35, 35));
        NotificationBanner.Visibility = Visibility.Visible;
        _notificationTimer.Stop();
        _notificationTimer.Start();
    }

    private void LoadStoredKeys()
    {
        try
        {
            Directory.CreateDirectory(_storageDir);
            if (!File.Exists(KeysStoragePath))
                return;

            var json = File.ReadAllText(KeysStoragePath);
            var state = JsonSerializer.Deserialize<StoredKeysState>(json);
            if (state is null)
                return;

            Keys.Clear();
            if (state.Keys is not null)
            {
                foreach (var k in state.Keys)
                {
                    Keys.Add(new ProfileKeyVm(
                        Guid.NewGuid(),
                        k.AccessKey ?? string.Empty,
                        k.Name ?? "Ключ",
                        Math.Max(0, k.RemainingDays),
                        k.VlessUri));
                }

                if (!string.IsNullOrWhiteSpace(state.ActiveAccessKey))
                {
                    var active = Keys.FirstOrDefault(x =>
                        x.AccessKey.Equals(state.ActiveAccessKey, StringComparison.OrdinalIgnoreCase));
                    if (active is not null)
                        SetActiveKey(active.Id);
                }
            }

            VpnApps.Clear();
            if (state.VpnAppPaths is not null)
            {
                foreach (var p in state.VpnAppPaths.Take(5))
                {
                    if (!string.IsNullOrWhiteSpace(p) && File.Exists(p.Trim()))
                        VpnApps.Add(new SelectedVpnAppVm(p.Trim()));
                }
            }
        }
        catch
        {
            // ignore broken file
        }
    }

    private void SaveKeys()
    {
        try
        {
            Directory.CreateDirectory(_storageDir);
            var dto = new StoredKeysState
            {
                ActiveAccessKey = ActiveKey?.AccessKey,
                Keys = Keys.Select(k => new StoredKeyDto
                {
                    AccessKey = k.AccessKey,
                    Name = k.Name,
                    RemainingDays = k.RemainingDays,
                    VlessUri = k.VlessUri
                }).ToList(),
                VpnAppPaths = VpnApps.Select(a => a.ExePath).ToList()
            };
            File.WriteAllText(KeysStoragePath, JsonSerializer.Serialize(dto, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch
        {
            // ignore
        }
    }

    private async Task RefreshActiveKeyFromBackendAsync()
    {
        var key = ActiveKey;
        if (key is null || string.IsNullOrWhiteSpace(key.AccessKey))
            return;

        try
        {
            var url = $"{BackendBaseUrl}/api/vpn/key/resolve";
            var payload = JsonSerializer.Serialize(new { key = key.AccessKey.Trim() });
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            using var res = await _http.SendAsync(req).ConfigureAwait(true);
            if (!res.IsSuccessStatusCode)
                return;

            var body = await res.Content.ReadAsStringAsync().ConfigureAwait(true);
            var r = JsonSerializer.Deserialize<ResolveKeyResponse>(body);
            if (r is null || !r.Ok || !r.Active)
                return;

            key.RemainingDays = Math.Max(0, r.RemainingDays);
            if (!string.IsNullOrWhiteSpace(r.KeyName))
                key.Name = r.KeyName;
            SaveKeys();
        }
        catch
        {
            // ignore
        }
    }

    private void UpdateVpnAppsChrome()
    {
        PickFirstVpnAppButton.Visibility = VpnApps.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        VpnAppsChipsPanel.Visibility = VpnApps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AddVpnAppPlusButton.Visibility = VpnApps.Count is > 0 and < 5 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenVpnAppPicker_Click(object sender, RoutedEventArgs e) =>
        _ = OpenVpnAppPickerAsync();

    private void AddVpnAppPlusButton_Click(object sender, RoutedEventArgs e) =>
        _ = OpenVpnAppPickerAsync();

    private async Task OpenVpnAppPickerAsync()
    {
        if (_vpn.IsRunning)
        {
            ShowNotification("Сначала отключите VPN, чтобы менять список приложений.", false);
            return;
        }

        AddKeyPanel.Visibility = Visibility.Collapsed;
        CloseConfirmPanel.Visibility = Visibility.Collapsed;
        ConfirmVpnAppPanel.Visibility = Visibility.Collapsed;
        PickVpnAppPanel.Visibility = Visibility.Visible;
        ModalOverlay.Visibility = Visibility.Visible;
        AppSearchTextBox.Text = string.Empty;
        PickAppEntries.Clear();
        _pickAppsAll.Clear();
        AppsPickLoadingText.Visibility = Visibility.Visible;
        AppsPickList.Visibility = Visibility.Collapsed;

        _pickScanCts?.Cancel();
        _pickScanCts = new CancellationTokenSource();
        var ct = _pickScanCts.Token;
        try
        {
            var list = await InstalledAppsScanner.ScanAsync(ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            _pickAppsAll = list;
            ApplyPickAppFilter();
            AppsPickLoadingText.Visibility = Visibility.Collapsed;
            AppsPickList.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch
        {
            AppsPickLoadingText.Text = "Не удалось загрузить список приложений.";
        }
    }

    private void ApplyPickAppFilter()
    {
        var q = (AppSearchTextBox.Text ?? string.Empty).Trim();
        PickAppEntries.Clear();
        IEnumerable<InstalledAppEntry> src = _pickAppsAll;
        if (!string.IsNullOrEmpty(q))
        {
            src = _pickAppsAll.Where(a =>
                a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(a.ExePath).Contains(q, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var a in src.Take(500))
            PickAppEntries.Add(a);
    }

    private void AppSearchTextBox_OnTextChanged(object sender, TextChangedEventArgs e) =>
        ApplyPickAppFilter();

    private void CancelPickVpnAppButton_OnClick(object sender, RoutedEventArgs e)
    {
        _pickScanCts?.Cancel();
        HideModalPanels();
    }

    private void AppsPickList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AppsPickList.SelectedItem is not InstalledAppEntry entry)
            return;
        _pendingVpnPick = entry;
        ConfirmVpnAppPanel.Visibility = Visibility.Visible;
        AppsPickList.SelectedItem = null;
    }

    private void CancelConfirmVpnAppButton_OnClick(object sender, RoutedEventArgs e) =>
        ConfirmVpnAppPanel.Visibility = Visibility.Collapsed;

    private void ConfirmAddVpnAppButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_pendingVpnPick is null)
            return;
        TryAddVpnApp(_pendingVpnPick);
        _pendingVpnPick = null;
        ConfirmVpnAppPanel.Visibility = Visibility.Collapsed;
        HideModalPanels();
    }

    private void TryAddVpnApp(InstalledAppEntry entry)
    {
        if (_vpn.IsRunning)
            return;
        if (VpnApps.Any(a => a.ExePath.Equals(entry.ExePath, StringComparison.OrdinalIgnoreCase)))
        {
            ShowNotification("Это приложение уже в списке.", false);
            return;
        }

        if (VpnApps.Count >= 5)
        {
            ShowNotification("Можно добавить не более 5 приложений.", false);
            return;
        }

        VpnApps.Add(new SelectedVpnAppVm(entry.ExePath));
        ShowNotification($"Добавлено: {entry.DisplayName}", true);
    }

    private void RemoveVpnAppChip_Click(object sender, RoutedEventArgs e)
    {
        if (_vpn.IsRunning)
        {
            ShowNotification("Сначала отключите VPN.", false);
            return;
        }

        if (sender is not System.Windows.Controls.Button b || b.Tag is not string path)
            return;
        var vm = VpnApps.FirstOrDefault(a => a.ExePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (vm is not null)
            VpnApps.Remove(vm);
    }
}

public sealed class ProfileKeyVm : INotifyPropertyChanged
{
    private bool _isActiveDot;
    private bool _isMarkedForDelete;
    private string _name;
    private int _remainingDays;

    public ProfileKeyVm(Guid id, string accessKey, string name, int remainingDays, string? vlessUri)
    {
        Id = id;
        AccessKey = accessKey;
        _name = name;
        _remainingDays = remainingDays;
        VlessUri = vlessUri;
    }

    public Guid Id { get; }

    public string AccessKey { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
                return;
            _name = value;
            OnPropertyChanged(nameof(Name));
        }
    }

    public int RemainingDays
    {
        get => _remainingDays;
        set
        {
            if (_remainingDays == value)
                return;
            _remainingDays = value;
            OnPropertyChanged(nameof(RemainingDays));
            OnPropertyChanged(nameof(RemainingLabel));
        }
    }

    public string? VlessUri { get; }

    public string RemainingLabel => $"Осталось: {RemainingDays} дней";

    public Visibility DotVisibility =>
        _isActiveDot ? Visibility.Visible : Visibility.Collapsed;

    public Visibility InactiveDotVisibility =>
        _isActiveDot ? Visibility.Collapsed : Visibility.Visible;

    public bool IsMarkedForDelete => _isMarkedForDelete;
    public bool IsActive => _isActiveDot;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetActive(bool active)
    {
        if (_isActiveDot == active)
            return;
        _isActiveDot = active;
        OnPropertyChanged(nameof(DotVisibility));
        OnPropertyChanged(nameof(InactiveDotVisibility));
    }

    public void SetMarkedForDelete(bool marked)
    {
        if (_isMarkedForDelete == marked)
            return;
        _isMarkedForDelete = marked;
        OnPropertyChanged(nameof(IsMarkedForDelete));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class StoredKeyDto
{
    public string? AccessKey { get; set; }
    public string? Name { get; set; }
    public int RemainingDays { get; set; }
    public string? VlessUri { get; set; }
}

public sealed class StoredKeysState
{
    public string? ActiveAccessKey { get; set; }
    public List<StoredKeyDto>? Keys { get; set; }

    public List<string>? VpnAppPaths { get; set; }
}

public sealed class ResolveKeyResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("key_name")]
    public string KeyName { get; set; } = "Ключ";

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("remaining_days")]
    public int RemainingDays { get; set; }

    [JsonPropertyName("vless_uri")]
    public string? VlessUri { get; set; }
}
