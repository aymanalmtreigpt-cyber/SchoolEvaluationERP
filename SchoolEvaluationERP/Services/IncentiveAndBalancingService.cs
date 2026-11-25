using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using SchoolEvaluationERP.Models;

namespace SchoolEvaluationERP.Services
{
    public class IncentiveAndBalancingService
    {
        private readonly string _connectionString;

        public IncentiveAndBalancingService() : this(AppConfigService.ConnectionString) { }

        public IncentiveAndBalancingService(string connectionString)
        {
            _connectionString = connectionString;
        }

        private async Task<SqlConnection> OpenReadyConnectionAsync()
        {
            var conn = new SqlConnection(_connectionString);
            await DatabaseSchemaService.EnsureSchemaV2Async(conn);
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync();
            }
            return conn;
        }

        private static bool CheckPermission(string moduleCode, string action, out string errorMsg)
        {
            errorMsg = string.Empty;
            if (AuthService.CurrentSession == null)
            {
                errorMsg = "انتهت جلسة العمل، يرجى إعادة تسجيل الدخول.";
                return false;
            }
            if (!AuthService.CurrentSession.HasPermission(moduleCode, action))
            {
                string arAction = action switch
                {
                    "insert" => "إضافة وكتابة",
                    "update" => "تعديل",
                    "delete" => "حذف",
                    _ => "قراءة"
                };
                errorMsg = $"عذراً، لا تملك صلاحية ({arAction}) في هذه الشاشة.";
                return false;
            }
            return true;
        }

        private class RawEvalScoreWithDelta
        {
            public int EmployeeID { get; set; }
            public int EvaluatorID { get; set; }
            public int GroupID { get; set; }
            public decimal RawScoreWithDeltaOut100 { get; set; }
            public decimal SnapshotWeight { get; set; }
        }

