namespace SoundMeeter.Docking.Entities
{
    /// <summary>
    /// Настройки док-панели SoundMeeter в OBS. Док — это браузерная страница,
    /// которую приложение само отдаёт по http://127.0.0.1:Port, а OBS открывает
    /// как Browser Dock (View → Docks). Никаких плагинов OBS не требуется.
    ///
    /// Хранится в settings.json рядом с остальными настройками, поэтому переживает
    /// перезапуск приложения.
    /// </summary>
    public class ObsDockSettings
    {
        /// <summary>Сервер дока поднимать при старте приложения.</summary>
        public bool Enabled { get; set; }

        /// <summary>Порт локального сервера. 0 — выбрать свободный автоматически.</summary>
        public int Port { get; set; } = DefaultPort;

        /// <summary>
        /// Показывать в доке все входные стрипы. Список каналов ниже добавляется
        /// к этому перечню, поэтому «все входы» и пару нужных выходов можно
        /// получить одним движением.
        /// </summary>
        public bool ShowAllInputs { get; set; } = true;

        /// <summary>Показывать в доке все выходные шины.</summary>
        public bool ShowAllOutputs { get; set; }

        /// <summary>Явно выбранные каналы (дополняют ShowAllInputs/ShowAllOutputs).</summary>
        public List<ObsDockChannelRef> Channels { get; set; } = new();

        /// <summary>Порт по умолчанию (тот же, что в прошлых сборках не использовался).</summary>
        public const int DefaultPort = 17954;

        /// <summary>
        /// Порт, на котором реально работает сервер: настройка, но с подстановкой
        /// значения по умолчанию, если в файле лежит мусор.
        /// </summary>
        public int EffectivePort => Port is > 0 and < 65536 ? Port : DefaultPort;
    }
}
