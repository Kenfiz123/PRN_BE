using ReportService.Attachments;

namespace ClubReportHub.Tests;

public sealed class ReportAttachmentPolicyTests
{
    private readonly ReportAttachmentOptions _options = new();

    [Fact]
    public void ValidateAcceptsSupportedEvidenceFiles()
    {
        var result = ReportAttachmentPolicy.Validate(
            "evidence.pdf",
            "application/pdf",
            1024,
            _options);

        Assert.True(result.Succeeded);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void ValidateRejectsOversizedFiles()
    {
        var result = ReportAttachmentPolicy.Validate(
            "evidence.pdf",
            "application/pdf",
            _options.MaxSizeBytes + 1,
            _options);

        Assert.False(result.Succeeded);
        Assert.Contains("Maximum attachment size", result.ErrorMessage);
    }

    [Theory]
    [InlineData("script.exe", "application/pdf")]
    [InlineData("evidence.pdf", "application/x-msdownload")]
    public void ValidateRejectsUnsupportedEvidenceFiles(string fileName, string contentType)
    {
        var result = ReportAttachmentPolicy.Validate(fileName, contentType, 1024, _options);

        Assert.False(result.Succeeded);
    }

    [Fact]
    public void GetSafeFileNameRemovesDirectorySegments()
    {
        var safeName = ReportAttachmentPolicy.GetSafeFileName(@"..\..\folder/secret.xlsx");

        Assert.Equal("secret.xlsx", safeName);
        Assert.DoesNotContain("..", safeName);
        Assert.DoesNotContain("\\", safeName);
    }

    [Fact]
    public void ResolveStorageRootKeepsRelativePathsInsideContentRoot()
    {
        var root = ReportAttachmentPolicy.ResolveStorageRoot("attachments", @"C:\app");

        Assert.Equal(Path.GetFullPath(@"C:\app\attachments"), root);
    }
}
