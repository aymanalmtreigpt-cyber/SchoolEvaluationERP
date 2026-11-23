using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
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

        private class ClosedReportSnapshotRow
        {
            public bool IsLocked { get; set; }
            public DateTime? LockedAt { get; set; }
            public string? LockedByUserName { get; set; }
            public string? SnapshotJson { get; set; }
        }

        private class RawMonthlyEvaluatorScore
        {
            public int EmployeeID { get; set; }
            public int EvaluatorID { get; set; }
            public int GroupID { get; set; }
            public string GroupName { get; set; } = string.Empty;
            public string EvaluatorName { get; set; } = string.Empty;
            public decimal RawMonthSumOutOf100 { get; set; }
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
            bool onlyDeservingIncentive = false,
            bool ignoreLockedSnapshot = false,
            bool onlyIncompleteEvaluations = false)
        {
            using var conn = new SqlConnection(_connectionString);
            await DatabaseSchemaService.EnsureSchemaV2Async(conn);
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync();
            }

            string cleanSearch = (searchQuery ?? string.Empty).Trim();

            if (!ignoreLockedSnapshot)
            {
                var closedRow = await conn.QueryFirstOrDefaultAsync<ClosedReportSnapshotRow>(@"
                    SELECT IsLocked, LockedAt, LockedByUserName, SnapshotJson
                    FROM dbo.MonthlyClosedReports
                    WHERE EvalYear = @Year AND EvalMonth = @Month",
                    new { Year = year, Month = month });

                if (closedRow is { IsLocked: true } && !string.IsNullOrWhiteSpace(closedRow.SnapshotJson))
                {
                    var frozen = JsonSerializer.Deserialize<MonthlyFinalReportSummary>(closedRow.SnapshotJson);
                    if (frozen != null)
                    {
                        var filteredRows = (frozen.Rows ?? new List<EmployeeMonthlyReportRowDto>()).AsEnumerable();

                        if (filterBranchId.HasValue && filterBranchId.Value > 0)
                            filteredRows = filteredRows.Where(r => r.BranchID == filterBranchId.Value);

                        if (filterCategoryId.HasValue && filterCategoryId.Value > 0)
                            filteredRows = filteredRows.Where(r => r.CategoryID == filterCategoryId.Value);

                        if (!string.IsNullOrEmpty(cleanSearch))
                        {
                            filteredRows = filteredRows.Where(r =>
                                r.EvaluationName.Contains(cleanSearch, StringComparison.OrdinalIgnoreCase) ||
                                r.SystemName.Contains(cleanSearch, StringComparison.OrdinalIgnoreCase) ||
                                r.JobTitle.Contains(cleanSearch, StringComparison.OrdinalIgnoreCase) ||
                                r.JobTypeName.Contains(cleanSearch, StringComparison.OrdinalIgnoreCase));
                        }

                        if (onlyDeservingIncentive)
                        {
                            bool hasAnyFrozenPayout = (frozen.Rows ?? new List<EmployeeMonthlyReportRowDto>()).Any(r => r.FinalIncentiveAmount > 0m);
                            filteredRows = hasAnyFrozenPayout
                                ? filteredRows.Where(r => r.HasIncentive && r.MonthlyEvaluationAverage > 80m && r.FinalIncentiveAmount > 0m)
                                : filteredRows.Where(r => r.HasIncentive && r.MonthlyEvaluationAverage >= 81m);
                        }

                        if (onlyIncompleteEvaluations)
                            filteredRows = filteredRows.Where(r => !r.IsAllEvaluatorsCompleted);

                        var finalFrozenList = filteredRows.ToList();
                        for (int i = 0; i < finalFrozenList.Count; i++)
                            finalFrozenList[i].RowNumber = i + 1;

                        frozen.Rows = finalFrozenList;
                        frozen.IsMonthLocked = true;
                        frozen.LockedAt = closedRow.LockedAt;
                        frozen.LockedByUserName = string.IsNullOrWhiteSpace(closedRow.LockedByUserName) ? "مدير النظام العام" : closedRow.LockedByUserName;
                        frozen.EffectiveConfigPeriodLabel = $"🔒 شهر معتمد ومغلق نهائياً بتاريخ {frozen.LockedAt?.ToString("yyyy-MM-dd hh:mm tt")} بواسطة ({frozen.LockedByUserName}) — القيد والأرقام مجمدة ومحمية بالكامل";
                        return frozen;
                    }
                }
            }

            var periodResolution = await MonthlyPeriodConfigService.ResolveEffectivePeriodAsync(conn, year, month);

            string empSql = @"
                SELECT e.EmployeeID, e.SystemName, e.EvaluationName,
                       e.BranchID, b.BranchName,
                       ISNULL(e.DepartmentID, 0) AS DepartmentID,
                       e.CategoryID, c.CategoryName,
                       j.JobTitle, jt.JobTypeName,
                       e.HasIncentive, e.IsContinuing
                FROM dbo.Employees e
                INNER JOIN dbo.Branches b ON e.BranchID = b.BranchID
                INNER JOIN dbo.Categories c ON e.CategoryID = c.CategoryID
                INNER JOIN dbo.Jobs j ON e.JobID = j.JobID
                INNER JOIN dbo.JobTypes jt ON e.JobTypeID = jt.JobTypeID
                WHERE e.IsActive = 1
                ORDER BY b.BranchName ASC, e.EvaluationName ASC";

            var allEmployees = (await conn.QueryAsync<EmployeeMonthlyReportRowDto>(empSql)).ToList();
            var catNameMap = (await conn.QueryAsync<dynamic>("SELECT CategoryID, CategoryName FROM dbo.Categories"))
                .ToDictionary(x => (int)x.CategoryID, x => (string)x.CategoryName);
            var snaps = await MonthlyPeriodConfigService.GetEffectiveEmployeeSnapshotsAsync(conn, year, month);

            var employees = new List<EmployeeMonthlyReportRowDto>();
            foreach (var emp in allEmployees)
            {
                if (snaps.TryGetValue(emp.EmployeeID, out var snap))
                {
                    emp.CategoryID = snap.CategoryID;
                    if (catNameMap.TryGetValue(snap.CategoryID, out var cname))
                        emp.CategoryName = cname;
                    emp.HasIncentive = snap.HasIncentive;
                    emp.IsContinuing = snap.IsContinuing;
                }

                if (!emp.IsContinuing) continue;
                if (filterBranchId.HasValue && filterBranchId.Value > 0 && emp.BranchID != filterBranchId.Value) continue;
                if (filterCategoryId.HasValue && filterCategoryId.Value > 0 && emp.CategoryID != filterCategoryId.Value) continue;
                if (!string.IsNullOrEmpty(cleanSearch) &&
                    !emp.EvaluationName.Contains(cleanSearch, StringComparison.OrdinalIgnoreCase) &&
                    !emp.SystemName.Contains(cleanSearch, StringComparison.OrdinalIgnoreCase) &&
                    !emp.JobTitle.Contains(cleanSearch, StringComparison.OrdinalIgnoreCase) &&
                    !emp.JobTypeName.Contains(cleanSearch, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                employees.Add(emp);
            }

            var catGroups = await MonthlyPeriodConfigService.GetEffectiveCategoryWeightsAsync(conn, year, month);
            var catGroupsMap = catGroups
                .GroupBy(x => x.CategoryID)
                .ToDictionary(g => g.Key, g => g.ToList());

            var categoryIncentiveMap = (await conn.QueryAsync<dynamic>(@"
                SELECT CategoryID, MaxIncentiveAmount, MinIncentiveAmount
                FROM dbo.MonthlyCategoryIncentives
                WHERE EvalYear = @Year AND EvalMonth = @Month",
                new { Year = year, Month = month }))
                .ToDictionary(k => (int)k.CategoryID, v => (MaxAmt: (decimal)v.MaxIncentiveAmount, MinAmt: (decimal)v.MinIncentiveAmount));

            bool hasAnyCategoryIncentiveConfigured = categoryIncentiveMap.Values.Any(v => v.MaxAmt > 0m);

            var empAssignedEvals = await MonthlyPeriodConfigService.GetEffectiveEmployeeAssignmentsAsync(conn, year, month);
            var empAssignedMap = empAssignedEvals
                .GroupBy(x => x.EmployeeID)
                .ToDictionary(g => g.Key, g => g.ToList());

            string scoresSql = @"
                SELECT es.EmployeeID,
                       er.EvaluatorID,
                       ev.GroupID,
                       ISNULL(eg.GroupName, N'') AS GroupName,
                       ev.EvaluatorName,
                       CASE
                           WHEN SUM(es.GivenScore) + ISNULL(MAX(adj.BalancingDelta), 0) > 100 THEN 100
                           WHEN SUM(es.GivenScore) + ISNULL(MAX(adj.BalancingDelta), 0) < 0 THEN 0
                           ELSE SUM(es.GivenScore) + ISNULL(MAX(adj.BalancingDelta), 0)
                       END AS RawMonthSumOutOf100,
                       MAX(es.SnapshotEvaluatorWeight) AS SnapshotWeight
                FROM dbo.EvaluationScores es
                INNER JOIN dbo.EvaluationRecords er ON es.RecordID = er.RecordID
                INNER JOIN dbo.Evaluators ev ON er.EvaluatorID = ev.EvaluatorID
                LEFT JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                LEFT JOIN dbo.EvaluationEmployeeAdjustments adj
                    ON adj.RecordID = es.RecordID AND adj.SheetID = es.SheetID AND adj.EmployeeID = es.EmployeeID
                WHERE er.EvalYear = @Year AND er.EvalMonth = @Month
                GROUP BY es.EmployeeID, er.EvaluatorID, ev.GroupID, eg.GroupName, ev.EvaluatorName, es.SheetID
                HAVING COUNT(*) > 0 AND COUNT(*) = COUNT(es.GivenScore)";

            var rawScores = (await conn.QueryAsync<RawMonthlyEvaluatorScore>(scoresSql, new
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
                RoundToNearest50 = roundToNearest50,
                IsMonthLocked = false,
                EffectiveConfigPeriodLabel = periodResolution.StatusBadgeText
            };

            var finalRows = new List<EmployeeMonthlyReportRowDto>();

            foreach (var emp in employees)
            {
                scoresByEmployee.TryGetValue(emp.EmployeeID, out var empScores);
                empScores ??= new List<RawMonthlyEvaluatorScore>();

                // توحيد قراءة كل مقيم في حال تكرر في أكثر من ورقة داخل نفس القالب بالخطأ
                var rawByEvalId = empScores
                    .GroupBy(s => s.EvaluatorID)
                    .ToDictionary(
                        g => g.Key,
                        g => (
                            RawOut100: Math.Min(100m, g.Average(x => x.RawMonthSumOutOf100)),
                            SnapshotWeight: g.Max(x => x.SnapshotWeight),
                            EvaluatorName: g.First().EvaluatorName,
                            GroupID: g.First().GroupID,
                            GroupName: g.First().GroupName
                        ));

                catGroupsMap.TryGetValue(emp.CategoryID, out var reqGroups);
                reqGroups ??= new List<EffectiveCategoryWeightDto>();

                empAssignedMap.TryGetValue(emp.EmployeeID, out var assignedForEmp);
                assignedForEmp ??= new List<EffectiveEmployeeAssignmentDto>();

                decimal monthlyTotalScore = 0m;
                var breakdownParts = new List<string>();
                var missingEvaluatorNames = new List<string>();
                int totalEvaluatedCount = 0;

                if (reqGroups.Count > 0)
                {
                    foreach (var grp in reqGroups)
                    {
                        int gId = grp.GroupID;
                        string gName = grp.GroupName;
                        decimal groupWeight = grp.WeightScore;

                        var assignedInGroup = assignedForEmp.Where(a => a.GroupID == gId).ToList();
                        if (assignedInGroup.Count == 0)
                        {
                            missingEvaluatorNames.Add(gName);
                            continue;
                        }

                        int assignedCount = assignedInGroup.Count;
                        decimal rawSumInGroup = 0m;
                        var memberTexts = new List<string>();

                        foreach (var member in assignedInGroup)
                        {
                            int evId = member.EvaluatorID;
                            string evName = member.EvaluatorName;

                            if (rawByEvalId.TryGetValue(evId, out var evalScore))
                            {
                                rawSumInGroup += evalScore.RawOut100;
                                totalEvaluatedCount++;
                                decimal individualShare = Math.Round(groupWeight / assignedCount, 2, MidpointRounding.AwayFromZero);
                                decimal individualWeighted = Math.Round((evalScore.RawOut100 / 100m) * (groupWeight / assignedCount), 2, MidpointRounding.AwayFromZero);
                                memberTexts.Add($"{evName}: {individualWeighted:0.##}/{individualShare:0.##} ({evalScore.RawOut100:0}%)");
                            }
                            else
                            {
                                missingEvaluatorNames.Add(evName);
                            }
                        }

                        // المعادلة المعتمدة لمجموعة المقيمين: تقسيم وزن المجموعة على المقيمين المسندين للموظف وحساب محصلتهم الدقيقة
                        decimal groupContribution = ((rawSumInGroup / assignedCount) / 100m) * groupWeight;
                        monthlyTotalScore += groupContribution;

                        if (memberTexts.Count > 0)
                        {
                            if (assignedCount == 1)
                            {
                                breakdownParts.Add(memberTexts[0]);
                            }
                            else
                            {
                                decimal roundedGrpContrib = Math.Round(groupContribution, 2, MidpointRounding.AwayFromZero);
                                breakdownParts.Add($"{gName} [{string.Join(" + ", memberTexts)} = {roundedGrpContrib:0.##}/{groupWeight:0.##}]");
                            }
                        }
                    }
                }
                else if (rawByEvalId.Count > 0)
                {
                    // حماية السجلات التاريخية إذا تغيرت الفئات لاحقاً
                    foreach (var kv in rawByEvalId.Values)
                    {
                        decimal w = kv.SnapshotWeight > 0m ? kv.SnapshotWeight : 100m;
                        decimal contrib = (kv.RawOut100 / 100m) * w;
                        monthlyTotalScore += contrib;
                        totalEvaluatedCount++;
                        breakdownParts.Add($"{kv.EvaluatorName}: {Math.Round(contrib, 2, MidpointRounding.AwayFromZero):0.##}/{w:0.##}");
                    }
                }

                monthlyTotalScore = Math.Min(100m, Math.Round(monthlyTotalScore, 2, MidpointRounding.AwayFromZero));

                emp.IsAllEvaluatorsCompleted = missingEvaluatorNames.Count == 0 && totalEvaluatedCount > 0;

                if (missingEvaluatorNames.Count > 0)
                {
                    breakdownParts.Add($"[ينقصه تقييم: {string.Join("، ", missingEvaluatorNames)}]");
                }

                emp.EvaluatorsBreakdownSummary = breakdownParts.Count > 0
                    ? string.Join("  |  ", breakdownParts)
                    : "لم يتم إدخال التقييم الشهري بعد";

                // حساب معدل الأداء والحافز: إذا كان الموظف (بدون حافز) يُحفظ معدل أدائه من 100% لكن الحافز المالي = 0 دائماً
                if (!emp.HasIncentive)
                {
                    emp.MonthlyEvaluationAverage = monthlyTotalScore;
                    emp.ExactIncentivePercent = 0m;
                    emp.DisplayedIncentivePercent = 0;
                    emp.FinalIncentiveAmount = 0m;
                    emp.DebitAccount = debitAccountTitle;
                    emp.CreditAccountSystemName = emp.SystemName;
                    emp.StatementText = string.Empty;
                    int lastDay = DateTime.DaysInMonth(year, month);
                    emp.EntryDateFormatted = new DateTime(year, month, lastDay).ToString("yyyy-MM-dd");
                }
                else
                {
                    decimal catMaxAmt = categoryIncentiveMap.TryGetValue(emp.CategoryID, out var lim) ? lim.MaxAmt : 0m;
                    decimal catMinAmt = categoryIncentiveMap.TryGetValue(emp.CategoryID, out var lim2) ? lim2.MinAmt : 0m;

                    var calc = IncentiveCalculatorService.CalculateMonthlyIncentive(
                        emp.SystemName,
                        emp.BranchName,
                        monthlyTotalScore,
                        catMaxAmt,
                        catMinAmt,
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
                }

                if (onlyDeservingIncentive)
                {
                    bool isDeserving = hasAnyCategoryIncentiveConfigured
                        ? (emp.HasIncentive && emp.MonthlyEvaluationAverage > 80m && emp.FinalIncentiveAmount > 0m)
                        : (emp.HasIncentive && emp.MonthlyEvaluationAverage >= 81m);
                    if (!isDeserving)
                        continue;
                }

                if (onlyIncompleteEvaluations && emp.IsAllEvaluatorsCompleted)
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

        public async Task<(bool IsSuccess, string Message)> LockMonthReportAsync(
            int year,
            int month,
            string debitAccountTitle,
            decimal maxIncentiveAmount,
            decimal minIncentiveAmount,
            bool roundToNearest50)
        {
            if (AuthService.CurrentSession == null)
                return (false, "انتهت جلسة العمل، يرجى إعادة تسجيل الدخول.");

            if (!AuthService.CurrentSession.IsAdmin &&
                !AuthService.CurrentSession.HasPermission("FinalReport", "update") &&
                !AuthService.CurrentSession.HasPermission("FinalReport", "insert"))
            {
                return (false, "عذراً، لا تملك صلاحية اعتماد وإغلاق التقرير الشهري.");
            }

            var fullReport = await GenerateMonthlyReportAsync(
                year, month, debitAccountTitle, maxIncentiveAmount, minIncentiveAmount, roundToNearest50,
                filterBranchId: null, filterCategoryId: null, searchQuery: "", onlyDeservingIncentive: false,
                ignoreLockedSnapshot: true);

            if (fullReport.Rows.Count == 0)
                return (false, "لا توجد بيانات موظفين في هذا الشهر لاعتمادها وإغلاقها.");

            string lockedBy = AuthService.CurrentSession.FullName;
            fullReport.IsMonthLocked = true;
            fullReport.LockedByUserName = lockedBy;
            fullReport.LockedAt = DateTime.Now;

            string snapshotJson = JsonSerializer.Serialize(fullReport);

            using var conn = new SqlConnection(_connectionString);
            await DatabaseSchemaService.EnsureSchemaV2Async(conn);
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            await conn.ExecuteAsync(@"
                IF EXISTS (SELECT 1 FROM dbo.MonthlyClosedReports WHERE EvalYear = @Year AND EvalMonth = @Month)
                BEGIN
                    UPDATE dbo.MonthlyClosedReports
                    SET IsLocked = 1,
                        LockedAt = SYSDATETIME(),
                        LockedByUserName = @LockedBy,
                        SnapshotJson = @Json
                    WHERE EvalYear = @Year AND EvalMonth = @Month;
                END
                ELSE
                BEGIN
                    INSERT INTO dbo.MonthlyClosedReports (EvalYear, EvalMonth, IsLocked, LockedAt, LockedByUserName, SnapshotJson)
                    VALUES (@Year, @Month, 1, SYSDATETIME(), @LockedBy, @Json);
                END",
                new { Year = year, Month = month, LockedBy = lockedBy, Json = snapshotJson });

            string mName = IncentiveCalculatorService.GetArabicMonthName(month);
            return (true,
                $"🔒 تم اعتماد وإغلاق شهر ({mName} {year} م) نهائياً بنجاح!\n" +
                $"• إجمالي الموظفين المجمدين في القيد: {fullReport.TotalEmployeesCount} موظف\n" +
                $"• إجمالي الحوافز المعتمدة للصرف: {fullReport.TotalIncentivePayout:N0} ريال\n" +
                "لن يتأثر هذا الشهر بأي تعديلات مستقبلية على المقيمين أو الفئات أو الموظفين.");
        }

        public async Task<(bool IsSuccess, string Message)> UnlockMonthReportAsync(int year, int month)
        {
            if (AuthService.CurrentSession == null)
                return (false, "انتهت جلسة العمل، يرجى إعادة تسجيل الدخول.");

            if (!AuthService.CurrentSession.IsAdmin)
                return (false, "فك إغلاق شهر مالي معتمد متاح لصلاحية (مدير النظام العام) فقط.");

            using var conn = new SqlConnection(_connectionString);
            await DatabaseSchemaService.EnsureSchemaV2Async(conn);
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            await conn.ExecuteAsync(
                "DELETE FROM dbo.MonthlyClosedReports WHERE EvalYear = @Year AND EvalMonth = @Month",
                new { Year = year, Month = month });

            string mName = IncentiveCalculatorService.GetArabicMonthName(month);
            return (true, $"🔓 تم فك إغلاق شهر ({mName} {year} م) بنجاح. أصبح الشهر مفتوحاً لإعادة الاحتساب والتعديل.");
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
            wsJournal.Cell(rowIdx, 5).Value = $"إجمالي المستحقين: {report.DeservingEmployeesCount} من أصل {report.TotalEmployeesCount} موظف مستمر — شهر {monthName}/{report.Year} م";

            var totalRange = wsJournal.Range(rowIdx, 1, rowIdx, 6);
            totalRange.Style.Font.Bold = true;
            totalRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#EFF6FF");
            totalRange.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;

            wsJournal.Columns().AdjustToContents();

            // الورقة الثانية: تفاصيل التقييم الشهري والأداء ومجموعات المقيمين
            var wsDetails = wb.Worksheets.Add("تفاصيل التقييم الشهري");
            wsDetails.RightToLeft = true;

            string[] detailHeaders =
            {
                "م",
                "الاسم التقييمي",
                "الاسم في النظام",
                "الفرع",
                "الفئة",
                "الوظيفة ونوعها",
                "حالة الحافز",
                "تفصيل درجات مجموعات المقيمين",
                "اكتمال التقييم",
                "معدل الأداء الشهري %",
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
                wsDetails.Cell(dRow, 5).Value = r.CategoryName;
                wsDetails.Cell(dRow, 6).Value = r.FullJobDisplay;
                wsDetails.Cell(dRow, 7).Value = r.HasIncentiveDisplay;
                wsDetails.Cell(dRow, 8).Value = r.EvaluatorsBreakdownSummary;
                wsDetails.Cell(dRow, 9).Value = r.CompletionStatusDisplay;
                wsDetails.Cell(dRow, 10).Value = r.MonthlyEvaluationAverage;
                wsDetails.Cell(dRow, 11).Value = r.IncentivePercentDisplay;
                wsDetails.Cell(dRow, 12).Value = r.FinalIncentiveAmount;
                wsDetails.Cell(dRow, 12).Style.NumberFormat.Format = "#,##0";

                for (int c = 1; c <= 12; c++)
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
                            col.Item().PaddingTop(3).Text($"الحساب المدين: {report.DebitAccountTitle}   |   حدود الحوافز: حسب أساسيات الحوافز المعتمدة لكل فئة")
                               .FontSize(10).SemiBold();
                        });

                        row.ConstantItem(220).AlignLeft().Column(col =>
                        {
                            col.Item().Text($"تاريخ الإصدار: {DateTime.Now:yyyy-MM-dd}").FontSize(9.5f);
                            col.Item().PaddingTop(3).Text($"إجمالي المستمرين: {report.TotalEmployeesCount}  |  المستحقون: {report.DeservingEmployeesCount}")
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
                                cols.ConstantColumn(52);
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
                                header.Cell().Element(PdfHeaderStyle).Text("الأداء");
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
                                table.Cell().Element(PdfBodyStyle).AlignCenter().Text(r.IncentivePercentDisplay).FontSize(8.5f);
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















