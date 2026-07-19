using System.Security.Cryptography;
using ClosedXML.Excel;
using ExportService.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace ExportService.Services;

public sealed record GeneratedExportFile(
    string FileName,
    string ContentType,
    string FilePath,
    long SizeBytes,
    string Checksum);

public sealed class ExportFileGenerator(IConfiguration configuration)
{
    public GeneratedExportFile Generate(ExportRequest request)
    {
        var storagePath = Path.GetFullPath(configuration["Exports:StoragePath"]
            ?? Path.Combine(AppContext.BaseDirectory, "exports"));
        Directory.CreateDirectory(storagePath);

        var extension = request.ExportType == ExportTypes.Pdf ? "pdf" : "xlsx";
        var safeScope = string.Concat(request.Scope
            .Where(character => char.IsLetterOrDigit(character) || character is '-' or '_'))
            .ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(safeScope))
        {
            safeScope = "report";
        }

        var fileName = $"clubreport-{safeScope}-{request.Id}.{extension}";
        var filePath = Path.Combine(storagePath, fileName);
        var contentType = request.ExportType == ExportTypes.Pdf
            ? "application/pdf"
            : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        if (request.ExportType == ExportTypes.Pdf)
        {
            GeneratePdf(request, filePath);
        }
        else
        {
            GenerateExcel(request, filePath);
        }

        var fileInfo = new FileInfo(filePath);
        using var stream = fileInfo.OpenRead();
        var checksum = Convert.ToHexString(SHA256.HashData(stream));
        return new GeneratedExportFile(fileName, contentType, filePath, fileInfo.Length, checksum);
    }

    private static void GeneratePdf(ExportRequest request, string filePath)
    {
        QuestPDF.Settings.License = LicenseType.Community;
        Document.Create(document =>
        {
            document.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(style => style.FontSize(11));
                page.Header()
                    .Text("ClubReportHub - Data Export Report")
                    .SemiBold()
                    .FontSize(18);
                page.Content().PaddingVertical(20).Column(column =>
                {
                    column.Spacing(10);
                    column.Item().Text($"Request ID: {request.Id}");
                    column.Item().Text($"File type: {request.ExportType}");
                    column.Item().Text($"Scope: {request.Scope}");
                    column.Item().Text($"Reporting period: {request.Period ?? "All"}");
                    column.Item().Text($"Club: {(request.ClubId?.ToString() ?? "All")}");
                    column.Item().Text($"Requested by: {request.RequestedByName}");
                    column.Item().Text($"Created at: {request.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
                });
                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span("Trang ");
                    text.CurrentPageNumber();
                });
            });
        }).GeneratePdf(filePath);
    }

    private static void GenerateExcel(ExportRequest request, string filePath)
    {
        using var workbook = new XLWorkbook();
        var worksheet = workbook.Worksheets.Add("Export");
        worksheet.Cell("A1").Value = "ClubReportHub - Data Export Report";
        worksheet.Range("A1:B1").Merge().Style.Font.SetBold().Font.SetFontSize(16);

        var rows = new (string Label, object Value)[]
        {
            ("Request ID", request.Id),
            ("File type", request.ExportType),
            ("Scope", request.Scope),
            ("Reporting period", request.Period ?? "All"),
            ("Club", request.ClubId?.ToString() ?? "All"),
            ("Requested by", request.RequestedByName),
            ("Created at", request.CreatedAtUtc.UtcDateTime)
        };

        for (var index = 0; index < rows.Length; index++)
        {
            var row = index + 3;
            worksheet.Cell(row, 1).Value = rows[index].Label;
            worksheet.Cell(row, 1).Style.Font.Bold = true;
            worksheet.Cell(row, 2).Value = XLCellValue.FromObject(rows[index].Value);
        }

        worksheet.Columns().AdjustToContents();
        workbook.SaveAs(filePath);
    }
}