        public async Task<List<CategoryIncentiveSettingRow>> GetCategoriesIncentiveRowsAsync(
            int year,
            int month,
            bool roundToNearest50 = true)
        {
            using var conn = await OpenReadyConnectionAsync();

            var categories = (await conn.QueryAsync<CategoryIncentiveSettingRow>(@"
                SELECT c.CategoryID,
                       c.CategoryName,
                       ISNULL(mci.MaxIncentiveAmount, 0) AS MaxIncentiveAmount,
                       ISNULL(mci.MinIncentiveAmount, 0) AS MinIncentiveAmount,
                       (SELECT COUNT(1) FROM dbo.Employees e WHERE e.CategoryID = c.CategoryID AND e.IsActive = 1 AND e.IsContinuing = 1) AS TotalContinuingEmployeesCount,
                       (SELECT COUNT(1) FROM dbo.Employees e WHERE e.CategoryID = c.CategoryID AND e.IsActive = 1 AND e.IsContinuing = 1 AND e.HasIncentive = 1) AS IncentiveEligibleEmployeesCount
                FROM dbo.Categories c
                LEFT JOIN dbo.MonthlyCategoryIncentives mci
                    ON mci.CategoryID = c.CategoryID AND mci.EvalYear = @Year AND mci.EvalMonth = @Month
                WHERE c.IsActive = 1
                ORDER BY c.CategoryID ASC", new { Year = year, Month = month })).ToList();

            var rawEmployees = (await conn.QueryAsync<dynamic>(@"
                SELECT e.EmployeeID, e.CategoryID, e.SystemName, b.BranchName, e.HasIncentive, e.IsContinuing
                FROM dbo.Employees e
                INNER JOIN dbo.Branches b ON e.BranchID = b.BranchID
                WHERE e.IsActive = 1")).ToList();

            var snaps = await MonthlyPeriodConfigService.GetEffectiveEmployeeSnapshotsAsync(conn, year, month);
            var employees = new List<EmployeeRawMonthlyScoreItem>();
            foreach (var re in rawEmployees)
            {
                int empId = (int)re.EmployeeID;
                int catId = (int)re.CategoryID;
                bool hasInc = Convert.ToBoolean(re.HasIncentive);
                bool isCont = Convert.ToBoolean(re.IsContinuing);

                if (snaps.TryGetValue(empId, out var snap))
                {
                    catId = snap.CategoryID;
                    hasInc = snap.HasIncentive;
                    isCont = snap.IsContinuing;
                }

                if (!isCont) continue;

                employees.Add(new EmployeeRawMonthlyScoreItem
                {
                    EmployeeID = empId,
                    CategoryID = catId,
                    SystemName = (string)re.SystemName,
                    BranchName = (string)re.BranchName,
                    HasIncentive = hasInc
                });
            }

            var catGroups = await MonthlyPeriodConfigService.GetEffectiveCategoryWeightsAsync(conn, year, month);
            var catGroupsMap = catGroups
                .GroupBy(x => x.CategoryID)
                .ToDictionary(g => g.Key, g => g.ToList());

            var empAssignedEvals = await MonthlyPeriodConfigService.GetEffectiveEmployeeAssignmentsAsync(conn, year, month);
            var empAssignedMap = empAssignedEvals
                .GroupBy(x => x.EmployeeID)
                .ToDictionary(g => g.Key, g => g.ToList());

            string scoresSql = @"
                SELECT es.EmployeeID,
                       er.EvaluatorID,
                       ev.GroupID,
                       CASE
                           WHEN SUM(es.GivenScore) + ISNULL(MAX(adj.BalancingDelta), 0) > 100 THEN 100
                           WHEN SUM(es.GivenScore) + ISNULL(MAX(adj.BalancingDelta), 0) < 0 THEN 0
                           ELSE SUM(es.GivenScore) + ISNULL(MAX(adj.BalancingDelta), 0)
                       END AS RawScoreWithDeltaOut100,
                       MAX(es.SnapshotEvaluatorWeight) AS SnapshotWeight
                FROM dbo.EvaluationScores es
                INNER JOIN dbo.EvaluationRecords er ON es.RecordID = er.RecordID
                INNER JOIN dbo.Evaluators ev ON er.EvaluatorID = ev.EvaluatorID
                LEFT JOIN dbo.EvaluationEmployeeAdjustments adj
                    ON adj.RecordID = es.RecordID AND adj.SheetID = es.SheetID AND adj.EmployeeID = es.EmployeeID
                WHERE er.EvalYear = @Year AND er.EvalMonth = @Month
                GROUP BY es.EmployeeID, er.EvaluatorID, ev.GroupID, es.SheetID
                HAVING COUNT(*) > 0 AND COUNT(*) = COUNT(es.GivenScore)";

            var rawScores = (await conn.QueryAsync<RawEvalScoreWithDelta>(scoresSql, new { Year = year, Month = month })).ToList();
            var scoresByEmployee = rawScores
                .GroupBy(s => s.EmployeeID)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var emp in employees)
            {
                scoresByEmployee.TryGetValue(emp.EmployeeID, out var empScores);
                empScores ??= new List<RawEvalScoreWithDelta>();

                var rawByEvalId = empScores
                    .GroupBy(s => s.EvaluatorID)
                    .ToDictionary(
                        g => g.Key,
                        g => (
                            RawOut100: Math.Clamp(g.Average(x => x.RawScoreWithDeltaOut100), 0m, 100m),
                            SnapshotWeight: g.Max(x => x.SnapshotWeight)
                        ));

                catGroupsMap.TryGetValue(emp.CategoryID, out var reqGroups);
                reqGroups ??= new List<EffectiveCategoryWeightDto>();

                empAssignedMap.TryGetValue(emp.EmployeeID, out var assignedForEmp);
                assignedForEmp ??= new List<EffectiveEmployeeAssignmentDto>();

                decimal monthlyTotalScore = 0m;

                if (reqGroups.Count > 0)
                {
                    foreach (var grp in reqGroups)
                    {
                        int gId = grp.GroupID;
                        decimal groupWeight = grp.WeightScore;

                        var assignedInGroup = assignedForEmp.Where(a => a.GroupID == gId).ToList();
                        if (assignedInGroup.Count == 0) continue;

                        int assignedCount = assignedInGroup.Count;
                        decimal rawSumInGroup = 0m;

                        foreach (var member in assignedInGroup)
                        {
                            int evId = member.EvaluatorID;
                            if (rawByEvalId.TryGetValue(evId, out var evalScore))
                            {
                                rawSumInGroup += evalScore.RawOut100;
                            }
                        }

                        decimal groupContribution = ((rawSumInGroup / assignedCount) / 100m) * groupWeight;
                        monthlyTotalScore += groupContribution;
                    }
                }
                else if (rawByEvalId.Count > 0)
                {
                    foreach (var kv in rawByEvalId.Values)
                    {
                        decimal w = kv.SnapshotWeight > 0m ? kv.SnapshotWeight : 100m;
                        monthlyTotalScore += (kv.RawOut100 / 100m) * w;
                    }
                }

                emp.MonthlyScoreOut100 = Math.Clamp(Math.Round(monthlyTotalScore, 2, MidpointRounding.AwayFromZero), 0m, 100m);
            }

            var empsByCategory = employees
                .GroupBy(e => e.CategoryID)
                .ToDictionary(g => g.Key, g => g.ToList());

            for (int i = 0; i < categories.Count; i++)
            {
                var catRow = categories[i];
                catRow.RowNumber = i + 1;
                empsByCategory.TryGetValue(catRow.CategoryID, out var catEmps);
                var listForCat = catEmps ?? new List<EmployeeRawMonthlyScoreItem>();
                catRow.TotalContinuingEmployeesCount = listForCat.Count;
                catRow.IncentiveEligibleEmployeesCount = listForCat.Count(e => e.HasIncentive);
                catRow.AttachMonthlyEmployeeScores(listForCat, year, month, roundToNearest50);
            }

            return categories;
        }

        public async Task<(bool IsSuccess, string Message)> SaveCategoriesIncentiveSettingsAsync(
            int year,
            int month,
            IEnumerable<CategoryIncentiveSettingRow> rows)
        {
            if (!CheckPermission("IncentiveBasics", "update", out string permErr) &&
                !CheckPermission("IncentiveBasics", "insert", out permErr) &&
                !CheckPermission("MasterData", "update", out permErr))
            {
                return (false, permErr);
            }

            if (year < 2020 || year > 2100 || month < 1 || month > 12)
                return (false, "يرجى اختيار سنة وشهر صحيحين لحفظ حدود الحوافز.");

            var list = rows?.ToList() ?? new List<CategoryIncentiveSettingRow>();
            if (list.Count == 0)
                return (false, "لا توجد فئات لحفظ حدود الحوافز لها.");

            foreach (var r in list)
            {
                if (r.MaxIncentiveAmount < 0m || r.MinIncentiveAmount < 0m)
                    return (false, $"لا يمكن إدخال قيمة سالبة في الفئة ({r.CategoryName}).");

                if (r.MinIncentiveAmount > r.MaxIncentiveAmount)
                    return (false, $"في الفئة ({r.CategoryName}): الحد الأدنى للحافز ({r.MinIncentiveAmount:N0}) لا يمكن أن يكون أكبر من الحد الأعلى ({r.MaxIncentiveAmount:N0}).");
            }

            using var conn = await OpenReadyConnectionAsync();
            var (isLocked, lockedBy, _) = await MonthlyPeriodConfigService.GetMonthLockStatusAsync(conn, year, month);
            if (isLocked)
            {
                string lockName = IncentiveCalculatorService.GetArabicMonthName(month);
                return (false, $"لا يمكن تعديل حدود الحوافز لشهر ({lockName} {year} م) لأنه معتمد ومغلق نهائياً بواسطة ({lockedBy}).");
            }

            using var tx = conn.BeginTransaction();
            try
            {
                foreach (var r in list)
                {
                    await conn.ExecuteAsync(@"
                        IF EXISTS (SELECT 1 FROM dbo.MonthlyCategoryIncentives WHERE EvalYear = @Year AND EvalMonth = @Month AND CategoryID = @CatID)
                        BEGIN
                            UPDATE dbo.MonthlyCategoryIncentives
                            SET MaxIncentiveAmount = @MaxAmt,
                                MinIncentiveAmount = @MinAmt,
                                UpdatedAt = SYSDATETIME()
                            WHERE EvalYear = @Year AND EvalMonth = @Month AND CategoryID = @CatID;
                        END
                        ELSE
                        BEGIN
                            INSERT INTO dbo.MonthlyCategoryIncentives (EvalYear, EvalMonth, CategoryID, MaxIncentiveAmount, MinIncentiveAmount, UpdatedAt)
                            VALUES (@Year, @Month, @CatID, @MaxAmt, @MinAmt, SYSDATETIME());
                        END",
                        new
                        {
                            Year = year,
                            Month = month,
                            MaxAmt = r.MaxIncentiveAmount,
                            MinAmt = r.MinIncentiveAmount,
                            CatID = r.CategoryID
                        }, tx);
                }

                await tx.CommitAsync();
                string arMonth = IncentiveCalculatorService.GetArabicMonthName(month);
                return (true, $"تم حفظ واعتماد حدود الحوافز لشهر ({arMonth} {year} م) لـ ({list.Count}) فئة بنجاح.");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return (false, $"حدث خطأ أثناء حفظ حدود الحوافز: {ex.Message}");
            }
        }

        public async Task<List<EvaluationRecordSummaryDto>> GetEvaluatorRecordsForMonthAsync(int year, int month)
        {
            var evalService = new WeeklyEvaluationService(_connectionString);
            return await evalService.GetEvaluationHistoryLogsAsync("", year, month);
        }

        public async Task<(bool IsSuccess, string Message)> SaveRecordBalancingAdjustmentsAsync(
            WeeklyEvaluationWorkspaceModel workspace)
        {
            if (!CheckPermission("ScoreBalancing", "update", out string permErr) &&
                !CheckPermission("ScoreBalancing", "insert", out permErr) &&
                !CheckPermission("HistoryLog", "update", out permErr))
            {
                return (false, permErr);
            }

            if (workspace == null || workspace.RecordID <= 0)
                return (false, "يرجى اختيار سجل تقييم شهري صالح أولاً.");

            using var conn = await OpenReadyConnectionAsync();
            var (isLocked, lockedBy, _) = await MonthlyPeriodConfigService.GetMonthLockStatusAsync(conn, workspace.EvalYear, workspace.EvalMonth);
            if (isLocked)
            {
                string lockName = IncentiveCalculatorService.GetArabicMonthName(workspace.EvalMonth);
                return (false, $"لا يمكن تعديل نسب الموازنة لشهر ({lockName} {workspace.EvalYear} م) لأنه معتمد ومغلق نهائياً بواسطة ({lockedBy}).");
            }

            using var tx = conn.BeginTransaction();
            try
            {
                int currentUserId = AuthService.CurrentSession?.UserID ?? 1;

                await conn.ExecuteAsync(@"
                    UPDATE dbo.EvaluationRecords
                    SET LastModifiedByUserID = @UserID, LastModifiedAt = SYSDATETIME()
                    WHERE RecordID = @RecordID",
                    new { UserID = currentUserId, workspace.RecordID }, tx);

                await conn.ExecuteAsync(
                    "DELETE FROM dbo.EvaluationEmployeeAdjustments WHERE RecordID = @RecordID",
                    new { workspace.RecordID }, tx);

                string insertAdjSql = @"
                    INSERT INTO dbo.EvaluationEmployeeAdjustments (RecordID, SheetID, EmployeeID, BalancingDelta)
                    VALUES (@RecordID, @SheetID, @EmployeeID, @Delta)";

                int adjustedCount = 0;
                foreach (var sheet in workspace.Sheets)
                {
                    foreach (var emp in sheet.EmployeeRows)
                    {
                        if (!emp.HasAnyEmptyCriteria && emp.BalancingDelta != 0m)
                        {
                            await conn.ExecuteAsync(insertAdjSql, new
                            {
                                workspace.RecordID,
                                sheet.SheetID,
                                emp.EmployeeID,
                                Delta = emp.BalancingDelta
                            }, tx);
                            adjustedCount++;
                        }
                    }
                }

                await tx.CommitAsync();
                string arMonth = IncentiveCalculatorService.GetArabicMonthName(workspace.EvalMonth);
                return (true, $"تم حفظ واعتماد نسب الموازنة لسجل المقيم ({workspace.EvaluatorName} — شهر {arMonth} {workspace.EvalYear} م) بنجاح.\n(عدد الموظفين المعدلين بنسبة موازنة: {adjustedCount}).");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return (false, $"حدث خطأ أثناء حفظ نسب الموازنة: {ex.Message}");
            }
        }
    }
}







