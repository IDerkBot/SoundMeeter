using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SoundMeeter.Models;
using SoundMeeter.Services;
using System.Collections.ObjectModel;
using System.Linq;

namespace SoundMeeter.ViewModels;

/// <summary>
/// Пользовательская кнопка FUNC на входном стрипе: назначенный набор выходов
/// (аппаратных OUT и/или виртуальных VIRT) и то, что кнопка с ним делает.
///
/// Кнопка работает переключателем: нажатие применяет назначение к роутингу
/// стрипа, повторное нажатие снимает и возвращает то, что было до первого
/// нажатия. Состояние «нажата» и базовый роутинг живут не здесь, а в
/// <see cref="InputChannelModel"/> (<c>EngagedFunc</c>,
/// <c>FuncBaseRouting</c>) и принадлежат владельцу стрипа: активной может быть
/// только одна кнопка, а база — одна на стрип, а не на кнопку.
///
/// Ключевое решение: назначение и роутинг разведены. Отметки в попапе только
/// пишут заготовку в модель, а маршруты стрипа трогает <see cref="TurnOn"/> —
/// иначе снятая галка с единственного выхода молча убрала бы звук со стрипа, а
/// кнопка выглядела бы сломанной.
///
/// Два режима применения (<see cref="IsExclusive"/>): «только свои» — кнопка
/// приводит роутинг стрипа ровно к своему списку (сценарий «в колонки» /
/// «в стрим» одним щелчком), и «дописать» — добавляет свои выходы, чужие не
/// трогая.
/// </summary>
public partial class FuncButtonViewModel : LocalizedViewModel
{
    /// <summary>Сколько кнопок FUNC на стрипе (разметка и модель считают так же).</summary>
    public const int SlotCount = InputChannelModel.FuncButtonSlotCount;

    /// <summary>Длиннее подпись в кнопку не влезает, да и смысла в ней нет.</summary>
    private const int MaxLabelLength = 12;

    private readonly FuncButtonModel _model;
    private readonly Func<string, bool> _isRouteEnabled;
    private readonly Func<FuncButtonViewModel, bool> _isEngaged;
    private readonly Action<FuncButtonViewModel, bool> _setEngaged;
    private readonly Action<FuncButtonViewModel> _onAssignmentChanged;
    private readonly Action<FuncButtonViewModel> _onLabelChanged;

    /// <summary>Номер слота кнопки (1 или 2) — входит в подпись по умолчанию.</summary>
    public int Slot { get; }

    /// <summary>Индекс слота в <c>InputChannelModel.FuncButtons</c> (0 или 1).</summary>
    public int Index => Slot - 1;

    /// <summary>Аппаратные выходы (OUT), доступные для назначения.</summary>
    public ObservableCollection<FuncTargetViewModel> HardwareTargets { get; } = new();

    /// <summary>Виртуальные выходы (VIRT), доступные для назначения.</summary>
    public ObservableCollection<FuncTargetViewModel> VirtualTargets { get; } = new();

    /// <summary>Подпись кнопки: своя метка или «F1»/«F2».</summary>
    public string ButtonText => string.IsNullOrWhiteSpace(Label) ? $"F{Slot}" : Label.Trim();

    /// <summary>
    /// Кнопка без назначения приглушена: нажать её нельзя, но назначать её
    /// нужно, а попап открывается только ПКМ по самой кнопке. Приглушение
    /// намекает на это, вместо того чтобы гасить кнопку (у отключённой
    /// элемента не приходит ПКМ, и назначить выходы станет негде).
    /// </summary>
    public double ButtonOpacity => CanApply ? 1d : 0.45d;

    /// <summary>Заголовок попапа настройки.</summary>
    public string PopupTitle => Loc.Get("Sm.Func.PopupTitle", ButtonText);

    /// <summary>Подсказка кнопки на стрипе: что назначено и что она сделает.</summary>
    public string ToolTip
    {
        get
        {
            if (SelectedCount == 0) return Loc.Get("Sm.Func.ButtonEmpty", ButtonText);

            string assigned = string.Join(", ", AssignedTitles);
            return IsEngaged
                ? Loc.Get("Sm.Func.ButtonOn", ButtonText, assigned)
                : Loc.Get("Sm.Func.ButtonOff", ButtonText, assigned);
        }
    }

