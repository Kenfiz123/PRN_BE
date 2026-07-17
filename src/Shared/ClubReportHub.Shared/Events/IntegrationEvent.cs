namespace ClubReportHub.Shared.Events;

public abstract record IntegrationEvent(Guid EventId, DateTimeOffset OccurredAtUtc);

public sealed record ClubCreatedEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    int ClubId,
    string ClubCode,
    string ClubName)
    : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record UserRegisteredEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    int UserId,
    string Email,
    string FullName)
    : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record ActivityCreatedEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    int ActivityId,
    int ClubId,
    string ClubName,
    string Title,
    DateTimeOffset StartTimeUtc)
    : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record ReportSubmittedEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    int ReportId,
    int ClubId,
    string ClubName,
    string Period,
    int SubmittedByUserId,
    string Status,
    int? RecipientUserId)
    : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record ReportApprovedEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    int ReportId,
    int ClubId,
    string ClubName,
    string Period,
    int ApprovedByUserId,
    int RecipientUserId)
    : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record ReportRejectedEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    int ReportId,
    int ClubId,
    string ClubName,
    string Period,
    int RejectedByUserId,
    int RecipientUserId,
    string Feedback)
    : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record KpiCalculatedEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    int ClubId,
    string ClubName,
    string Period,
    decimal Points)
    : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record BudgetProposalSubmittedEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    int ProposalId,
    int ClubId,
    string ClubName,
    decimal RequestedAmount,
    int ProposedByUserId)
    : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record BudgetApprovedEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    int ProposalId,
    int ClubId,
    string ClubName,
    decimal ApprovedAmount,
    int ApprovedByUserId,
    int RecipientUserId)
    : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record SettlementOverdueEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    int ProposalId,
    int ClubId,
    string ClubName)
    : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record ExportRequestedEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    int ExportRequestId,
    string ExportType,
    string Scope,
    int RequestedByUserId)
    : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record ExportCompletedEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    int ExportRequestId,
    string ExportType,
    string FileName,
    int RequestedByUserId)
    : IntegrationEvent(EventId, OccurredAtUtc);

public sealed record ReportDeadlineReminderEvent(
    Guid EventId,
    DateTimeOffset OccurredAtUtc,
    string Period,
    DateOnly DueDate,
    IReadOnlyCollection<int> MissingClubIds)
    : IntegrationEvent(EventId, OccurredAtUtc);
