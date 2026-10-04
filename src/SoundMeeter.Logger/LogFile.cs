namespace SoundMeeter.Services.Logging
{
    public class LogFile(string fullname)
    {
        public string Fullname { get; set; } = fullname;
        public string Name { get; set; } = new FileInfo(fullname).Name;
    }
}
