namespace ReportService.Contracts;

public sealed record ReportResponse(
    int Id,
    int ClubId,
    string ClubName,
    string Period,
    string ReportType,
    string Tag,
    string Status,
    int CreatedByUserId,
    DateOnly DueDate,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? ReviewedAtUtc,
    int Version,
    IReadOnlyCollection<ReportDetailResponse> Details,
    IReadOnlyCollection<ReportAttachmentResponse> Attachments,
    IReadOnlyCollection<ReportFeedbackResponse> Feedback);

public sealed record ReportDetailResponse(
    int Id,
    string ActivityName,
    DateOnly ActivityDate,
    string Description,
    int ParticipantCount,
    string Outcome);

public sealed record ReportAttachmentResponse(
    int Id,
    int? ReportDetailId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string StoragePath,
    DateTimeOffset UploadedAtUtc);

public sealed record ReportFeedbackResponse(
    int Id,
    int ReviewerUserId,
    string ReviewerName,
    string Decision,
    string Message,
    DateTimeOffset CreatedAtUtc);

public sealed record CreateReportRequest(
    int ClubId,
    string ClubName,
    string Period,
    string? ReportType,
    string? Tag,
    DateOnly DueDate,
    IReadOnlyCollection<UpsertReportDetailRequest> Details);

public sealed record UpdateReportRequest(
    string Period,
    string? ReportType,
    string? Tag,
    DateOnly DueDate,
    IReadOnlyCollection<UpsertReportDetailRequest> Details);

public sealed record UpsertReportDetailRequest(
    int? Id,
    string ActivityName,
    DateOnly ActivityDate,
    string Description,
    int ParticipantCount,
    string Outcome);

public sealed record AddAttachmentRequest(
    int? ReportDetailId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string StoragePath);

public sealed record ReviewRequest(string? Feedback);

public sealed record DeadlineRequest(string Period, DateOnly DueDate, bool IsActive);

public sealed record ReportSummaryResponse(
    int Total,
    int Draft,
    int Submitted,
    int UnderReview,
    int Approved,
    int Rejected,
    int Overdue);

public sealed record AggregationResponse(
    string? Period,
    int ApprovedReports,
    int TotalActivities,
    int TotalParticipants,
    IReadOnlyCollection<ClubAggregationRow> Clubs);

public sealed record ClubAggregationRow(
    int ClubId,
    string ClubName,
    int ApprovedReports,
    int Activities,
    int Participants);

public sealed record KpiLeaderboardResponse(
    string? Period,
    DateTimeOffset CalculatedAtUtc,
    IReadOnlyCollection<KpiLeaderboardRow> Clubs);

public sealed record KpiLeaderboardRow(
    int Rank,
    int ClubId,
    string ClubName,
    decimal Points,
    int ApprovedReports,
    int Activities,
    int Participants,
    int RejectedReports,
    int OverdueReports);

public sealed record KpiRuleResponse(
    string Code,
    string Name,
    decimal Points,
    string Description);
