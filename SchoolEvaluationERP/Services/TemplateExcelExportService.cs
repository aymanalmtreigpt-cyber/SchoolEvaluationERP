using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using SchoolEvaluationERP.Models;

namespace SchoolEvaluationERP.Services
{
    /// <summary>
    /// تصدير قالب التقييم إلى ملف Excel منسق وجاهز للتعبئة أو الطباعة،
    /// مع دعم استيراد الدرجات الشهرية مباشرة من نفس ملف الإكسل إلى شاشة الإدخال الشهري.
    /// </summary>
    public class ExcelImportMismatchEntry
    {
        public string SheetName { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
    }

    public class TemplateExcelImportResult
    {
        public bool IsSuccess { get; set; }
        public string ErrorOrHintMessage { get; set; } = string.Empty;
        public int TotalSystemSheetsCount { get; set; }
        public int MatchedSheetsCount { get; set; }
        public int ImportedEmployeesCount { get; set; }
        public int MatchedEmptyEmployeesCount { get; set; }
        public int ClampedWarningsCount { get; set; }

        public List<string> MissingSheetsInExcel { get; set; } = new();
        public List<string> ExtraSheetsInExcel { get; set; } = new();
        public List<ExcelImportMismatchEntry> MissingEmployeesInExcel { get; set; } = new();
        public List<ExcelImportMismatchEntry> ExtraEmployeesInExcel { get; set; } = new();
        public List<ExcelImportMismatchEntry> CriteriaColumnWarnings { get; set; } = new();

        public List<(WeeklyScoreRowViewModel Row, int CriteriaIndex, int Score)> PendingUpdates { get; set; } = new();
    }

