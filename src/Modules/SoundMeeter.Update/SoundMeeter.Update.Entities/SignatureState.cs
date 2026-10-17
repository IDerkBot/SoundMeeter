namespace SoundMeeter.Update.Entities;

/// <summary>
/// Результат сверки подписи Authenticode файла.
/// </summary>
public enum SignatureState
{
    /// <summary>Подписи нет (обычное состояние сборок без сертификата).</summary>
    Unsigned,

    /// <summary>Подпись есть, и цепочка доверия строится.</summary>
    Valid,

    /// <summary>Подпись есть, но цепочку доверия построить не удалось.</summary>
    Invalid
}
