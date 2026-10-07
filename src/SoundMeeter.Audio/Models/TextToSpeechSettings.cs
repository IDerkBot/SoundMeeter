namespace SoundMeeter.Models;

/// <summary>
/// Настройки модуля синтеза речи (SM-E01). Лежат в settings.json рядом с
/// остальными, поэтому переживают перезапуск приложения.
///
/// Говорят они и про то, куда уходит голос, и о том, как его читать из чата.
/// Второе — подготовка: сам подключения к чату Twitch здесь нет (для него есть
/// <c>ITtsMessageSource</c>), но разбор команд уже готов, и подключение к
/// Twitch — это только источник сообщений, а не переработка всей этой логики.
/// </summary>
public class TextToSpeechSettings
{
    /// <summary>
    /// Модуль включён: принимает сообщения и озвучивает их. Выключатель такой же
    /// по смыслу, как «сервер дока» в OBS — модуль поднимается вместе с
    /// приложением и живёт, пока его не выключили.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Id входного стрипа, в который уходит синтезированный голос.
    /// Пусто — модуль не знает, куда говорить, и молчит (озвучивание при этом
    /// можно держать включённым: как только канал выбран, заработает).
    /// </summary>
    public string TargetInputId { get; set; } = "";

    /// <summary>
    /// DeviceId устройства целевого стрипа. Держим рядом с Id, как это делает
    /// док OBS: движок пересоздаёт стрипы при обновлении настроек, и по одному
    /// лишь Id выбранный канал молча сбросился бы на «не выбран».
    /// </summary>
    public string TargetInputDeviceId { get; set; } = "";

    /// <summary>
    /// Имя голоса по умолчанию (SAPI). Пусто — взять лучший доступный для
    /// языка модуля, см. <see cref="Limits.RussianCulture"/>.
    /// </summary>
    public string DefaultVoice { get; set; } = "";

    /// <summary>
    /// Голос отдельного пользователя чата: логин без «@» → имя голоса.
    /// Так модераторы на разных голосах, и по звуку слышно, кто написал.
    /// </summary>
    public List<TtsUserVoice> UserVoices { get; set; } = new();

    /// <summary>Пользователи, чьи сообщения модуль не читает никогда.</summary>
    public List<string> IgnoredUsers { get; set; } = new();

    /// <summary>Темп речи, −10…+10 (как в SAPI: 0 — обычный голос).</summary>
    public int Rate { get; set; }

    /// <summary>Громкость синтеза, 0…100.</summary>
    public int Volume { get; set; } = 100;

    /// <summary>Команда озвучки в чате: «{команда} привет стример».</summary>
    public string SpeakCommand { get; set; } = DefaultSpeakCommand;

    /// <summary>Команда выбора голоса: «{команда} имя_голоса».</summary>
    public string VoiceCommand { get; set; } = DefaultVoiceCommand;

    /// <summary>
    /// Разрешать ли смену голоса командой из чата. По умолчанию включено —
    /// это и есть «каждый модератор выбрал себе голос». Выключается, когда
    /// список голосов в чате лишний (агенты, реклама, спам).
    /// </summary>
    public bool AllowVoiceChange { get; set; } = true;

    /// <summary>
    /// Сколько сообщений может ждать озвучки. Очередь нужна, чтобы не терять
    /// сообщения, но её предел обязателен: без него рейд или бот заваливают
    /// микшер очередью на десятки минут, и к началу стрима она всё ещё не
    /// закончится.
    /// </summary>
    public int MaxQueueLength { get; set; } = 10;

    /// <summary>
    /// Сколько сообщений в минуту принимается от одного пользователя. Защита от
    /// спама одним аккаунтом: «!tts» в цикле иначе занимает эфир.
    /// </summary>
    public int MaxMessagesPerMinute { get; set; } = 5;

    /// <summary>
    /// Обрезка слишком длинных реплик, символов. Длинный текст синтезатор
    /// читает несколько минут, и очередь встаёт.
    /// </summary>
    public int MaxMessageChars { get; set; } = 300;

    public const string DefaultSpeakCommand = "!tts";
    public const string DefaultVoiceCommand = "!ttsvoice";

    /// <summary>
    /// Границы значений. Заданы здесь, а не у мест использования, потому что
    /// их трогают три независимые стороны: миграция схемы приводит к ним файл,
    /// окно настрощивает по ним крутилки, а разбор команд режет по ним текст.
    /// Разъезд этих чисел означал бы, что интерфейс обещает одно, а разбор
    /// режет по другому.
    /// </summary>
    public static class Limits
    {
        public const int RateMin = -10;
        public const int RateMax = 10;
        public const int VolumeMin = 0;
        public const int VolumeMax = 100;
        public const int QueueMin = 1;
        public const int QueueMax = 100;
        public const int MessagesPerMinuteMin = 1;
        public const int MessagesPerMinuteMax = 60;
        public const int MessageCharsMin = 20;
        public const int MessageCharsMax = 2000;

        /// <summary>
        /// Язык, голос которого выбирается по умолчанию. Модуль задуман как
        /// русскоязычный, поэтому при пустом <see cref="DefaultVoice"/> берётся
        /// первый голос с этим <c>VoiceInfo.Culture</c>, а если такого нет —
        /// любой доступный, с предупреждением в журнале.
        /// </summary>
        public const string RussianCulture = "ru-RU";

        /// <summary>Длина имени голоса, символов: столько нужно SAPI.</summary>
        public const int VoiceNameMaxLength = 128;

        /// <summary>Длина логина чата, символов (правда Twitch — 25).</summary>
        public const int UserNameMaxLength = 64;
    }
}

/// <summary>
/// Голос, выбранный пользователем чата. Отдельный класс, а не пара в словаре,
/// потому что в settings.json нужен читаемый список, а не запись
/// <c>"login": {"voice": ...}</c>.
/// </summary>
public class TtsUserVoice
{
    /// <summary>Логин без «@». Сравнивается без учёта регистра.</summary>
    public string User { get; set; } = "";

    /// <summary>Имя голоса SAPI.</summary>
    public string Voice { get; set; } = "";

    public TtsUserVoice() { }

    public TtsUserVoice(string user, string voice)
    {
        User = user;
        Voice = voice;
    }

    public TtsUserVoice Clone() => new(User, Voice);
}
