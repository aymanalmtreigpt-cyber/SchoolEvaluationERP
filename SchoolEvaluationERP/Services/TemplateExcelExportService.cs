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
    public static class TemplateExcelExportService
    {
        public static void GenerateTemplateExcel(TemplateFullModel template, string outputFilePath)
        {
            using var wb = new XLWorkbook();
            var usedSheetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int sIdx = 0; sIdx < template.Sheets.Count; sIdx++)
            {
                var sheet = template.Sheets[sIdx];
                string safeTabName = MakeValidWorksheetName(sheet.SheetName, sIdx + 1, usedSheetNames);
                usedSheetNames.Add(safeTabName);

                var ws = wb.Worksheets.Add(safeTabName);
                ws.RightToLeft = true;

                int criteriaCount = sheet.CriteriaColumns.Count;
                // الأعمدة: 1:م | 2:كود الموظف | 3:الاسم التقييمي | 4:الفرع | 5:الوظيفة ونوعها | 6..(5+criteriaCount):المبادئ | الأخير:المجموع (100)
                int totalCols = 6 + criteriaCount;

                // ترويسة الورقة (الصف 1 والصف 2)
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

                ws.Cell(2, 1).Value = $"تعليمات: أدخل درجات صحيحة بدون كسور في أعمدة المبادئ بحيث لا تتجاوز الدرجة القصوى الموضحة في رأس كل عمود. (يمكن استيراد هذا الملف مباشرة في شاشة إدخال التقييمات الشهرية)";
                var subRange = ws.Range(2, 1, 2, totalCols);
                subRange.Merge();
                subRange.Style.Font.FontSize = 10.5;
                subRange.Style.Font.FontColor = XLColor.FromHtml("#1E40AF");
                subRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#EFF6FF");
                subRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                subRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                ws.Row(2).Height = 24;

                // رؤوس الجدول في الصف 4 (والصف 3 فارغ خفيف للفصل)
                int headerRow = 4;
                ws.Cell(headerRow, 1).Value = "م";
                ws.Cell(headerRow, 2).Value = "رقم الموظف";
                ws.Cell(headerRow, 3).Value = "الاسم التقييمي للموظف";
                ws.Cell(headerRow, 4).Value = "الفرع";
                ws.Cell(headerRow, 5).Value = "الوظيفة ونوعها";

                for (int c = 0; c < criteriaCount; c++)
                {
                    var crit = sheet.CriteriaColumns[c];
                    ws.Cell(headerRow, 6 + c).Value = $"{crit.CriteriaTitle}\n(من {crit.MaxScore:0})";
                }

                ws.Cell(headerRow, totalCols).Value = "المجموع\n(من 100)";

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

                int rowIdx = headerRow + 1;
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
                        scoreCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F8FAFC");
                        scoreCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        scoreCell.Style.Font.Bold = true;
                    }

                    if (criteriaCount > 0)
                    {
                        string firstCritRef = ws.Cell(rowIdx, 6).Address.ToStringRelative();
                        string lastCritRef = ws.Cell(rowIdx, 5 + criteriaCount).Address.ToStringRelative();
                        var sumCell = ws.Cell(rowIdx, totalCols);
                        sumCell.FormulaA1 = $"SUM({firstCritRef}:{lastCritRef})";
                        sumCell.Style.Font.Bold = true;
                        sumCell.Style.Font.FontColor = XLColor.FromHtml("#047857");
                        sumCell.Style.Fill.BackgroundColor = XLColor.FromHtml("#ECFDF5");
                        sumCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
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

                ws.Columns(1, totalCols).AdjustToContents();
                ws.Column(2).Width = 11;
                if (ws.Column(3).Width < 26) ws.Column(3).Width = 26;
                for (int c = 0; c < criteriaCount; c++)
                {
                    if (ws.Column(6 + c).Width < 15)
                        ws.Column(6 + c).Width = 15;
                }

                ws.PageSetup.PageOrientation = XLPageOrientation.Portrait;
                ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
                ws.PageSetup.FitToPages(1, 0);
            }

            wb.SaveAs(outputFilePath);
        }

        public static (bool IsSuccess, string Message) ImportScoresIntoWorkspace(
            WeeklyEvaluationWorkspaceModel workspace,
            string excelFilePath)
        {
            if (!File.Exists(excelFilePath))
                return (false, "ملف الإكسل المحدد غير موجود.");

            try
            {
                using var wb = new XLWorkbook(excelFilePath);
                int matchedEmployees = 0;
                int clampedWarnings = 0;

                for (int sIdx = 0; sIdx < workspace.Sheets.Count; sIdx++)
                {
                    var targetSheet = workspace.Sheets[sIdx];
                    IXLWorksheet? ws = wb.Worksheets.FirstOrDefault(w =>
                        string.Equals(w.Name.Trim(), targetSheet.SheetName.Trim(), StringComparison.OrdinalIgnoreCase));

                    if (ws == null && sIdx < wb.Worksheets.Count)
                    {
                        ws = wb.Worksheet(sIdx + 1);
                    }
                    if (ws == null) continue;

                    var empById = targetSheet.EmployeeRows.ToDictionary(e => e.EmployeeID, e => e);
                    var empByName = targetSheet.EmployeeRows
                        .GroupBy(e => e.EvaluationName.Trim(), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                    // تحديد صف البداية تلقائياً (عادة الصف 5 بعد رأس الجدول في الصف 4)
                    int lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
                    for (int r = 2; r <= lastRow; r++)
                    {
                        string idCellText = ws.Cell(r, 2).GetString().Trim();
                        string nameCellText = ws.Cell(r, 3).GetString().Trim();

                        WeeklyScoreRowViewModel? empRow = null;
                        if (int.TryParse(idCellText, out int parsedEmpId) && empById.TryGetValue(parsedEmpId, out var byId))
                        {
                            empRow = byId;
                        }
                        else if (!string.IsNullOrWhiteSpace(nameCellText) && empByName.TryGetValue(nameCellText, out var byName))
                        {
                            empRow = byName;
                        }

                        if (empRow == null) continue;

                        bool anyScoreRead = false;
                        for (int c = 0; c < empRow.CriteriaCount; c++)
                        {
                            var cell = ws.Cell(r, 6 + c);
                            string rawVal = WeeklyScoreRowViewModel.NormalizeAsciiDigits(cell.GetString().Trim());
                            if (string.IsNullOrWhiteSpace(rawVal)) continue;

                            if (decimal.TryParse(rawVal.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal decVal))
                            {
                                int intScore = (int)Math.Round(decVal, 0, MidpointRounding.AwayFromZero);
                                int maxScore = empRow.GetMaxScoreAt(c);
                                if (intScore > maxScore)
                                {
                                    intScore = maxScore;
                                    clampedWarnings++;
                                }
                                if (intScore < 0) intScore = 0;

                                empRow.SetScoreAt(c, intScore);
                                anyScoreRead = true;
                            }
                        }

                        if (anyScoreRead)
                        {
                            matchedEmployees++;
                        }
                    }
                }

                if (matchedEmployees == 0)
                {
                    return (false, "لم يتم العثور على درجات مطابقة لموظفي هذا القالب داخل ملف الإكسل. تأكد من استخدام ملف القالب المستخرج لنفس المقيم وتعبئة أعمدة الدرجات.");
                }

                string msg = $"تم استيراد الدرجات بنجاح لـ ({matchedEmployees}) موظف من ملف الإكسل.";
                if (clampedWarnings > 0)
                {
                    msg += $"\n(ملاحظة: تم ضبط {clampedWarnings} خلية تجاوزت الحد الأقصى للعمود إلى الدرجة القصوى المسموحة).";
                }
                msg += "\nيرجى مراجعة الجدول ثم الضغط على (💾 حفظ واعتماد تقييم الشهر).";

                return (true, msg);
            }
            catch (Exception ex)
            {
                return (false, $"تعذر قراءة ملف الإكسل (تأكد من إغلاق الملف في برنامج Excel أولاً): {ex.Message}");
            }
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
