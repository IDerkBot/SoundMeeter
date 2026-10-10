using Microsoft.Extensions.Logging;
using SoundMeeter.Core.Contract;
using SoundMeeter.Infrastructure.Logger;
using SoundMeeter.Midi.Contract;
using SoundMeeter.Midi.Entities;

namespace SoundMeeter.Midi.UseCases;

/// <summary>
/// Единая точка входа модуля MIDI для остальных сборок.
///
/// Держит ровно две вещи, и обе — про то, кто и когда вызывает сценарии. Ни
/// логики привязок, ни знания о настройках здесь нет: она в сценариях.
///
/// Первое — подписка на MIDI-порт. Она нужна ровно одна на процесс: два
/// подписчика означали бы, что сообщение применяется дважды, и кнопка
/// переключалась бы за одно нажатие дважды.
///
/// Второе — маршалинг на UI-поток, и он не декоративный. MIDI-порт читает
/// СВОЙ поток, это не подстраховка: без диспетчера уведомления о свойствах
/// поднимутся не из UI-потока и привязка WPF упадёт на первом же движении
/// контроллера. Маршалинг живёт здесь, а не в сценарии, потому что это
/// единственное место, где известно, что модуль обслуживает UI.
///
/// Экземпляр создаётся вместе с <c>MainViewModel</c> (то есть на старте
/// приложения), поэтому подписка всегда жива, пока открыт микшер.
/// </summary>
public sealed class MidiController : IDisposable
{
    private readonly IMidiService _midi;
    private readonly IDispatcherService _dispatcher;
    private readonly RouteMidiMessageUseCase _route;
    private readonly ILogger _logger = AppLog.For<MidiController>();
    private bool _disposed;

    public MidiController(
        IMidiService midi,
        IDispatcherService dispatcher,
        RouteMidiMessageUseCase route)
    {
        _midi = midi;
        _dispatcher = dispatcher;
        _route = route;

        _midi.MessageReceived += OnMessageReceived;
    }

    /// <summary>Устройство ввода для старта приложения и импорта пресета.</summary>
    public IMidiService MidiService => _midi;

    private void OnMessageReceived(MidiMessageInfo message)
    {
        if (_dispatcher.HasThreadAccess) Apply(message);
        else _dispatcher.Post(() => Apply(message));
    }

    private void Apply(MidiMessageInfo message)
    {
        try
        {
            _route.Execute(message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Сценарий на MIDI-пачке исключением не роняет модуль, но и сюда
            // оно дойти не должно: в обработчике на UI-потоке стек уже потерян,
            // и «ошибка применения сообщения» ничего не объяснит. Поэтому
            // причина логируется здесь, целиком.
            _logger.LogError(ex, "MIDI: ошибка применения сообщения: {Message}", ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _midi.MessageReceived -= OnMessageReceived;
    }
}