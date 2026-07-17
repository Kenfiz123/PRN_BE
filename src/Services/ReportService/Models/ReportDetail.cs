namespace ReportService.Models;

public sealed class ReportDetail
{
    public int Id { get; set; }
    public int ReportId { get; set; }
    public Report Report { get; set; } = null!;
    public string ActivityName { get; set; } = string.Empty;
    public DateOnly ActivityDate { get; set; }
    public string Description { get; set; } = string.Empty;
    public int ParticipantCount { get; set; }
    public string Outcome { get; set; } = string.Empty;
}
