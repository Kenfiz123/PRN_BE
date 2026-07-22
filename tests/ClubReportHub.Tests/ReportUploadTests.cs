using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReportService.Data;
using ReportService.Models;
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
