namespace SoundMeeter.AppRouting.Contract;

/// <summary>
/// Перенаправление звука приложения на другой endpoint Windows.
/// </summary>
/// <remarks>
/// Существует ради одной причины: <c>AudioService</c> не должен знать про COM.
/// Реализация — это <c>RoGetActivationFactory</c> и <c>IAudioPolicyConfig</c>,
/// интероп с Windows, который нельзя ни подменить, ни проверить без системы.
/// С контрактом на руках проверка сценария не ходит в COM вовсе.
///
/// Раньше тип был статическим классом <c>AppRouter</c> в UseCases. Статика не
/// реализует интерфейс и не подменяется, поэтому её нельзя было проверить и
/// нельзя было отвязать от COM: ядро обработки звука тянуло модуль AppRouting
/// прямо себе в сборку, а сценариев у него не было вовсе.
/// </remarks>
public interface IAppRouter
{
    /// <summary>
    /// E_INVALIDARG: у процесса нет ни одного аудиопотока — перенаправлять
    /// нечего. Возвращается отдельно от прочих ошибок, чтобы вызывающий не
    /// сообщал пользователю о сбое там, где всё в порядке.
    /// </summary>
    const int ProcessNoAudio = unchecked((int)0x80070057);

    /// <summary>
    /// Endpoint, в который сейчас играет процесс, либо пустая строка, если он
    /// не играет никуда.
    /// </summary>
    string GetRouteEndpoint(uint processId, AudioFlow flow = AudioFlow.Render);

    /// <summary>
    /// Перенаправляет процесс на endpoint. 0 — успех,
    /// <see cref="ProcessNoAudio"/> — у процесса нет аудиопотока.
    /// </summary>
    int SetRoute(uint processId, string endpointId, AudioFlow flow = AudioFlow.Render);
}