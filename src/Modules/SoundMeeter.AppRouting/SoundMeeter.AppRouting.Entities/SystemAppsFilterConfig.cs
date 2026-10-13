namespace SoundMeeter.AppRouting.Entities;

public class SystemAppsFilterConfig
{
    public List<string> SystemNameKeywords { get; set; } = new();
    public List<string> SystemPublisherKeywords { get; set; } = new();
}
