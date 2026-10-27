using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace CountdownSaver;

public sealed record MediaInfo(
    string Title,
    string Artist,
    string Source,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    ImageSource? Cover);

// Собирает «что сейчас играет» из двух мест:
//  1) системные медиаконтролы Windows (та же плашка, что у регулятора громкости) — Spotify, Chrome,
//     Edge, Firefox, VLC и т.д.;
//  2) расширение для браузера (BrowserBridge) — для браузеров, которые не отдают трек в Windows,
//     например Яндекс Браузер.
public sealed class MediaWatcher
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly BrowserBridge _bridge = new();
    GlobalSystemMediaTransportControlsSessionManager? _manager;
    string? _coverKey;
    ImageSource? _cover;
    bool _busy;
    string? _lastError;

    public event Action<MediaInfo?>? Updated;

    // Кандидат на показ: сведения о треке (без обложки), ключ для кэша обложки и способ её загрузить.
    sealed record Candidate(MediaInfo Info, string Key, Func<Task<ImageSource?>> LoadCover);

    public async void Start()
    {
        _bridge.Start();
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            Log.Write("Media: подключено к системным медиаконтролам");
        }
        catch (Exception ex)
        {
            Log.Error("Media: RequestAsync", ex);
        }
        _timer.Tick += async (_, _) => await Poll();
        _timer.Start();
        await Poll();
    }

    async Task Poll()
    {
        if (_busy)
            return;
        _busy = true;
        MediaInfo? info = null;
        try
        {
            info = await Read();
        }
        catch (Exception ex)
        {
            // Не засоряем лог одной и той же ошибкой каждую секунду.
            if (ex.Message != _lastError)
                Log.Error("Media: чтение", ex);
            _lastError = ex.Message;
        }
        try
        {
            Updated?.Invoke(info);
        }
        catch (Exception ex)
        {
            Log.Error("Media: отображение", ex);
        }
        finally
        {
            _busy = false;
        }
    }

    async Task<MediaInfo?> Read()
    {
        Candidate? system = null;
        if (_manager != null)
        {
            try
            {
                system = await ReadSystem(_manager);
            }
            catch (Exception ex)
            {
                if (ex.Message != _lastError)
                    Log.Error("Media: системные медиаконтролы", ex);
                _lastError = ex.Message;
            }
        }
        var web = _bridge.Latest is BrowserTrack t ? FromBrowser(t) : null;

        // Играющее важнее остановленного; при равенстве — системный источник.
        var chosen = system != null && (system.Info.IsPlaying || web == null || !web.Info.IsPlaying) ? system : web;
        if (chosen == null)
            return null;

        if (chosen.Key != _coverKey)
        {
            _cover = await chosen.LoadCover();
            // Обложка часто появляется с задержкой — пробуем снова, пока не получим.
            _coverKey = _cover != null ? chosen.Key : null;
        }
        return chosen.Info with { Cover = _cover };
    }

    static async Task<Candidate?> ReadSystem(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        // Предпочитаем то, что реально играет; иначе — «текущую» сессию Windows.
        GlobalSystemMediaTransportControlsSession? session = null;
        foreach (var s in manager.GetSessions())
        {
            if (s.GetPlaybackInfo()?.PlaybackStatus == PlaybackStatus.Playing)
            {
                session = s;
                break;
            }
        }
        session ??= manager.GetCurrentSession();
        if (session == null)
            return null;

        var props = await session.TryGetMediaPropertiesAsync();
        if (props == null || string.IsNullOrWhiteSpace(props.Title))
            return null;

        bool playing = session.GetPlaybackInfo()?.PlaybackStatus == PlaybackStatus.Playing;

        var timeline = session.GetTimelineProperties();
        var duration = timeline.EndTime - timeline.StartTime;
        var position = timeline.Position - timeline.StartTime;
        // Позицию приложения обновляют редко — досчитываем сами.
        if (playing && timeline.LastUpdatedTime.Year > 2000)
            position += DateTimeOffset.Now - timeline.LastUpdatedTime;

        var info = new MediaInfo(
            props.Title.Trim(),
            (props.Artist ?? "").Trim(),
            FriendlySource(session.SourceAppUserModelId),
            playing,
            Clamp(position, duration),
            duration,
            null);
        var thumbnail = props.Thumbnail;
        return new Candidate(info, $"{session.SourceAppUserModelId}|{props.Title}|{props.Artist}",
            () => LoadThumbnail(thumbnail));
    }

    static Candidate FromBrowser(BrowserTrack t)
    {
        var duration = t.Duration is double d ? TimeSpan.FromSeconds(d) : TimeSpan.Zero;
        var position = t.Position is double p ? TimeSpan.FromSeconds(p) : TimeSpan.Zero;
        if (t.Playing)
            position += DateTime.Now - t.ReceivedAt;

        var info = new MediaInfo(t.Title, t.Artist, t.Source, t.Playing, Clamp(position, duration), duration, null);
        return new Candidate(info, $"web|{t.Title}|{t.Artist}|{t.Artwork}", () => LoadUrl(t.Artwork));
    }

    static TimeSpan Clamp(TimeSpan position, TimeSpan duration)
    {
        if (position < TimeSpan.Zero)
            return TimeSpan.Zero;
        if (duration > TimeSpan.Zero && position > duration)
            return duration;
        return position;
    }

    static async Task<ImageSource?> LoadThumbnail(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail == null)
            return null;
        try
        {
            using var winStream = await thumbnail.OpenReadAsync();
            using var stream = winStream.AsStreamForRead();
            var memory = new MemoryStream();
            await stream.CopyToAsync(memory);
            return Decode(memory.ToArray());
        }
        catch (Exception ex)
        {
            Log.Error("Media: обложка", ex);
            return null;
        }
    }

    static async Task<ImageSource?> LoadUrl(string? url)
    {
        if (url == null || !(url.StartsWith("https://") || url.StartsWith("http://")))
            return null;
        try
        {
            return Decode(await Http.GetByteArrayAsync(url));
        }
        catch (Exception ex)
        {
            Log.Error("Browser: обложка " + url, ex);
            return null;
        }
    }

    static ImageSource Decode(byte[] bytes)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    static string FriendlySource(string? appId)
    {
        string id = (appId ?? "").ToLowerInvariant();
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("yandex")) return "Яндекс Браузер";
        if (id.Contains("msedge")) return "Microsoft Edge";
        if (id.Contains("chrome")) return "Google Chrome";
        if (id.Contains("firefox") || id.Contains("308046b0af4a39cb")) return "Firefox";
        if (id.Contains("opera")) return "Opera";
        if (id.Contains("brave")) return "Brave";
        if (id.Contains("vivaldi")) return "Vivaldi";
        if (id.Contains("vlc")) return "VLC";
        if (id.Contains("zunemusic")) return "Медиаплеер";
        if (id.Length == 0) return "Музыка";
        // «Company.App_xxx!App» или «app.exe» → «App»
        string name = appId!.Split('!')[0].Split('_')[0];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        int dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }
}
