using System;
using System.Collections.Generic;
using System.Text;

namespace SoundMeeter.TrayIcon.Contract;

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
