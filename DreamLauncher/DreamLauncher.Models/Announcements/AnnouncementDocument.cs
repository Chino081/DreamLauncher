namespace DreamLauncher.Models.Announcements;

public sealed class AnnouncementDocument
{
    public string Title { get; set; } = "服务器公告";

    public List<AnnouncementItem> Items { get; set; } = [];
}