    /// <summary>Сколько выходов отмечено в назначении.</summary>
    public int SelectedCount { get; private set; }

    public bool HasTargets => SelectedCount > 0;

    /// <summary>Кнопку без назначения нажимать нечего — она отключена в UI.</summary>
    public bool CanApply => SelectedCount > 0;

    /// <summary>
    /// true — кнопка нажата и её правило применено к роутингу стрипа. Пока она
    /// нажата, роутинг принадлежит ей: повторное нажатие возвращает то, что
    /// было до первого, и снимает кнопку.
    ///
    /// Активной может быть только одна кнопка стрипа, поэтому состояние хранится
    /// не здесь, а у владельца стрипа (<c>InputChannelModel.EngagedFunc</c>).
    /// </summary>
    public bool IsEngaged
    {
        get => _isEngaged(this);
        set => _setEngaged(this, value);
    }

    /// <summary>Подсказка под списками выходов в попапе.</summary>
    public string EmptyHint => HardwareTargets.Count == 0 && VirtualTargets.Count == 0
        ? Loc.Get("Sm.Func.NoOutputs")
        : SelectedCount == 0
            ? Loc.Get("Sm.Func.NotAssigned")
            : "";

    public bool ShowEmptyHint => EmptyHint.Length > 0;

    /// <summary>Пояснение, пока кнопка нажата: роутинг стрипа принадлежит ей.</summary>
    public string EngagedHint => IsEngaged ? Loc.Get("Sm.Func.EngagedHint") : "";

    public bool ShowEngagedHint => IsEngaged;

    /// <summary>Своя подпись вместо «F1»/«F2».</summary>
    [ObservableProperty]
    private string _label;

    /// <summary>true — кнопка приводит роутинг стрипа ровно к своему списку.</summary>
    [ObservableProperty]
    private bool _isExclusive;

    /// <summary>
    /// Правило применено или снято: попап пора закрыть, чтобы был виден
    /// результат (подсвеченная кнопка и метры стрипа).
    /// </summary>
    public event EventHandler? StateToggled;

    public FuncButtonViewModel(
        int slot,
        FuncButtonModel model,
        IReadOnlyList<OutputBusModel> buses,
        Func<string, bool> isRouteEnabled,
        Func<FuncButtonViewModel, bool> isEngaged,
        Action<FuncButtonViewModel, bool> setEngaged,
        Action<FuncButtonViewModel> onAssignmentChanged,
        Action<FuncButtonViewModel> onLabelChanged)
    {
        Slot = slot;
        _model = model;
        _isRouteEnabled = isRouteEnabled;
        _isEngaged = isEngaged;
        _setEngaged = setEngaged;
        _onAssignmentChanged = onAssignmentChanged;
        _onLabelChanged = onLabelChanged;

        _label = model.Label ?? "";
        _isExclusive = model.Exclusive;

        foreach (var bus in buses)
        {
            var target = new FuncTargetViewModel(
                bus.Id,
                bus.Name,
                bus.ChannelName,
                model.BusIds.Contains(bus.Id),
                OnTargetToggled);

            if (CablePairing.IsVirtualCableName(bus.Name)) VirtualTargets.Add(target);
            else HardwareTargets.Add(target);
        }

        RefreshState();
    }

