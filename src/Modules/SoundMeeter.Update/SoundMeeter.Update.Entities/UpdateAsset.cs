using System.Globalization;

namespace SoundMeeter.Update.Entities
{
    /// <summary>Файл релиза, пригодный для автообновления (portable zip).</summary>
    public sealed class UpdateAsset
    {
        public string Name { get; init; } = "";
        public string DownloadUrl { get; init; } = "";
        public long Size { get; init; }

        public string HumanSize
        {
            get
            {
                double size = Size;
                string[] units = ["B", "KB", "MB", "GB"];
                var unit = 0;
                while (size >= 1024 && unit < units.Length - 1)
                {
                    size /= 1024;
                    unit++;
                }

                return unit == 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{Size} {units[unit]}")
                    : string.Create(CultureInfo.InvariantCulture, $"{size:0.#} {units[unit]}");
            }
        }
    }
}
