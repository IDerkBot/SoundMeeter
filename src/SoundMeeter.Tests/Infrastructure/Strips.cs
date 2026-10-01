using SoundMeeter.Models;
using SoundMeeter.Services;
using SoundMeeter.ViewModels;

namespace SoundMeeter.Tests.Infrastructure;

/// <summary>
/// Общие заготовки стрипов и шин. Тесты про роутинг, эффекты и MIDI строят одну и
/// ту же ленту из двух аппаратных выходов и одного виртуального кабеля: третий
/// выход нужен, чтобы отличать «в колонки» от «в кабель» и проверять
/// аддитивный режим кнопки FUNC.
/// </summary>
public static class Strips
{
    public const string SpeakerBusId = "b1";
    public const string HeadphonesBusId = "b2";
    public const string CableBusId = "v1";

    public static List<OutputBusModel> Buses() =>
    [
        new() { Id = SpeakerBusId, Name = "Speakers (Realtek)", ChannelName = "" },
        new() { Id = HeadphonesBusId, Name = "Headphones", ChannelName = "" },
        new() { Id = CableBusId, Name = "CABLE-A Output (VB-Audio Virtual Cable)", ChannelName = "Stream" }
    ];

    /// <summary>Входной стрип-микрофон: с крутилкой усиления и тремя выходами.</summary>
    public static InputChannelViewModel Microphone(
        FakeAudioEngine engine,
        List<OutputBusModel>? buses = null,
        Action? markDirty = null)
    {
        buses ??= Buses();
        var model = new InputChannelModel
        {
            Id = "i1",
            Name = "Mic",
            DeviceId = "mic-device",
            IsMicrophone = true,
            BusRouting = buses.ToDictionary(b => b.Id, _ => new BusRouting())
        };

        return new InputChannelViewModel(
            model, engine, buses, new List<DeviceInfo>(), markDirty ?? (() => { }));
    }

    /// <summary>
    /// Роутинг стрипа одной строкой: «b1=1 b2=0 v1=0». Сравнение строк вместо
    /// проверки отдельных флагов — в роутинге важно всё сразу: правило кнопки
    /// может снять один выход и не тронуть два других.
    /// </summary>
    public static string Routing(InputChannelViewModel vm) =>
        string.Join(" ", vm.HardwareOutputs.Concat(vm.VirtualOutputs)
            .Select(o => $"{o.BusId}={(o.IsEnabled ? 1 : 0)}"));

    /// <summary>Кнопка FUNC по индексу слота (0 или 1).</summary>
    public static FuncButtonViewModel Func(InputChannelViewModel vm, int index) =>
        index == 0 ? vm.Func1 : vm.Func2;

    /// <summary>Нажать кнопку FUNC так же, как это делает ToggleButton в UI.</summary>
    public static void Engage(InputChannelViewModel vm, int index) => Func(vm, index).IsEngaged = true;

    /// <summary>Снять кнопку FUNC.</summary>
    public static void Disengage(InputChannelViewModel vm, int index) => Func(vm, index).IsEngaged = false;

    /// <summary>Назначение кнопки FUNC1 на виртуальный кабель, FUNC2 — на оба OUT.</summary>
    public static void AssignDefaults(InputChannelViewModel vm)
    {
        vm.Func1.VirtualTargets[0].IsSelected = true;
        vm.Func2.HardwareTargets[0].IsSelected = true;
        vm.Func2.HardwareTargets[1].IsSelected = true;
    }
}
