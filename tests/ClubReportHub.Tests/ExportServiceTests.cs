using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ClubReportHub.Shared.Auth;
using ExportService.Contracts;
using ExportService.Data;
using ExportService.Models;
using ExportService.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Infrastructure;

namespace ClubReportHub.Tests;

public class ExportServiceTests : IDisposable
{
    private readonly ExportDbContext _db;

    public ExportServiceTests()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        var options = new DbContextOptionsBuilder<ExportDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new ExportDbContext(options);
    }

    public void Dispose()
    {
        _db.Dispose();
    }

    [Fact]
    public void ExportTypes_Normalize_ShouldReturnCorrectNormalizedType()
    {
        ExportTypes.Normalize("pdf").Should().Be("PDF");
        ExportTypes.Normalize("xlsx").Should().Be("XLSX");
        ExportTypes.Normalize("excel").Should().Be("XLSX");
        ExportTypes.Normalize("docx").Should().Be("DOCX");
        ExportTypes.Normalize("word").Should().Be("DOCX");
        ExportTypes.Normalize("unknown").Should().BeNull();
    }

    [Fact]
    public void ExportRequest_DefaultStatus_ShouldBePending()
    {
        var request = new ExportRequest();
        request.Status.Should().Be("Pending");
    }

    [Fact]
    public async Task CreateExportRequest_ShouldDeriveClubIdAndPeriod_FromSnapshot()
    {
        var snapshot = new ReportExportSnapshot(
            Id: 10,
            ClubId: 5,
            ClubName: "IT Club",
            Period: "2026-Q3",
            Status: "Submitted",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            ExecutiveSummary: "Exec Summary Test",
            Achievements: "Achieve 1",
            Challenges: "None",
            Recommendations: "None",
            NextPeriodPlan: "Plan",
            TotalActivities: 1,
            TotalParticipants: 50,
            TotalBudgetSpent: 1000000,
            Details: new List<ReportActivitySnapshot>
            {
                new ReportActivitySnapshot(1, 10, "Hackathon 2026", DateOnly.FromDateTime(DateTime.UtcNow), 50, 1000000)
            },
            Attachments: new List<ReportAttachmentSnapshot>(),
            Feedback: new List<ReportFeedbackSnapshot>
            {
                new ReportFeedbackSnapshot(1, "Good job", "Admin User", DateTimeOffset.UtcNow)
            }
        );

        var request = new ExportRequest
        {
            ExportType = "PDF",
            Scope = "Report",
            ReportId = snapshot.Id,
            ClubId = snapshot.ClubId,
            Period = snapshot.Period,
            RequestedByUserId = 100,
            RequestedByName = "Test User",
            SnapshotJson = JsonSerializer.Serialize(snapshot)
        };

        _db.ExportRequests.Add(request);
        await _db.SaveChangesAsync();

        var saved = await _db.ExportRequests.FindAsync(request.Id);
        saved.Should().NotBeNull();
        saved!.ClubId.Should().Be(5);
        saved.Period.Should().Be("2026-Q3");
    }

    [Fact]
    public void GeneratePdf_ShouldStartWithPdfMagicHeader()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid()}.pdf");
        try
        {
            var snapshot = new ReportExportSnapshot(
                1, 1, "IT Club", "2026-Q1", "Approved", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Summary", null, null, null, null, 1, 10, 500,
                new List<ReportActivitySnapshot>(), new List<ReportAttachmentSnapshot>(), new List<ReportFeedbackSnapshot>()
            );
            var request = new ExportRequest
            {
                Id = 1,
                ExportType = "PDF",
                Scope = "Report",
                SnapshotJson = JsonSerializer.Serialize(snapshot)
            };

            ExportFileGenerator.Generate(request, Path.GetTempPath());
            var createdFile = Path.Combine(Path.GetTempPath(), $"clubreport-report-1.pdf");
            File.Exists(createdFile).Should().BeTrue();

            var bytes = File.ReadAllBytes(createdFile);
            var header = Encoding.ASCII.GetString(bytes, 0, 5);
            header.Should().StartWith("%PDF-");
            File.Delete(createdFile);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void GenerateExcel_ShouldContainWorkbookXmlAndData()
    {
        var tempDir = Path.GetTempPath();
        var snapshot = new ReportExportSnapshot(
            Id: 2,
            ClubId: 3,
            ClubName: "Robotics Club",
            Period: "2026-Q2",
            Status: "Approved",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            ExecutiveSummary: "Robotics Contest Summary",
            Achievements: "First Place",
            Challenges: "Battery issue",
            Recommendations: "More funds",
            NextPeriodPlan: "Next contest",
            TotalActivities: 1,
            TotalParticipants: 30,
            TotalBudgetSpent: 2000000,
            Details: new List<ReportActivitySnapshot>
            {
                new ReportActivitySnapshot(1, 2, "Robot Sumo", DateOnly.FromDateTime(DateTime.UtcNow), 30, 2000000)
            },
            Attachments: new List<ReportAttachmentSnapshot>(),
            Feedback: new List<ReportFeedbackSnapshot>()
        );

        var request = new ExportRequest
        {
            Id = 2,
            ExportType = "XLSX",
            Scope = "Report",
            SnapshotJson = JsonSerializer.Serialize(snapshot)
        };

        ExportFileGenerator.Generate(request, tempDir);
        var createdFile = Path.Combine(tempDir, "clubreport-report-2.xlsx");
        File.Exists(createdFile).Should().BeTrue();

        using (var archive = ZipFile.OpenRead(createdFile))
        {
            var workbookEntry = archive.GetEntry("xl/workbook.xml");
            workbookEntry.Should().NotBeNull();

            var sharedStringsEntry = archive.GetEntry("xl/sharedStrings.xml");
            if (sharedStringsEntry != null)
            {
                using var reader = new StreamReader(sharedStringsEntry.Open());
                var content = reader.ReadToEnd();
                content.Should().Contain("Robotics Club");
            }
        }
        File.Delete(createdFile);
    }

    [Fact]
    public void GenerateDocx_ShouldContainDocumentXmlAndRealReportData()
    {
        var tempDir = Path.GetTempPath();
        var snapshot = new ReportExportSnapshot(
            Id: 3,
            ClubId: 4,
            ClubName: "Music Club",
            Period: "2026-Q4",
            Status: "Approved",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            SubmittedAtUtc: DateTimeOffset.UtcNow,
            ExecutiveSummary: "Music Festival Report",
            Achievements: "Best Concert",
            Challenges: "Sound system",
            Recommendations: "New Mic",
            NextPeriodPlan: "Spring concert",
            TotalActivities: 1,
            TotalParticipants: 150,
            TotalBudgetSpent: 5000000,
            Details: new List<ReportActivitySnapshot>
            {
                new ReportActivitySnapshot(10, 3, "Winter Concert", DateOnly.FromDateTime(DateTime.UtcNow), 150, 5000000)
            },
            Attachments: new List<ReportAttachmentSnapshot>(),
            Feedback: new List<ReportFeedbackSnapshot>
            {
                new ReportFeedbackSnapshot(1, "Great show", "SA Officer", DateTimeOffset.UtcNow)
            }
        );

        var request = new ExportRequest
        {
            Id = 3,
            ExportType = "DOCX",
            Scope = "Report",
            SnapshotJson = JsonSerializer.Serialize(snapshot)
        };

        ExportFileGenerator.Generate(request, tempDir);
        var createdFile = Path.Combine(tempDir, "clubreport-report-3.docx");
        File.Exists(createdFile).Should().BeTrue();

        using (var archive = ZipFile.OpenRead(createdFile))
        {
            var docEntry = archive.GetEntry("word/document.xml");
            docEntry.Should().NotBeNull();

            using var reader = new StreamReader(docEntry!.Open());
            var content = reader.ReadToEnd();
            content.Should().Contain("Music Club");
            content.Should().Contain("2026-Q4");
            content.Should().Contain("Winter Concert");
            content.Should().Contain("Music Festival Report");
        }
        File.Delete(createdFile);
    }

    [Fact]
    public async Task MissingFile_ShouldReturnNotFound_AndSetIsAvailableFalse()
    {
        var file = new ExportFile
        {
            Id = 1,
            ExportRequestId = 10,
            FileName = "nonexistent.pdf",
            FilePath = "nonexistent_path/nonexistent.pdf",
            ContentType = "application/pdf",
            SizeBytes = 100,
            Checksum = "abc",
            IsAvailable = true,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1)
        };

        _db.ExportFiles.Add(file);
        await _db.SaveChangesAsync();

        File.Exists(file.FilePath).Should().BeFalse();
    }

    [Fact]
    public async Task JobRetry_DoesNotCreateDuplicateExportFileRecords()
    {
        var request = new ExportRequest
        {
            ExportType = "PDF",
            Scope = "Report",
            Status = "Pending",
            RequestedByUserId = 1,
            RequestedByName = "User"
        };
        _db.ExportRequests.Add(request);
        await _db.SaveChangesAsync();

        var firstFile = new ExportFile
        {
            ExportRequestId = request.Id,
            FileName = "test.pdf",
            FilePath = "/path/test.pdf",
            ContentType = "application/pdf",
            SizeBytes = 1000,
            Checksum = "hash1",
            IsAvailable = true,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(24)
        };

        _db.ExportFiles.Add(firstFile);
        await _db.SaveChangesAsync();

        var existingFilesCount = await _db.ExportFiles.CountAsync(x => x.ExportRequestId == request.Id);
        existingFilesCount.Should().Be(1);
    }
}
