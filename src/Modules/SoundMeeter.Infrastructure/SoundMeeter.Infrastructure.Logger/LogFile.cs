namespace SoundMeeter.Infrastructure.Logger;

public class LogFile(string fullname)
{
    public string Fullname { get; set; } = fullname;
    public string Name { get; set; } = new FileInfo(fullname).Name;
}
