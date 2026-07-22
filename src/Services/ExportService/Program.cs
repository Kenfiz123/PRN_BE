using System.Security.Claims;
using System.Text.Json;
using ClubReportHub.Shared.Auth;
using ClubReportHub.Shared.Data;
using ClubReportHub.Shared.Events;
using ClubReportHub.Shared.Messaging;
using ExportService.Contracts;
using ExportService.Data;
using ExportService.Models;
using ExportService.Services;
using Hangfire;
using Hangfire.Common;
using Hangfire.SqlServer;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<ExportDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddClubReportJwt(builder.Configuration);
builder.Services.AddRedisStreamEventBus(builder.Configuration);
builder.Services.AddSingleton<ExportFileGenerator>();
builder.Services.AddScoped<ExportGenerationJob>();
builder.Services.AddScoped<ExportRetentionJob>();
builder.Services.AddHttpClient("ReportService", client =>
{
    client.BaseAddress = new Uri("http://report-service:8080");
});
builder.Services.AddHangfire(configuration => configuration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        new SqlServerStorageOptions
        {
            PrepareSchemaIfNecessary = true,
            QueuePollInterval = TimeSpan.FromSeconds(2)
        }));
builder.Services.AddHangfireServer(options => options.Queues = ["exports", "default"]);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy =>
    {
        var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? ["http://localhost:3000", "http://localhost:5173"];
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});
builder.Services.AddHealthChecks();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/error");
}

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors("frontend");
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapGet("/error", () => Results.Problem("An unexpected error occurred.")).AllowAnonymous();
app.MapGet("/", () => Results.Ok(new { service = "Export Service", status = "running" }));

var exports = app.MapGroup("/api/exports")
    .WithTags("Exports")
    .RequireAuthorization(AuthPolicies.StudentAffairsAdministration);

exports.MapGet("/", async (
    string? status,
    int page,
    int pageSize,
    ExportDbContext db,
    CancellationToken cancellationToken) =>
{
    page = Math.Max(page, 1);
    pageSize = pageSize is <= 0 or > 100 ? 20 : pageSize;

    var query = db.ExportRequests.Include(x => x.File).AsNoTracking();
    if (!string.IsNullOrWhiteSpace(status))
    {
        var normalizedStatus = status.Trim();
        query = query.Where(x => x.Status == normalizedStatus);
    }

    var total = await query.CountAsync(cancellationToken);
    var rows = await query
        .OrderByDescending(x => x.CreatedAtUtc)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .ToListAsync(cancellationToken);

    return Results.Ok(new
    {
        items = rows.Select(ToResponse),
        total,
        page,
        pageSize
    });
});

exports.MapGet("/{id:int}", async (
    int id,
    ExportDbContext db,
    CancellationToken cancellationToken) =>
{
    var request = await db.ExportRequests
        .Include(x => x.File)
        .AsNoTracking()
        .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    return request is null ? Results.NotFound() : Results.Ok(ToResponse(request));
});

exports.MapPost("/", async (
    CreateExportRequest input,
    ExportDbContext db,
    ClaimsPrincipal user,
    HttpContext httpContext,
    IHttpClientFactory httpClientFactory,
    IEventBus eventBus,
    IBackgroundJobClient backgroundJobs,
    CancellationToken cancellationToken) =>
{
    var errors = Validate(input);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    string? snapshotJson = null;
    if (input.Scope.Trim().Equals("Report", StringComparison.OrdinalIgnoreCase))
    {
        var client = httpClientFactory.CreateClient("ReportService");
        
        if (httpContext.Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            client.DefaultRequestHeaders.Add("Authorization", authHeader.ToString());
        }

        var response = await client.GetAsync($"/api/reports/{input.ReportId}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return response.StatusCode == System.Net.HttpStatusCode.Forbidden 
                ? Results.Forbid() 
                : Results.BadRequest(new { message = "Failed to fetch report from ReportService. It might not exist." });
        }
        
        var reportSnapshot = await response.Content.ReadFromJsonAsync<ReportExportSnapshot>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, 
            cancellationToken: cancellationToken);
            
        if (reportSnapshot is null)
        {
            return Results.BadRequest(new { message = "Report data is invalid or empty." });
        }
        snapshotJson = JsonSerializer.Serialize(reportSnapshot);
    }

    var exportType = ExportTypes.Normalize(input.ExportType)!;
    var request = new ExportService.Models.ExportRequest
    {
        ExportType = exportType,
        Scope = input.Scope.Trim(),
        Status = ExportStatuses.Pending,
        Period = string.IsNullOrWhiteSpace(input.Period) ? null : input.Period.Trim(),
        ClubId = input.ClubId,
        ReportId = input.ReportId,
        RequestedByUserId = user.GetUserId(),
        RequestedByName = user.GetDisplayName(),
        CriteriaJson = JsonSerializer.Serialize(input),
        SnapshotJson = snapshotJson,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    db.ExportRequests.Add(request);
    await db.SaveChangesAsync(cancellationToken);

    await eventBus.PublishAsync(
        new ExportRequestedEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            request.Id,
            request.ExportType,
            request.Scope,
            request.RequestedByUserId),
        EventRoutingKeys.ExportRequested,
        cancellationToken);

    backgroundJobs.Enqueue<ExportGenerationJob>(
        job => job.GenerateAsync(request.Id, CancellationToken.None));

    return Results.Accepted($"/api/exports/{request.Id}", ToResponse(request));
});

