using SoundMeeter.TextToSpeech.Entities;
using SoundMeeter.Twitch.Entities;
using System;
using System.Collections.Generic;
using System.Text;

namespace SoundMeeter.Twitch.Contract
{
    public interface ITwitchService : IDisposable
    {
        // === Состояние ===
        TwitchStreamState StreamState { get; }
        bool IsChatConnected { get; }
        bool IsAuthorized { get; }
        string AuthorizedLogin { get; }
        string LastError { get; }

        // Примечание: если TwitchSettings мутабелен, лучше вернуть IReadOnly-интерфейс, 
        // чтобы потребители не могли менять настройки в обход ApplySettings.
        TwitchSettings Settings { get; }

        // === События ===
        event Action<ChatMessage> MessageReceived;
        event Action StatusChanged;

        // === Управление ===
        Task RestoreAsync(CancellationToken cancellationToken = default);
        Task<DeviceCodeChallenge?> BeginLoginAsync(CancellationToken cancellationToken = default);
        Task<DeviceAuthResult> ContinueLoginAsync(DeviceCodeChallenge challenge, CancellationToken cancellationToken = default);
        Task LogoutAsync(CancellationToken cancellationToken = default);
        void ApplySettings();
    }
}
