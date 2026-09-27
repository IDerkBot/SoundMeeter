using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.Audio;

namespace SoundMeeter.ViewModels;

/// <summary>
/// Один выбираемый выход для стрипа (чекбокс в попапе OUT/VIRT) плюс
/// индивидуальная посылка на этот выход в дБ.
/// Переключение и правка уровня сразу применяются в движке.
/// </summary>
public partial class OutputOptionViewModel : ObservableObject
{
    private readonly Action<OutputOptionViewModel> _onChanged;
    private readonly Action<OutputOptionViewModel, float> _onGainChanged;
    private readonly Action<OutputOptionViewModel> _onHidden;

    private string _channelName = "";

    public string BusId { get; }

    /// <summary>Имя устройства из системы — показываем в подсказке.</summary>
    public string DeviceName { get; }

    /// <summary>
    /// Подпись в списке выходов: переименованный пользователем канал,
    /// а если имени нет — имя устройства. Так в OUT/VIRT видно то же,
    /// что написано на стрипе выхода.
    /// </summary>
    public string Title => string.IsNullOrWhiteSpace(_channelName) ? DeviceName : _channelName;

    /// <summary>Подсказка: с переименованием показываем и канал, и устройство.</summary>
    public string ToolTip => string.IsNullOrWhiteSpace(_channelName)
        ? DeviceName
        : $"{_channelName}  ({DeviceName})";

    [ObservableProperty]
    private bool _isEnabled;

    /// <summary>
    /// Посылка этого стрипа в эту шину, дБ. Читается тапом на каждом пакете,
    /// поэтому изменение слышно сразу и без перезапуска аудиодвижка.
    /// </summary>
    [ObservableProperty]
    private float _gainDb;

    public OutputOptionViewModel(string busId, string deviceName, string channelName, bool isEnabled, float gainDb,
        Action<OutputOptionViewModel> onChanged,
        Action<OutputOptionViewModel, float> onGainChanged,
        Action<OutputOptionViewModel> onHidden)
    {
        BusId = busId;
        DeviceName = deviceName;
        _channelName = channelName ?? "";
        _isEnabled = isEnabled;
        _gainDb = float.IsFinite(gainDb) ? gainDb : 0f;
        _onChanged = onChanged;
        _onGainChanged = onGainChanged;
        _onHidden = onHidden;
    }

    /// <summary>
    /// Переименование канала на стрипе выхода: обновляет подпись во всех
    /// попапах OUT/VIRT, где выбрана эта шина.
    /// </summary>
    public void SetChannelName(string? channelName)
    {
        string value = channelName ?? "";
        if (string.Equals(_channelName, value, StringComparison.Ordinal)) return;
        _channelName = value;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ToolTip));
    }

    /// <summary>Диапазон слайдера посылки — тот же, что принимает движок.</summary>
    public double MinGainDb => BusTap.MinGainDb;

    public double MaxGainDb => BusTap.MaxGainDb;

    /// <summary>Подпись значения; ноль показываем явно, чтобы было видно «ровно 0 дБ».</summary>
    public string GainText => GainDb > 0.05f ? $"+{GainDb:0.0}" : GainDb.ToString("0.0");

    /// <summary>Скрывает это устройство (шину) из всех стрипов и попапов.</summary>
    [RelayCommand]
    private void Hide()
    {
        IsEnabled = false;
        _onHidden(this);
    }

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowGain));
        _onChanged(this);
    }

    partial void OnGainDbChanged(float value)
    {
        OnPropertyChanged(nameof(GainText));
        _onGainChanged(this, value);
    }

    /// <summary>
    /// Уровень посылки показываем только у включённых маршрутов: на выключенной
    /// посылке ползунок только занимает место в узком попапе.
    /// </summary>
    public bool ShowGain => IsEnabled;
}
