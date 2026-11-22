#nullable disable warnings
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
    public class MonthlyPeriodResolutionDto
    {
        public int RequestedYear { get; set; }
        public int RequestedMonth { get; set; }
        public int EffectiveYear { get; set; }
        public int EffectiveMonth { get; set; }
        public bool IsOwnConfig { get; set; }
        public bool HasAnyMonthlyConfig { get; set; }
        public bool IsMonthLocked { get; set; }
        public string LockedByUserName { get; set; } = string.Empty;
        public DateTime? LockedAt { get; set; }

        public string RequestedPeriodDisplay => $"{IncentiveCalculatorService.GetArabicMonthName(RequestedMonth)} {RequestedYear} م";
        public string EffectivePeriodDisplay => $"{IncentiveCalculatorService.GetArabicMonthName(EffectiveMonth)} {EffectiveYear} م";

        public string StatusBadgeText
        {
            get
            {
                if (IsMonthLocked)
                    return $"🔒 شهر ({RequestedPeriodDisplay}) معتمد ومغلق نهائياً — للقراءة فقط";

                if (!HasAnyMonthlyConfig)
                    return $"⚙ أساسيات شهر ({RequestedPeriodDisplay}) — التهيئة الافتراضية";

                if (IsOwnConfig)
                    return $"✔ أساسيات مستقلة محفوظة لشهر ({RequestedPeriodDisplay})";

                return $"🔗 يرث تلقائياً من ({EffectivePeriodDisplay}) — أي تعديل أو استيراد سيحفظ نسخة مستقلة لشهر ({RequestedPeriodDisplay})";
            }
        }
    }

    public class ConfiguredPeriodOptionDto
    {
        public int EvalYear { get; set; }
        public int EvalMonth { get; set; }
        public string DisplayText => $"{EvalYear} / {EvalMonth:00} — {IncentiveCalculatorService.GetArabicMonthName(EvalMonth)} {EvalYear} م";
    }

    public class EffectiveCategoryWeightDto
    {
        public int CategoryID { get; set; }
        public int GroupID { get; set; }
        public string GroupName { get; set; } = string.Empty;
        public decimal WeightScore { get; set; }
        public bool IsSystemGroup { get; set; }
        public string SystemGroupCode { get; set; } = string.Empty;
    }

    public class EffectiveEmployeeAssignmentDto
    {
        public int EmployeeID { get; set; }
        public int EvaluatorID { get; set; }
        public int GroupID { get; set; }
        public string EvaluatorName { get; set; } = string.Empty;
    }

    public class EffectiveEmployeeSnapshotDto
    {
        public int EmployeeID { get; set; }
        public int CategoryID { get; set; }
        public bool HasIncentive { get; set; } = true;
        public bool IsContinuing { get; set; } = true;
    }

    public class MonthlyPeriodConfigService
    {
        private readonly string _connectionString;

        public MonthlyPeriodConfigService() : this(AppConfigService.ConnectionString) { }

        public MonthlyPeriodConfigService(string connectionString)
        {
            _connectionString = connectionString;
        }

        private async Task<SqlConnection> OpenReadyConnectionAsync()
        {
            var conn = new SqlConnection(_connectionString);
            await DatabaseSchemaService.EnsureSchemaV2Async(conn);
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();
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
                errorMsg = "عذراً، لا تملك صلاحية تنفيذ هذا الإجراء.";
                return false;
            }
            return true;
        }

        public static async Task<(int Year, int Month)> GetLatestConfiguredPeriodAsync(SqlConnection conn, SqlTransaction? tx = null)
        {
            var latest = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT TOP 1 EvalYear, EvalMonth
                FROM (
                    SELECT EvalYear, EvalMonth FROM dbo.MonthlyCategoryEvaluatorGroups
                    UNION
                    SELECT EvalYear, EvalMonth FROM dbo.MonthlyEmployeeEvaluators
                    UNION
                    SELECT EvalYear, EvalMonth FROM dbo.MonthlyEmployeeSnapshots
                ) p
                ORDER BY (EvalYear * 100 + EvalMonth) DESC", transaction: tx);

            if (latest != null)
                return ((int)latest.EvalYear, (int)latest.EvalMonth);

            var latestEval = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT TOP 1 EvalYear, EvalMonth
                FROM dbo.EvaluationRecords
                ORDER BY (EvalYear * 100 + EvalMonth) DESC", transaction: tx);

            if (latestEval != null)
                return ((int)latestEval.EvalYear, (int)latestEval.EvalMonth);

            return (DateTime.Now.Year, DateTime.Now.Month);
        }

        public async Task<(int Year, int Month)> GetLatestConfiguredPeriodAsync()
        {
            using var conn = await OpenReadyConnectionAsync();
            return await GetLatestConfiguredPeriodAsync(conn);
        }

        public async Task<List<ConfiguredPeriodOptionDto>> GetAllConfiguredPeriodsAsync()
        {
            using var conn = await OpenReadyConnectionAsync();
            var list = (await conn.QueryAsync<ConfiguredPeriodOptionDto>(@"
                SELECT DISTINCT EvalYear, EvalMonth
                FROM (
                    SELECT EvalYear, EvalMonth FROM dbo.MonthlyCategoryEvaluatorGroups
                    UNION
                    SELECT EvalYear, EvalMonth FROM dbo.MonthlyEmployeeEvaluators
                    UNION
                    SELECT EvalYear, EvalMonth FROM dbo.MonthlyEmployeeSnapshots
                    UNION
                    SELECT EvalYear, EvalMonth FROM dbo.EvaluationRecords
                ) p
                ORDER BY EvalYear DESC, EvalMonth DESC")).ToList();

            if (list.Count == 0)
            {
                list.Add(new ConfiguredPeriodOptionDto
                {
                    EvalYear = DateTime.Now.Year,
                    EvalMonth = DateTime.Now.Month
                });
            }

            return list;
        }

        public static async Task<(bool IsLocked, string LockedBy, DateTime? LockedAt)> GetMonthLockStatusAsync(
            SqlConnection conn, int year, int month, SqlTransaction? tx = null)
        {
            var row = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT IsLocked, LockedByUserName, LockedAt
                FROM dbo.MonthlyClosedReports
                WHERE EvalYear = @Year AND EvalMonth = @Month",
                new { Year = year, Month = month }, tx);

            if (row == null || !Convert.ToBoolean(row.IsLocked))
                return (false, string.Empty, null);

            return (true, (row.LockedByUserName as string) ?? "مدير النظام العام", (DateTime?)row.LockedAt);
        }

        public async Task<(bool IsLocked, string LockedBy, DateTime? LockedAt)> GetMonthLockStatusAsync(int year, int month)
        {
            using var conn = await OpenReadyConnectionAsync();
            return await GetMonthLockStatusAsync(conn, year, month);
        }

        private static async Task<(int? EffYear, int? EffMonth, bool IsOwn, bool HasAny)> ResolveTablePeriodAsync(
            SqlConnection conn, string tableName, int year, int month, SqlTransaction? tx = null)
        {
            int targetKey = (year * 100) + month;

            var matched = await conn.QueryFirstOrDefaultAsync<dynamic>($@"
                SELECT TOP 1 EvalYear, EvalMonth
                FROM dbo.{tableName}
                WHERE (EvalYear * 100 + EvalMonth) <= @TargetKey
                GROUP BY EvalYear, EvalMonth
                ORDER BY (EvalYear * 100 + EvalMonth) DESC",
                new { TargetKey = targetKey }, tx);

            if (matched != null)
            {
                int ey = (int)matched.EvalYear;
                int em = (int)matched.EvalMonth;
                return (ey, em, ey == year && em == month, true);
            }

            var earliest = await conn.QueryFirstOrDefaultAsync<dynamic>($@"
                SELECT TOP 1 EvalYear, EvalMonth
                FROM dbo.{tableName}
                GROUP BY EvalYear, EvalMonth
                ORDER BY (EvalYear * 100 + EvalMonth) ASC", transaction: tx);

            if (earliest != null)
            {
                int ey = (int)earliest.EvalYear;
                int em = (int)earliest.EvalMonth;
                return (ey, em, ey == year && em == month, true);
            }

            return (null, null, false, false);
        }

        public static async Task<MonthlyPeriodResolutionDto> ResolveEffectivePeriodAsync(
            SqlConnection conn, int year, int month, SqlTransaction? tx = null)
        {
            var (isLocked, lockedBy, lockedAt) = await GetMonthLockStatusAsync(conn, year, month, tx);

            var wRes = await ResolveTablePeriodAsync(conn, "MonthlyCategoryEvaluatorGroups", year, month, tx);
            var aRes = await ResolveTablePeriodAsync(conn, "MonthlyEmployeeEvaluators", year, month, tx);
            var sRes = await ResolveTablePeriodAsync(conn, "MonthlyEmployeeSnapshots", year, month, tx);

            bool hasAny = wRes.HasAny || aRes.HasAny || sRes.HasAny;
            bool isOwn = wRes.IsOwn || aRes.IsOwn || sRes.IsOwn;

            int effYear = year;
            int effMonth = month;

            if (wRes.EffYear.HasValue)
            {
                effYear = wRes.EffYear.Value;
                effMonth = wRes.EffMonth!.Value;
            }
            else if (aRes.EffYear.HasValue)
            {
                effYear = aRes.EffYear.Value;
                effMonth = aRes.EffMonth!.Value;
            }
            else if (sRes.EffYear.HasValue)
            {
                effYear = sRes.EffYear.Value;
                effMonth = sRes.EffMonth!.Value;
            }

            return new MonthlyPeriodResolutionDto
            {
                RequestedYear = year,
                RequestedMonth = month,
                EffectiveYear = effYear,
                EffectiveMonth = effMonth,
                IsOwnConfig = isOwn,
                HasAnyMonthlyConfig = hasAny,
                IsMonthLocked = isLocked,
                LockedByUserName = lockedBy,
                LockedAt = lockedAt
            };
        }

        public async Task<MonthlyPeriodResolutionDto> ResolveEffectivePeriodAsync(int year, int month)
        {
            using var conn = await OpenReadyConnectionAsync();
            return await ResolveEffectivePeriodAsync(conn, year, month);
        }

        public static async Task EnsureMonthMaterializedAsync(
            SqlConnection conn, SqlTransaction? tx, int year, int month)
        {
            int ownWeightsCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.MonthlyCategoryEvaluatorGroups WHERE EvalYear = @Year AND EvalMonth = @Month",
                new { Year = year, Month = month }, tx);

            if (ownWeightsCount == 0)
            {
                var wRes = await ResolveTablePeriodAsync(conn, "MonthlyCategoryEvaluatorGroups", year, month, tx);
                if (wRes.EffYear.HasValue)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.MonthlyCategoryEvaluatorGroups (EvalYear, EvalMonth, CategoryID, GroupID, WeightScore, UpdatedAt)
                        SELECT @DstYear, @DstMonth, CategoryID, GroupID, WeightScore, SYSDATETIME()
                        FROM dbo.MonthlyCategoryEvaluatorGroups
                        WHERE EvalYear = @SrcYear AND EvalMonth = @SrcMonth",
                        new { DstYear = year, DstMonth = month, SrcYear = wRes.EffYear.Value, SrcMonth = wRes.EffMonth!.Value }, tx);
                }
                else
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.MonthlyCategoryEvaluatorGroups (EvalYear, EvalMonth, CategoryID, GroupID, WeightScore, UpdatedAt)
                        SELECT @DstYear, @DstMonth, CategoryID, GroupID, WeightScore, SYSDATETIME()
                        FROM dbo.CategoryEvaluatorGroups",
                        new { DstYear = year, DstMonth = month }, tx);
                }
            }

            int ownAssignCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.MonthlyEmployeeEvaluators WHERE EvalYear = @Year AND EvalMonth = @Month",
                new { Year = year, Month = month }, tx);

            if (ownAssignCount == 0)
            {
                var aRes = await ResolveTablePeriodAsync(conn, "MonthlyEmployeeEvaluators", year, month, tx);
                if (aRes.EffYear.HasValue)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.MonthlyEmployeeEvaluators (EvalYear, EvalMonth, EmployeeID, EvaluatorID, UpdatedAt)
                        SELECT @DstYear, @DstMonth, EmployeeID, EvaluatorID, SYSDATETIME()
                        FROM dbo.MonthlyEmployeeEvaluators
                        WHERE EvalYear = @SrcYear AND EvalMonth = @SrcMonth",
                        new { DstYear = year, DstMonth = month, SrcYear = aRes.EffYear.Value, SrcMonth = aRes.EffMonth!.Value }, tx);
                }
                else
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.MonthlyEmployeeEvaluators (EvalYear, EvalMonth, EmployeeID, EvaluatorID, UpdatedAt)
                        SELECT @DstYear, @DstMonth, EmployeeID, EvaluatorID, SYSDATETIME()
                        FROM dbo.EmployeeEvaluators",
                        new { DstYear = year, DstMonth = month }, tx);
                }
            }

            int ownSnapCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.MonthlyEmployeeSnapshots WHERE EvalYear = @Year AND EvalMonth = @Month",
                new { Year = year, Month = month }, tx);

            if (ownSnapCount == 0)
            {
                var sRes = await ResolveTablePeriodAsync(conn, "MonthlyEmployeeSnapshots", year, month, tx);
                if (sRes.EffYear.HasValue)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.MonthlyEmployeeSnapshots (EvalYear, EvalMonth, EmployeeID, CategoryID, HasIncentive, IsContinuing, UpdatedAt)
                        SELECT @DstYear, @DstMonth, EmployeeID, CategoryID, HasIncentive, IsContinuing, SYSDATETIME()
                        FROM dbo.MonthlyEmployeeSnapshots
                        WHERE EvalYear = @SrcYear AND EvalMonth = @SrcMonth",
                        new { DstYear = year, DstMonth = month, SrcYear = sRes.EffYear.Value, SrcMonth = sRes.EffMonth!.Value }, tx);
                }
                else
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.MonthlyEmployeeSnapshots (EvalYear, EvalMonth, EmployeeID, CategoryID, HasIncentive, IsContinuing, UpdatedAt)
                        SELECT @DstYear, @DstMonth, EmployeeID, CategoryID, HasIncentive, IsContinuing, SYSDATETIME()
                        FROM dbo.Employees
                        WHERE IsActive = 1",
                        new { DstYear = year, DstMonth = month }, tx);
                }
            }
        }

        public static async Task<List<EffectiveCategoryWeightDto>> GetEffectiveCategoryWeightsAsync(
            SqlConnection conn, int year, int month, SqlTransaction? tx = null)
        {
            var wRes = await ResolveTablePeriodAsync(conn, "MonthlyCategoryEvaluatorGroups", year, month, tx);
            if (wRes.EffYear.HasValue)
            {
                return (await conn.QueryAsync<EffectiveCategoryWeightDto>(@"
                    SELECT m.CategoryID, m.GroupID, eg.GroupName, m.WeightScore,
                           ISNULL(eg.IsSystemGroup, 0) AS IsSystemGroup,
                           ISNULL(eg.SystemGroupCode, N'') AS SystemGroupCode
                    FROM dbo.MonthlyCategoryEvaluatorGroups m
                    INNER JOIN dbo.EvaluatorGroups eg ON m.GroupID = eg.GroupID
                    WHERE m.EvalYear = @Year AND m.EvalMonth = @Month
                    ORDER BY m.WeightScore DESC, m.GroupID ASC",
                    new { Year = wRes.EffYear.Value, Month = wRes.EffMonth!.Value }, tx)).ToList();
            }

            return (await conn.QueryAsync<EffectiveCategoryWeightDto>(@"
                SELECT ceg.CategoryID, ceg.GroupID, eg.GroupName, ceg.WeightScore,
                       ISNULL(eg.IsSystemGroup, 0) AS IsSystemGroup,
                       ISNULL(eg.SystemGroupCode, N'') AS SystemGroupCode
                FROM dbo.CategoryEvaluatorGroups ceg
                INNER JOIN dbo.EvaluatorGroups eg ON ceg.GroupID = eg.GroupID
                WHERE eg.IsActive = 1
                ORDER BY ceg.WeightScore DESC, ceg.GroupID ASC", transaction: tx)).ToList();
        }

        public static async Task<List<EffectiveEmployeeAssignmentDto>> GetEffectiveEmployeeAssignmentsAsync(
            SqlConnection conn, int year, int month, SqlTransaction? tx = null)
        {
            var aRes = await ResolveTablePeriodAsync(conn, "MonthlyEmployeeEvaluators", year, month, tx);
            if (aRes.EffYear.HasValue)
            {
                return (await conn.QueryAsync<EffectiveEmployeeAssignmentDto>(@"
                    SELECT m.EmployeeID, m.EvaluatorID, ev.GroupID, ev.EvaluatorName
                    FROM dbo.MonthlyEmployeeEvaluators m
                    INNER JOIN dbo.Evaluators ev ON m.EvaluatorID = ev.EvaluatorID
                    WHERE m.EvalYear = @Year AND m.EvalMonth = @Month",
                    new { Year = aRes.EffYear.Value, Month = aRes.EffMonth!.Value }, tx)).ToList();
            }

            return (await conn.QueryAsync<EffectiveEmployeeAssignmentDto>(@"
                SELECT ee.EmployeeID, ee.EvaluatorID, ev.GroupID, ev.EvaluatorName
                FROM dbo.EmployeeEvaluators ee
                INNER JOIN dbo.Evaluators ev ON ee.EvaluatorID = ev.EvaluatorID
                WHERE ev.IsActive = 1", transaction: tx)).ToList();
        }

        public static async Task<Dictionary<int, EffectiveEmployeeSnapshotDto>> GetEffectiveEmployeeSnapshotsAsync(
            SqlConnection conn, int year, int month, SqlTransaction? tx = null)
        {
            var sRes = await ResolveTablePeriodAsync(conn, "MonthlyEmployeeSnapshots", year, month, tx);
            if (sRes.EffYear.HasValue)
            {
                var list = await conn.QueryAsync<EffectiveEmployeeSnapshotDto>(@"
                    SELECT EmployeeID, CategoryID, HasIncentive, IsContinuing
                    FROM dbo.MonthlyEmployeeSnapshots
                    WHERE EvalYear = @Year AND EvalMonth = @Month",
                    new { Year = sRes.EffYear.Value, Month = sRes.EffMonth!.Value }, tx);

                return list.GroupBy(x => x.EmployeeID).ToDictionary(g => g.Key, g => g.First());
            }

            var live = await conn.QueryAsync<EffectiveEmployeeSnapshotDto>(@"
                SELECT EmployeeID, CategoryID, HasIncentive, IsContinuing
                FROM dbo.Employees
                WHERE IsActive = 1", transaction: tx);

            return live.GroupBy(x => x.EmployeeID).ToDictionary(g => g.Key, g => g.First());
        }

        public static async Task SyncLiveTablesIfLatestPeriodAsync(
            SqlConnection conn, SqlTransaction? tx, int year, int month)
        {
            var (latestYear, latestMonth) = await GetLatestConfiguredPeriodAsync(conn, tx);
            int targetKey = (year * 100) + month;
            int latestKey = (latestYear * 100) + latestMonth;

            if (targetKey < latestKey)
                return;

            int hasWeights = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.MonthlyCategoryEvaluatorGroups WHERE EvalYear = @Year AND EvalMonth = @Month",
                new { Year = year, Month = month }, tx);

            if (hasWeights > 0)
            {
                await conn.ExecuteAsync(@"
                    DELETE FROM dbo.CategoryEvaluatorGroups;
                    INSERT INTO dbo.CategoryEvaluatorGroups (CategoryID, GroupID, WeightScore)
                    SELECT m.CategoryID, m.GroupID, m.WeightScore
                    FROM dbo.MonthlyCategoryEvaluatorGroups m
                    INNER JOIN dbo.Categories c ON c.CategoryID = m.CategoryID
                    INNER JOIN dbo.EvaluatorGroups eg ON eg.GroupID = m.GroupID
                    WHERE m.EvalYear = @Year AND m.EvalMonth = @Month;",
                    new { Year = year, Month = month }, tx);
            }

            int hasAssigns = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.MonthlyEmployeeEvaluators WHERE EvalYear = @Year AND EvalMonth = @Month",
                new { Year = year, Month = month }, tx);

            if (hasAssigns > 0)
            {
                await conn.ExecuteAsync(@"
                    DELETE FROM dbo.EmployeeEvaluators;
                    INSERT INTO dbo.EmployeeEvaluators (EmployeeID, EvaluatorID)
                    SELECT DISTINCT m.EmployeeID, m.EvaluatorID
                    FROM dbo.MonthlyEmployeeEvaluators m
                    INNER JOIN dbo.Employees e ON e.EmployeeID = m.EmployeeID
                    INNER JOIN dbo.Evaluators ev ON ev.EvaluatorID = m.EvaluatorID
                    WHERE m.EvalYear = @Year AND m.EvalMonth = @Month;",
                    new { Year = year, Month = month }, tx);
            }
        }

        public async Task<(bool IsSuccess, string Message)> CopyPeriodConfigAsync(
            int srcYear,
            int srcMonth,
            int dstYear,
            int dstMonth,
            bool copyWeights,
            bool copyAssignmentsAndSnapshots,
            bool copyMonthlyIncentives)
        {
            if (!CheckPermission("MasterData", "update", out string permErr) &&
                !CheckPermission("MasterData", "insert", out permErr))
            {
                return (false, permErr);
            }

            if (srcYear == dstYear && srcMonth == dstMonth)
                return (false, "لا يمكن النسخ إلى نفس الشهر؛ يرجى اختيار شهر هدف مختلف.");

            if (!copyWeights && !copyAssignmentsAndSnapshots && !copyMonthlyIncentives)
                return (false, "يرجى تحديد عنصر واحد على الأقل للنسخ.");

            using var conn = await OpenReadyConnectionAsync();

            var (isLocked, lockedBy, _) = await GetMonthLockStatusAsync(conn, dstYear, dstMonth);
            if (isLocked)
            {
                string dstName = $"{IncentiveCalculatorService.GetArabicMonthName(dstMonth)} {dstYear} م";
                return (false, $"لا يمكن اللصق في شهر ({dstName}) لأنه معتمد ومغلق نهائياً بواسطة ({lockedBy}). يرجى فك الإغلاق أولاً إن لزم الأمر.");
            }

            using var tx = conn.BeginTransaction();
            try
            {
                int copiedWeights = 0;
                int copiedAssigns = 0;
                int copiedSnaps = 0;
                int copiedIncentives = 0;

                if (copyWeights)
                {
                    var srcWeights = await GetEffectiveCategoryWeightsAsync(conn, srcYear, srcMonth, tx);
                    await conn.ExecuteAsync(
                        "DELETE FROM dbo.MonthlyCategoryEvaluatorGroups WHERE EvalYear = @Year AND EvalMonth = @Month",
                        new { Year = dstYear, Month = dstMonth }, tx);

                    foreach (var w in srcWeights)
                    {
                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.MonthlyCategoryEvaluatorGroups (EvalYear, EvalMonth, CategoryID, GroupID, WeightScore, UpdatedAt)
                            VALUES (@Year, @Month, @CatID, @GrpID, @Weight, SYSDATETIME())",
                            new { Year = dstYear, Month = dstMonth, CatID = w.CategoryID, GrpID = w.GroupID, Weight = w.WeightScore }, tx);
                        copiedWeights++;
                    }
                }

                if (copyAssignmentsAndSnapshots)
                {
                    var srcAssigns = await GetEffectiveEmployeeAssignmentsAsync(conn, srcYear, srcMonth, tx);
                    await conn.ExecuteAsync(
                        "DELETE FROM dbo.MonthlyEmployeeEvaluators WHERE EvalYear = @Year AND EvalMonth = @Month",
                        new { Year = dstYear, Month = dstMonth }, tx);

                    foreach (var a in srcAssigns)
                    {
                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.MonthlyEmployeeEvaluators (EvalYear, EvalMonth, EmployeeID, EvaluatorID, UpdatedAt)
                            VALUES (@Year, @Month, @EmpID, @EvalID, SYSDATETIME())",
                            new { Year = dstYear, Month = dstMonth, EmpID = a.EmployeeID, EvalID = a.EvaluatorID }, tx);
                        copiedAssigns++;
                    }

                    var srcSnaps = await GetEffectiveEmployeeSnapshotsAsync(conn, srcYear, srcMonth, tx);
                    await conn.ExecuteAsync(
                        "DELETE FROM dbo.MonthlyEmployeeSnapshots WHERE EvalYear = @Year AND EvalMonth = @Month",
                        new { Year = dstYear, Month = dstMonth }, tx);

                    foreach (var s in srcSnaps.Values)
                    {
                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.MonthlyEmployeeSnapshots (EvalYear, EvalMonth, EmployeeID, CategoryID, HasIncentive, IsContinuing, UpdatedAt)
                            VALUES (@Year, @Month, @EmpID, @CatID, @HasInc, @IsCont, SYSDATETIME())",
                            new
                            {
                                Year = dstYear,
                                Month = dstMonth,
                                EmpID = s.EmployeeID,
                                CatID = s.CategoryID,
                                HasInc = s.HasIncentive,
                                IsCont = s.IsContinuing
                            }, tx);
                        copiedSnaps++;
                    }
                }

                if (copyMonthlyIncentives)
                {
                    var srcInc = (await conn.QueryAsync<dynamic>(@"
                        SELECT CategoryID, MaxIncentiveAmount, MinIncentiveAmount
                        FROM dbo.MonthlyCategoryIncentives
                        WHERE EvalYear = @Year AND EvalMonth = @Month",
                        new { Year = srcYear, Month = srcMonth }, tx)).ToList();

                    if (srcInc.Count > 0)
                    {
                        await conn.ExecuteAsync(
                            "DELETE FROM dbo.MonthlyCategoryIncentives WHERE EvalYear = @Year AND EvalMonth = @Month",
                            new { Year = dstYear, Month = dstMonth }, tx);

                        foreach (var inc in srcInc)
                        {
                            await conn.ExecuteAsync(@"
                                INSERT INTO dbo.MonthlyCategoryIncentives (EvalYear, EvalMonth, CategoryID, MaxIncentiveAmount, MinIncentiveAmount, UpdatedAt)
                                VALUES (@Year, @Month, @CatID, @MaxAmt, @MinAmt, SYSDATETIME())",
                                new
                                {
                                    Year = dstYear,
                                    Month = dstMonth,
                                    CatID = (int)inc.CategoryID,
                                    MaxAmt = (decimal)inc.MaxIncentiveAmount,
                                    MinAmt = (decimal)inc.MinIncentiveAmount
                                }, tx);
                            copiedIncentives++;
                        }
                    }
                }

                await SyncLiveTablesIfLatestPeriodAsync(conn, tx, dstYear, dstMonth);
                await tx.CommitAsync();

                string srcLabel = $"{IncentiveCalculatorService.GetArabicMonthName(srcMonth)} {srcYear} م";
                string dstLabel = $"{IncentiveCalculatorService.GetArabicMonthName(dstMonth)} {dstYear} م";
                var parts = new List<string>();
                if (copyWeights) parts.Add($"• توزيع درجات الفئات: {copiedWeights} ربط");
                if (copyAssignmentsAndSnapshots) parts.Add($"• إسناد الموظفين للمقيمين: {copiedAssigns} إسناد ({copiedSnaps} موظف)");
                if (copyMonthlyIncentives) parts.Add($"• حدود الحوافز الشهرية: {copiedIncentives} فئة");

                return (true, $"✔ تم نسخ أساسيات ({srcLabel}) ولصقها في ({dstLabel}) بنجاح:\n{string.Join("\n", parts)}");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return (false, $"حدث خطأ أثناء نسخ أساسيات الشهر: {ex.Message}");
            }
        }

        public async Task<(bool IsSuccess, string Message)> ResetMonthToInheritAsync(int year, int month)
        {
            if (!CheckPermission("MasterData", "delete", out string permErr) &&
                !CheckPermission("MasterData", "update", out permErr))
            {
                return (false, permErr);
            }

            using var conn = await OpenReadyConnectionAsync();
            var (isLocked, lockedBy, _) = await GetMonthLockStatusAsync(conn, year, month);
            if (isLocked)
                return (false, $"لا يمكن تعديل هذا الشهر لأنه مغلق نهائياً بواسطة ({lockedBy}).");

            int targetKey = (year * 100) + month;
            int earlierPeriodsCount = await conn.ExecuteScalarAsync<int>(@"
                SELECT COUNT(1)
                FROM (
                    SELECT DISTINCT EvalYear, EvalMonth FROM dbo.MonthlyCategoryEvaluatorGroups
                    WHERE (EvalYear * 100 + EvalMonth) < @TargetKey
                ) x", new { TargetKey = targetKey });

            if (earlierPeriodsCount == 0)
                return (false, "لا يمكن حذف تخصيص هذا الشهر لأنه أقدم شهر أساسيات مهيّأ في النظام ولا يوجد شهر سابق ليرث منه.");

            using var tx = conn.BeginTransaction();
            try
            {
                await conn.ExecuteAsync(@"
                    DELETE FROM dbo.MonthlyCategoryEvaluatorGroups WHERE EvalYear = @Year AND EvalMonth = @Month;
                    DELETE FROM dbo.MonthlyEmployeeEvaluators WHERE EvalYear = @Year AND EvalMonth = @Month;
                    DELETE FROM dbo.MonthlyEmployeeSnapshots WHERE EvalYear = @Year AND EvalMonth = @Month;",
                    new { Year = year, Month = month }, tx);

                var (latestY, latestM) = await GetLatestConfiguredPeriodAsync(conn, tx);
                await SyncLiveTablesIfLatestPeriodAsync(conn, tx, latestY, latestM);
                await tx.CommitAsync();

                string label = $"{IncentiveCalculatorService.GetArabicMonthName(month)} {year} م";
                return (true, $"✔ تمت إعادة شهر ({label}) للوراثة التلقائية من أقرب شهر سابق بنجاح.");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return (false, $"تعذر إعادة ضبط الشهر: {ex.Message}");
            }
        }

        public async Task<(bool IsSuccess, string Message)> RebuildMonthConfigFromSavedEvaluationsAsync(int year, int month)
        {
            if (!CheckPermission("MasterData", "update", out string permErr) &&
                !CheckPermission("FinalReport", "update", out permErr))
            {
                return (false, permErr);
            }

            using var conn = await OpenReadyConnectionAsync();
            var (isLocked, lockedBy, _) = await GetMonthLockStatusAsync(conn, year, month);
            if (isLocked)
                return (false, $"الشهر مغلق نهائياً بواسطة ({lockedBy})؛ يرجى فك الإغلاق أولاً.");

            var savedPairs = (await conn.QueryAsync<dynamic>(@"
                SELECT es.EmployeeID,
                       er.EvaluatorID,
                       ev.GroupID,
                       MAX(es.SnapshotEvaluatorWeight) AS SnapWeight,
                       MAX(es.SnapshotCategoryName) AS SnapCatName,
                       MAX(CAST(es.SnapshotHasIncentive AS INT)) AS SnapHasInc,
                       MAX(e.CategoryID) AS LiveCatID,
                       MAX(CAST(e.HasIncentive AS INT)) AS LiveHasInc
                FROM dbo.EvaluationScores es
                INNER JOIN dbo.EvaluationRecords er ON es.RecordID = er.RecordID
                INNER JOIN dbo.Evaluators ev ON er.EvaluatorID = ev.EvaluatorID
                LEFT JOIN dbo.Employees e ON es.EmployeeID = e.EmployeeID
                WHERE er.EvalYear = @Year AND er.EvalMonth = @Month
                GROUP BY es.EmployeeID, er.EvaluatorID, ev.GroupID",
                new { Year = year, Month = month })).ToList();

            if (savedPairs.Count == 0)
                return (false, "لا توجد تقييمات شهرية محفوظة في هذا الشهر لاسترجاع الأساسيات منها.");

            var categories = (await conn.QueryAsync<dynamic>("SELECT CategoryID, CategoryName FROM dbo.Categories")).ToList();
            var catNameToId = categories
                .GroupBy(c => ((string)c.CategoryName).Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (int)g.First().CategoryID, StringComparer.OrdinalIgnoreCase);

            var empSnapshots = new Dictionary<int, (int CategoryID, bool HasIncentive, bool IsContinuing)>();
            var empAssignments = new HashSet<(int EmployeeID, int EvaluatorID)>();
            var catGroupWeightSamples = new Dictionary<(int CatID, int GroupID), List<decimal>>();

            foreach (var empGrp in savedPairs.GroupBy(x => (int)x.EmployeeID))
            {
                int empId = empGrp.Key;
                var first = empGrp.First();

                int catId = first.LiveCatID != null ? (int)first.LiveCatID : 0;
                string snapCatName = (first.SnapCatName as string)?.Trim() ?? string.Empty;
                if (!string.IsNullOrEmpty(snapCatName) && catNameToId.TryGetValue(snapCatName, out int matchedCatId))
                {
                    catId = matchedCatId;
                }

                bool hasInc = first.SnapHasInc != null
                    ? ((int)first.SnapHasInc) == 1
                    : (first.LiveHasInc == null || ((int)first.LiveHasInc) == 1);

                if (catId > 0)
                {
                    empSnapshots[empId] = (catId, hasInc, true);
                }

                foreach (var row in empGrp)
                {
                    empAssignments.Add((empId, (int)row.EvaluatorID));
                }

                if (catId > 0)
                {
                    foreach (var grpMembers in empGrp.GroupBy(x => (int)x.GroupID))
                    {
                        int groupId = grpMembers.Key;
                        decimal grpWeightSum = grpMembers.Sum(x => (decimal)x.SnapWeight);
                        if (grpWeightSum > 0m)
                        {
                            decimal roundedW = Math.Round(grpWeightSum, 0, MidpointRounding.AwayFromZero);
                            if (roundedW > 0m && roundedW <= 100m)
                            {
                                var key = (catId, groupId);
                                if (!catGroupWeightSamples.TryGetValue(key, out var list))
                                {
                                    list = new List<decimal>();
                                    catGroupWeightSamples[key] = list;
                                }
                                list.Add(roundedW);
                            }
                        }
                    }
                }
            }

            var rebuiltWeights = new Dictionary<(int CatID, int GroupID), decimal>();
            foreach (var kv in catGroupWeightSamples)
            {
                decimal modeWeight = kv.Value
                    .GroupBy(v => v)
                    .OrderByDescending(g => g.Count())
                    .ThenByDescending(g => g.Key)
                    .First().Key;
                rebuiltWeights[kv.Key] = modeWeight;
            }

            using var tx = conn.BeginTransaction();
            try
            {
                var fallbackWeights = await GetEffectiveCategoryWeightsAsync(conn, year, month, tx);
                var rebuiltCats = rebuiltWeights.Keys.Select(k => k.CatID).ToHashSet();

                await conn.ExecuteAsync(
                    "DELETE FROM dbo.MonthlyCategoryEvaluatorGroups WHERE EvalYear = @Year AND EvalMonth = @Month",
                    new { Year = year, Month = month }, tx);

                foreach (var kv in rebuiltWeights)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.MonthlyCategoryEvaluatorGroups (EvalYear, EvalMonth, CategoryID, GroupID, WeightScore, UpdatedAt)
                        VALUES (@Year, @Month, @CatID, @GrpID, @Weight, SYSDATETIME())",
                        new { Year = year, Month = month, CatID = kv.Key.CatID, GrpID = kv.Key.GroupID, Weight = kv.Value }, tx);
                }

                foreach (var fw in fallbackWeights)
                {
                    if (!rebuiltCats.Contains(fw.CategoryID))
                    {
                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.MonthlyCategoryEvaluatorGroups (EvalYear, EvalMonth, CategoryID, GroupID, WeightScore, UpdatedAt)
                            VALUES (@Year, @Month, @CatID, @GrpID, @Weight, SYSDATETIME())",
                            new { Year = year, Month = month, CatID = fw.CategoryID, GrpID = fw.GroupID, Weight = fw.WeightScore }, tx);
                    }
                }

                await conn.ExecuteAsync(
                    "DELETE FROM dbo.MonthlyEmployeeEvaluators WHERE EvalYear = @Year AND EvalMonth = @Month",
                    new { Year = year, Month = month }, tx);

                foreach (var pair in empAssignments)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.MonthlyEmployeeEvaluators (EvalYear, EvalMonth, EmployeeID, EvaluatorID, UpdatedAt)
                        VALUES (@Year, @Month, @EmpID, @EvalID, SYSDATETIME())",
                        new { Year = year, Month = month, EmpID = pair.EmployeeID, EvalID = pair.EvaluatorID }, tx);
                }

                await conn.ExecuteAsync(
                    "DELETE FROM dbo.MonthlyEmployeeSnapshots WHERE EvalYear = @Year AND EvalMonth = @Month",
                    new { Year = year, Month = month }, tx);

                foreach (var snap in empSnapshots)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.MonthlyEmployeeSnapshots (EvalYear, EvalMonth, EmployeeID, CategoryID, HasIncentive, IsContinuing, UpdatedAt)
                        VALUES (@Year, @Month, @EmpID, @CatID, @HasInc, @IsCont, SYSDATETIME())",
                        new
                        {
                            Year = year,
                            Month = month,
                            EmpID = snap.Key,
                            CatID = snap.Value.CategoryID,
                            HasInc = snap.Value.HasIncentive,
                            IsCont = snap.Value.IsContinuing
                        }, tx);
                }

                await tx.CommitAsync();

                string label = $"{IncentiveCalculatorService.GetArabicMonthName(month)} {year} م";
                return (true,
                    $"✔ تم استرجاع وبناء أساسيات شهر ({label}) بدقة من التقييمات المحفوظة فيه:\n" +
                    $"• الفئات المسترجعة أوزانها: {rebuiltCats.Count} فئة\n" +
                    $"• الموظفون المسترجعة حالاتهم: {empSnapshots.Count} موظف\n" +
                    $"• إسنادات المقيمين المسترجعة: {empAssignments.Count} إسناد");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return (false, $"حدث خطأ أثناء استرجاع أساسيات الشهر: {ex.Message}");
            }
        }
    }
}
