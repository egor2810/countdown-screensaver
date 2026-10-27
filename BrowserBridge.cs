using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace CountdownSaver;

public sealed record BrowserTrack(
    string Title,
    string Artist,
    string? Artwork,
    bool Playing,
    double? Position,
    double? Duration,
    string Source,
    DateTime ReceivedAt);

// Принимает от расширения браузера (папка extension) POST-запросы на http://127.0.0.1:38211/media
// с тем, что играет во вкладках. Нужно для браузеров, которые не передают трек в Windows (Яндекс и др.).
public sealed class BrowserBridge
{
    public const int Port = 38211;
    static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(5);

    readonly object _lock = new();
    BrowserTrack? _latest;

    public BrowserTrack? Latest
    {
        get
        {
            lock (_lock)
                return _latest != null && DateTime.Now - _latest.ReceivedAt < MaxAge ? _latest : null;
        }
    }

    public void Start() => new Thread(Run) { IsBackground = true, Name = "BrowserBridge" }.Start();

    void Run()
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Start();
            Log.Write($"Browser: жду расширение на 127.0.0.1:{Port}");
        }
        catch (Exception ex)
        {
            Log.Error("Browser: не удалось открыть порт", ex);
            return;
        }

        while (true)
        {
            try
            {
                using var client = listener.AcceptTcpClient();
                client.ReceiveTimeout = 2000;
                client.SendTimeout = 2000;
                Handle(client.GetStream());
            }
            catch
            {
                // Оборванное соединение — просто ждём следующее.
            }
        }
    }

    void Handle(NetworkStream stream)
    {
        var data = new MemoryStream();
        var chunk = new byte[8192];
        int headerEnd;
        while ((headerEnd = IndexOfHeaderEnd(data)) < 0)
        {
            int n = stream.Read(chunk, 0, chunk.Length);
            if (n <= 0 || data.Length > 64 * 1024)
                return;
            data.Write(chunk, 0, n);
        }

        string headers = Encoding.ASCII.GetString(data.GetBuffer(), 0, headerEnd);
        int length = 0;
        foreach (var line in headers.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line[15..].Trim(), out length);
        }
        if (length is < 0 or > 1024 * 1024)
            return;

        int bodyStart = headerEnd + 4;
        while (data.Length - bodyStart < length)
        {
            int n = stream.Read(chunk, 0, chunk.Length);
            if (n <= 0)
                return;
            data.Write(chunk, 0, n);
        }

        if (headers.StartsWith("POST", StringComparison.Ordinal) && length > 0)
            Parse(Encoding.UTF8.GetString(data.GetBuffer(), bodyStart, length));

        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 204 No Content\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Headers: *\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n\r\n");
        stream.Write(response, 0, response.Length);
    }

    static int IndexOfHeaderEnd(MemoryStream data)
    {
        var buffer = data.GetBuffer();
        for (int i = 0; i + 3 < data.Length; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    void Parse(string body)
    {
        BrowserTrack? track = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string browser = Str(root, "browser") ?? "Браузер";
            if (root.TryGetProperty("media", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                string title = Str(m, "title") ?? "";
                if (title.Length > 0)
                {
                    string site = Str(m, "site") ?? "";
                    if (site.StartsWith("www."))
                        site = site[4..];
                    track = new BrowserTrack(
                        title,
                        Str(m, "artist") ?? "",
                        Str(m, "artwork"),
                        m.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.True,
                        Num(m, "position"),
                        Num(m, "duration"),
                        site.Length > 0 ? $"{browser} · {site}" : browser,
                        DateTime.Now);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Browser: разбор данных", ex);
            return;
        }

        lock (_lock)
            _latest = track;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : null;

    static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.GetDouble() >= 0 ? v.GetDouble() : null;
}
