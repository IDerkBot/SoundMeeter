using Microsoft.Extensions.Logging;
using SoundMeeter.Services.Logging;
using System.Drawing;
using System.Windows.Forms;

namespace SoundMeeter.Services;

/// <summary>
/// Значок в системном лотке и поведение окна при закрытии (SM-D02).
///
/// Реализован на <c>System.Windows.Forms.NotifyIcon</c>, а не на прямом вызове
/// <c>Shell_NotifyIcon</c>. Прямой P/Invoke здесь был написан первым и стоил
/// реальной поломки: чтение структуры <c>NOTIFYICONDATA</c> из
/// неинициализированной памяти давало <c>System.ExecutionEngineException</c> —
/// ошибку уровня CLR, которую ничем не диагностировать. Класс из WinForms делает
/// то же самое, но проверен тысячами приложений и сам следит за версией
/// структуры, перезапуском проводника и длиной подсказки.
///
/// Затраты — одна строка <c>UseWindowsForms</c> в csproj и подсистема WinForms
/// рядом с WPF; сборники этих двух технологий умеют сосуществовать.
/// </summary>
public interface ITrayIconService : IDisposable
{
    /// <summary>Показывать ли иконку в трее (настройка пользователя).</summary>
    bool IsEnabled { get; set; }

    /// <summary>Свернуть окно вместо закрытия, пока иконка в трее включена.</summary>
    bool SuppressClose { get; set; }

    /// <summary>Применить настройки: показать или убрать иконку прямо сейчас.</summary>
    void Refresh();

    /// <summary>Всплывающее уведомление (используется для ошибок).</summary>
    void ShowBalloon(string title, string message);
}

/// <summary>
/// Реализация <see cref="ITrayIconService"/> поверх WinForms
/// <see cref="NotifyIcon"/>.
/// </summary>
public sealed class TrayIconService : ITrayIconService
{
    /// <summary>
    /// Предел подсказки в трее — 63 символа (не 127, как заявлено в
    /// документации к некоторым версиям .NET Framework). NotifyIcon бросает
    /// <see cref="ArgumentException"/> на более длинной строке, поэтому текст
    /// режется здесь, а не падает при попытке показать иконку.
    /// </summary>
    private const int MaxTooltipLength = 63;

    private readonly NotifyIcon _icon;
    private readonly ILogger _logger = AppLog.For<TrayIconService>();
    private readonly string _tooltip;
    private bool _disposed;

    public bool IsEnabled { get; set; }

    /// <summary>
    /// Держится отдельно от <see cref="IsEnabled"/>: иконку можно убрать из трея,
    /// а закрытие — нет, и наоборот (окно скрыто, но закрытие работает).
    /// </summary>
    public bool SuppressClose { get; set; }

    public TrayIconService(string tooltip, Action showWindow, Action toggleRun, Action exit)
    {
        _tooltip = tooltip;

        var menu = new ContextMenuStrip { ShowImageMargin = false };
        menu.Items.Add(Loc.Get("Sm.Tray.Open"), null, (_, _) => showWindow());
        menu.Items.Add(Loc.Get("Sm.Tray.RunToggle"), null, (_, _) => toggleRun());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Loc.Get("Sm.Tray.Exit"), null, (_, _) => exit());

        _icon = new NotifyIcon
        {
            Icon = LoadApplicationIcon(),
            Text = Clamp(_tooltip),
            ContextMenuStrip = menu,
            Visible = false
        };

        // Двойной щелчок по значку. Именно DoubleClick, а не MouseClick: одиночный
        // клик по значку в трее — это обычно «открыть контекстное меню», и
        // реагировать на него показом окна было бы навязчиво.
        _icon.DoubleClick += (_, _) => showWindow();
    }

    /// <summary>Применить настройки: показать или убрать иконку.</summary>
    public void Refresh()
    {
        if (_disposed) return;

        try
        {
            _icon.Visible = IsEnabled;

            if (IsEnabled)
                _logger.LogInformation("Иконка добавлена в трей");
            else
                _logger.LogInformation("Иконка убрана из трея");
        }
        catch (Exception ex)
        {
            // Значок трея — украшение: его неудача не должна ронять микшер.
            _logger.LogWarning(ex, "Не удалось применить настройки трея: {Message}", ex.Message);
        }
    }

    public void ShowBalloon(string title, string message)
    {
        if (_disposed || !_icon.Visible) return;

        try
        {
            _icon.BalloonTipTitle = Clamp(title);
            _icon.BalloonTipText = message;
            _icon.ShowBalloonTip(4000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось показать уведомление трея: {Message}", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _icon.Visible = false;
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка освобождения трея: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Иконка приложения. Берётся из того же исполняемого файла, который
    /// запущен, — иначе в portable-сборке, где <c>Icon.ico</c> рядом может не
    /// быть, значок трея был бы пустым.
    ///
    /// Копия нужна обязательно: объект, отданный
    /// <see cref="Icon.ExtractAssociatedIcon(string?)"/>, освобождать нельзя — он
    /// разделяет внутреннее состояние GDI с самим файлом.
    /// </summary>
    private static Icon LoadApplicationIcon()
    {
        try
        {
            string? path = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                using var source = Icon.ExtractAssociatedIcon(path);
                if (source != null) return new Icon(source, source.Width, source.Height);
            }
        }
        catch (Exception ex)
        {
            AppLog.For<TrayIconService>().LogWarning(ex, "Не удалось взять иконку приложения: {Message}", ex.Message);
        }

        // Запасной вариант: системная иконка приложения. Копия, потому что
        // SystemIcons.Application общий и принадлежит системе.
        return (Icon)SystemIcons.Application.Clone();
    }

    private static string Clamp(string text) =>
        text.Length <= MaxTooltipLength ? text : text[..MaxTooltipLength];
}