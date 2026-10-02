using CommunityToolkit.Mvvm.ComponentModel;

namespace SoundMeeter.ViewModels;

/// <summary>
/// Один выход в списке назначения кнопки FUNC. То же, что строка в попапах
/// OUT/VIRT, но без посылки и без «скрыть устройство»: здесь выбирают, куда
/// кнопка отправит стрип, а посылка остаётся там, где она уже настроена.
/// </summary>
public partial class FuncTargetViewModel : ObservableObject
{
    private readonly Action<FuncTargetViewModel> _onToggled;
    private string _channelName;

    public string BusId { get; }

    /// <summary>Имя устройства из системы — показываем в подсказке.</summary>
    public string DeviceName { get; }

    /// <summary>Подпись: переименованный пользователем канал, иначе устройство.</summary>
    public string Title => string.IsNullOrWhiteSpace(_channelName) ? DeviceName : _channelName;

    /// <summary>Подсказка: с переименованием показываем и канал, и устройство.</summary>
    public string ToolTip => string.IsNullOrWhiteSpace(_channelName)
        ? DeviceName
        : $"{_channelName}  ({DeviceName})";

    /// <summary>Отмечен ли этот выход в назначении кнопки.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// true — стрип сейчас реально идёт в этот выход. Показывается точкой в
    /// попапе, чтобы было видно разницу между «назначено кнопке» и «уже
    /// включено в роутинге»: одно без другого кнопку не гасит.
    /// </summary>
    [ObservableProperty]
    private bool _isActive;

    public FuncTargetViewModel(
        string busId,
        string deviceName,
        string channelName,
        bool isSelected,
        Action<FuncTargetViewModel> onToggled)
    {
        BusId = busId;
        DeviceName = deviceName;
        _channelName = channelName ?? "";
        _isSelected = isSelected;
        _onToggled = onToggled;
    }

    /// <summary>Канал переименован на стрипе выхода — обновляем подпись во всех попапах.</summary>
    public void SetChannelName(string? channelName)
    {
        string value = channelName ?? "";
        if (string.Equals(_channelName, value, StringComparison.Ordinal)) return;
        _channelName = value;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ToolTip));
    }

    partial void OnIsSelectedChanged(bool value) => _onToggled(this);
}