    /// <summary>Id выходов, отмеченных в назначении.</summary>
    public HashSet<string> SelectedBusIds { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Нажать кнопку: применить назначение к роутингу стрипа. Роутинг меняет
    /// владелец стрипа (<c>InputChannelViewModel</c>): он один знает про шины,
    /// базовый роутинг и про то, как об этом узнаёт движок.
    /// </summary>
    [RelayCommand]
    private void TurnOn()
    {
        if (!CanApply || IsEngaged) return;
        IsEngaged = true;
        StateToggled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Снять кнопку: вернуть стрип к роутингу, который был до нажатия.</summary>
    [RelayCommand]
    private void TurnOff()
    {
        if (!IsEngaged) return;
        IsEngaged = false;
        StateToggled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Снимает всё назначение (не трогая роутинг стрипа).</summary>
    [RelayCommand]
    private void Clear()
    {
        if (SelectedCount == 0) return;

        // Отметки снимаются через сеттер: каждый вызов сам пишет модель и
        // пересчитывает кнопку, отдельного сохранения здесь не нужно.
        foreach (var target in AllTargets) target.IsSelected = false;
    }

    /// <summary>
    /// Пересчитывает состояние кнопки: назначение, «идёт ли стрип в этот
    /// выход» в попапе и подписи. Зовётся после любого изменения роутинга
    /// стрипа, состава шин и после смены языка.
    /// </summary>
    public void RefreshState()
    {
        var targets = AllTargets;

        SelectedBusIds.Clear();
        foreach (var target in targets.Where(t => t.IsSelected)) SelectedBusIds.Add(target.BusId);
        SelectedCount = SelectedBusIds.Count;

        foreach (var target in targets) target.IsActive = _isRouteEnabled(target.BusId);

        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(ButtonOpacity));
        OnPropertyChanged(nameof(PopupTitle));
        OnPropertyChanged(nameof(ToolTip));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasTargets));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(IsEngaged));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(ShowEmptyHint));
        OnPropertyChanged(nameof(EngagedHint));
        OnPropertyChanged(nameof(ShowEngagedHint));
    }

    /// <summary>Канал на стрипе выхода переименован — обновляем подписи в попапе.</summary>
    public void UpdateBusChannelName(string busId, string? channelName)
    {
        foreach (var target in AllTargets)
            if (target.BusId == busId) target.SetChannelName(channelName);

        OnPropertyChanged(nameof(ToolTip));
    }

    protected override void OnLanguageChangedCore()
    {
        OnPropertyChanged(nameof(PopupTitle));
        OnPropertyChanged(nameof(ToolTip));
        OnPropertyChanged(nameof(EmptyHint));
        OnPropertyChanged(nameof(ShowEmptyHint));
        OnPropertyChanged(nameof(EngagedHint));
    }

    /// <summary>
    /// Отметка в списке — это правка заготовки: пишем её в модель и пересчитываем
    /// кнопку. Роутинг стрипа при этом не меняется, пока кнопку не нажали.
    /// </summary>
    private void OnTargetToggled(FuncTargetViewModel target)
    {
        SyncModel();
        RefreshState();
        _onAssignmentChanged(this);
    }

    /// <summary>
    /// Переносит отметки из списка в модель. Заодно выкидывает Id шин, которых
    /// больше нет (устройство скрыто или отключено) — иначе кнопка осталась бы
    /// «назначена» в выход, которого нет, и нажатие выглядело бы пустым.
    /// </summary>
    private void SyncModel()
    {
        var assigned = AllTargets
            .Where(t => t.IsSelected)
            .Select(t => t.BusId)
            .ToList();

        _model.BusIds.Clear();
        _model.BusIds.AddRange(assigned);
    }

    partial void OnLabelChanged(string value)
    {
        if (value.Length > MaxLabelLength)
        {
            Label = value[..MaxLabelLength];
            return;
        }

        _model.Label = value;
        OnPropertyChanged(nameof(ButtonText));
        OnPropertyChanged(nameof(PopupTitle));
        OnPropertyChanged(nameof(ToolTip));
        // Метка не трогает роутинг, поэтому нажатую кнопку не снимаем.
        _onLabelChanged(this);
    }

    partial void OnIsExclusiveChanged(bool value)
    {
        _model.Exclusive = value;
        RefreshState();
        _onAssignmentChanged(this);
    }

    private IEnumerable<FuncTargetViewModel> AllTargets => HardwareTargets.Concat(VirtualTargets);

    private IEnumerable<string> AssignedTitles =>
        AllTargets.Where(t => t.IsSelected).Select(t => t.Title);
}
