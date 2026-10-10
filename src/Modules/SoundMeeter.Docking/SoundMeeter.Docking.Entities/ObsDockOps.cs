namespace SoundMeeter.Docking.Entities
{
    /// <summary>Операции, которые док может прислать приложению.</summary>
    public static class ObsDockOps
    {
        /// <summary>Громкость стрипа, дБ (value).</summary>
        public const string Volume = "vol";

        /// <summary>Mute (value = true/false).</summary>
        public const string Mute = "mute";

        /// <summary>Solo (value = true/false).</summary>
        public const string Solo = "solo";

        /// <summary>Mono (value = true/false).</summary>
        public const string Mono = "mono";
    }
}
