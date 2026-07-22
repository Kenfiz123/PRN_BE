using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReportService.Data;
using ReportService.Models;
using ReportService.Services;
using Xunit;

namespace ClubReportHub.Tests;

public class ReportUploadTests
{
    private static ReportDbContext CreateDbContext(string dbName)
    {
        var options = new DbContextOptionsBuilder<ReportDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new ReportDbContext(options);
    }

    private static IFormFile CreateFormFile(string fileName, string contentType, byte[] content)
    {
        var stream = new MemoryStream(content);
        return new FormFile(stream, 0, content.Length, "file", fileName)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    [Fact]
    public void ValidateUploadedReportFile_ValidFormats_ReturnsSuccess()
    {
        var pdf = CreateFormFile("report.pdf", "application/pdf", new byte[] { 0x25, 0x50, 0x44, 0x46 });
        var docx = CreateFormFile("report.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", new byte[] { 0x50, 0x4B, 0x03, 0x04 });
        var xlsx = CreateFormFile("report.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", new byte[] { 0x50, 0x4B, 0x03, 0x04 });

        Assert.True(ValidateFile(pdf).IsValid);
        Assert.True(ValidateFile(docx).IsValid);
        Assert.True(ValidateFile(xlsx).IsValid);
    }

    [Fact]
    public void ValidateUploadedReportFile_UnsupportedExtension_ReturnsError()
    {
        var exe = CreateFormFile("malware.exe", "application/octet-stream", new byte[] { 0x4D, 0x5A });
        var doc = CreateFormFile("old.doc", "application/msword", new byte[] { 0xD0, 0xCF });

        var res1 = ValidateFile(exe);
        var res2 = ValidateFile(doc);

        Assert.False(res1.IsValid);
        Assert.Contains("Định dạng tệp không được hỗ trợ", res1.ErrorMessage);
        Assert.False(res2.IsValid);
    }

    [Fact]
    public void ValidateUploadedReportFile_FileOver20MB_ReturnsError()
    {
        var largeStream = new MemoryStream(new byte[100]);
        var file = new FormFile(largeStream, 0, 21L * 1024 * 1024, "file", "big.pdf")
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf"
        };

        var res = ValidateFile(file);
        Assert.False(res.IsValid);
        Assert.Contains("vượt quá giới hạn", res.ErrorMessage);
    }

    [Fact]
    public async Task ReportDbContext_ReportUploadedFile_SavesAndRetrievesCorrectly()
    {
        using var db = CreateDbContext("ReportUploadTest_DbSave");

        var report = new Report
        {
            ClubId = 1,
            ClubName = "IT Club",
            Period = "2026-Q3",
            ReportType = "QUARTERLY",
            Tag = "Activity report",
            Status = ReportStatuses.Draft,
            ContentSource = ReportContentSources.UploadedFile,
            CreatedByUserId = 4
        };

        var uploadedFile = new ReportUploadedFile
        {
            OriginalFileName = "report_2026_q3.pdf",
            StoredFileName = "uploaded-report-1-test.pdf",
            ContentType = "application/pdf",
            FileExtension = ".pdf",
            SizeBytes = 1024,
            StoragePath = "/app/report-uploads/uploaded-report-1-test.pdf",
            Checksum = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            UploadedByUserId = 4,
            IsActive = true,
            Report = report
        };

        report.UploadedFile = uploadedFile;
        db.Reports.Add(report);
        await db.SaveChangesAsync();

        var fetched = await db.Reports
            .Include(x => x.UploadedFile)
            .FirstOrDefaultAsync(x => x.Id == report.Id);

        Assert.NotNull(fetched);
        Assert.Equal(ReportContentSources.UploadedFile, fetched.ContentSource);
        Assert.NotNull(fetched.UploadedFile);
        Assert.Equal("report_2026_q3.pdf", fetched.UploadedFile.OriginalFileName);
        Assert.True(fetched.UploadedFile.IsActive);
    }

    [Fact]
    public void PathTraversal_Filename_IsSanitized()
    {
        var maliciousName = "../../../etc/passwd_report.pdf";
        var rawName = Path.GetFileName(maliciousName);
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(rawName.Where(c => !invalidChars.Contains(c))).Trim();

        Assert.DoesNotContain("/", sanitized);
        Assert.DoesNotContain("\\", sanitized);
        Assert.Equal("passwd_report.pdf", sanitized);
    }

    // --- Submission Validation Unit Tests ---

