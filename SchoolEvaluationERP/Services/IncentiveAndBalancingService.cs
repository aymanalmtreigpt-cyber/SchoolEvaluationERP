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
                       c.MaxIncentiveAmount,
                       c.MinIncentiveAmount,
                       (SELECT COUNT(1) FROM dbo.Employees e WHERE e.CategoryID = c.CategoryID AND e.IsActive = 1 AND e.IsContinuing = 1) AS TotalContinuingEmployeesCount,
                       (SELECT COUNT(1) FROM dbo.Employees e WHERE e.CategoryID = c.CategoryID AND e.IsActive = 1 AND e.IsContinuing = 1 AND e.HasIncentive = 1) AS IncentiveEligibleEmployeesCount
                FROM dbo.Categories c
                WHERE c.IsActive = 1
                ORDER BY c.CategoryID ASC")).ToList();

            var employees = (await conn.QueryAsync<EmployeeRawMonthlyScoreItem>(@"
                SELECT e.EmployeeID, e.CategoryID, e.SystemName, b.BranchName, e.HasIncentive
                FROM dbo.Employees e
                INNER JOIN dbo.Branches b ON e.BranchID = b.BranchID
                WHERE e.IsActive = 1 AND e.IsContinuing = 1")).ToList();

            var catGroups = (await conn.QueryAsync<dynamic>(@"
                SELECT ceg.CategoryID, ceg.GroupID, ceg.WeightScore
                FROM dbo.CategoryEvaluatorGroups ceg
                INNER JOIN dbo.EvaluatorGroups eg ON ceg.GroupID = eg.GroupID
                WHERE eg.IsActive = 1")).ToList();

            var catGroupsMap = catGroups
                .GroupBy(x => (int)x.CategoryID)
                .ToDictionary(g => g.Key, g => g.ToList());

            var empAssignedEvals = (await conn.QueryAsync<dynamic>(@"
                SELECT ee.EmployeeID, ee.EvaluatorID, ev.GroupID
                FROM dbo.EmployeeEvaluators ee
                INNER JOIN dbo.Evaluators ev ON ee.EvaluatorID = ev.EvaluatorID
                WHERE ev.IsActive = 1")).ToList();

            var empAssignedMap = empAssignedEvals
                .GroupBy(x => (int)x.EmployeeID)
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
                GROUP BY es.EmployeeID, er.EvaluatorID, ev.GroupID, es.SheetID";

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
                reqGroups ??= new List<dynamic>();

                empAssignedMap.TryGetValue(emp.EmployeeID, out var assignedForEmp);
                assignedForEmp ??= new List<dynamic>();

                decimal monthlyTotalScore = 0m;

                if (reqGroups.Count > 0)
                {
                    foreach (var grp in reqGroups)
                    {
                        int gId = (int)grp.GroupID;
                        decimal groupWeight = (decimal)grp.WeightScore;

                        var assignedInGroup = assignedForEmp.Where(a => (int)a.GroupID == gId).ToList();
                        if (assignedInGroup.Count == 0) continue;

                        int assignedCount = assignedInGroup.Count;
                        decimal rawSumInGroup = 0m;

                        foreach (var member in assignedInGroup)
                        {
                            int evId = (int)member.EvaluatorID;
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
                catRow.AttachMonthlyEmployeeScores(catEmps ?? new List<EmployeeRawMonthlyScoreItem>(), year, month, roundToNearest50);
            }

            return categories;
        }

        public async Task<(bool IsSuccess, string Message)> SaveCategoriesIncentiveSettingsAsync(
            IEnumerable<CategoryIncentiveSettingRow> rows)
        {
            if (!CheckPermission("IncentiveBasics", "update", out string permErr) &&
                !CheckPermission("IncentiveBasics", "insert", out permErr) &&
                !CheckPermission("MasterData", "update", out permErr))
            {
                return (false, permErr);
            }

            var list = rows?.ToList() ?? new List<CategoryIncentiveSettingRow>();
            if (list.Count == 0)
                return (false, "لا توجد فئات لحفظ حدود الحوافز لها.");

            foreach (var r in list)
            {
                if (r.MaxIncentiveAmount < 0m || r.MinIncentiveAmount < 0m)
                    return (false, $"لا يمكن إدخال قيمة سالبة في الفئة ({r.CategoryName}).");

                if (r.MinIncentiveAmount > r.MaxIncentiveAmount && r.MaxIncentiveAmount > 0m)
                    return (false, $"في الفئة ({r.CategoryName}): الحد الأدنى للحافز ({r.MinIncentiveAmount:N0}) لا يمكن أن يكون أكبر من الحد الأعلى ({r.MaxIncentiveAmount:N0}).");
            }

            using var conn = await OpenReadyConnectionAsync();
            using var tx = conn.BeginTransaction();
            try
            {
                foreach (var r in list)
                {
                    await conn.ExecuteAsync(@"
                        UPDATE dbo.Categories
                        SET MaxIncentiveAmount = @MaxAmt,
                            MinIncentiveAmount = @MinAmt
                        WHERE CategoryID = @CatID",
                        new
                        {
                            MaxAmt = r.MaxIncentiveAmount,
                            MinAmt = r.MinIncentiveAmount,
                            CatID = r.CategoryID
                        }, tx);
                }

                await tx.CommitAsync();
                return (true, $"تم حفظ واعتماد حدود الحوافز (الحد الأعلى والحد الأدنى) لـ ({list.Count}) فئة بنجاح.");
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
                        if (emp.BalancingDelta != 0m)
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
