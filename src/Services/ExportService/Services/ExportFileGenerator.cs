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
                    .Text("ClubReportHub - Báo cáo xuất dữ liệu")
                    .SemiBold()
                    .FontSize(18);
                page.Content().PaddingVertical(20).Column(column =>
                {
                    column.Spacing(10);
                    column.Item().Text($"Mã yêu cầu: {request.Id}");
                    column.Item().Text($"Loại tệp: {request.ExportType}");
                    column.Item().Text($"Phạm vi: {request.Scope}");
                    column.Item().Text($"Kỳ báo cáo: {request.Period ?? "Tất cả"}");
                    column.Item().Text($"Câu lạc bộ: {(request.ClubId?.ToString() ?? "Tất cả")}");
                    column.Item().Text($"Người yêu cầu: {request.RequestedByName}");
                    column.Item().Text($"Thời điểm tạo: {request.CreatedAtUtc:dd/MM/yyyy HH:mm:ss} UTC");
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
        worksheet.Cell("A1").Value = "ClubReportHub - Báo cáo xuất dữ liệu";
        worksheet.Range("A1:B1").Merge().Style.Font.SetBold().Font.SetFontSize(16);

        var rows = new (string Label, object Value)[]
        {
            ("Mã yêu cầu", request.Id),
            ("Loại tệp", request.ExportType),
            ("Phạm vi", request.Scope),
            ("Kỳ báo cáo", request.Period ?? "Tất cả"),
            ("Câu lạc bộ", request.ClubId?.ToString() ?? "Tất cả"),
            ("Người yêu cầu", request.RequestedByName),
            ("Thời điểm tạo", request.CreatedAtUtc.UtcDateTime)
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
