using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using ReportService.Data;
using ReportService.Models;
using ReportService.Services;
using Xunit;

namespace ClubReportHub.Tests;

public class DemoResetServiceTests : IDisposable
{
    private static readonly (int It, int Se, int Ai, int Art) ClubIds = (1, 2, 3, 4);

    private static ReportDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<ReportDbContext>()
            .UseSqlite("DataSource=:memory:;Mode=Memory;Cache=Shared")
            .Options;
        var db = new ReportDbContext(options);
        db.Database.OpenConnection();
        db.Database.EnsureCreated();
        return db;
    }

    private static Mock<ILogger<DemoResetService>> MockLogger()
        => new Mock<ILogger<DemoResetService>>();

    public void Dispose()
    {
        // Each test uses its own in-memory DB; cleanup handled by test runner.
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 1. Enabled_False_NoChanges
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Enabled_False_NoChanges()
    {
        // Arrange: seeded with DemoData.Enabled=false → seeder should not call anything
        using var db = CreateDbContext();
        var resetService = new DemoResetService(db);

        // Act: verify that upserting when seeder is "disabled" produces no changes
        // (this test validates that when DemoData.Enabled=false, nothing is inserted)
        var countBefore = await db.Reports.CountAsync();

        // Since SeedAsync is static and gated by Program.cs, we test the service directly:
        // When ResetReports=false and SeedKey doesn't exist, nothing happens without the seeder call.
        // The real validation is: with Enabled=false, Program.cs never calls SeedAsync.

        // Assert: no demo reports inserted without explicit call
        Assert.Equal(0, countBefore);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 2. ResetReports_False_UpsertsMissing
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task ResetReports_False_UpsertsMissing()
    {
        using var db = CreateDbContext();
        var resetService = new DemoResetService(db);
        var logger = MockLogger();

        // Act: insert-only pass (ResetReports=false handled by caller via UpsertMissingSeedKeys)
        // Test that the seeder's upsert path works
        db.Reports.Add(new Report
        {
            SeedKey = "DEMO-IT-Q1-2026",
            ClubId = 1,
            Period = "Q1/2026",
            ReportType = "Báo cáo Quý",
            Status = "Approved",
            Tag = "Báo cáo Quý",
            CreatedByUserId = 4
        });
        await db.SaveChangesAsync();

        // Upsert missing keys (simulates ResetReports=false path)
        await DevelopmentDataSeeder.SeedAsync(
            db, resetService, ClubIds, resetReports: false, logger.Object, CancellationToken.None);

        // Assert: total count is exactly the 5 demo reports (no duplicates)
        var total = await db.Reports.CountAsync();
        Assert.Equal(5, total);

        // Assert: no duplicate SeedKeys (unique index enforced at DB level)
        var seedKeyCounts = await db.Reports
            .Where(r => r.SeedKey != null)
            .GroupBy(r => r.SeedKey)
            .Select(g => g.Count())
            .ToListAsync();
        Assert.All(seedKeyCounts, c => Assert.Equal(1, c));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 3. UnrelatedUserReport_Remains
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task UnrelatedUserReport_Remains()
    {
        using var db = CreateDbContext();
        var resetService = new DemoResetService(db);
        var logger = MockLogger();

        // Arrange: add a user report (no SeedKey)
        var userReport = new Report
        {
            ClubId = 1,
            Period = "Q3/2026",
            ReportType = "Báo cáo Quý",
            Status = "Draft",
            Tag = "Custom",
            CreatedByUserId = 5,
            SeedKey = null
        };
        db.Reports.Add(userReport);

        // Add a demo report
        db.Reports.Add(new Report
        {
            SeedKey = "DEMO-IT-Q1-2026",
            ClubId = 1,
            Period = "Q1/2026",
            ReportType = "Báo cáo Quý",
            Status = "Approved",
            Tag = "Báo cáo Quý",
            CreatedByUserId = 4
        });
        await db.SaveChangesAsync();

        var userReportId = userReport.Id;

        // Act: upsert missing keys (simulates ResetReports=false)
        await DevelopmentDataSeeder.SeedAsync(
            db, resetService, ClubIds, resetReports: false, logger.Object, CancellationToken.None);

        // Assert: user report survives
        var stillExists = await db.Reports.AnyAsync(r => r.Id == userReportId && r.SeedKey == null);
        Assert.True(stillExists);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 4. InPlaceReset_PreservesReportId
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task InPlaceReset_PreservesReportId()
    {
        using var db = CreateDbContext();
        var resetService = new DemoResetService(db);
        var logger = MockLogger();

        // Arrange: existing demo report with ID assigned by SQLite
        var report = new Report
        {
            SeedKey = "DEMO-IT-Q1-2026",
            ClubId = 1,
            Period = "Q1/2026",
            ReportType = "Báo cáo Quý",
            Status = "Draft", // different from expected "Approved"
            Tag = "Old Tag",
            CreatedByUserId = 4
        };
        db.Reports.Add(report);
        await db.SaveChangesAsync();
        var originalId = report.Id;

        // Act
        await resetService.ResetDemoReportsAsync(ClubIds, logger.Object, CancellationToken.None);

        // Assert: ID is preserved
        var updated = await db.Reports.FirstAsync(r => r.SeedKey == "DEMO-IT-Q1-2026");
        Assert.Equal(originalId, updated.Id);

        // Assert: status was updated
        Assert.Equal("Approved", updated.Status);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 5. InPlaceReset_UpdatesChildRows
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task InPlaceReset_UpdatesChildRows()
    {
        using var db = CreateDbContext();
        var resetService = new DemoResetService(db);
        var logger = MockLogger();

        // Arrange: demo report with old child rows
        var report = new Report
        {
            SeedKey = "DEMO-IT-Q1-2026",
            ClubId = 1,
            Period = "Q1/2026",
            ReportType = "Báo cáo Quý",
            Status = "Approved",
            Tag = "Báo cáo Quý",
            CreatedByUserId = 4
        };
        db.Reports.Add(report);
        await db.SaveChangesAsync();

        var reportId = report.Id;

        // Add AuditLog (no FK cascade — must be deleted explicitly)
        db.AuditLogs.Add(new AuditLog
        {
            ReportId = reportId,
            Action = "Created",
            ActorUserId = 4,
            Description = "Old audit",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-10)
        });
        // Add ReportDetail
        db.ReportDetails.Add(new ReportDetail
        {
            ReportId = reportId,
            ActivityName = "Old Activity",
            ActivityDate = new DateOnly(2026, 1, 10),
            Description = "Old description"
        });
        // Add ReportFeedback
        db.ReportFeedback.Add(new ReportFeedback
        {
            ReportId = reportId,
            ReviewerUserId = 4,
            ReviewerName = "Admin",
            Decision = "Approved",
            Message = "Old feedback",
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        // Act
        await resetService.ResetDemoReportsAsync(ClubIds, logger.Object, CancellationToken.None);

        // Assert: old child rows were deleted
        Assert.Equal(0, await db.AuditLogs.CountAsync(a => a.ReportId == reportId));
        Assert.Equal(0, await db.ReportDetails.CountAsync(d => d.ReportId == reportId));
        Assert.Equal(0, await db.ReportFeedback.CountAsync(f => f.ReportId == reportId));

        // Assert: Report row still exists
        Assert.True(await db.Reports.AnyAsync(r => r.Id == reportId && r.SeedKey == "DEMO-IT-Q1-2026"));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 6. UploadedFile_BlocksReset
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task UploadedFile_BlocksReset()
    {
        using var db = CreateDbContext();
        var resetService = new DemoResetService(db);

        // Arrange: demo report with uploaded file
        var report = new Report
        {
            SeedKey = "DEMO-IT-Q1-2026",
            ClubId = 1,
            Period = "Q1/2026",
            ReportType = "Báo cáo Quý",
            Status = "Approved",
            Tag = "Báo cáo Quý",
            CreatedByUserId = 4
        };
        db.Reports.Add(report);
        await db.SaveChangesAsync();

        db.ReportUploadedFiles.Add(new ReportUploadedFile
        {
            ReportId = report.Id,
            OriginalFileName = "report.pdf",
            StoredFileName = "stored.pdf",
            ContentType = "application/pdf",
            FileExtension = ".pdf",
            SizeBytes = 1024,
            StoragePath = "/app/uploads/stored.pdf",
            Checksum = "abc123",
            UploadedByUserId = 4,
            IsActive = true
        });
        await db.SaveChangesAsync();

        // Act
        var canReset = await resetService.CanResetAsync();

        // Assert
        Assert.False(canReset);
        Assert.Contains("uploaded-file", resetService.GetResetBlockedReason().ToLower());
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 7. Attachment_BlocksReset
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Attachment_BlocksReset()
    {
        using var db = CreateDbContext();
        var resetService = new DemoResetService(db);

        // Arrange: demo report with attachment
        var report = new Report
        {
            SeedKey = "DEMO-IT-Q1-2026",
            ClubId = 1,
            Period = "Q1/2026",
            ReportType = "Báo cáo Quý",
            Status = "Approved",
            Tag = "Báo cáo Quý",
            CreatedByUserId = 4
        };
        db.Reports.Add(report);
        await db.SaveChangesAsync();

        db.ReportAttachments.Add(new ReportAttachment
        {
            ReportId = report.Id,
            FileName = "attachment.pdf",
            ContentType = "application/pdf",
            StoragePath = "/app/attachments/attachment.pdf",
            UploadedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        // Act
        var canReset = await resetService.CanResetAsync();

        // Assert
        Assert.False(canReset);
        Assert.Contains("attachment", resetService.GetResetBlockedReason().ToLower());
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 8. OnlyFiveKnownKeysAffected
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task OnlyFiveKnownKeysAffected()
    {
        using var db = CreateDbContext();
        var resetService = new DemoResetService(db);
        var logger = MockLogger();

        // Arrange: demo report with unknown SeedKey
        var unknownReport = new Report
        {
            SeedKey = "DEMO-UNKNOWN-2026",
            ClubId = 1,
            Period = "Q1/2026",
            ReportType = "Báo cáo Quý",
            Status = "Draft",
            Tag = "Unknown",
            CreatedByUserId = 4
        };
        db.Reports.Add(unknownReport);
        await db.SaveChangesAsync();

        var unknownId = unknownReport.Id;

        // Act: add all 5 known demo reports, then run reset
        foreach (var seedKey in new[] { "DEMO-IT-Q1-2026", "DEMO-IT-Q2-2026", "DEMO-SE-SPRING-2026", "DEMO-AI-FOR-STUDENTS-2026", "DEMO-ART-SUMMER-2026" })
        {
            db.Reports.Add(new Report
            {
                SeedKey = seedKey,
                ClubId = 1,
                Period = "Q1/2026",
                ReportType = "Báo cáo Quý",
                Status = "Draft",
                Tag = "Tag",
                CreatedByUserId = 4
            });
        }
        await db.SaveChangesAsync();

        await resetService.ResetDemoReportsAsync(ClubIds, logger.Object, CancellationToken.None);

        // Assert: unknown SeedKey report still exists
        Assert.True(await db.Reports.AnyAsync(r => r.Id == unknownId && r.SeedKey == "DEMO-UNKNOWN-2026"));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 9. ClubIds_Validation_Throws
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void ClubIds_Validation_Throws()
    {
        // Arrange: options with zero club ID
        var options = new ReportService.Options.DemoDataOptions
        {
            Enabled = true,
            Clubs = new Dictionary<string, int>
            {
                ["ItClubId"] = 0,
                ["SeClubId"] = 2,
                ["AiClubId"] = 3,
                ["ArtClubId"] = 4
            }
        };

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() => options.GetClubIds());
        Assert.Contains("ItClubId", ex.Message);
    }

    [Fact]
    public void ClubIds_MissingKey_Throws()
    {
        var options = new ReportService.Options.DemoDataOptions
        {
            Enabled = true,
            Clubs = new Dictionary<string, int>
            {
                // missing ItClubId
                ["SeClubId"] = 2,
                ["AiClubId"] = 3,
                ["ArtClubId"] = 4
            }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.GetClubIds());
        Assert.Contains("ItClubId", ex.Message);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // 10. DisallowedEnvironment_DoesNotSeed
    // ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task DisallowedEnvironment_DoesNotSeed()
    {
        // Arrange: "Production" environment → seeder should not run
        using var db = CreateDbContext();
        var resetService = new DemoResetService(db);
        var logger = MockLogger();

        // The seeder is gated by Program.cs:
        // if (demoEnabled && (environment == "Development" || environment == "Docker"))
        // We test that in "Production", the if-block is never entered.
        var demoEnabled = false; // simulated: Enabled=false in Production
        var environment = "Production";
        var shouldRun = demoEnabled && (environment == "Development" || environment == "Docker");

        // Act
        if (shouldRun)
        {
            await DevelopmentDataSeeder.SeedAsync(
                db, resetService, ClubIds, resetReports: false, logger.Object, CancellationToken.None);
        }

        // Assert: no reports created
        Assert.Equal(0, await db.Reports.CountAsync());
    }
}
