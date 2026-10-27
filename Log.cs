using System;
using System.IO;

namespace CountdownSaver;

// Пишет ошибки в %APPDATA%\CountdownSaver\error.log, чтобы было что разбирать.
public static class Log
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CountdownSaver", "error.log");

    public static event Action<string>? Written;

    public static void Error(string where, Exception ex) => Write($"{where}: {ex}");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
        Written?.Invoke(message);
    }
}
