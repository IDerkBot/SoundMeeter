using Microsoft.Extensions.DependencyInjection;
using SoundMeeter.Framework.Base.Modules;
using SoundMeeter.Midi.Contract;
using SoundMeeter.Midi.Contract.Implementation;
using SoundMeeter.Midi.UseCases;

namespace SoundMeeter.Midi;

/// <summary>
/// Состав модуля MIDI: что зарегистрировано и чем это должно пользоваться.
///
/// Всё, что здесь регистрируется, — singleton. Не из симпатии к одиночкам, а по
/// двум причинам, обе ломаются тихо.
///
/// Состояние. <see cref="MidiPressTracker"/> помнит нажатые кнопки и откатываемое
/// состояние PTT. Transient дал бы маршрутизации один трекер, а сбросу — другой,
/// и кнопка, зажатая по удержанию, откатывалась бы «не в том» экземпляре, то
/// есть никогда.
///
/// Привязки. <see cref="IMidiBindingStore"/> поверх настроек движка должен быть
/// один на процесс: два хранилища означали бы, что окно привязок и маршрутизация
/// видят разные настройки, и только что привязанная кнопка не сработала бы до
/// перезапуска приложения.
///
/// Сценарии регистрируются явно, хотя почти все stateless: незарегистрированный
/// сценарий — это класс, который компилируется, выглядит рабочим и молча не
/// используется. <c>SelectMidiDeviceUseCase</c> уже пролежал в проекте
/// незарегистрированным именно так.
/// </summary>
public class MidiModule : IModule
{
    public string Name => "MidiModule";

    public void RegisterServices(IServiceCollection services)
    {
        // Порт: единственный на процесс. Открыт он или нет — состояние IMidiService.
        services.AddSingleton<IMidiService, MidiService>();

        // Хранилище привязок: ViewModels больше не трогают настройки движка
        // напрямую, всё идёт через сценарии, а сценарии — через него.
        services.AddSingleton<IMidiBindingStore, MidiBindingStore>();

        services.AddSingleton<MidiStripAccessor>();
        services.AddSingleton<MidiPressTracker>();

        services.AddSingleton<ResetMidiTrackingUseCase>();
        services.AddSingleton<RouteMidiMessageUseCase>();
        services.AddSingleton<SelectMidiDeviceUseCase>();
        services.AddSingleton<BindMidiControlUseCase>();
        services.AddSingleton<UpdateMidiBindingUseCase>();
        services.AddSingleton<ClearMidiBindingUseCase>();

        // Подписка на MIDI-порт живёт здесь же и создаётся вместе с
        // MainViewModel — иначе порт открывался бы, а сообщения бы никто не слушал.
        services.AddSingleton<MidiController>();
    }
}