    public static class TemplateExcelExportService
    {
        public static void GenerateAllEvaluatorsEmployeesExcel(
            List<EvaluatorEmployeesSheetExportDto> evaluatorSheets,
            string outputFilePath)
        {
            using var wb = new XLWorkbook();
            var usedSheetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var duplicateEvalNames = evaluatorSheets
                .GroupBy(e => e.EvaluatorName.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            int sheetIndex = 1;
            foreach (var evalSheet in evaluatorSheets)
            {
                string desiredTabTitle = evalSheet.EvaluatorName.Trim();
                if (duplicateEvalNames.Contains(desiredTabTitle) && !string.IsNullOrWhiteSpace(evalSheet.GroupName))
                {
                    desiredTabTitle = $"{evalSheet.EvaluatorName.Trim()} - {evalSheet.GroupName.Trim()}";
                }

                string safeTabName = MakeValidWorksheetName(desiredTabTitle, sheetIndex++, usedSheetNames);
                usedSheetNames.Add(safeTabName);

                var ws = wb.Worksheets.Add(safeTabName);
                ws.RightToLeft = true;

                int totalCols = 9;

                string groupBadge = string.IsNullOrWhiteSpace(evalSheet.GroupName)
                    ? string.Empty
                    : $" (مجموعة: {evalSheet.GroupName})";

                ws.Cell(1, 1).Value = $"كشف بيانات الموظفين التابعين للمقيم: {evalSheet.EvaluatorName}{groupBadge}";
                var titleRange = ws.Range(1, 1, 1, totalCols);
                titleRange.Merge();
                titleRange.Style.Font.Bold = true;
                titleRange.Style.Font.FontSize = 13.5;
                titleRange.Style.Font.FontColor = XLColor.White;
                titleRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A8A");
                titleRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                titleRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                ws.Row(1).Height = 30;

                int totalEmps = evalSheet.Employees.Count;
                int continuingCount = evalSheet.Employees.Count(e => e.IsContinuing);
                int discontinuedCount = totalEmps - continuingCount;
                int withIncentiveCount = evalSheet.Employees.Count(e => e.HasIncentive);
                int withoutIncentiveCount = totalEmps - withIncentiveCount;

                ws.Cell(2, 1).Value =
                    $"إجمالي الموظفين: {totalEmps}   |   مستمر: {continuingCount}   |   منقطع: {discontinuedCount}   |   بحافز: {withIncentiveCount}   |   بدون حافز: {withoutIncentiveCount}";
                var summaryRange = ws.Range(2, 1, 2, totalCols);
                summaryRange.Merge();
                summaryRange.Style.Font.Bold = true;
                summaryRange.Style.Font.FontSize = 10.5;
                summaryRange.Style.Font.FontColor = XLColor.FromHtml("#1E40AF");
                summaryRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#EFF6FF");
                summaryRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                summaryRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                ws.Row(2).Height = 24;

                int headerRow = 4;
                string[] headers =
                {
                    "م",
                    "الاسم التقييمي للموظف",
                    "الاسم في النظام",
                    "الفرع",
                    "فئة الموظف",
                    "الوظيفة",
                    "نوع الوظيفة",
                    "الحافز (بحافز / بدون حافز)",
                    "حالة الدوام (مستمر / منقطع)"
                };

                for (int c = 0; c < headers.Length; c++)
                {
                    ws.Cell(headerRow, c + 1).Value = headers[c];
                }

                var headerRange = ws.Range(headerRow, 1, headerRow, totalCols);
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Font.FontSize = 11;
                headerRange.Style.Font.FontColor = XLColor.White;
                headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#0F172A");
                headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                headerRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                headerRange.Style.Alignment.WrapText = true;
                headerRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                headerRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                ws.Row(headerRow).Height = 28;

                int rowIdx = headerRow + 1;
                int seq = 1;

                foreach (var emp in evalSheet.Employees)
                {
                    ws.Cell(rowIdx, 1).Value = seq++;
                    ws.Cell(rowIdx, 2).Value = emp.EvaluationName;
                    ws.Cell(rowIdx, 3).Value = emp.SystemName;
                    ws.Cell(rowIdx, 4).Value = emp.BranchName;
                    ws.Cell(rowIdx, 5).Value = emp.CategoryName;
                    ws.Cell(rowIdx, 6).Value = emp.JobTitle;
                    ws.Cell(rowIdx, 7).Value = emp.JobTypeName;
                    ws.Cell(rowIdx, 8).Value = emp.IncentiveStatusText;
                    ws.Cell(rowIdx, 9).Value = emp.ContinuingStatusText;

                    var rRange = ws.Range(rowIdx, 1, rowIdx, totalCols);
                    rRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    rRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                    rRange.Style.Border.OutsideBorderColor = XLColor.FromHtml("#CBD5E1");
                    rRange.Style.Border.InsideBorderColor = XLColor.FromHtml("#CBD5E1");
                    rRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

                    if (seq % 2 == 1)
                    {
                        rRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#F8FAFC");
                    }

                    ws.Cell(rowIdx, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Cell(rowIdx, 2).Style.Font.Bold = true;
                    ws.Cell(rowIdx, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Cell(rowIdx, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Cell(rowIdx, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Cell(rowIdx, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                    var incCell = ws.Cell(rowIdx, 8);
                    incCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    incCell.Style.Font.Bold = true;
                    if (emp.HasIncentive)
                    {
                        incCell.Style.Font.FontColor = XLColor.FromHtml("#047857");
                        incCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#ECFDF5");
                    }
                    else
                    {
                        incCell.Style.Font.FontColor = XLColor.FromHtml("#B45309");
                        incCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#FFFBEB");
                    }

                    var contCell = ws.Cell(rowIdx, 9);
                    contCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    contCell.Style.Font.Bold = true;
                    if (emp.IsContinuing)
                    {
                        contCell.Style.Font.FontColor = XLColor.FromHtml("#1E40AF");
                    }
                    else
                    {
                        contCell.Style.Font.FontColor = XLColor.FromHtml("#B91C1C");
                        contCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF2F2");
                    }

                    ws.Row(rowIdx).Height = 23;
                    rowIdx++;
                }

                if (evalSheet.Employees.Count > 0)
                {
                    ws.Range(headerRow, 1, rowIdx - 1, totalCols).SetAutoFilter();
                }
                ws.SheetView.FreezeRows(headerRow);

                ws.Columns(1, totalCols).AdjustToContents();
                ws.Column(1).Width = 7;
                if (ws.Column(2).Width < 26) ws.Column(2).Width = 26;
                if (ws.Column(3).Width < 30) ws.Column(3).Width = 30;
                if (ws.Column(4).Width < 12) ws.Column(4).Width = 12;
                if (ws.Column(5).Width < 16) ws.Column(5).Width = 16;
                if (ws.Column(6).Width < 16) ws.Column(6).Width = 16;
                if (ws.Column(7).Width < 18) ws.Column(7).Width = 18;
                if (ws.Column(8).Width < 22) ws.Column(8).Width = 22;
                if (ws.Column(9).Width < 22) ws.Column(9).Width = 22;

                ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
                ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
                ws.PageSetup.FitToPages(1, 0);
            }

            wb.SaveAs(outputFilePath);
        }

        public static void GenerateTemplateExcel(
            TemplateFullModel template,
            string outputFilePath,
            int? evalYear = null,
            int? evalMonth = null,
            string? protectionPassword = null)
        {
            using var wb = new XLWorkbook();
            var usedSheetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int targetYear = evalYear ?? (template.RequestedYear > 0 ? template.RequestedYear : DateTime.Now.Year);
            int targetMonth = evalMonth ?? (template.RequestedMonth > 0 ? template.RequestedMonth : DateTime.Now.Month);
            string monthArabicName = IncentiveCalculatorService.GetArabicMonthName(targetMonth);
            string effectivePassword = string.IsNullOrWhiteSpace(protectionPassword)
                ? TemplateService.DefaultExcelProtectionPassword
                : protectionPassword.Trim();

            for (int sIdx = 0; sIdx < template.Sheets.Count; sIdx++)
            {
                var sheet = template.Sheets[sIdx];
                string safeTabName = MakeValidWorksheetName(sheet.SheetName, sIdx + 1, usedSheetNames);
                usedSheetNames.Add(safeTabName);

                var ws = wb.Worksheets.Add(safeTabName);
                ws.RightToLeft = true;

                int criteriaCount = sheet.CriteriaColumns.Count;
                // الأعمدة: 1:م | 2:كود الموظف | 3:الاسم التقييمي | 4:الفرع | 5:الوظيفة ونوعها | 6..(5+criteriaCount):المبادئ | الأخير:المجموع
                int totalCols = 6 + criteriaCount;

                // الصف 1: ترويسة الورقة
                ws.Cell(1, 1).Value = $"استمارة التقييم الشهري — المقيم: {template.EvaluatorName} — الورقة: {sheet.SheetName} ({sheet.CategoryWithBranchDisplay})";
                var titleRange = ws.Range(1, 1, 1, totalCols);
                titleRange.Merge();
                titleRange.Style.Font.Bold = true;
                titleRange.Style.Font.FontSize = 13;
                titleRange.Style.Font.FontColor = XLColor.White;
                titleRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A8A");
                titleRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                titleRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                ws.Row(1).Height = 30;

                // الصف 2: تعليمات الإدخال
                ws.Cell(2, 1).Value = template.IsSystemTemplate
                    ? $"تعليمات: أدخل عدد مرات ({template.EvaluatorName}) كرقم صحيح بين 0 و 31 (الحد المسموح: {template.MaxAllowedCount}). خلايا الأسماء مقفلة ومحمية."
                    : "تعليمات: أدخل درجات صحيحة بدون كسور في أعمدة المبادئ المحددة باللون الفاتح فقط (استخدم Tab للانتقال أفقياً و Enter عمودياً). الأسماء والمجموع مقفلة ومحمية.";
                var subRange = ws.Range(2, 1, 2, totalCols);
                subRange.Merge();
                subRange.Style.Font.FontSize = 10.5;
                subRange.Style.Font.FontColor = XLColor.FromHtml("#1E40AF");
                subRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#EFF6FF");
                subRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                subRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                ws.Row(2).Height = 23;

                // الصف 3: شريط الفترة التقييمية (الشهر والسنة)
                ws.Cell(3, 1).Value = $"📅 الفترة التقييمية: شهر {monthArabicName} ({targetMonth:00}) — {targetYear} م   |   النسخة: v{template.VersionNumber}";
                var periodRange = ws.Range(3, 1, 3, totalCols);
                periodRange.Merge();
                periodRange.Style.Font.Bold = true;
                periodRange.Style.Font.FontSize = 11;
                periodRange.Style.Font.FontColor = XLColor.FromHtml("#065F46");
                periodRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#ECFDF5");
                periodRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                periodRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                periodRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                periodRange.Style.Border.OutsideBorderColor = XLColor.FromHtml("#A7F3D0");
                ws.Row(3).Height = 24;

                // الصف 4: رؤوس الجدول
                int headerRow = 4;
                ws.Cell(headerRow, 1).Value = "م";
                ws.Cell(headerRow, 2).Value = "رقم الموظف";
                ws.Cell(headerRow, 3).Value = "الاسم التقييمي للموظف";
                ws.Cell(headerRow, 4).Value = "الفرع";
                ws.Cell(headerRow, 5).Value = "الوظيفة ونوعها";

                for (int c = 0; c < criteriaCount; c++)
                {
                    var crit = sheet.CriteriaColumns[c];
                    ws.Cell(headerRow, 6 + c).Value = template.IsSystemTemplate
                        ? $"{crit.CriteriaTitle}\n(عدد من 0 إلى 31)"
                        : $"{crit.CriteriaTitle}\n(من {crit.MaxScore:0})";
                }

                ws.Cell(headerRow, totalCols).Value = template.IsSystemTemplate
                    ? "إجمالي العدد"
                    : "المجموع\n(من 100)";

                var headerRange = ws.Range(headerRow, 1, headerRow, totalCols);
                headerRange.Style.Font.Bold = true;
                headerRange.Style.Font.FontSize = 11;
                headerRange.Style.Font.FontColor = XLColor.White;
                headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#0F172A");
                headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                headerRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                headerRange.Style.Alignment.WrapText = true;
                headerRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                headerRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                ws.Row(headerRow).Height = 34;

                int firstDataRow = headerRow + 1;
                int rowIdx = firstDataRow;
                int seq = 1;
                foreach (var emp in sheet.EmployeeRows)
                {
                    ws.Cell(rowIdx, 1).Value = seq++;
                    ws.Cell(rowIdx, 2).Value = emp.EmployeeID;
                    ws.Cell(rowIdx, 3).Value = emp.EvaluationName;
                    ws.Cell(rowIdx, 4).Value = emp.BranchName;
                    ws.Cell(rowIdx, 5).Value = emp.FullJobDisplay;

                    for (int c = 0; c < criteriaCount; c++)
                    {
                        var scoreCell = ws.Cell(rowIdx, 6 + c);
                        scoreCell.Value = Blank.Value;
                        scoreCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#FFFBEB");
                        scoreCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        scoreCell.Style.Font.Bold = true;
                        scoreCell.Style.Font.FontColor = XLColor.FromHtml("#1E3A8A");
                        scoreCell.Style.Protection.SetLocked(false);
                    }

                    if (criteriaCount > 0)
                    {
                        string firstCritRef = ws.Cell(rowIdx, 6).Address.ToStringRelative();
                        string lastCritRef = ws.Cell(rowIdx, 5 + criteriaCount).Address.ToStringRelative();
                        var sumCell = ws.Cell(rowIdx, totalCols);
                        sumCell.FormulaA1 = $"IF(COUNT({firstCritRef}:{lastCritRef})=0,\"\",SUM({firstCritRef}:{lastCritRef}))";
                        sumCell.Style.Font.Bold = true;
                        sumCell.Style.Font.FontColor = XLColor.FromHtml("#047857");
                        sumCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#ECFDF5");
                        sumCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        sumCell.Style.Protection.SetLocked(true);
                    }

                    ws.Cell(rowIdx, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Cell(rowIdx, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Cell(rowIdx, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                    var rRange = ws.Range(rowIdx, 1, rowIdx, totalCols);
                    rRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    rRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                    rRange.Style.Border.OutsideBorderColor = XLColor.FromHtml("#CBD5E1");
                    rRange.Style.Border.InsideBorderColor = XLColor.FromHtml("#CBD5E1");
                    rRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                    ws.Row(rowIdx).Height = 24;

                    rowIdx++;
                }

                int lastDataRow = rowIdx - 1;

                // التحقق التلقائي من صحة الدرجات (Data Validation) لكل عمود مبدأ
                if (lastDataRow >= firstDataRow && criteriaCount > 0)
                {
                    for (int c = 0; c < criteriaCount; c++)
                    {
                        int maxAllowed = template.IsSystemTemplate
                            ? 31
                            : Math.Max(0, (int)Math.Round(sheet.CriteriaColumns[c].MaxScore, 0, MidpointRounding.AwayFromZero));

                        var colRange = ws.Range(firstDataRow, 6 + c, lastDataRow, 6 + c);
                        var dv = colRange.CreateDataValidation();
                        dv.IgnoreBlanks = true;
                        dv.WholeNumber.Between(0, maxAllowed);
                        dv.ErrorStyle = XLErrorStyle.Stop;
                        dv.ErrorTitle = template.IsSystemTemplate ? "قيمة عدد غير صالحة" : "درجة غير صالحة";
                        dv.ErrorMessage = template.IsSystemTemplate
                            ? $"يرجى إدخال عدد صحيح بدون كسور بين 0 و {maxAllowed} فقط."
                            : $"يرجى إدخال درجة صحيحة بدون كسور بين 0 و {maxAllowed} لهذا المبدأ ({sheet.CriteriaColumns[c].CriteriaTitle}).";
                        dv.ShowErrorMessage = true;
                    }

                    ws.Cell(firstDataRow, 6).SetActive();
                }

                ws.Columns(1, totalCols).AdjustToContents();
                ws.Column(2).Width = 11;
                if (ws.Column(3).Width < 26) ws.Column(3).Width = 26;
                for (int c = 0; c < criteriaCount; c++)
                {
                    if (ws.Column(6 + c).Width < 15)
                        ws.Column(6 + c).Width = 15;
                }

                // تجميد أول 4 صفوف وأول 5 أعمدة أثناء التمرير
                ws.SheetView.Freeze(headerRow, 5);

                ws.PageSetup.PageOrientation = XLPageOrientation.Portrait;
                ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
                ws.PageSetup.FitToPages(1, 0);

                // حماية الورقة بكلمة السر وحصر التحديد والتنقل على خلايا الدرجات المفتوحة فقط
                var sheetProtection = ws.Protect(effectivePassword);
                sheetProtection.AllowedElements = XLSheetProtectionElements.SelectUnlockedCells;
            }

            // حماية هيكل الملف لمنع حذف أو إعادة تسمية الأوراق
            wb.Protect(effectivePassword);

            wb.SaveAs(outputFilePath);
        }

        public static (int? Year, int? Month, string RawPeriodBanner) TryReadExcelTemplatePeriod(string excelFilePath)
        {
            if (string.IsNullOrWhiteSpace(excelFilePath) || !File.Exists(excelFilePath))
                return (null, null, string.Empty);

            try
            {
                using var wb = new XLWorkbook(excelFilePath);
                var ws = wb.Worksheets.FirstOrDefault();
                if (ws == null) return (null, null, string.Empty);

                string banner = WeeklyScoreRowViewModel.NormalizeAsciiDigits(ws.Cell(3, 1).GetString().Trim());
                if (string.IsNullOrWhiteSpace(banner))
                    return (null, null, string.Empty);

                int? foundMonth = null;
                int? foundYear = null;

                var monthNumMatch = System.Text.RegularExpressions.Regex.Match(banner, @"\((\d{1,2})\)");
                if (monthNumMatch.Success && int.TryParse(monthNumMatch.Groups[1].Value, out int parsedM) && parsedM >= 1 && parsedM <= 12)
                {
                    foundMonth = parsedM;
                }
                else
                {
                    for (int m = 1; m <= 12; m++)
                    {
                        string arName = IncentiveCalculatorService.GetArabicMonthName(m);
                        if (banner.Contains(arName, StringComparison.OrdinalIgnoreCase))
                        {
                            foundMonth = m;
                            break;
                        }
                    }
                }

                var yearMatch = System.Text.RegularExpressions.Regex.Match(banner, @"\b(20\d{2})\b");
                if (yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out int parsedY))
                {
                    foundYear = parsedY;
                }

                return (foundYear, foundMonth, banner);
            }
            catch
            {
                return (null, null, string.Empty);
            }
        }

        public static (int? Year, int? Month, string RawPeriodBanner, string EvaluatorName) TryReadExcelTemplateMetadata(string excelFilePath)
        {
            if (string.IsNullOrWhiteSpace(excelFilePath) || !File.Exists(excelFilePath))
                return (null, null, string.Empty, string.Empty);

            try
            {
                using var fs = new FileStream(excelFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                ms.Position = 0;

                using var wb = new XLWorkbook(ms);
                var ws = wb.Worksheets.FirstOrDefault();
                if (ws == null) return (null, null, string.Empty, string.Empty);

                // 1. قراءة اسم المقيم من الترويسة في الصف الأول
                string row1 = ws.Cell(1, 1).GetString().Trim();
                string foundEvaluator = string.Empty;
                var evalMatch = System.Text.RegularExpressions.Regex.Match(
                    row1,
                    @"المقيم\s*:\s*(.+?)(?:\s*[—\-–]\s*الورقة\s*:|$)");
                if (evalMatch.Success)
                {
                    foundEvaluator = evalMatch.Groups[1].Value.Trim();
                }

                // 2. قراءة الشهر والسنة من شريط الفترة في الصف الثالث
                string banner = WeeklyScoreRowViewModel.NormalizeAsciiDigits(ws.Cell(3, 1).GetString().Trim());
                int? foundMonth = null;
                int? foundYear = null;

                if (!string.IsNullOrWhiteSpace(banner))
                {
                    var monthNumMatch = System.Text.RegularExpressions.Regex.Match(banner, @"\((\d{1,2})\)");
                    if (monthNumMatch.Success && int.TryParse(monthNumMatch.Groups[1].Value, out int parsedM) && parsedM >= 1 && parsedM <= 12)
                    {
                        foundMonth = parsedM;
                    }
                    else
                    {
                        for (int m = 1; m <= 12; m++)
                        {
                            string arName = IncentiveCalculatorService.GetArabicMonthName(m);
                            if (banner.Contains(arName, StringComparison.OrdinalIgnoreCase))
                            {
                                foundMonth = m;
                                break;
                            }
                        }
                    }

                    var yearMatch = System.Text.RegularExpressions.Regex.Match(banner, @"\b(20\d{2})\b");
                    if (yearMatch.Success && int.TryParse(yearMatch.Groups[1].Value, out int parsedY))
                    {
                        foundYear = parsedY;
                    }
                }

                return (foundYear, foundMonth, banner, foundEvaluator);
            }
            catch
            {
                return (null, null, string.Empty, string.Empty);
            }
        }

        public static bool AreEvaluatorNamesEquivalent(string? nameA, string? nameB)
        {
            string a = NormalizeArabicForMatch(nameA);
            string b = NormalizeArabicForMatch(nameB);
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return true;
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        public static TemplateExcelImportResult ParseAndMatchExcelScores(
            WeeklyEvaluationWorkspaceModel workspace,
            string excelFilePath)
        {
            var result = new TemplateExcelImportResult
            {
                TotalSystemSheetsCount = workspace.Sheets.Count
            };

            if (!File.Exists(excelFilePath))
            {
                result.IsSuccess = false;
                result.ErrorOrHintMessage = "ملف الإكسل المحدد غير موجود.";
                return result;
            }

            try
            {
                using var fs = new FileStream(excelFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var ms = new MemoryStream();
                fs.CopyTo(ms);
                ms.Position = 0;

                using var wb = new XLWorkbook(ms);
                var matchedExcelSheets = new HashSet<IXLWorksheet>();
                var allExcelSheets = wb.Worksheets.ToList();

                for (int sIdx = 0; sIdx < workspace.Sheets.Count; sIdx++)
                {
                    var targetSheet = workspace.Sheets[sIdx];

                    // 1. مطابقة الورقة بالاسم فقط (بدون أي سحب تلقائي بالترتيب الرقمي)
                    IXLWorksheet? ws = FindMatchingWorksheetByName(allExcelSheets, matchedExcelSheets, targetSheet.SheetName);
                    if (ws == null)
                    {
                        result.MissingSheetsInExcel.Add(targetSheet.SheetName);
                        continue;
                    }

                    matchedExcelSheets.Add(ws);
                    result.MatchedSheetsCount++;

                    // 2. تحديد صف رؤوس الأعمدة (الصف 4) وعمود الاسم التقييمي وأعمدة مبادئ التقييم بالاسم
                    int headerRow = DetectHeaderRowNumber(ws);
                    int lastCol = Math.Max(ws.Row(headerRow).LastCellUsed()?.Address.ColumnNumber ?? 0, 6 + targetSheet.CriteriaColumns.Count);
                    int nameColIndex = DetectEvaluationNameColumn(ws, headerRow, lastCol);

                    var excelCriteriaCols = new List<(int ColIndex, string RawHeader, string CleanTitle)>();
                    for (int col = 1; col <= lastCol; col++)
                    {
                        if (col == nameColIndex) continue;
                        string rawHdr = ws.Cell(headerRow, col).GetString().Trim();
                        if (string.IsNullOrWhiteSpace(rawHdr)) continue;
                        if (IsFixedMetadataOrTotalColumn(rawHdr, col)) continue;

                        string cleanTitle = ExtractCriteriaTitleFromHeader(rawHdr);
                        if (!string.IsNullOrWhiteSpace(cleanTitle))
                        {
                            excelCriteriaCols.Add((col, rawHdr, cleanTitle));
                        }
                    }

                    var matchedExcelColIndices = new HashSet<int>();
                    var criteriaColMap = new List<(int SystemCriteriaIndex, int ExcelColIndex, string CriteriaTitle)>();

                    for (int c = 0; c < targetSheet.CriteriaColumns.Count; c++)
                    {
                        string sysTitle = CollapseSpaces(targetSheet.CriteriaColumns[c].CriteriaTitle.Trim());
                        string sysNorm = NormalizeArabicForMatch(sysTitle);

                        var matchedCol = excelCriteriaCols.FirstOrDefault(ec =>
                            !matchedExcelColIndices.Contains(ec.ColIndex) &&
                            string.Equals(ec.CleanTitle, sysTitle, StringComparison.OrdinalIgnoreCase));

                        if (matchedCol.ColIndex == 0)
                        {
                            matchedCol = excelCriteriaCols.FirstOrDefault(ec =>
                                !matchedExcelColIndices.Contains(ec.ColIndex) &&
                                string.Equals(NormalizeArabicForMatch(ec.CleanTitle), sysNorm, StringComparison.OrdinalIgnoreCase));
                        }

                        if (matchedCol.ColIndex > 0)
                        {
                            matchedExcelColIndices.Add(matchedCol.ColIndex);
                            criteriaColMap.Add((c, matchedCol.ColIndex, targetSheet.CriteriaColumns[c].CriteriaTitle));
                        }
                        else
                        {
                            result.CriteriaColumnWarnings.Add(new ExcelImportMismatchEntry
                            {
                                SheetName = targetSheet.SheetName,
                                ItemName = $"عمود مفقود: {targetSheet.CriteriaColumns[c].CriteriaTitle}",
                                Details = "مبدأ التقييم موجود في قالب النظام ولم يُعثر على عمود مطابق لاسمه في الصف 4 بملف الإكسل."
                            });
                        }
                    }

                    foreach (var extraCol in excelCriteriaCols)
                    {
                        if (!matchedExcelColIndices.Contains(extraCol.ColIndex))
                        {
                            result.CriteriaColumnWarnings.Add(new ExcelImportMismatchEntry
                            {
                                SheetName = targetSheet.SheetName,
                                ItemName = $"عمود إضافي: {extraCol.CleanTitle}",
                                Details = "موجود في الصف 4 بملف الإكسل ولا يوجد مبدأ تقييم مطابق لاسمه في قالب النظام (تم تجاهله)."
                            });
                        }
                    }

                    // 3. بناء فهرس الموظفين في النظام للمطابقة الحرفية بالاسم التقييمي فقط (بدون رقم الموظف)
                    var empByExactName = new Dictionary<string, List<WeeklyScoreRowViewModel>>(StringComparer.Ordinal);
                    foreach (var emp in targetSheet.EmployeeRows)
                    {
                        string key = (emp.EvaluationName ?? string.Empty).Trim();
                        if (string.IsNullOrEmpty(key)) continue;
                        if (!empByExactName.TryGetValue(key, out var bucket))
                        {
                            bucket = new List<WeeklyScoreRowViewModel>();
                            empByExactName[key] = bucket;
                        }
                        bucket.Add(emp);
                    }

                    var foundInExcelEmpIds = new HashSet<int>();
                    var importedInSheetEmpIds = new HashSet<int>();

                    int lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
                    for (int r = headerRow + 1; r <= lastRow; r++)
                    {
                        string excelName = ws.Cell(r, nameColIndex).GetString().Trim();
                        if (string.IsNullOrWhiteSpace(excelName))
                            continue;

                        if (string.Equals(excelName, "الاسم التقييمي للموظف", StringComparison.Ordinal) ||
                            string.Equals(excelName, "الاسم التقييمي", StringComparison.Ordinal))
                            continue;

                        // مطابقة حرفية تامة للاسم التقييمي داخل نفس الورقة
                        if (!empByExactName.TryGetValue(excelName, out var matchedRows))
                        {
                            bool hasAnyScoreInExtraRow = false;
                            foreach (var (_, excelColIdx, _) in criteriaColMap)
                            {
                                string v = ws.Cell(r, excelColIdx).GetString().Trim();
                                if (!string.IsNullOrWhiteSpace(v))
                                {
                                    hasAnyScoreInExtraRow = true;
                                    break;
                                }
                            }

                            result.ExtraEmployeesInExcel.Add(new ExcelImportMismatchEntry
                            {
                                SheetName = targetSheet.SheetName,
                                ItemName = excelName,
                                Details = hasAnyScoreInExtraRow
                                    ? $"الصف {r} في الإكسل (توجد له درجات لم تُسحب لعدم تطابق الاسم حرفياً في هذه الورقة)"
                                    : $"الصف {r} في الإكسل (غير موجود في هذه الورقة بالنظام)"
                            });
                            continue;
                        }

                        foreach (var empRow in matchedRows)
                        {
                            foundInExcelEmpIds.Add(empRow.EmployeeID);

                            bool anyScoreRead = false;
                            foreach (var (sysCritIdx, excelColIdx, _) in criteriaColMap)
                            {
                                var cell = ws.Cell(r, excelColIdx);
                                string rawVal = WeeklyScoreRowViewModel.NormalizeAsciiDigits(cell.GetString().Trim());
                                if (string.IsNullOrWhiteSpace(rawVal)) continue;

                                if (decimal.TryParse(rawVal.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal decVal))
                                {
                                    int intScore = (int)Math.Round(decVal, 0, MidpointRounding.AwayFromZero);
                                    int maxScore = workspace.IsCountBasedEvaluation ? 365 : empRow.GetMaxScoreAt(sysCritIdx);
                                    if (intScore > maxScore)
                                    {
                                        intScore = maxScore;
                                        result.ClampedWarningsCount++;
                                    }
                                    if (intScore < 0) intScore = 0;

                                    result.PendingUpdates.Add((empRow, sysCritIdx, intScore));
                                    anyScoreRead = true;
                                }
                            }

                            if (anyScoreRead)
                            {
                                importedInSheetEmpIds.Add(empRow.EmployeeID);
                            }
                            else
                            {
                                result.MatchedEmptyEmployeesCount++;
                            }
                        }
                    }

                    result.ImportedEmployeesCount += importedInSheetEmpIds.Count;

                    // 4. رصد الموظفين الموجودين في النظام وغير الموجودين في ورقة الإكسل (باستثناء من وُجد اسمه وخلاياه فارغة)
                    foreach (var emp in targetSheet.EmployeeRows)
                    {
                        if (!foundInExcelEmpIds.Contains(emp.EmployeeID))
                        {
                            result.MissingEmployeesInExcel.Add(new ExcelImportMismatchEntry
                            {
                                SheetName = targetSheet.SheetName,
                                ItemName = emp.EvaluationName,
                                Details = $"{emp.BranchName} — {emp.FullJobDisplay}"
                            });
                        }
                    }
                }

                // 5. رصد أي أوراق إضافية موجودة في الإكسل ولم تطابق أي ورقة في قالب النظام
                foreach (var ws in allExcelSheets)
                {
                    if (!matchedExcelSheets.Contains(ws))
                    {
                        result.ExtraSheetsInExcel.Add(ws.Name.Trim());
                    }
                }

                if (result.MatchedSheetsCount == 0)
                {
                    result.IsSuccess = false;
                    result.ErrorOrHintMessage = "لم تتطابق أي ورقة في ملف الإكسل مع أسماء أوراق القالب المفتوح حالياً. تأكد من رفع الملف الصحيح الخاص بنفس المقيم.";
                }
                else if (result.ImportedEmployeesCount == 0)
                {
                    result.IsSuccess = false;
                    result.ErrorOrHintMessage = "تمت مطابقة أوراق الملف ولكن لم يتم العثور على أي درجات مدخلة لموظفين متطابقين حرفياً. تأكد من تعبئة أعمدة الدرجات ومراجعة التقرير أدناه.";
                }
                else
                {
                    result.IsSuccess = true;
                }

                return result;
            }
            catch (Exception ex)
            {
                result.IsSuccess = false;
                result.ErrorOrHintMessage = $"تعذر قراءة ملف الإكسل: {ex.Message}";
                return result;
            }
        }

        public static void ApplyParsedImportUpdates(TemplateExcelImportResult result)
        {
            foreach (var (row, criteriaIndex, score) in result.PendingUpdates)
            {
                row.SetScoreAt(criteriaIndex, score);
            }
        }

        public static (bool IsSuccess, string Message) ImportScoresIntoWorkspace(
            WeeklyEvaluationWorkspaceModel workspace,
            string excelFilePath)
        {
            var res = ParseAndMatchExcelScores(workspace, excelFilePath);
            if (res.IsSuccess)
            {
                ApplyParsedImportUpdates(res);
                return (true, $"تم استيراد الدرجات بنجاح لـ ({res.ImportedEmployeesCount}) موظف من ملف الإكسل.");
            }
            return (false, res.ErrorOrHintMessage);
        }

        private static IXLWorksheet? FindMatchingWorksheetByName(
            List<IXLWorksheet> allExcelSheets,
            HashSet<IXLWorksheet> usedSheets,
            string targetSheetName)
        {
            string trimmedTarget = (targetSheetName ?? string.Empty).Trim();
            string sanitizedTarget = SanitizeWorksheetNameBase(trimmedTarget);
            string normTarget = NormalizeArabicForMatch(sanitizedTarget);

            var exact = allExcelSheets.FirstOrDefault(w =>
                !usedSheets.Contains(w) &&
                string.Equals(w.Name.Trim(), trimmedTarget, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            var sanitizedMatch = allExcelSheets.FirstOrDefault(w =>
                !usedSheets.Contains(w) &&
                string.Equals(SanitizeWorksheetNameBase(w.Name), sanitizedTarget, StringComparison.OrdinalIgnoreCase));
            if (sanitizedMatch != null) return sanitizedMatch;

            var normMatch = allExcelSheets.FirstOrDefault(w =>
                !usedSheets.Contains(w) &&
                string.Equals(NormalizeArabicForMatch(SanitizeWorksheetNameBase(w.Name)), normTarget, StringComparison.OrdinalIgnoreCase));
            if (normMatch != null) return normMatch;

            return null;
        }

        private static string SanitizeWorksheetNameBase(string rawName)
        {
            string clean = (rawName ?? string.Empty).Trim();
            foreach (char invalid in new[] { ':', '\\', '/', '?', '*', '[', ']' })
            {
                clean = clean.Replace(invalid, '-');
            }
            clean = CollapseSpaces(clean);
            if (clean.Length > 28)
            {
                clean = clean[..28].Trim();
            }
            return clean;
        }

        private static int DetectHeaderRowNumber(IXLWorksheet ws)
        {
            for (int r = 1; r <= 8; r++)
            {
                int rowLastCol = Math.Min(ws.Row(r).LastCellUsed()?.Address.ColumnNumber ?? 0, 20);
                for (int c = 1; c <= rowLastCol; c++)
                {
                    string val = ws.Cell(r, c).GetString().Trim();
                    if (val.Contains("الاسم التقييمي", StringComparison.OrdinalIgnoreCase))
                        return r;
                }
            }
            return 4;
        }

        private static int DetectEvaluationNameColumn(IXLWorksheet ws, int headerRow, int lastCol)
        {
            for (int c = 1; c <= Math.Min(lastCol, 15); c++)
            {
                string val = ws.Cell(headerRow, c).GetString().Trim();
                if (val.Contains("الاسم التقييمي", StringComparison.OrdinalIgnoreCase))
                    return c;
            }
            return 3;
        }

        private static bool IsFixedMetadataOrTotalColumn(string rawHeader, int colIndex)
        {
            string clean = NormalizeArabicForMatch(ExtractCriteriaTitleFromHeader(rawHeader));
            if (string.IsNullOrEmpty(clean)) return true;

            if (clean == "م" ||
                clean == "رقم الموظف" ||
                clean == "كود الموظف" ||
                clean.StartsWith("الاسم التقييمي") ||
                clean == "الفرع" ||
                clean.StartsWith("الوظيفه") ||
                clean.StartsWith("المجموع") ||
                clean.StartsWith("اجمالي العدد") ||
                clean.StartsWith("حصه المقيم"))
            {
                return true;
            }

            return colIndex <= 5 && (clean == "الاسم" || clean == "الفئه");
        }

        private static string ExtractCriteriaTitleFromHeader(string rawHeader)
        {
            if (string.IsNullOrWhiteSpace(rawHeader)) return string.Empty;
            string firstLine = rawHeader
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Trim() ?? string.Empty;

            firstLine = System.Text.RegularExpressions.Regex.Replace(
                firstLine,
                @"\s*\((?:من|عدد)\s+[^)]*\)\s*$",
                string.Empty).Trim();

            return CollapseSpaces(firstLine);
        }

        private static string CollapseSpaces(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            return System.Text.RegularExpressions.Regex.Replace(text.Trim(), @"\s+", " ");
        }

        private static string NormalizeArabicForMatch(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            return CollapseSpaces(text)
                .Replace('أ', 'ا')
                .Replace('إ', 'ا')
                .Replace('آ', 'ا')
                .Replace('ة', 'ه')
                .Replace('ى', 'ي')
                .Replace("ـ", string.Empty);
        }
        private static string MakeValidWorksheetName(string rawName, int index, HashSet<string> usedNames)
        {
            string clean = string.IsNullOrWhiteSpace(rawName) ? $"ورقة {index}" : rawName.Trim();
            foreach (char invalid in new[] { ':', '\\', '/', '?', '*', '[', ']' })
            {
                clean = clean.Replace(invalid, '-');
            }
            if (clean.Length > 28)
            {
                clean = clean[..28].Trim();
            }
            if (string.IsNullOrWhiteSpace(clean))
            {
                clean = $"ورقة {index}";
            }

            string candidate = clean;
            int suffix = 2;
            while (usedNames.Contains(candidate))
            {
                candidate = $"{clean}_{suffix++}";
            }
            return candidate;
        }
    }
}




