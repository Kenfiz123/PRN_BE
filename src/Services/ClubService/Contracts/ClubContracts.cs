using System.ComponentModel.DataAnnotations;

namespace ClubService.Contracts;

public sealed record ClubResponse(
    int Id,
    string Code,
    string Name,
    string Description,
    string ContactEmail,
    string ContactPhone,
    bool IsActive,
    IReadOnlyCollection<ManagerAssignmentResponse> Managers,
    IReadOnlyCollection<ClubMembershipResponse> Members);

public sealed record ManagerAssignmentResponse(
    int Id,
    int ManagerUserId,
    string ManagerName,
    DateTimeOffset AssignedAtUtc,
    DateTimeOffset? EndedAtUtc,
    bool IsActive);

public sealed record ClubMembershipResponse(
    int Id,
    int ClubId,
    string ClubName,
    int UserId,
    string FullName,
    string Role,
    string Status,
    string? RequestMessage,
    string PersonalInfo,
    string Goals,
    string Reason,
    string? ReviewNote,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? ReviewedAtUtc,
    int? ReviewedByUserId);

public sealed record ClubAccessResponse(
    int ClubId,
    string ClubName,
    bool IsManager,
    bool IsTreasurer,
    bool IsApprovedMember,
    IReadOnlyCollection<int> ManagerUserIds);

public sealed record ClubCreationApplicationResponse(
    int Id,
    int RequesterUserId,
    string RequesterName,
    string Code,
    string Name,
    string Description,
    string Purpose,
    string Reason,
    string ContactEmail,
    string ContactPhone,
    string Status,
    string? ReviewNote,
    int? CreatedClubId,
    DateTimeOffset SubmittedAtUtc,
    DateTimeOffset? ReviewedAtUtc,
    int? ReviewedByUserId);

public sealed record CreateClubRequest(
    [StringLength(20)] string Code,
    [StringLength(200)] string Name,
    [StringLength(1000)] string Description,
    [StringLength(255), EmailAddress] string ContactEmail,
    [StringLength(20)] string ContactPhone);

public sealed record UpdateClubRequest(
    [StringLength(200)] string Name,
    [StringLength(1000)] string Description,
    [StringLength(255), EmailAddress] string ContactEmail,
    [StringLength(20)] string ContactPhone,
    bool IsActive);

public sealed record AssignManagerRequest(
    int ManagerUserId,
    [StringLength(200)] string ManagerName);

public sealed record JoinClubRequest(
    [StringLength(500)] string? Message,
    [StringLength(500)] string PersonalInfo,
    [StringLength(500)] string Goals,
    [StringLength(500)] string Reason);

public sealed record ReviewClubMembershipRequest([StringLength(1000)] string? Note);

public sealed record AssignTreasurerRequest(
    int MemberUserId,
    [StringLength(200)] string MemberName);

public sealed record CreateClubApplicationRequest(
    [StringLength(20)] string Code,
    [StringLength(200)] string Name,
    [StringLength(1000)] string Description,
    [StringLength(500)] string Purpose,
    [StringLength(500)] string Reason,
    [StringLength(255), EmailAddress] string ContactEmail,
    [StringLength(20)] string ContactPhone);

public sealed record ReviewClubApplicationRequest([StringLength(1000)] string? Note);
