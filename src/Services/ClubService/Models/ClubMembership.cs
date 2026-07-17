namespace ClubService.Models;

public sealed class ClubMembership
{
    public int Id { get; set; }
    public int ClubId { get; set; }
    public Club Club { get; set; } = null!;
    public int UserId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Role { get; set; } = ClubMemberRoles.Member;
    public string Status { get; set; } = ClubMembershipStatuses.Pending;
    public string? RequestMessage { get; set; }
    public string PersonalInfo { get; set; } = string.Empty;
    public string Goals { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string? ReviewNote { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public int? ReviewedByUserId { get; set; }
}