    [Fact]
    public void UploadedReportAuthorAccess_ApprovedCreator_Allowed()
    {
        var report = new Report
        {
            Id = 20,
            CreatedByUserId = 10,
            ContentSource = ReportContentSources.UploadedFile
        };

        var allowed = ReportSubmissionRules.CanUseUploadedReportAuthorAccess(
            report,
            currentUserId: 10,
            isApprovedClubMember: true);

        Assert.True(allowed);
    }

    [Theory]
    [InlineData(99, true)]
    [InlineData(10, false)]
    public void UploadedReportAuthorAccess_OtherUserOrInactiveMember_Denied(
        int currentUserId,
        bool isApprovedClubMember)
    {
        var report = new Report
        {
            Id = 21,
            CreatedByUserId = 10,
            ContentSource = ReportContentSources.UploadedFile
        };

        var allowed = ReportSubmissionRules.CanUseUploadedReportAuthorAccess(
            report,
            currentUserId,
            isApprovedClubMember);

        Assert.False(allowed);
    }

    [Fact]
    public void Submit_StructuredReport_WithoutActivityDetail_Rejected()
    {
        var report = new Report
        {
            Id = 1,
            CreatedByUserId = 10,
            Status = ReportStatuses.Draft,
            ContentSource = ReportContentSources.StructuredForm,
            Details = []
        };

        var (isValid, errorMessage, isForbidden) = ReportSubmissionRules.ValidateSubmission(
            report, currentUserId: 10, hasAuthorAccess: true, fileExistsCheck: _ => true);

        Assert.False(isValid);
        Assert.False(isForbidden);
        Assert.Equal("At least one activity detail is required before submission.", errorMessage);
    }

    [Fact]
    public void Submit_StructuredReport_WithActivityDetail_Submitted()
    {
        var report = new Report
        {
            Id = 1,
            CreatedByUserId = 10,
            Status = ReportStatuses.Draft,
            ContentSource = ReportContentSources.StructuredForm,
            Details = [new ReportDetail { ActivityName = "Tech Workshop", ActivityDate = DateOnly.FromDateTime(DateTime.UtcNow) }]
        };

        var (isValid, errorMessage, isForbidden) = ReportSubmissionRules.ValidateSubmission(
            report, currentUserId: 10, hasAuthorAccess: true, fileExistsCheck: _ => true);

        Assert.True(isValid);
        Assert.False(isForbidden);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void Submit_UploadedReport_WithActiveFileAndNoActivityDetail_Submitted()
    {
        var report = new Report
        {
            Id = 2,
            CreatedByUserId = 10,
            Status = ReportStatuses.Draft,
            ContentSource = ReportContentSources.UploadedFile,
            Details = []
        };
        report.UploadedFile = new ReportUploadedFile
        {
            Id = 101,
            ReportId = 2,
            IsActive = true,
            StoragePath = "/app/report-uploads/file.docx"
        };

        var (isValid, errorMessage, isForbidden) = ReportSubmissionRules.ValidateSubmission(
            report, currentUserId: 10, hasAuthorAccess: true, fileExistsCheck: _ => true);

        Assert.True(isValid);
        Assert.False(isForbidden);
        Assert.Null(errorMessage);
    }

    [Fact]
    public void Submit_UploadedReport_WithoutActiveFile_Rejected()
    {
        var report = new Report
        {
            Id = 3,
            CreatedByUserId = 10,
            Status = ReportStatuses.Draft,
            ContentSource = ReportContentSources.UploadedFile,
            UploadedFile = null,
            Details = []
        };

        var (isValid, errorMessage, isForbidden) = ReportSubmissionRules.ValidateSubmission(
            report, currentUserId: 10, hasAuthorAccess: true, fileExistsCheck: _ => true);

        Assert.False(isValid);
        Assert.False(isForbidden);
        Assert.Equal("An uploaded report file is required before submission.", errorMessage);
    }

    [Fact]
    public void Submit_UploadedReport_FileBelongingToAnotherReport_Rejected()
    {
        var report = new Report
        {
            Id = 4,
            CreatedByUserId = 10,
            Status = ReportStatuses.Draft,
            ContentSource = ReportContentSources.UploadedFile,
            Details = []
        };
        report.UploadedFile = new ReportUploadedFile
        {
            Id = 102,
            ReportId = 999, // Mismatched ReportId!
            IsActive = true,
            StoragePath = "/app/report-uploads/file.docx"
        };

        var (isValid, errorMessage, isForbidden) = ReportSubmissionRules.ValidateSubmission(
            report, currentUserId: 10, hasAuthorAccess: true, fileExistsCheck: _ => true);

        Assert.False(isValid);
        Assert.False(isForbidden);
        Assert.Equal("An uploaded report file is required before submission.", errorMessage);
    }

    [Fact]
    public void Submit_UnauthorizedManager_Forbidden()
    {
        var report = new Report
        {
            Id = 5,
            CreatedByUserId = 10,
            Status = ReportStatuses.Draft,
            ContentSource = ReportContentSources.StructuredForm,
            Details = [new ReportDetail { ActivityName = "Meeting" }]
        };

        // User ID 99 attempts to submit user 10's report
        var (isValid, errorMessage, isForbidden) = ReportSubmissionRules.ValidateSubmission(
            report, currentUserId: 99, hasAuthorAccess: true, fileExistsCheck: _ => true);

        Assert.False(isValid);
        Assert.True(isForbidden);
        Assert.Equal("Forbidden", errorMessage);
    }

    [Fact]
    public void Submit_AlreadySubmittedReport_Rejected()
    {
        var report = new Report
        {
            Id = 6,
            CreatedByUserId = 10,
            Status = ReportStatuses.Submitted, // Already submitted!
            ContentSource = ReportContentSources.StructuredForm,
            Details = [new ReportDetail { ActivityName = "Annual Gala" }]
        };

        var (isValid, errorMessage, isForbidden) = ReportSubmissionRules.ValidateSubmission(
            report, currentUserId: 10, hasAuthorAccess: true, fileExistsCheck: _ => true);

        Assert.False(isValid);
        Assert.False(isForbidden);
        Assert.Equal("Only draft or rejected reports can be submitted.", errorMessage);
    }

    [Fact]
    public async Task GeneratePreview_PdfFile_SetsAvailableStatusAndPdfContentType()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "report_tests_" + Guid.NewGuid().ToString("N"));
        var uploadsDir = Path.Combine(tempDir, "uploads");
        var previewsDir = Path.Combine(tempDir, "previews");
        Directory.CreateDirectory(uploadsDir);
        Directory.CreateDirectory(previewsDir);

