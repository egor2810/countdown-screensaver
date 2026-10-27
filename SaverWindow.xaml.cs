using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ScreenRect = System.Drawing.Rectangle;

namespace CountdownSaver;

public partial class SaverWindow : Window
{
    static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    readonly ScreenRect? _bounds;
    readonly Settings _settings;
    readonly Ambient _ambient;
    readonly DispatcherTimer _tick = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(200) };
    readonly DateTime _shownAt = DateTime.Now;
    Point? _mouseStart;
    long _lastSecond = -1;
    object? _lastCover = new();
    bool? _mediaVisible;
    bool? _playing;
    bool Debug => _bounds == null;

    // bounds == null — отладочный режим: обычное окно, не закрывается от мыши, показывает ошибки.
    public SaverWindow(ScreenRect? bounds, Settings settings, MediaWatcher? media, bool showContent)
    {
        InitializeComponent();
        _bounds = bounds;
        _settings = settings;
        _ambient = new Ambient(AmbientCanvas);

        if (bounds is ScreenRect b)
        {
            // Примерная позиция в DIP; точная выставляется в OnSourceInitialized в пикселях.
            Left = b.Left;
            Top = b.Top;
            Width = b.Width;
            Height = b.Height;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = false;
            ShowInTaskbar = true;
            Cursor = null;
            Width = 1280;
            Height = 720;
            Title = "CountdownSaver — отладка (ошибки пишутся в " + Log.FilePath + ")";
            DebugText.Visibility = Visibility.Visible;
            Log.Written += message => Dispatcher.InvokeAsync(() =>
            {
                DebugText.Text = (message.Split('\n')[0] + "\n" + DebugText.Text).Trim();
            });
        }

        if (!showContent)
        {
            ContentBox.Visibility = Visibility.Collapsed;
            return;
        }

        TitleText.Text = Spaced(settings.Title.ToUpper(Ru));
        TitleText.Visibility = string.IsNullOrWhiteSpace(settings.Title) ? Visibility.Collapsed : Visibility.Visible;
        DoneText.Text = settings.FinishedText;
        ClockPanel.Visibility = settings.ShowClock ? Visibility.Visible : Visibility.Collapsed;
        SetMediaVisible(false, animate: false);

        if (media != null)
            media.Updated += OnMediaUpdated;

        _tick.Tick += (_, _) =>
        {
            try
            {
                UpdateTime();
            }
            catch (Exception ex)
            {
                Log.Error("Таймер", ex);
            }
        };
        _tick.Start();
        UpdateTime();
    }

    // ---------- Время ----------

    void UpdateTime()
    {
        var now = DateTime.Now;
        long second = now.Ticks / TimeSpan.TicksPerSecond;
        if (second == _lastSecond)
            return;
        _lastSecond = second;

        ClockText.Text = now.ToString("HH:mm", Ru);
        DateText.Text = Capitalize(now.ToString("dddd, d MMMM", Ru));

        string info = _settings.Target.ToString("d MMMM yyyy", Ru);
        if (_settings.Target.TimeOfDay != TimeSpan.Zero)
            info += _settings.Target.ToString(", HH:mm", Ru);
        InfoText.Text = info;

        var left = _settings.Target - now;
        if (left <= TimeSpan.Zero)
        {
            CountdownRows.Visibility = Visibility.Collapsed;
            DoneText.Visibility = Visibility.Visible;
            return;
        }

        // Округляем вверх, чтобы «00:00:00» совпало с наступлением момента.
        long total = (long)Math.Ceiling(left.TotalSeconds);
        long days = total / 86400;
        int hours = (int)(total / 3600 % 24);
        int minutes = (int)(total / 60 % 60);
        int seconds = (int)(total % 60);

        DaysRow.Visibility = days > 0 ? Visibility.Visible : Visibility.Collapsed;
        DaysNum.Text = days.ToString(Ru);
        HoursNum.Text = hours.ToString("00");
        MinutesNum.Text = minutes.ToString("00");
        SecondsNum.Text = seconds.ToString("00");

        DaysUnit.Text = Plural(days, "день", "дня", "дней");
        HoursUnit.Text = Plural(hours, "час", "часа", "часов");
        MinutesUnit.Text = Plural(minutes, "минута", "минуты", "минут");
        SecondsUnit.Text = Plural(seconds, "секунда", "секунды", "секунд");
    }

    static string Plural(long n, string one, string few, string many)
    {
        long n100 = n % 100, n10 = n % 10;
        if (n100 is >= 11 and <= 14) return many;
        if (n10 == 1) return one;
        if (n10 is >= 2 and <= 4) return few;
        return many;
    }

    // В WPF нет межбуквенного интервала — разрежаем текст тонкими пробелами.
    static string Spaced(string s)
    {
        var parts = new System.Collections.Generic.List<string>();
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext())
            parts.Add(e.GetTextElement() == " " ? "\u2002" : e.GetTextElement());
        return string.Join("\u2009", parts);
    }

    static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0], Ru) + s[1..];

    // ---------- Сейчас играет ----------

    void OnMediaUpdated(MediaInfo? info)
    {
        if (info == null)
        {
            SetMediaVisible(false);
            if (_lastCover != null)
            {
                _lastCover = null;
                _ambient.SetPalette(Ambient.DefaultPalette);
            }
            SetPlaying(true);
            return;
        }

        SongText.Text = info.Title;
        ArtistText.Text = info.Artist;
        ArtistText.Visibility = info.Artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SourceText.Text = Spaced((info.IsPlaying ? "▶ СЕЙЧАС ИГРАЕТ · " : "❚❚ ПАУЗА · ") + info.Source.ToUpper(Ru));

        if (info.Duration > TimeSpan.FromSeconds(1))
        {
            ProgressRow.Visibility = Visibility.Visible;
            PositionText.Text = FormatTime(info.Position);
            DurationText.Text = FormatTime(info.Duration);
            ProgressScale.ScaleX = Math.Clamp(info.Position.TotalSeconds / info.Duration.TotalSeconds, 0, 1);
        }
        else
        {
            ProgressRow.Visibility = Visibility.Collapsed;
        }

        if (!ReferenceEquals(info.Cover, _lastCover))
        {
            _lastCover = info.Cover;
            CoverImage.Source = info.Cover;
            CoverImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(600)));
            _ambient.SetPalette(Ambient.ExtractPalette(info.Cover));
        }
        SetPlaying(info.IsPlaying);
        SetMediaVisible(true);
    }

    void SetPlaying(bool playing)
    {
        if (playing == _playing)
            return;
        _playing = playing;
        _ambient.SetBrightness(playing ? 1 : 0.55);
    }

    static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    // С музыкой: обложка слева, отсчёт справа. Без музыки: отсчёт по центру.
    void SetMediaVisible(bool visible, bool animate = true)
    {
        if (visible == _mediaVisible)
            return;
        _mediaVisible = visible;

        void Apply()
        {
            // Берём актуальное состояние: оно могло снова смениться, пока шло затухание.
            visible = _mediaVisible == true;
            MediaPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            CountdownColumn.HorizontalAlignment = visible ? HorizontalAlignment.Left : HorizontalAlignment.Center;
            CountdownColumn.Margin = visible ? new Thickness(880, 0, 0, 0) : new Thickness(0);
            MediaPanel.Opacity = 1;
        }

        if (!animate)
        {
            Apply();
            return;
        }

        var fadeOut = new DoubleAnimation(0, TimeSpan.FromMilliseconds(500));
        fadeOut.Completed += (_, _) =>
        {
            Apply();
            Layout.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(700)));
        };
        Layout.BeginAnimation(OpacityProperty, fadeOut);
    }

    // ---------- Окно и выход ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_bounds is not ScreenRect b)
            return;
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, SWP_SHOWWINDOW);
    }

    bool InGracePeriod => (DateTime.Now - _shownAt).TotalMilliseconds < 700;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (Debug)
            return;
        var p = e.GetPosition(this);
        if (_mouseStart == null || InGracePeriod)
        {
            _mouseStart = p;
            return;
        }
        if ((p - _mouseStart.Value).Length > 12)
            Exit();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e) => Exit();
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e) => Exit();
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e) => Exit();

    void Exit()
    {
        if (Debug)
            return;
        if (!InGracePeriod)
            Application.Current.Shutdown();
    }

    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_SHOWWINDOW = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
