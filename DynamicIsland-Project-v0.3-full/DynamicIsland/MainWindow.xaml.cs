using System.IO;
using System.Threading;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using Windows.Media.Control;

namespace DynamicIsland;

public sealed class ClipboardEntry : INotifyPropertyChanged
{
    private static readonly Brush PinnedBrush = MakeBrush(0x0A, 0x84, 0xFF);
    private static readonly Brush NormalBrush = MakeBrush(0x8E, 0x8E, 0x93);

    private static Brush MakeBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private bool _isPinned;

    public ClipboardEntry(string text, DateTime copiedAt)
    {
        Text = text;
        CopiedAt = copiedAt;
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        Preview = flat.Length > 140 ? flat[..140] + "…" : flat;
    }

    public string Text { get; }
    public DateTime CopiedAt { get; }
    public string Preview { get; }

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value) return;
            _isPinned = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinned)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinBrush)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeText)));
        }
    }

    public Brush PinBrush => IsPinned ? PinnedBrush : NormalBrush;
    public string TimeText => IsPinned ? $"Закреплено · {CopiedAt:HH:mm}" : $"{CopiedAt:HH:mm}";

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private const double CompactW = 200, CompactH = 36;
    private const double ExpandedW = 400, ExpandedH = 480;
    private const double CompactWinW = 270, CompactWinH = 80;
    private const double ExpandedWinW = 460, ExpandedWinH = 530;

    private readonly ObservableCollection<ClipboardEntry> _history = new();
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _clipboardTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private readonly DispatcherTimer _mediaTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _finishTimer = new() { Interval = TimeSpan.FromMilliseconds(260) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(1400) };

    private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));
    private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(0x63, 0x63, 0x66));

    private string _lastClipboard = "";
    private string _trackKey = "";
    private int _limit = 5;
    private bool _paused;
    private bool _expanded;
    private bool _refreshingMedia;
    private bool _volumeUpdateFromCode;
    private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _audioDevice;

    public MainWindow()
    {
        InitializeComponent();
        ClipboardItems.ItemsSource = _history;
        _history.CollectionChanged += (_, _) => UpdateEmptyState();
        UpdateEmptyState();

        _collapseTimer.Tick += (_, _) => OnCollapseTimer();
        _finishTimer.Tick += (_, _) => FinishCollapse();
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); StatusText.Text = ""; };

        Loaded += async (_, _) =>
        {
            CenterWindow();
            UpdateClock();
            _clockTimer.Tick += (_, _) => UpdateClock();
            _clockTimer.Start();

            _clipboardTimer.Tick += (_, _) => ReadClipboard();
            _clipboardTimer.Start();

            RefreshVolume();

            await InitializeMediaAsync();
            _mediaTimer.Tick += async (_, _) => await RefreshMediaAsync();
            _mediaTimer.Start();
        };

        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; };
    }

    // ---------- Положение и анимация ----------

    private void CenterWindow()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top;
    }

    private void Root_MouseEnter(object sender, MouseEventArgs e) => Expand();

    private void Root_MouseLeave(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private void OnCollapseTimer()
    {
        _collapseTimer.Stop();
        if (RootBorder.IsMouseOver) return;
        // Не сворачиваем, пока тянут ползунок или открыто контекстное меню.
        if (Mouse.LeftButton == MouseButtonState.Pressed || IslandMenu.IsOpen)
        {
            _collapseTimer.Start();
            return;
        }
        Collapse();
    }

    private static DoubleAnimation Animate(double to, int ms, int delayMs = 0) =>
        new(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

    private void Expand()
    {
        _collapseTimer.Stop();
        _finishTimer.Stop();
        if (_expanded) return;
        _expanded = true;

        Width = ExpandedWinW;
        Height = ExpandedWinH;
        CenterWindow();

        CompactPanel.Visibility = Visibility.Collapsed;
        ExpandedPanel.Visibility = Visibility.Visible;
        RootBorder.CornerRadius = new CornerRadius(26);

        RootBorder.BeginAnimation(FrameworkElement.WidthProperty, Animate(ExpandedW, 280));
        RootBorder.BeginAnimation(FrameworkElement.HeightProperty, Animate(ExpandedH, 280));
        ExpandedPanel.BeginAnimation(UIElement.OpacityProperty, Animate(1, 220, 70));

        UpdateClock();
        RefreshVolume();
        _ = RefreshMediaAsync();
    }

    private void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;

        ExpandedPanel.BeginAnimation(UIElement.OpacityProperty, Animate(0, 120));
        RootBorder.BeginAnimation(FrameworkElement.WidthProperty, Animate(CompactW, 240));
        RootBorder.BeginAnimation(FrameworkElement.HeightProperty, Animate(CompactH, 240));
        _finishTimer.Stop();
        _finishTimer.Start();
    }

    private void FinishCollapse()
    {
        _finishTimer.Stop();
        if (_expanded) return;
        ExpandedPanel.Visibility = Visibility.Collapsed;
        CompactPanel.Visibility = Visibility.Visible;
        RootBorder.CornerRadius = new CornerRadius(18);
        Width = CompactWinW;
        Height = CompactWinH;
        CenterWindow();
    }

    // ---------- Часы ----------

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        var time = now.ToString("HH:mm", ru);
        ClockText.Text = time;
        ClockBig.Text = time;
        var date = now.ToString("dddd, d MMMM", ru);
        DateText.Text = char.ToUpper(date[0], ru) + date[1..];
    }

    // ---------- Буфер обмена ----------

    private void ReadClipboard()
    {
        if (_paused) return;
        try
        {
            if (!Clipboard.ContainsText()) return;
            var value = Clipboard.GetText().Trim();
            if (string.IsNullOrWhiteSpace(value) || value == _lastClipboard) return;
            _lastClipboard = value;

            // Не собираем то, что похоже на пароли, коды и номера карт.
            if (LooksSensitive(value)) return;

            if (value.Length > 2000) value = value[..2000];
            AddEntry(value);
        }
        catch { /* Буфер может быть временно занят другим приложением. */ }
    }

    private void AddEntry(string text)
    {
        var existing = _history.FirstOrDefault(e => e.Text == text);
        if (existing is not null)
        {
            if (existing.IsPinned) return;
            _history.Remove(existing);
        }
        var pinnedCount = _history.Count(e => e.IsPinned);
        _history.Insert(pinnedCount, new ClipboardEntry(text, DateTime.Now));
        TrimHistory();
    }

    private void TrimHistory()
    {
        while (_history.Count(e => !e.IsPinned) > _limit)
        {
            var last = _history.Last(e => !e.IsPinned);
            _history.Remove(last);
        }
    }

    private void UpdateEmptyState() =>
        EmptyText.Visibility = _history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void ClipboardItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClipboardEntry entry }) return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                _lastClipboard = entry.Text;
                Clipboard.SetText(entry.Text);
                ShowStatus("Скопировано");
                return;
            }
            catch { Thread.Sleep(30); }
        }
        ShowStatus("Буфер занят");
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClipboardEntry entry }) return;
        entry.IsPinned = !entry.IsPinned;
        _history.Remove(entry);
        var pinnedCount = _history.Count(x => x.IsPinned);
        _history.Insert(entry.IsPinned ? 0 : pinnedCount, entry);
        TrimHistory();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClipboardEntry entry }) return;
        _history.Remove(entry);
    }

    private void Limit_Click(object sender, RoutedEventArgs e)
    {
        _limit = _limit switch { 5 => 10, 10 => 20, _ => 5 };
        LimitButton.Content = $"Лимит: {_limit}";
        TrimHistory();
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        PauseButton.Content = _paused ? "Продолжить" : "Пауза";
        ShowStatus(_paused ? "Запись остановлена" : "Запись идёт");
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in _history.Where(x => !x.IsPinned).ToList())
            _history.Remove(entry);
    }

    private static bool LooksSensitive(string value)
    {
        string[] markers =
        {
            "password", "passwd", "пароль", "подтверждения", "verification code",
            "one-time code", "bearer ", "api_key", "api-key", "secret="
        };

        var lower = value.ToLowerInvariant();
        if (markers.Any(lower.Contains)) return true;

        var digits = Regex.Replace(value, @"\D", "");
        return digits.Length is >= 13 and <= 19 && PassesLuhn(digits);
    }

    private static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var doubleDigit = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var n = digits[i] - '0';
            if (doubleDigit)
            {
                n *= 2;
                if (n > 9) n -= 9;
            }
            sum += n;
            doubleDigit = !doubleDigit;
        }
        return sum % 10 == 0;
    }

    // ---------- Музыка ----------

    private async Task InitializeMediaAsync()
    {
        try
        {
            _mediaManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            await RefreshMediaAsync();
        }
        catch
        {
            TrackTitle.Text = "Медиасессии недоступны";
            TrackArtist.Text = "Плеер не поддерживает управление Windows";
        }
    }

    private void SetNoMedia()
    {
        TrackTitle.Text = "Ничего не играет";
        TrackArtist.Text = "Запустите музыку в Spotify или другом плеере";
        PlayPauseButton.Content = "\uE768";
        PlayingDot.Fill = GrayBrush;
        if (_trackKey != "")
        {
            _trackKey = "";
            ArtImage.Source = null;
        }
    }

    private async Task RefreshMediaAsync()
    {
        if (_mediaManager is null || _refreshingMedia) return;
        _refreshingMedia = true;
        try
        {
            var session = _mediaManager.GetCurrentSession();
            if (session is null)
            {
                SetNoMedia();
                return;
            }

            var info = await session.TryGetMediaPropertiesAsync();
            var title = string.IsNullOrWhiteSpace(info.Title) ? "Неизвестная композиция" : info.Title;
            var artist = string.IsNullOrWhiteSpace(info.Artist) ? session.SourceAppUserModelId : info.Artist;
            TrackTitle.Text = title;
            TrackArtist.Text = artist;

            var key = $"{title}|{artist}";
            if (key != _trackKey)
            {
                _trackKey = key;
                ArtImage.Source = await LoadArtAsync(info);
            }

            var playing = session.GetPlaybackInfo().PlaybackStatus ==
                          GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            PlayPauseButton.Content = playing ? "\uE769" : "\uE768";
            PlayingDot.Fill = playing ? GreenBrush : GrayBrush;
        }
        catch { }
        finally { _refreshingMedia = false; }
    }

    private static async Task<BitmapImage?> LoadArtAsync(GlobalSystemMediaTransportControlsSessionMediaProperties info)
    {
        try
        {
            if (info.Thumbnail is null) return null;
            using var randomAccess = await info.Thumbnail.OpenReadAsync();
            using var stream = randomAccess.AsStreamForRead();
            var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            memory.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 128;
            bitmap.StreamSource = memory;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch { return null; }
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = _mediaManager?.GetCurrentSession();
            if (session is not null) await session.TryTogglePlayPauseAsync();
            await RefreshMediaAsync();
        }
        catch { }
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = _mediaManager?.GetCurrentSession();
            if (s is not null) await s.TrySkipPreviousAsync();
            await Task.Delay(400);
            await RefreshMediaAsync();
        }
        catch { }
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var s = _mediaManager?.GetCurrentSession();
            if (s is not null) await s.TrySkipNextAsync();
            await Task.Delay(400);
            await RefreshMediaAsync();
        }
        catch { }
    }

    // ---------- Громкость ----------

    private void RefreshVolume()
    {
        if (VolumeSlider.IsMouseCaptureWithin) return;
        try
        {
            _audioDevice?.Dispose();
            _audioDevice = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            SetVolumeUi(_audioDevice.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
        }
        catch { VolumeText.Text = "н/д"; }
    }

    private void SetVolumeUi(double value)
    {
        _volumeUpdateFromCode = true;
        VolumeSlider.Value = Math.Clamp(value, 0, 100);
        VolumeText.Text = $"{(int)Math.Round(VolumeSlider.Value)}%";
        _volumeUpdateFromCode = false;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (VolumeText is null) return;
        VolumeText.Text = $"{(int)Math.Round(VolumeSlider.Value)}%";
        if (_volumeUpdateFromCode || _audioDevice is null) return;
        try { _audioDevice.AudioEndpointVolume.MasterVolumeLevelScalar = (float)(VolumeSlider.Value / 100); }
        catch { }
    }

    // ---------- Меню и завершение ----------

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    protected override void OnClosed(EventArgs e)
    {
        _clockTimer.Stop();
        _clipboardTimer.Stop();
        _mediaTimer.Stop();
        _collapseTimer.Stop();
        _finishTimer.Stop();
        _statusTimer.Stop();
        _audioDevice?.Dispose();
        _enumerator.Dispose();
        base.OnClosed(e);
    }
}