        try
        {
            var pdfPath = Path.Combine(uploadsDir, "test.pdf");
            await File.WriteAllBytesAsync(pdfPath, new byte[] { 0x25, 0x50, 0x44, 0x46 });

            var uploadedFile = new ReportUploadedFile
            {
                OriginalFileName = "test.pdf",
                StoredFileName = "test.pdf",
                FileExtension = ".pdf",
                StoragePath = pdfPath
            };

            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Uploads:StoragePath"] = uploadsDir,
                    ["Uploads:PreviewStoragePath"] = previewsDir
                })
                .Build();

            await ReportPreviewGenerator.GeneratePreviewAsync(uploadedFile, config);

            Assert.Equal("Available", uploadedFile.PreviewStatus);
            Assert.Equal("application/pdf", uploadedFile.PreviewContentType);
            Assert.NotNull(uploadedFile.PreviewStoragePath);
            Assert.True(File.Exists(uploadedFile.PreviewStoragePath));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task GeneratePreview_UnsupportedFile_SetsUnsupportedStatus()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "report_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var xlsxPath = Path.Combine(tempDir, "test.xlsx");
            await File.WriteAllBytesAsync(xlsxPath, new byte[] { 0x50, 0x4B, 0x03, 0x04 });

            var uploadedFile = new ReportUploadedFile
            {
                OriginalFileName = "test.xlsx",
                StoredFileName = "test.xlsx",
                FileExtension = ".xlsx",
                StoragePath = xlsxPath
            };

            var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Uploads:StoragePath"] = tempDir,
                    ["Uploads:PreviewStoragePath"] = tempDir
                })
                .Build();

            await ReportPreviewGenerator.GeneratePreviewAsync(uploadedFile, config);

            Assert.Equal("Unsupported", uploadedFile.PreviewStatus);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    private static (bool IsValid, string ErrorMessage, string ContentType) ValidateFile(IFormFile file)
    {
        if (file is null || file.Length == 0)
        {
            return (false, "Vui lòng chọn tệp báo cáo.", string.Empty);
        }

        if (file.Length > 20 * 1024 * 1024)
        {
            return (false, "Dung lượng tệp vượt quá giới hạn cho phép (tối đa 20 MB).", string.Empty);
        }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".pdf" or ".docx" or ".xlsx"))
        {
            return (false, "Định dạng tệp không được hỗ trợ. Chỉ chấp nhận các tệp .pdf, .docx, .xlsx.", string.Empty);
        }

        var contentType = ext switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "application/octet-stream"
        };

        return (true, string.Empty, contentType);
    }
}
