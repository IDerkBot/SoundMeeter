using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.Services;
using SoundMeeter.Services.Logging;
using System.IO;
using System.Windows;

namespace SoundMeeter.ViewModels;

/// <summary>
/// Окно просмотра журнала: текущий файл, уровень детализации, копирование
/// диагностики в буфер обмена (SM-A03).
///
/// Пользователю сдаётся не «лог-файл вообще», а связка «журнал + версия +
/// список устройств»: по отдельности ни один из кусков ничего не объясняет,
/// а вместе их достаточно, чтобы понять состояние машины без разработчика.
/// </summary>
public partial class LogViewModel : LocalizedViewModel
{
    private readonly IAudioEngine _engine;

    [ObservableProperty]
    private string _content = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _selectedFile = "";

    [ObservableProperty]
    private bool _isRefreshing;

    public LogViewModel(IAudioEngine engine, SettingsService settings)
    {
        _engine = engine;
        Settings = settings;
        LogLevels = AppLog.AvailableLevels;
        SelectedLogLevel = AppLog.Level.ToString();
        Files = AppLog.LogFiles();
        SelectedFile = AppLog.CurrentFilePath;
        if (string.IsNullOrEmpty(SelectedFile) && Files.Count > 0) SelectedFile = Files[0];
        Refresh();
    }

    public SettingsService Settings { get; }

    public IReadOnlyList<string> LogLevels { get; }

    public IReadOnlyList<string> Files { get; }

    public string LogDirectory => AppLog.DirectoryPath;

    /// <summary>Куда физически пишется журнал — показываем пользователю открытый путь.</summary>
    public string CurrentFile => AppLog.CurrentFilePath;

    /// <summary>
    /// Уровень детализации. Переключается на лету: провайдер проверяет порог
    /// при каждой записи, перезапускать приложение не нужно.
    /// </summary>
    [ObservableProperty]
    private string _selectedLogLevel;

    partial void OnSelectedLogLevelChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        AppLog.SetLevel(AppLog.ParseLevel(value));
        Settings.Settings.LogLevel = value;
        Settings.Save();
        Status = Loc.Get("Sm.Log.Status.LevelSet", AppLog.Level);
        Refresh();
    }

    partial void OnSelectedFileChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        IsRefreshing = true;
        try
        {
            var path = string.IsNullOrWhiteSpace(SelectedFile) ? AppLog.CurrentFilePath : SelectedFile;
            Content = string.IsNullOrEmpty(path) || !File.Exists(path)
                ? Loc.Get("Sm.Log.NotCreated")
                : AppLog.ReadLog(path);
            Status = Loc.Get("Sm.Log.Status.Size", Path.GetFileName(path), Content.Length);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        var text = AppLog.BuildDiagnosticsText(DescribeDevices());
        try
        {
            Clipboard.SetText(text);
            Status = Loc.Get("Sm.Log.Status.Copied");
        }
        catch (Exception ex)
        {
            Status = Loc.Get("Sm.Log.Status.CopyFailed", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(LogDirectory)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Status = Loc.Get("Sm.Log.Status.FolderFailed", ex.Message);
        }
    }

    /// <summary>
    /// Список устройств для диагностики: тот же каталог, что использует движок,
    /// плюс состояние стрипов. Снимок берём один раз, чтобы отчёт соответствовал
    /// моменту копирования.
    /// </summary>
    private string DescribeDevices()
    {
        var lines = new List<string>();
        var catalog = _engine.Catalog.ToList();
        var names = catalog.ToDictionary(d => d.DeviceId, d => d.Name);
        foreach (var device in catalog.OrderBy(d => d.IsMicrophone).ThenBy(d => d.Name))
        {
            // Для половины кабеля важно видеть и пару: по ней приложения
            // направляются в выход, а снимается вход.
            var cable = device.IsVirtualCable
                ? device.CablePeerId != null && names.TryGetValue(device.CablePeerId, out var peer)
                    ? $"  cable-peer={peer}"
                    : "  cable-peer=(none)"
                : "";

            lines.Add($"{(device.IsMicrophone ? "capture" : "render ")}  {device.Name}  " +
                      $"[{device.DeviceId}]{cable}");
        }

        lines.Add(string.Empty);
        lines.Add("--- стрипы ---");
        foreach (var input in _engine.Inputs)
        {
            // apps-> — реальная цель переноса приложений на стрип; у входа
            // виртуального кабеля это его выход, а не сам стрип.
            var appTarget = CablePairing.GetAppRenderDeviceId(
                catalog.FirstOrDefault(d => d.DeviceId == input.DeviceId),
                input.IsMicrophone,
                input.AppTargetDeviceId);
            var apps = appTarget == null
                ? ""
                : $" apps->[{appTarget}]{(names.TryGetValue(appTarget, out var n) ? " " + n : "")}";

            lines.Add($"INPUT  {StripTitle(input)}  device={input.DeviceId} " +
                      $"vol={input.VolumeDb:0.0} dB gain={input.GainDb:0.0} dB mute={input.IsMuted} " +
                      $"mono={input.IsMono} solo={input.IsSolo} denoise={input.DenoiserEnabled}{apps}");
        }
        foreach (var bus in _engine.Buses)
        {
            lines.Add($"OUTPUT {StripTitle(bus)}  device={bus.DeviceId} " +
                      $"vol={bus.VolumeDb:0.0} dB mute={bus.IsMuted} mono={bus.IsMono} solo={bus.IsSolo}");
        }

        lines.Add(string.Empty);
        lines.Add("--- маршруты ---");
        foreach (var input in _engine.Inputs)
        {
            foreach (var route in input.BusRouting.Where(r => r.Value.Enabled))
            {
                var bus = _engine.Buses.FirstOrDefault(b => b.Id == route.Key);
                lines.Add($"{StripTitle(input)} -> {bus?.Name ?? route.Key}: посылка {route.Value.GainDb:0.0} dB");
            }
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string StripTitle(Models.InputChannelModel model) =>
        string.IsNullOrWhiteSpace(model.ChannelName) ? model.Name : model.ChannelName;

    private static string StripTitle(Models.OutputBusModel model) =>
        string.IsNullOrWhiteSpace(model.ChannelName) ? model.Name : model.ChannelName;
}