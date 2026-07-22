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
        
        ExportService.Contracts.ReportExportSnapshot? snapshot = null;
        if (!string.IsNullOrWhiteSpace(request.SnapshotJson))
        {
            try
            {
                snapshot = System.Text.Json.JsonSerializer.Deserialize<ExportService.Contracts.ReportExportSnapshot>(
                    request.SnapshotJson,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { }
        }

        Document.Create(document =>
        {
            document.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(40);
                page.DefaultTextStyle(style => style.FontSize(11));
                
                if (snapshot != null)
                {
                    page.Header().Text($"Club Activity Report: {snapshot.ClubName}").SemiBold().FontSize(18).FontColor(Colors.Blue.Darken2);
                    page.Content().PaddingVertical(20).Column(column =>
                    {
                        column.Spacing(15);
                        column.Item().Text($"Period: {snapshot.Period}").SemiBold().FontSize(14);
                        column.Item().Text($"Status: {snapshot.Status}");
                        if (snapshot.SubmittedAtUtc.HasValue)
                        {
                            column.Item().Text($"Submitted at: {snapshot.SubmittedAtUtc.Value:yyyy-MM-dd HH:mm:ss} UTC");
                        }
                        
                        column.Item().LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                        
                        if (!string.IsNullOrWhiteSpace(snapshot.ExecutiveSummary))
                        {
                            column.Item().Text("Executive Summary").SemiBold().FontSize(14);
                            column.Item().Text(snapshot.ExecutiveSummary);
                        }
                        
                        if (!string.IsNullOrWhiteSpace(snapshot.Achievements))
                        {
                            column.Item().Text("Achievements").SemiBold().FontSize(14);
                            column.Item().Text(snapshot.Achievements);
                        }
                        
                        if (!string.IsNullOrWhiteSpace(snapshot.Challenges))
                        {
                            column.Item().Text("Challenges").SemiBold().FontSize(14);
                            column.Item().Text(snapshot.Challenges);
                        }
                        
                        if (!string.IsNullOrWhiteSpace(snapshot.Recommendations))
                        {
                            column.Item().Text("Recommendations").SemiBold().FontSize(14);
                            column.Item().Text(snapshot.Recommendations);
                        }
                        
                        if (!string.IsNullOrWhiteSpace(snapshot.NextPeriodPlan))
                        {
                            column.Item().Text("Next Period Plan").SemiBold().FontSize(14);
                            column.Item().Text(snapshot.NextPeriodPlan);
                        }
                        
                        column.Item().LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                        
                        column.Item().Text("Activities Summary").SemiBold().FontSize(14);
                        column.Item().Text($"Total Activities: {snapshot.TotalActivities}");
                        column.Item().Text($"Total Participants: {snapshot.TotalParticipants}");
                        column.Item().Text($"Total Budget Spent: {snapshot.TotalBudgetSpent:C}");
                        
                        if (snapshot.Details != null && snapshot.Details.Count > 0)
                        {
                            column.Item().PaddingTop(10).Table(table =>
                            {
                                table.ColumnsDefinition(columns =>
                                {
                                    columns.RelativeColumn(3);
                                    columns.RelativeColumn(2);
                                    columns.RelativeColumn(2);
                                    columns.RelativeColumn(2);
                                });
                                
                                table.Header(header =>
                                {
                                    header.Cell().BorderBottom(1).Padding(2).Text("Activity Name").SemiBold();
                                    header.Cell().BorderBottom(1).Padding(2).Text("Date").SemiBold();
                                    header.Cell().BorderBottom(1).Padding(2).Text("Participants").SemiBold();
                                    header.Cell().BorderBottom(1).Padding(2).Text("Budget").SemiBold();
                                });
                                
                                foreach (var detail in snapshot.Details)
                                {
                                    table.Cell().Padding(2).Text(detail.ActivityName);
                                    table.Cell().Padding(2).Text(detail.ActivityDate?.ToString("yyyy-MM-dd") ?? "N/A");
                                    table.Cell().Padding(2).Text(detail.ParticipantCount.ToString());
                                    table.Cell().Padding(2).Text(detail.BudgetSpent?.ToString("C") ?? "N/A");
                                }
                            });
                        }
                        
                        if (snapshot.Feedback != null && snapshot.Feedback.Count > 0)
                        {
                            column.Item().LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                            column.Item().Text("Feedback").SemiBold().FontSize(14);
                            foreach (var f in snapshot.Feedback)
                            {
                                column.Item().PaddingLeft(10).Column(fCol =>
                                {
                                    fCol.Item().Text($"{f.ReviewerName} ({f.CreatedAtUtc:yyyy-MM-dd HH:mm})").SemiBold();
                                    fCol.Item().Text(f.Message).Italic();
                                });
                            }
                        }
                    });
                }
                else
                {
                    // Fallback to metadata
                    page.Header().Text("ClubReportHub - Data Export Report").SemiBold().FontSize(18);
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
                }
                
                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span("Page ");
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
