namespace ActivityService.Contracts;

public sealed record ActivityResponse(
    int Id,
    int ClubId,
    string ClubName,
    string Title,
    string Description,
    DateTimeOffset StartTimeUtc,
    DateTimeOffset EndTimeUtc,
    string Location,
    string Status,
    int CreatedByUserId,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyCollection<ActivityParticipantResponse> Participants);

public sealed record ActivityParticipantResponse(
    int Id,
    int UserId,
    string FullName,
    string AttendanceStatus,
    DateTimeOffset RegisteredAtUtc);

public sealed record CreateActivityRequest(
    int ClubId,
    string ClubName,
    string Title,
    string Description,
    DateTimeOffset StartTimeUtc,
    DateTimeOffset EndTimeUtc,
    string Location);

public sealed record UpdateActivityRequest(
    string Title,
    string Description,
    DateTimeOffset StartTimeUtc,
    DateTimeOffset EndTimeUtc,
    string Location,
    string Status);

public sealed record RegisterActivityParticipantRequest(int? UserId, string? FullName);
