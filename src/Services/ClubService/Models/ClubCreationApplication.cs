namespace ClubService.Models;

public sealed class ClubCreationApplication
{
    public int Id { get; set; }
    public int RequesterUserId { get; set; }
    public string RequesterName { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string ContactEmail { get; set; } = string.Empty;
    public string ContactPhone { get; set; } = string.Empty;
    public string Status { get; set; } = ClubApplicationStatuses.Submitted;
    public string? ReviewNote { get; set; }
    public int? CreatedClubId { get; set; }
    public DateTimeOffset SubmittedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public int? ReviewedByUserId { get; set; }
}
