using System;
using System.IO;
using System.Text.Json;

namespace CountdownSaver;

public sealed class Settings
{
    public string Title { get; set; } = "До Нового года";
    public DateTime Target { get; set; } = new DateTime(DateTime.Now.Year + 1, 1, 1, 0, 0, 0);
    public string FinishedText { get; set; } = "С Новым годом! 🎉";
    public bool ShowClock { get; set; } = true;
    public bool ShowMedia { get; set; } = true;
    public bool AllMonitors { get; set; } = true;

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CountdownSaver", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            // Повреждённый файл — просто используем значения по умолчанию.
        }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
