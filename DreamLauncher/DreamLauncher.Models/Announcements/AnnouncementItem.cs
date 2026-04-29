namespace DreamLauncher.Models.Announcements;

public sealed class AnnouncementItem
{
    public string Title { get; set; } = "";

    public string Content { get; set; } = "";

    public DateOnly? Date { get; set; }
}
