namespace SoundMeeter.Docking.Entities
{
    /// <summary>
    /// Ссылка на стрип микшера в списке каналов дока. Хранится и Id стрипа, и
    /// DeviceId устройства: движок при повторном обнаружении устройств пересоздаёт
    /// стрип с новым Id, и по одному лишь Id выбранный канал молча исчез бы из дока.
    /// Привязка к устройству переживает пересоздание стрипа.
    /// </summary>
    public class ObsDockChannelRef
    {
        /// <summary>Id стрипа микшера (InputChannelModel.Id / OutputBusModel.Id).</summary>
        public string StripId { get; set; } = "";

        /// <summary>DeviceId устройства стрипа (может быть пустым у неназначенного стрипа).</summary>
        public string DeviceId { get; set; } = "";

        /// <summary>Kind входа (in) или выхода (out).</summary>
        public string Kind { get; set; } = ObsDockChannels.Input;

        public ObsDockChannelRef() { }

        public ObsDockChannelRef(string stripId, string deviceId, string kind)
        {
            StripId = stripId;
            DeviceId = deviceId;
            Kind = kind;
        }

        public ObsDockChannelRef Clone() => new(StripId, DeviceId, Kind);
    }
}
