using System;
using System.Globalization;
using System.Windows;

namespace CountdownSaver;

public partial class ConfigWindow : Window
{
    readonly Settings _settings = Settings.Load();

    public ConfigWindow()
    {
        InitializeComponent();
        Language = System.Windows.Markup.XmlLanguage.GetLanguage("ru-RU");

        TitleBox.Text = _settings.Title;
        DateBox.SelectedDate = _settings.Target.Date;
        TimeBox.Text = _settings.Target.ToString("HH:mm");
        FinishedBox.Text = _settings.FinishedText;
        ClockBox.IsChecked = _settings.ShowClock;
        MediaBox.IsChecked = _settings.ShowMedia;
        SystemBox.IsChecked = _settings.ShowSystem;
        MonitorsBox.IsChecked = _settings.AllMonitors;
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        if (DateBox.SelectedDate is not DateTime date)
        {
            MessageBox.Show(this, "Выберите дату.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!TimeSpan.TryParseExact(TimeBox.Text.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var time)
            || time >= TimeSpan.FromDays(1))
        {
            MessageBox.Show(this, "Время укажите в формате ЧЧ:ММ, например 23:59.", Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.Title = TitleBox.Text.Trim();
        _settings.Target = date.Date + time;
        _settings.FinishedText = FinishedBox.Text.Trim();
        _settings.ShowClock = ClockBox.IsChecked == true;
        _settings.ShowMedia = MediaBox.IsChecked == true;
        _settings.ShowSystem = SystemBox.IsChecked == true;
        _settings.AllMonitors = MonitorsBox.IsChecked == true;

        try
        {
            _settings.Save();
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Не удалось сохранить настройки:\n" + ex.Message, Title,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
