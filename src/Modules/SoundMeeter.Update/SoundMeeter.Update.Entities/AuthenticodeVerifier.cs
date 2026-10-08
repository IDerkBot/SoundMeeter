using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SoundMeeter.Services;

/// <summary>
/// Результат сверки подписи Authenticode файла.
/// </summary>
internal enum SignatureState
{
    /// <summary>Подписи нет (обычное состояние сборок без сертификата).</summary>
    Unsigned,

    /// <summary>Подпись есть, и цепочка доверия строится.</summary>
    Valid,

    /// <summary>Подпись есть, но цепочку доверия построить не удалось.</summary>
    Invalid
}

/// <summary>
/// Проверка подписи Authenticode файла перед автообновлением.
///
/// Почему без WinVerifyTrust: «настоящая» проверка через wintrust.dll требует
/// системного поставщика политики подписи, и на части машин он недоступен —
/// вызов возвращает TRUST_E_PROVIDER_UNKNOWN даже для файлов, подписанных
/// Microsoft, а X509Certificate.CreateFromSignedFile на них падает с
/// CryptographicException («нет подписи»). На такой машине любая строгая
/// проверка объявила бы нашу сборку недействительной и заблокировала бы
/// обновление целиком.
///
/// Поэтому здесь полностью управляемый путь: сертификат подписи извлекается из
/// файла, и цепочка доверия строится вручную (проверка отзыва отключена:
/// обновление скачивается до того, как пользователь получил доступ к сети, и её
/// обрыв превращался бы в ложный отказ).
///
/// Важно: «подпись не удалось прочитать» и «подписи нет» — это <b>Unsigned</b>,
/// а не <b>Invalid</b>. Отказ вызывается только тем, что подпись действительно
/// прочитана и цепочка не строится. Иначе сбой подписной инфраструктуры на
/// машине пользователя ломал бы обновление там, где всё в порядке.
/// </summary>
internal static class AuthenticodeVerifier
{
    /// <summary>
    /// Возвращает состояние подписи, Subject подписанта и пояснение.
    /// Исключения наружу не бросает: невозможность прочитать подпись — это
    /// результат проверки, а не сбой приложения.
    /// </summary>
    public static (SignatureState State, string Subject, string Detail) Verify(string path)
    {
        byte[] signerBlob;
        try
        {
            // CreateFromSignedFile — единственный API, который умеет достать
            // сертификат подписанта из Authenticode-блока PE-файла; аналога
            // в X509CertificateLoader нет. Остальную работу с сертификатом
            // делаем современным загрузчиком.
#pragma warning disable SYSLIB0057
            using var embedded = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            signerBlob = embedded.GetRawCertData();
        }
        catch (CryptographicException)
        {
            return (SignatureState.Unsigned, "", Loc.Get("Sm.Signature.UnsignedNone"));
        }
        catch (Exception ex)
        {
            return (SignatureState.Unsigned, "", Loc.Get("Sm.Signature.UnsignedUnreadable", ex.Message));
        }

        try
        {
            using var signer = X509CertificateLoader.LoadCertificate(signerBlob);
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;

            if (chain.Build(signer))
                return (SignatureState.Valid, signer.Subject, Loc.Get("Sm.Signature.Trusted"));

            // Статусы цепочки — перечисление .NET, его строки одинаковы в обоих
            // языках и служат точным диагностическим признаком, поэтому берём как есть.
            var problems = chain.ChainStatus.Length == 0
                ? Loc.Get("Sm.Signature.UntrustedRoot")
                : string.Join("; ", chain.ChainStatus.Select(s => s.Status.ToString()));
            return (SignatureState.Invalid, signer.Subject, problems);
        }
        catch (Exception ex)
        {
            return (SignatureState.Invalid, "", Loc.Get("Sm.Signature.CheckFailed", ex.Message));
        }
    }
}