namespace ExportService.Contracts;

public sealed record CreateExportRequest(
    string ExportType,
    string Scope,
    string? Period,
    int? ClubId);

public sealed record ExportResponse(
    int Id,
    string ExportType,
    string Scope,
    string Status,
    string? Period,
    int? ClubId,
    int RequestedByUserId,
    string RequestedByName,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ErrorMessage,
    ExportFileResponse? File);

public sealed record ExportFileResponse(
    int Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    DateTimeOffset ExpiresAtUtc,
    string Checksum,
    bool IsAvailable);
