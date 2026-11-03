using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Dapper;
using Microsoft.Data.SqlClient;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SchoolEvaluationERP.Models;

namespace SchoolEvaluationERP.Services
{
    public class FinalReportService
    {
        private readonly string _connectionString;

        public FinalReportService() : this(AppConfigService.ConnectionString) { }

        public FinalReportService(string connectionString)
        {
            _connectionString = connectionString;
        }

        private class RawWeeklyEvaluatorScore
        {
            public int EmployeeID { get; set; }
            public int EvaluatorID { get; set; }
            public string EvaluatorName { get; set; } = string.Empty;
            public int WeekNumber { get; set; }
            public decimal RawWeekSumOutOf100 { get; set; }
            public decimal SnapshotWeight { get; set; }
        }

        public async Task<MonthlyFinalReportSummary> GenerateMonthlyReportAsync(
            int year,
            int month,
            string debitAccountTitle,
            decimal maxIncentiveAmount,
            decimal minIncentiveAmount,
            bool roundToNearest50,
            int? filterBranchId = null,
            int? filterCategoryId = null,
            string searchQuery = "",
            bool onlyDeservingIncentive = false)
        {
            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            string empSql = @"
                SELECT e.EmployeeID, e.SystemName, e.EvaluationName,
                       e.BranchID, b.BranchName,
                       j.JobTitle,
                       e.CategoryID, c.CategoryName
                FROM dbo.Employees e
                INNER JOIN dbo.Branches b ON e.BranchID = b.BranchID
                INNER JOIN dbo.Jobs j ON e.JobID = j.JobID
                INNER JOIN dbo.Categories c ON e.CategoryID = c.CategoryID
                WHERE e.IsActive = 1
                  AND (@BranchID IS NULL OR e.BranchID = @BranchID)
                  AND (@CategoryID IS NULL OR e.CategoryID = @CategoryID)
                  AND (@Search = '' OR e.EvaluationName LIKE @Pattern OR e.SystemName LIKE @Pattern OR j.JobTitle LIKE @Pattern)
                ORDER BY b.BranchName ASC, e.EvaluationName ASC";

            var employees = (await conn.QueryAsync<EmployeeMonthlyReportRowDto>(empSql, new
            {
                BranchID = filterBranchId,
                CategoryID = filterCategoryId,
                Search = searchQuery.Trim(),
                Pattern = $"%{searchQuery.Trim()}%"
            })).ToList();

            var catEvaluators = (await conn.QueryAsync<dynamic>(@"
                SELECT ce.CategoryID, ce.EvaluatorID, ev.EvaluatorName, ce.WeightScore
                FROM dbo.CategoryEvaluators ce
                INNER JOIN dbo.Evaluators ev ON ce.EvaluatorID = ev.EvaluatorID")).ToList();

            var catEvalMap = catEvaluators
                .GroupBy(x => (int)x.CategoryID)
                .ToDictionary(g => g.Key, g => g.ToList());

            string scoresSql = @"
                SELECT es.EmployeeID,
                       er.EvaluatorID,
                       ev.EvaluatorName,
                       er.WeekNumber,
                       SUM(es.GivenScore) AS RawWeekSumOutOf100,
                       MAX(es.SnapshotEvaluatorWeight) AS SnapshotWeight
                FROM dbo.EvaluationScores es
                INNER JOIN dbo.EvaluationRecords er ON es.RecordID = er.RecordID
                INNER JOIN dbo.Evaluators ev ON er.EvaluatorID = ev.EvaluatorID
                WHERE er.EvalYear = @Year AND er.EvalMonth = @Month
                GROUP BY es.EmployeeID, er.EvaluatorID, ev.EvaluatorName, er.WeekNumber, es.SheetID";

            var rawScores = (await conn.QueryAsync<RawWeeklyEvaluatorScore>(scoresSql, new
            {
                Year = year,
                Month = month
            })).ToList();

            var scoresByEmployee = rawScores
                .GroupBy(s => s.EmployeeID)
                .ToDictionary(g => g.Key, g => g.ToList());

            var reportSummary = new MonthlyFinalReportSummary
            {
                Year = year,
                Month = month,
                DebitAccountTitle = debitAccountTitle,
                MaxIncentiveAmount = maxIncentiveAmount,
                MinIncentiveAmount = minIncentiveAmount,
                RoundToNearest50 = roundToNearest50
            };

            var finalRows = new List<EmployeeMonthlyReportRowDto>();

            foreach (var emp in employees)
            {
                scoresByEmployee.TryGetValue(emp.EmployeeID, out var empScores);
                empScores ??= new List<RawWeeklyEvaluatorScore>();

                catEvalMap.TryGetValue(emp.CategoryID, out var requiredCatEvals);
                requiredCatEvals ??= new List<dynamic>();

                var fallbackWeightByEvalId = requiredCatEvals.ToDictionary(
                    k => (int)k.EvaluatorID,
                    v => (decimal)v.WeightScore);

                var normalizedScores = empScores
                    .GroupBy(x => new { x.EvaluatorID, x.EvaluatorName, x.WeekNumber })
                    .Select(g =>
                    {
                        decimal rawOut100 = Math.Min(100m, g.Average(i => i.RawWeekSumOutOf100));
                        decimal weight = g.Max(i => i.SnapshotWeight);
                        if (weight <= 0m && fallbackWeightByEvalId.TryGetValue(g.Key.EvaluatorID, out decimal fbW))
                        {
                            weight = fbW;
                        }
                        if (weight <= 0m && requiredCatEvals.Count == 0)
                        {
                            weight = 100m;
                        }
                        decimal weightedScore = (rawOut100 / 100m) * weight;
                        return new
                        {
                            g.Key.EvaluatorID,
                            g.Key.EvaluatorName,
                            g.Key.WeekNumber,
                            RawOut100 = rawOut100,
                            Weight = weight,
                            WeightedScore = weightedScore
                        };
                    }).ToList();

                decimal? GetWeekTotal(int wNum)
                {
                    var weekItems = normalizedScores.Where(x => x.WeekNumber == wNum).ToList();
                    if (weekItems.Count == 0) return null;
                    return Math.Round(weekItems.Sum(x => x.WeightedScore), 2, MidpointRounding.AwayFromZero);
                }

                emp.Week1Score = GetWeekTotal(1);
                emp.Week2Score = GetWeekTotal(2);
                emp.Week3Score = GetWeekTotal(3);
                emp.Week4Score = GetWeekTotal(4);

                var distinctWeeks = normalizedScores.Select(x => x.WeekNumber).Distinct().ToList();
                emp.ActualWeeksCount = distinctWeeks.Count;

                var byEvaluator = normalizedScores
                    .GroupBy(x => new { x.EvaluatorID, x.EvaluatorName })
                    .Select(g =>
                    {
                        int evalWeeksCount = g.Count();
                        decimal avgWeightedForEval = g.Sum(x => x.WeightedScore) / evalWeeksCount;
                        decimal evalWeight = g.Max(x => x.Weight);
                        return new
                        {
                            g.Key.EvaluatorID,
                            g.Key.EvaluatorName,
                            WeeksCount = evalWeeksCount,
                            AvgWeighted = Math.Round(avgWeightedForEval, 2, MidpointRounding.AwayFromZero),
                            MaxWeight = evalWeight
                        };
                    }).ToList();

                decimal monthlyTotalScore = byEvaluator.Sum(x => x.AvgWeighted);
                monthlyTotalScore = Math.Min(100m, Math.Round(monthlyTotalScore, 2, MidpointRounding.AwayFromZero));

                var breakdownParts = new List<string>();
                foreach (var evSummary in byEvaluator)
                {
                    breakdownParts.Add($"{evSummary.EvaluatorName}: {evSummary.AvgWeighted:0.##}/{evSummary.MaxWeight:0.##} ({evSummary.WeeksCount} أسابيع)");
                }

                var evaluatedIds = byEvaluator.Select(x => x.EvaluatorID).ToHashSet();
                var missingEvals = requiredCatEvals
                    .Where(r => !evaluatedIds.Contains((int)r.EvaluatorID))
                    .Select(r => (string)r.EvaluatorName)
                    .ToList();

                emp.IsAllEvaluatorsCompleted = missingEvals.Count == 0 && byEvaluator.Count > 0;

                if (missingEvals.Count > 0)
                {
                    breakdownParts.Add($"[ينقصه تقييم: {string.Join("، ", missingEvals)}]");
                }

                emp.EvaluatorsBreakdownSummary = breakdownParts.Count > 0
                    ? string.Join("  |  ", breakdownParts)
                    : "لم يتم إدخال تقييمات لهذا الشهر بعد";

                var calc = IncentiveCalculatorService.CalculateMonthlyIncentive(
                    emp.SystemName,
                    emp.BranchName,
                    monthlyTotalScore,
                    maxIncentiveAmount,
                    minIncentiveAmount,
                    year,
                    month,
                    debitAccountTitle,
                    roundToNearest50);

                emp.MonthlyEvaluationAverage = calc.EvaluationAveragePercent;
                emp.ExactIncentivePercent = calc.ExactIncentivePercent;
                emp.DisplayedIncentivePercent = calc.DisplayedIncentivePercent;
                emp.FinalIncentiveAmount = calc.FinalAmount;
                emp.DebitAccount = calc.DebitAccount;
                emp.CreditAccountSystemName = calc.CreditAccountSystemName;
                emp.StatementText = calc.StatementText;
                emp.EntryDateFormatted = calc.EntryDateFormatted;

                if (onlyDeservingIncentive && emp.FinalIncentiveAmount <= 0m)
                    continue;

                finalRows.Add(emp);
            }

            for (int i = 0; i < finalRows.Count; i++)
            {
                finalRows[i].RowNumber = i + 1;
            }

            reportSummary.Rows = finalRows;
            return reportSummary;
        }

        public void ExportMonthlyReportToExcel(MonthlyFinalReportSummary report, string filePath)
        {
            using var wb = new XLWorkbook();
            string monthName = IncentiveCalculatorService.GetArabicMonthName(report.Month);

            // الورقة الأولى: القيد المحاسبي للحوافز (مطابق لنموذج المدرسة)
            var wsJournal = wb.Worksheets.Add("القيد المحاسبي للحوافز");
            wsJournal.RightToLeft = true;

            string[] journalHeaders =
            {
                "مكان حساب الموظف",
                "الحساب المدين",
                "المبلغ",
                "الحساب الدائن",
                "البيان",
                "التاريخ"
            };

            for (int c = 0; c < journalHeaders.Length; c++)
            {
                var cell = wsJournal.Cell(1, c + 1);
                cell.Value = journalHeaders[c];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontSize = 12;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A8A");
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            }
            wsJournal.Row(1).Height = 28;

            int rowIdx = 2;
            foreach (var r in report.Rows)
            {
                wsJournal.Cell(rowIdx, 1).Value = r.BranchName;
                wsJournal.Cell(rowIdx, 2).Value = r.DebitAccount;
                wsJournal.Cell(rowIdx, 3).Value = r.FinalIncentiveAmount;
                wsJournal.Cell(rowIdx, 3).Style.NumberFormat.Format = "#,##0";
                wsJournal.Cell(rowIdx, 4).Value = r.CreditAccountSystemName;
                wsJournal.Cell(rowIdx, 5).Value = r.StatementText;
                wsJournal.Cell(rowIdx, 6).Value = r.EntryDateFormatted;

                for (int c = 1; c <= 6; c++)
                {
                    var cell = wsJournal.Cell(rowIdx, c);
                    cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    cell.Style.Border.OutsideBorderColor = XLColor.FromHtml("#CBD5E1");
                    cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                    if (c == 1 || c == 3 || c == 6)
                        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                }

                if (r.FinalIncentiveAmount > 0m)
                {
                    wsJournal.Cell(rowIdx, 3).Style.Font.Bold = true;
                    wsJournal.Cell(rowIdx, 3).Style.Font.FontColor = XLColor.FromHtml("#047857");
                }

                rowIdx++;
            }

            wsJournal.Cell(rowIdx, 1).Value = "الإجمالي العام للحوافز";
            wsJournal.Range(rowIdx, 1, rowIdx, 2).Merge();
            wsJournal.Cell(rowIdx, 3).Value = report.TotalIncentivePayout;
            wsJournal.Cell(rowIdx, 3).Style.NumberFormat.Format = "#,##0";
            wsJournal.Cell(rowIdx, 5).Value = $"إجمالي المستحقين: {report.DeservingEmployeesCount} من أصل {report.TotalEmployeesCount} موظف — شهر {monthName}/{report.Year} م";

            var totalRange = wsJournal.Range(rowIdx, 1, rowIdx, 6);
            totalRange.Style.Font.Bold = true;
            totalRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#EFF6FF");
            totalRange.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;

            wsJournal.Columns().AdjustToContents();

            // الورقة الثانية: تفاصيل التقييم والأسابيع والمقيمين
            var wsDetails = wb.Worksheets.Add("تفاصيل التقييم والأسابيع");
            wsDetails.RightToLeft = true;

            string[] detailHeaders =
            {
                "م",
                "الاسم التقييمي",
                "الاسم في النظام",
                "الفرع",
                "الوظيفة",
                "الفئة",
                "الأسبوع 1",
                "الأسبوع 2",
                "الأسبوع 3",
                "الأسبوع 4",
                "عدد الأسابيع الفعلية",
                "تفصيل درجات المقيمين",
                "معدل التقييم الشهري %",
                "نسبة الحافز المستحقة %",
                "مبلغ الحافز المعتمد"
            };

            for (int c = 0; c < detailHeaders.Length; c++)
            {
                var cell = wsDetails.Cell(1, c + 1);
                cell.Value = detailHeaders[c];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#0F172A");
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            }
            wsDetails.Row(1).Height = 26;

            int dRow = 2;
            foreach (var r in report.Rows)
            {
                wsDetails.Cell(dRow, 1).Value = r.RowNumber;
                wsDetails.Cell(dRow, 2).Value = r.EvaluationName;
                wsDetails.Cell(dRow, 3).Value = r.SystemName;
                wsDetails.Cell(dRow, 4).Value = r.BranchName;
                wsDetails.Cell(dRow, 5).Value = r.JobTitle;
                wsDetails.Cell(dRow, 6).Value = r.CategoryName;
                wsDetails.Cell(dRow, 7).Value = r.Week1Display;
                wsDetails.Cell(dRow, 8).Value = r.Week2Display;
                wsDetails.Cell(dRow, 9).Value = r.Week3Display;
                wsDetails.Cell(dRow, 10).Value = r.Week4Display;
                wsDetails.Cell(dRow, 11).Value = r.ActualWeeksCount;
                wsDetails.Cell(dRow, 12).Value = r.EvaluatorsBreakdownSummary;
                wsDetails.Cell(dRow, 13).Value = r.MonthlyEvaluationAverage;
                wsDetails.Cell(dRow, 14).Value = r.DisplayedIncentivePercent;
                wsDetails.Cell(dRow, 15).Value = r.FinalIncentiveAmount;
                wsDetails.Cell(dRow, 15).Style.NumberFormat.Format = "#,##0";

                for (int c = 1; c <= 15; c++)
                {
                    wsDetails.Cell(dRow, c).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    wsDetails.Cell(dRow, c).Style.Border.OutsideBorderColor = XLColor.FromHtml("#E2E8F0");
                }

                dRow++;
            }

            wsDetails.Columns().AdjustToContents();
            wb.SaveAs(filePath);
        }

        public void ExportMonthlyReportToPdf(MonthlyFinalReportSummary report, string filePath)
        {
            string monthName = IncentiveCalculatorService.GetArabicMonthName(report.Month);

            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4.Landscape());
                    page.Margin(1.0f, Unit.Centimetre);
                    page.PageColor(Colors.White);
                    page.ContentFromRightToLeft();
                    page.DefaultTextStyle(x => x.FontFamily("Arial").FontSize(9.5f).FontColor(Colors.Grey.Darken4));

                    page.Header().PaddingBottom(8).BorderBottom(1.5f).BorderColor(Colors.Blue.Darken3).Row(row =>
                    {
                        row.RelativeItem().Column(col =>
                        {
                            col.Item().Text($"كشف القيد المحاسبي وصرف الحوافز الشهرية — شهر {monthName} / {report.Year} م")
                               .FontSize(15).Bold().FontColor(Colors.Blue.Darken4);
                            col.Item().PaddingTop(3).Text($"الحساب المدين: {report.DebitAccountTitle}   |   سقف الحافز: {report.MaxIncentiveAmount:N0}   |   الحد الأدنى: {report.MinIncentiveAmount:N0}")
                               .FontSize(10).SemiBold();
                        });

                        row.ConstantItem(220).AlignLeft().Column(col =>
                        {
                            col.Item().Text($"تاريخ الإصدار: {DateTime.Now:yyyy-MM-dd}").FontSize(9.5f);
                            col.Item().PaddingTop(3).Text($"إجمالي الموظفين: {report.TotalEmployeesCount}  |  المستحقون: {report.DeservingEmployeesCount}")
                               .FontSize(9.5f).Bold().FontColor(Colors.Green.Darken3);
                        });
                    });

                    page.Content().PaddingVertical(6).Column(mainCol =>
                    {
                        mainCol.Item().Table(table =>
                        {
                            table.ColumnsDefinition(cols =>
                            {
                                cols.ConstantColumn(26);
                                cols.ConstantColumn(60);
                                cols.RelativeColumn(1.5f);
                                cols.ConstantColumn(55);
                                cols.RelativeColumn(1.7f);
                                cols.ConstantColumn(45);
                                cols.ConstantColumn(45);
                                cols.RelativeColumn(2.2f);
                                cols.ConstantColumn(62);
                            });

                            table.Header(header =>
                            {
                                header.Cell().Element(PdfHeaderStyle).Text("م");
                                header.Cell().Element(PdfHeaderStyle).Text("مكان الحساب");
                                header.Cell().Element(PdfHeaderStyle).Text("الحساب المدين");
                                header.Cell().Element(PdfHeaderStyle).Text("المبلغ");
                                header.Cell().Element(PdfHeaderStyle).Text("الحساب الدائن");
                                header.Cell().Element(PdfHeaderStyle).Text("المعدل");
                                header.Cell().Element(PdfHeaderStyle).Text("النسبة");
                                header.Cell().Element(PdfHeaderStyle).Text("البيان");
                                header.Cell().Element(PdfHeaderStyle).Text("التاريخ");
                            });

                            foreach (var r in report.Rows)
                            {
                                table.Cell().Element(PdfBodyStyle).AlignCenter().Text(r.RowNumber.ToString());
                                table.Cell().Element(PdfBodyStyle).AlignCenter().Text(r.BranchName);
                                table.Cell().Element(PdfBodyStyle).Text(r.DebitAccount).FontSize(8.5f);
                                table.Cell().Element(PdfBodyStyle).AlignCenter().Text(r.FinalAmountDisplay).Bold().FontColor(r.FinalIncentiveAmount > 0 ? Colors.Green.Darken3 : Colors.Grey.Medium);
                                table.Cell().Element(PdfBodyStyle).Text(r.CreditAccountSystemName).SemiBold();
                                table.Cell().Element(PdfBodyStyle).AlignCenter().Text(r.MonthlyAverageDisplay);
                                table.Cell().Element(PdfBodyStyle).AlignCenter().Text(r.IncentivePercentDisplay);
                                table.Cell().Element(PdfBodyStyle).Text(r.StatementText).FontSize(8.5f);
                                table.Cell().Element(PdfBodyStyle).AlignCenter().Text(r.EntryDateFormatted).FontSize(8.5f);
                            }
                        });

                        mainCol.Item().PaddingTop(10).Background(Colors.Blue.Lighten5).Border(1).BorderColor(Colors.Blue.Darken2).Padding(8).Row(sumRow =>
                        {
                            sumRow.RelativeItem().Text($"إجمالي مبلغ الحوافز المعتمد للصرف لشهر {monthName} / {report.Year} م:   {report.TotalIncentivePayout:N0} ريال")
                                  .FontSize(12).Bold().FontColor(Colors.Blue.Darken4);

                            sumRow.ConstantItem(260).AlignLeft().Text($"متوسط الأداء العام: {report.OverallAverageScore:0.##}%   |   عدد المستحقين: {report.DeservingEmployeesCount}")
                                  .FontSize(10).SemiBold();
                        });

                        mainCol.Item().PaddingTop(18).Row(signRow =>
                        {
                            signRow.RelativeItem().AlignCenter().Text("إعداد الموارد البشرية / التقييم:\n\n.......................................").FontSize(10).SemiBold();
                            signRow.RelativeItem().AlignCenter().Text("مراجعة الإدارة المالية والمحاسبة:\n\n.......................................").FontSize(10).SemiBold();
                            signRow.RelativeItem().AlignCenter().Text("اعتماد المدير العام:\n\n.......................................").FontSize(10).SemiBold();
                        });
                    });

                    page.Footer().PaddingTop(4).BorderTop(0.5f).BorderColor(Colors.Grey.Lighten1).Row(row =>
                    {
                        row.RelativeItem().Text("نظام تقييم موظفي المدارس واحتساب الحوافز الشهرية — كشف رسمي مستخرج آلياً").FontSize(8.5f).FontColor(Colors.Grey.Darken2);
                        row.ConstantItem(100).AlignLeft().Text(txt =>
                        {
                            txt.Span("صفحة ").FontSize(8.5f);
                            txt.CurrentPageNumber().FontSize(8.5f).Bold();
                            txt.Span(" من ").FontSize(8.5f);
                            txt.TotalPages().FontSize(8.5f).Bold();
                        });
                    });
                });
            }).GeneratePdf(filePath);
        }

        private static IContainer PdfHeaderStyle(IContainer container)
        {
            return container
                .Border(0.75f)
                .BorderColor(Colors.Grey.Darken1)
                .Background(Colors.Blue.Lighten5)
                .PaddingVertical(5)
                .PaddingHorizontal(4)
                .AlignCenter()
                .AlignMiddle();
        }

        private static IContainer PdfBodyStyle(IContainer container)
        {
            return container
                .Border(0.5f)
                .BorderColor(Colors.Grey.Lighten1)
                .PaddingVertical(4)
                .PaddingHorizontal(4)
                .AlignMiddle();
        }
    }
}
