namespace SoundMeeter.Docking.Entities
{
    /// <summary>
    /// Протокол локального сервера дока (ObsDockServer).
    ///
    /// Это данные пересылки, а не поведение: снимок состояния уходит панели,
    /// команда возвращается обратно. Отдельный слой (<c>Docking.Entities</c>) —
    /// как <c>Update.Entities</c> и <c>Twitch.Entities</c>: знать эти типы должны
    /// контракт сервера, сам сервер и проверки, но не Core, не Audio и не Update.
    ///
    /// Состояние идёт от приложения к доку (push ~30 Гц) и содержит ровно то,
    /// что нужно панели: метры, фейдеры и кнопки. Формат — плоский JSON: его
    /// читает и страница в OBS, и наш харнесс.
    ///
    /// Сверка состояния идёт одним объектом (не дельтами): панель маленькая,
    /// пакет занимает единицы килобайт, зато док сам восстанавливается после
    /// любого потерянного сообщения и не хранит предыдущее состояние.
    ///
    /// Маркеры «вход»/«выход» (<see cref="ObsDockChannels"/>) остались в
    /// Core.Contract: на них держится <c>ObsDockChannelRef.Kind</c> в настройках,
    /// и увести их сюда значило бы заставить Core знать про док.
    /// </summary>
    public sealed class ObsDockChannelState
    {
        public string Id { get; init; } = "";
        public string Kind { get; init; } = ObsDockChannels.Input;
        public string Name { get; init; } = "";
        public string DeviceId { get; init; } = "";
        public string DeviceName { get; init; } = "";
        public float VolumeDb { get; init; }
        public float Peak { get; init; }
        public bool IsMuted { get; init; }
        public bool IsSolo { get; init; }
        public bool IsMono { get; init; }
        public bool IsAvailable { get; init; } = true;

        public ObsDockChannelState() { }

        public ObsDockChannelState(string id, string kind, string name, string deviceId, string deviceName,
            float volumeDb, float peak, bool isMuted, bool isSolo, bool isMono, bool isAvailable)
        {
            Id = id;
            Kind = kind;
            Name = name;
            DeviceId = deviceId;
            DeviceName = deviceName;
            VolumeDb = volumeDb;
            Peak = peak;
            IsMuted = isMuted;
            IsSolo = isSolo;
            IsMono = isMono;
            IsAvailable = isAvailable;
        }
    }
}
