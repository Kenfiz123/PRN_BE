namespace FinanceService.Models;

public sealed class BudgetProposal
{
    public int Id { get; set; }
    public int ClubId { get; set; }
    public string ClubName { get; set; } = string.Empty;
    public int? ActivityId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal RequestedAmount { get; set; }
    public decimal? ApprovedAmount { get; set; }
    public string Status { get; set; } = FinanceStatuses.Submitted;
    public int ProposedByUserId { get; set; }
    public DateTimeOffset ProposedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int? ReviewedByUserId { get; set; }
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    public string? ReviewNote { get; set; }
    public ICollection<Settlement> Settlements { get; set; } = [];
}