exports.MapGet("/{id:int}/download", async (
    int id,
    ExportDbContext db,
    CancellationToken cancellationToken) =>
{
    var request = await db.ExportRequests
        .Include(x => x.File)
        .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
    if (request?.File is null)
    {
        return Results.NotFound();
    }

    if (!request.File.IsAvailable || request.File.ExpiresAtUtc <= DateTimeOffset.UtcNow)
    {
        if (request.File.IsAvailable)
        {
            request.File.IsAvailable = false;
            await db.SaveChangesAsync(cancellationToken);
        }

        return Results.NotFound(new { message = "The export file has expired." });
    }

    if (!File.Exists(request.File.FilePath))
    {
        request.File.IsAvailable = false;
        await db.SaveChangesAsync(cancellationToken);
        return Results.NotFound(new { message = "Export file not found." });
    }

    return Results.File(
        request.File.FilePath,
        request.File.ContentType,
        request.File.FileName,
        enableRangeProcessing: true);
});

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ExportDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseStartup");
    await db.ApplyMigrationsWithRetryAsync(logger);
}

var recurringJobs = app.Services.GetRequiredService<IRecurringJobManager>();
recurringJobs.AddOrUpdate(
    "expired-export-cleanup",
    Job.FromExpression<ExportRetentionJob>(
        job => job.CleanupExpiredAsync(CancellationToken.None)),
    Cron.Hourly(),
    new RecurringJobOptions
    {
        TimeZone = TimeZoneInfo.Utc
    });

app.Run();

static Dictionary<string, string[]> Validate(CreateExportRequest input)
{
    var errors = new Dictionary<string, string[]>();
    if (ExportTypes.Normalize(input.ExportType) is null)
    {
        errors[nameof(input.ExportType)] = ["ExportType must be PDF or XLSX."];
    }

    if (string.IsNullOrWhiteSpace(input.Scope) || input.Scope.Trim().Length > 40)
    {
        errors[nameof(input.Scope)] = ["Scope is required and must not exceed 40 characters."];
    }

    if (input.Period?.Trim().Length > 40)
    {
        errors[nameof(input.Period)] = ["Period must not exceed 40 characters."];
    }

    if (input.ClubId is <= 0)
    {
        errors[nameof(input.ClubId)] = ["ClubId must be a positive number."];
    }

    if (input.Scope.Trim().Equals("Report", StringComparison.OrdinalIgnoreCase) && input.ReportId is null or <= 0)
    {
        errors[nameof(input.ReportId)] = ["ReportId is required when Scope is 'Report'."];
    }

    return errors;
}

static ExportResponse ToResponse(ExportService.Models.ExportRequest request) => new(
    request.Id,
    request.ExportType,
    request.Scope,
    request.Status,
    request.Period,
    request.ClubId,
    request.ReportId,
    request.RequestedByUserId,
    request.RequestedByName,
    request.CreatedAtUtc,
    request.CompletedAtUtc,
    request.ErrorMessage,
    request.File is null
        ? null
        : new ExportFileResponse(
            request.File.Id,
            request.File.FileName,
            request.File.ContentType,
            request.File.SizeBytes,
            request.File.ExpiresAtUtc,
            request.File.Checksum,
            request.File.IsAvailable && request.File.ExpiresAtUtc > DateTimeOffset.UtcNow));
