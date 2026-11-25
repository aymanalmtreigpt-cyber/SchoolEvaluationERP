#nullable disable warnings
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using SchoolEvaluationERP.Models;

namespace SchoolEvaluationERP.Services
{
    public class TemplateService
    {
        private static readonly SemaphoreSlim _systemTemplateSyncLock = new(1, 1);
        private readonly string _connectionString;

        public TemplateService() : this(AppConfigService.ConnectionString) { }

        public TemplateService(string connectionString)
        {
            _connectionString = connectionString;
        }

        public static async Task EnsureSchemaUpToDateAsync(SqlConnection conn)
        {
            await DatabaseSchemaService.EnsureSchemaV2Async(conn);
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync();
            }
        }

        private static bool CheckPermission(string action, out string errorMsg)
        {
            errorMsg = string.Empty;
            if (AuthService.CurrentSession == null)
            {
                errorMsg = "انتهت جلسة العمل، يرجى إعادة تسجيل الدخول.";
                return false;
            }
            if (!AuthService.CurrentSession.HasPermission("Templates", action))
            {
                string arAction = action switch
                {
                    "insert" => "إضافة وكتابة",
                    "update" => "تعديل",
                    "delete" => "حذف",
                    _ => "قراءة"
                };
                errorMsg = $"عذراً، لا تملك صلاحية ({arAction}) في شاشة قوالب التقييم.";
                return false;
            }
            return true;
        }

        private static readonly SemaphoreSlim _autoCategorySheetLock = new(1, 1);

        public async Task<List<EvaluatorDto>> SearchEvaluatorsAsync(string query = "", bool excludeSystemGroups = false)
        {
            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);

            string sql = @"
                SELECT ev.EvaluatorID, ev.GroupID, eg.GroupName, ev.EvaluatorName,
                       (SELECT COUNT(1) FROM dbo.CategoryEvaluatorGroups ceg WHERE ceg.GroupID = ev.GroupID) AS AssignedCategoriesCount,
                       (SELECT COUNT(1) FROM dbo.EmployeeEvaluators ee INNER JOIN dbo.Employees e ON ee.EmployeeID = e.EmployeeID WHERE ee.EvaluatorID = ev.EvaluatorID AND e.IsActive = 1 AND e.IsContinuing = 1) AS AssignedEmployeesCount,
                       ISNULL((
                           SELECT TOP 1 t.TemplateID
                           FROM dbo.Templates t
                           WHERE t.EvaluatorID = ev.EvaluatorID AND t.IsCurrentVersion = 1 AND t.IsDeleted = 0
                           ORDER BY t.TemplateID DESC
                       ), 0) AS ExistingTemplateID
                FROM dbo.Evaluators ev
                INNER JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE ev.IsActive = 1 AND eg.IsActive = 1
                  AND (@ExcludeSys = 0 OR ISNULL(eg.IsSystemGroup, 0) = 0)
                  AND (@Q = '' OR ev.EvaluatorName LIKE @Pattern OR eg.GroupName LIKE @Pattern)
                ORDER BY
                    CASE WHEN EXISTS (SELECT 1 FROM dbo.Templates t2 WHERE t2.EvaluatorID = ev.EvaluatorID AND t2.IsCurrentVersion = 1 AND t2.IsDeleted = 0) THEN 1 ELSE 0 END ASC,
                    ISNULL(eg.IsSystemGroup, 0) DESC,
                    eg.GroupName ASC,
                    ev.EvaluatorName ASC";

            var list = await conn.QueryAsync<EvaluatorDto>(sql, new
            {
                Q = (query ?? string.Empty).Trim(),
                Pattern = $"%{(query ?? string.Empty).Trim()}%",
                ExcludeSys = excludeSystemGroups ? 1 : 0
            });
            return list.ToList();
        }

        public static async Task EnsureMissingCategorySheetsForTemplateAsync(
            SqlConnection conn,
            int templateId,
            int evaluatorId,
            int year,
            int month)
        {
            if (templateId <= 0 || evaluatorId <= 0) return;

            await _autoCategorySheetLock.WaitAsync();
            try
            {
                var tmplInfo = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT t.TemplateID, ISNULL(eg.IsSystemGroup, 0) AS IsSystemGroup
                    FROM dbo.Templates t
                    INNER JOIN dbo.Evaluators ev ON t.EvaluatorID = ev.EvaluatorID
                    LEFT JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                    WHERE t.TemplateID = @TID AND t.IsDeleted = 0", new { TID = templateId });

                if (tmplInfo == null || Convert.ToBoolean(tmplInfo.IsSystemGroup))
                    return;

                var contEmps = await LoadEffectiveContinuingEmployeesAsync(conn, year, month);
                var effAssigns = await MonthlyPeriodConfigService.GetEffectiveEmployeeAssignmentsAsync(conn, year, month);
                var assignedToEval = effAssigns
                    .Where(a => a.EvaluatorID == evaluatorId)
                    .Select(a => a.EmployeeID)
                    .ToHashSet();

                if (assignedToEval.Count == 0)
                    return;

                var excludedEmpIds = (await conn.QueryAsync<int>(
                    "SELECT EmployeeID FROM dbo.TemplateExcludedEmployees WHERE EvaluatorID = @EvalID",
                    new { EvalID = evaluatorId })).ToHashSet();

                var eligibleEmps = contEmps
                    .Where(e => assignedToEval.Contains(e.EmployeeID) && !excludedEmpIds.Contains(e.EmployeeID))
                    .ToList();

                if (eligibleEmps.Count == 0)
                    return;

                var existingSheets = (await conn.QueryAsync<dynamic>(@"
                    SELECT SheetID, TargetCategoryID, TargetBranchID, SortOrder
                    FROM dbo.TemplateSheets
                    WHERE TemplateID = @TID", new { TID = templateId })).ToList();

                int maxSortOrder = existingSheets.Count > 0 ? existingSheets.Max(s => (int)s.SortOrder) : 0;

                var uncoveredByCategory = eligibleEmps
                    .Where(emp => !existingSheets.Any(sh =>
                        sh.TargetCategoryID != null &&
                        (int)sh.TargetCategoryID == emp.CategoryID &&
                        (sh.TargetBranchID == null || (int)sh.TargetBranchID <= 0 || (int)sh.TargetBranchID == emp.BranchID)))
                    .GroupBy(emp => emp.CategoryID)
                    .OrderBy(g => g.Key)
                    .ToList();

                if (uncoveredByCategory.Count == 0)
                    return;

                foreach (var catGroup in uncoveredByCategory)
                {
                    int catId = catGroup.Key;
                    string catName = catGroup.First().CategoryName;
                    maxSortOrder++;

                    int newSheetId = await conn.ExecuteScalarAsync<int>(@"
                        INSERT INTO dbo.TemplateSheets
                            (TemplateID, SheetName, TargetCategoryID, TargetBranchID, TargetCategoryName, SortOrder)
                        VALUES
                            (@TID, @Name, @CatID, NULL, @CatName, @SortOrder);
                        SELECT CAST(SCOPE_IDENTITY() AS INT);",
                        new
                        {
                            TID = templateId,
                            Name = catName,
                            CatID = catId,
                            CatName = catName,
                            SortOrder = maxSortOrder
                        });

                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.SheetCriteria (SheetID, CriteriaTitle, MaxScore, ColumnOrder)
                        VALUES (@SheetID, N'أساسيات التقييم', 100, 1);",
                        new { SheetID = newSheetId });

                    var orderedEmps = catGroup
                        .OrderBy(e => e.BranchName)
                        .ThenBy(e => e.EvaluationName)
                        .ToList();

                    for (int i = 0; i < orderedEmps.Count; i++)
                    {
                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.SheetEmployees (SheetID, EmployeeID, RowOrder)
                            VALUES (@SheetID, @EmpID, @Ord);",
                            new { SheetID = newSheetId, EmpID = orderedEmps[i].EmployeeID, Ord = i + 1 });
                    }
                }
            }
            finally
            {
                _autoCategorySheetLock.Release();
            }
        }

        public async Task<TemplateFullModel?> BuildAutoTemplateForEvaluatorAsync(
            EvaluatorDto evaluator,
            int? evalYear = null,
            int? evalMonth = null)
        {
            if (evaluator == null || evaluator.EvaluatorID <= 0)
                return null;

            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);
            var (year, month) = await ResolvePeriodOrLatestAsync(conn, evalYear, evalMonth);

            var categories = await GetCategoriesForEvaluatorAsync(evaluator.EvaluatorID, year, month);
            var nonEmptyCategories = categories.Where(c => c.AssignedEmployeesCount > 0).ToList();
            if (nonEmptyCategories.Count == 0)
                return null;

            var excludedEmpIds = (await conn.QueryAsync<int>(
                "SELECT EmployeeID FROM dbo.TemplateExcludedEmployees WHERE EvaluatorID = @EvalID",
                new { EvalID = evaluator.EvaluatorID })).ToHashSet();

            var model = new TemplateFullModel
            {
                TemplateID = 0,
                TemplateGroupGuid = Guid.NewGuid(),
                EvaluatorID = evaluator.EvaluatorID,
                EvaluatorName = evaluator.EvaluatorName,
                GroupName = evaluator.GroupName,
                VersionNumber = 1
            };

            int sortOrder = 1;
            foreach (var cat in nonEmptyCategories)
            {
                var eligible = await GetEligibleEmployeesForSheetAsync(cat.CategoryID, null, evaluator.EvaluatorID, year, month);
                var activeRows = eligible.Where(e => !excludedEmpIds.Contains(e.EmployeeID)).ToList();
                if (activeRows.Count == 0)
                    continue;

                var sheet = TemplateSheetModel.CreateDefault(
                    cat.CategoryName,
                    cat.CategoryID,
                    cat.CategoryName,
                    null,
                    "جميع الفروع (كلاهما)",
                    sortOrder++);

                for (int i = 0; i < activeRows.Count; i++)
                {
                    activeRows[i].RowOrder = i + 1;
                    sheet.EmployeeRows.Add(activeRows[i]);
                }

                model.Sheets.Add(sheet);
            }

            return model.Sheets.Count > 0 ? model : null;
        }

        public const string ExcelTemplatePasswordSettingKey = "ExcelTemplateProtectionPassword";
        public const string DefaultExcelProtectionPassword = "SchoolEval@2026#Protect";

        public async Task<string> GetSavedExcelProtectionPasswordRawAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);

            string? saved = await conn.ExecuteScalarAsync<string?>(
                "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = @Key",
                new { Key = ExcelTemplatePasswordSettingKey });

            return saved ?? string.Empty;
        }

        public async Task<string> GetEffectiveExcelProtectionPasswordAsync()
        {
            string saved = await GetSavedExcelProtectionPasswordRawAsync();
            return string.IsNullOrWhiteSpace(saved) ? DefaultExcelProtectionPassword : saved.Trim();
        }

        public async Task<(bool IsSuccess, string Message)> SaveExcelProtectionPasswordAsync(string? password)
        {
            if (AuthService.CurrentSession == null)
                return (false, "انتهت جلسة العمل، يرجى إعادة تسجيل الدخول.");

            if (!AuthService.CurrentSession.IsAdmin)
                return (false, "عذراً، تعديل كلمة سر قوالب الإكسل مخصص لمدير النظام فقط.");

            string cleanPwd = (password ?? string.Empty).Trim();

            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);

            await conn.ExecuteAsync(@"
                IF EXISTS (SELECT 1 FROM dbo.SystemSettings WHERE SettingKey = @Key)
                    UPDATE dbo.SystemSettings SET SettingValue = @Val, UpdatedAt = SYSDATETIME() WHERE SettingKey = @Key;
                ELSE
                    INSERT INTO dbo.SystemSettings (SettingKey, SettingValue, UpdatedAt) VALUES (@Key, @Val, SYSDATETIME());",
                new { Key = ExcelTemplatePasswordSettingKey, Val = cleanPwd });

            return (true, string.IsNullOrEmpty(cleanPwd)
                ? "تم مسح كلمة السر المخصصة — سيتم استخدام كلمة السر الافتراضية الداخلية لحماية قوالب الإكسل تلقائياً."
                : "✔ تم حفظ كلمة سر حماية قوالب الإكسل بنجاح؛ سيتم حماية جميع قوالب الإكسل المستخرجة بها.");
        }

        public async Task<bool> HasAnyActiveCategoriesAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);
            int count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM dbo.Categories WHERE IsActive = 1");
            return count > 0;
        }

        private static async Task<(int Year, int Month)> ResolvePeriodOrLatestAsync(
            SqlConnection conn, int? evalYear, int? evalMonth, SqlTransaction? tx = null)
        {
            if (evalYear.HasValue && evalMonth.HasValue &&
                evalYear.Value >= 2020 && evalYear.Value <= 2100 &&
                evalMonth.Value >= 1 && evalMonth.Value <= 12)
            {
                return (evalYear.Value, evalMonth.Value);
            }
            return await MonthlyPeriodConfigService.GetLatestConfiguredPeriodAsync(conn, tx);
        }

        private class ActiveEmployeeBaseRow
        {
            public int EmployeeID { get; set; }
            public string EvaluationName { get; set; } = string.Empty;
            public string SystemName { get; set; } = string.Empty;
            public int BranchID { get; set; }
            public string BranchName { get; set; } = string.Empty;
            public int CategoryID { get; set; }
            public string CategoryName { get; set; } = string.Empty;
            public string JobTitle { get; set; } = string.Empty;
            public string JobTypeName { get; set; } = string.Empty;
            public bool HasIncentive { get; set; }
            public bool IsContinuing { get; set; }
        }

        private static async Task<List<ActiveEmployeeBaseRow>> LoadEffectiveContinuingEmployeesAsync(
            SqlConnection conn, int year, int month, SqlTransaction? tx = null)
        {
            var rawEmps = (await conn.QueryAsync<ActiveEmployeeBaseRow>(@"
                SELECT e.EmployeeID, e.EvaluationName, e.SystemName,
                       e.BranchID, b.BranchName,
                       e.CategoryID, c.CategoryName,
                       j.JobTitle, jt.JobTypeName,
                       e.HasIncentive, e.IsContinuing
                FROM dbo.Employees e
                INNER JOIN dbo.Branches b ON e.BranchID = b.BranchID
                INNER JOIN dbo.Categories c ON e.CategoryID = c.CategoryID
                INNER JOIN dbo.Jobs j ON e.JobID = j.JobID
                INNER JOIN dbo.JobTypes jt ON e.JobTypeID = jt.JobTypeID
                WHERE e.IsActive = 1", transaction: tx)).ToList();

            var catNameMap = (await conn.QueryAsync<dynamic>("SELECT CategoryID, CategoryName FROM dbo.Categories", transaction: tx))
                .ToDictionary(x => (int)x.CategoryID, x => (string)x.CategoryName);

            var snaps = await MonthlyPeriodConfigService.GetEffectiveEmployeeSnapshotsAsync(conn, year, month, tx);

            var result = new List<ActiveEmployeeBaseRow>();
            foreach (var emp in rawEmps)
            {
                if (snaps.TryGetValue(emp.EmployeeID, out var snap))
                {
                    emp.CategoryID = snap.CategoryID;
                    if (catNameMap.TryGetValue(snap.CategoryID, out var cName))
                        emp.CategoryName = cName;
                    emp.HasIncentive = snap.HasIncentive;
                    emp.IsContinuing = snap.IsContinuing;
                }

                if (emp.IsContinuing)
                    result.Add(emp);
            }
            return result;
        }

        private static decimal ComputeEvaluatorWeightForEmployee(
            int employeeId,
            int categoryId,
            int groupId,
            bool isSystemGroup,
            Dictionary<(int CatID, int GroupID), decimal> weightMap,
            Dictionary<(int EmpID, int GroupID), int> assignedCountByEmpGroup)
        {
            if (!weightMap.TryGetValue((categoryId, groupId), out decimal grpWeight) || grpWeight <= 0m)
                return 0m;

            if (isSystemGroup)
                return Math.Round(grpWeight, 2, MidpointRounding.AwayFromZero);

            if (!assignedCountByEmpGroup.TryGetValue((employeeId, groupId), out int cnt) || cnt <= 0)
                return 0m;

            return Math.Round(grpWeight / cnt, 2, MidpointRounding.AwayFromZero);
        }

        public async Task<List<TemplateCategoryOptionDto>> GetCategoriesForEvaluatorAsync(
            int evaluatorId, int? evalYear = null, int? evalMonth = null)
        {
            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);
            var (year, month) = await ResolvePeriodOrLatestAsync(conn, evalYear, evalMonth);

            var evalInfo = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT ev.GroupID, ISNULL(eg.IsSystemGroup, 0) AS IsSystemGroup
                FROM dbo.Evaluators ev
                INNER JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE ev.EvaluatorID = @EvalID", new { EvalID = evaluatorId });

            if (evalInfo == null) return new List<TemplateCategoryOptionDto>();
            int groupId = (int)evalInfo.GroupID;
            bool isSys = Convert.ToBoolean(evalInfo.IsSystemGroup);

            var effWeights = await MonthlyPeriodConfigService.GetEffectiveCategoryWeightsAsync(conn, year, month);
            var allowedCatIds = effWeights.Where(w => w.GroupID == groupId).Select(w => w.CategoryID).Distinct().ToHashSet();
            if (allowedCatIds.Count == 0) return new List<TemplateCategoryOptionDto>();

            var categories = (await conn.QueryAsync<TemplateCategoryOptionDto>(@"
                SELECT CategoryID, CategoryName
                FROM dbo.Categories
                WHERE IsActive = 1 AND CategoryID IN @CatIDs
                ORDER BY CategoryID ASC", new { CatIDs = allowedCatIds })).ToList();

            var contEmps = await LoadEffectiveContinuingEmployeesAsync(conn, year, month);
            var effAssigns = await MonthlyPeriodConfigService.GetEffectiveEmployeeAssignmentsAsync(conn, year, month);
            var assignedToEval = effAssigns.Where(a => a.EvaluatorID == evaluatorId).Select(a => a.EmployeeID).ToHashSet();

            foreach (var cat in categories)
            {
                cat.AssignedEmployeesCount = contEmps.Count(e =>
                    e.CategoryID == cat.CategoryID && (isSys || assignedToEval.Contains(e.EmployeeID)));
            }

            return categories;
        }

        public async Task<List<TemplateBranchOptionDto>> GetBranchOptionsForEvaluatorCategoryAsync(
            int evaluatorId, int categoryId, int? evalYear = null, int? evalMonth = null)
        {
            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);
            var (year, month) = await ResolvePeriodOrLatestAsync(conn, evalYear, evalMonth);

            var evalInfo = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT ISNULL(eg.IsSystemGroup, 0) AS IsSystemGroup
                FROM dbo.Evaluators ev
                INNER JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE ev.EvaluatorID = @EvalID", new { EvalID = evaluatorId });
            bool isSys = evalInfo != null && Convert.ToBoolean(evalInfo.IsSystemGroup);

            var branches = (await conn.QueryAsync<TemplateBranchOptionDto>(@"
                SELECT BranchID, BranchName
                FROM dbo.Branches
                WHERE IsActive = 1
                ORDER BY BranchID ASC")).ToList();

            var contEmps = await LoadEffectiveContinuingEmployeesAsync(conn, year, month);
            var effAssigns = await MonthlyPeriodConfigService.GetEffectiveEmployeeAssignmentsAsync(conn, year, month);
            var assignedToEval = effAssigns.Where(a => a.EvaluatorID == evaluatorId).Select(a => a.EmployeeID).ToHashSet();

            var matchingEmps = contEmps.Where(e =>
                e.CategoryID == categoryId && (isSys || assignedToEval.Contains(e.EmployeeID))).ToList();

            foreach (var b in branches)
            {
                b.AssignedEmployeesCount = matchingEmps.Count(e => e.BranchID == b.BranchID);
            }

            int totalAcrossAll = branches.Sum(b => b.AssignedEmployeesCount);

            var result = new List<TemplateBranchOptionDto>
            {
                new()
                {
                    BranchID = null,
                    BranchName = "جميع الفروع (كلاهما)",
                    AssignedEmployeesCount = totalAcrossAll
                }
            };
            result.AddRange(branches);
            return result;
        }

        public async Task<List<SheetEmployeeRowModel>> GetEligibleEmployeesForSheetAsync(
            int? targetCategoryId,
            int? targetBranchId,
            int evaluatorId,
            int? evalYear = null,
            int? evalMonth = null)
        {
            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);
            var (year, month) = await ResolvePeriodOrLatestAsync(conn, evalYear, evalMonth);

            var evalGroupInfo = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT ev.GroupID, ISNULL(eg.IsSystemGroup, 0) AS IsSystemGroup
                FROM dbo.Evaluators ev
                INNER JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE ev.EvaluatorID = @EvalID", new { EvalID = evaluatorId });

            if (evalGroupInfo == null) return new List<SheetEmployeeRowModel>();

            bool isSystemGroup = Convert.ToBoolean(evalGroupInfo.IsSystemGroup);
            int groupId = (int)evalGroupInfo.GroupID;

            var effWeights = await MonthlyPeriodConfigService.GetEffectiveCategoryWeightsAsync(conn, year, month);
            var weightMap = effWeights.ToDictionary(w => (w.CategoryID, w.GroupID), w => w.WeightScore);
            var effAssigns = await MonthlyPeriodConfigService.GetEffectiveEmployeeAssignmentsAsync(conn, year, month);
            var assignedToEval = effAssigns.Where(a => a.EvaluatorID == evaluatorId).Select(a => a.EmployeeID).ToHashSet();
            var assignedCountByEmpGroup = effAssigns
                .GroupBy(a => (a.EmployeeID, a.GroupID))
                .ToDictionary(g => g.Key, g => g.Select(x => x.EvaluatorID).Distinct().Count());

            var contEmps = await LoadEffectiveContinuingEmployeesAsync(conn, year, month);

            IEnumerable<ActiveEmployeeBaseRow> filtered;
            if (isSystemGroup)
            {
                var sysCatIds = effWeights.Where(w => w.GroupID == groupId).Select(w => w.CategoryID).ToHashSet();
                filtered = contEmps
                    .Where(e => sysCatIds.Contains(e.CategoryID))
                    .OrderBy(e => e.CategoryID).ThenBy(e => e.BranchName).ThenBy(e => e.EvaluationName);
            }
            else
            {
                if (targetCategoryId == null || targetCategoryId <= 0)
                    return new List<SheetEmployeeRowModel>();

                int? effectiveBranchId = (targetBranchId.HasValue && targetBranchId.Value > 0) ? targetBranchId.Value : null;
                filtered = contEmps
                    .Where(e => assignedToEval.Contains(e.EmployeeID)
                                && e.CategoryID == targetCategoryId.Value
                                && (!effectiveBranchId.HasValue || e.BranchID == effectiveBranchId.Value))
                    .OrderBy(e => e.BranchName).ThenBy(e => e.EvaluationName);
            }

            return filtered.Select(e => new SheetEmployeeRowModel
            {
                EmployeeID = e.EmployeeID,
                EvaluationName = e.EvaluationName,
                SystemName = e.SystemName,
                BranchID = e.BranchID,
                BranchName = e.BranchName,
                JobTitle = e.JobTitle,
                JobTypeName = e.JobTypeName,
                CategoryName = e.CategoryName,
                HasIncentive = e.HasIncentive,
                IsContinuing = e.IsContinuing,
                EvaluatorWeightForEmployee = ComputeEvaluatorWeightForEmployee(
                    e.EmployeeID, e.CategoryID, groupId, isSystemGroup, weightMap, assignedCountByEmpGroup)
            }).ToList();
        }

        public static async Task<bool> CanCurrentUserAccessEvaluatorAsync(SqlConnection conn, int evaluatorId)
        {
            var session = AuthService.CurrentSession;
            if (session == null) return false;
            if (session.IsAdmin) return true;

            int cnt = await conn.ExecuteScalarAsync<int>(@"
                SELECT COUNT(1)
                FROM dbo.TemplateAssignedUsers
                WHERE EvaluatorID = @EvalID AND UserID = @UserID",
                new { EvalID = evaluatorId, UserID = session.UserID });

            return cnt > 0;
        }

        public async Task<List<TemplateAssignedUserDto>> GetActiveAssignableUsersAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);

            var list = await conn.QueryAsync<TemplateAssignedUserDto>(@"
                SELECT UserID, Username, FullName, IsAdmin, IsActive
                FROM dbo.Users
                WHERE IsActive = 1 AND IsAdmin = 0
                ORDER BY FullName ASC, Username ASC");

            return list.ToList();
        }

        public async Task<List<TemplateAssignedUserDto>> GetAssignedUsersForEvaluatorAsync(int evaluatorId)
        {
            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);

            var list = await conn.QueryAsync<TemplateAssignedUserDto>(@"
                SELECT u.UserID, u.Username, u.FullName, u.IsAdmin, u.IsActive
                FROM dbo.TemplateAssignedUsers tau
                INNER JOIN dbo.Users u ON tau.UserID = u.UserID
                WHERE tau.EvaluatorID = @EvalID
                ORDER BY u.IsActive DESC, u.FullName ASC",
                new { EvalID = evaluatorId });

            return list.ToList();
        }

        public async Task<(bool IsSuccess, string Message)> SaveTemplateAssignedUsersAsync(int evaluatorId, List<int> userIds)
        {
            if (AuthService.CurrentSession == null)
                return (false, "انتهت جلسة العمل، يرجى إعادة تسجيل الدخول.");

            if (!AuthService.CurrentSession.IsAdmin)
                return (false, "عذراً، تحديد المستخدمين المسؤولين عن القوالب مخصص لمدير النظام فقط.");

            if (evaluatorId <= 0)
                return (false, "معرف المقيم غير صالح.");

            var distinctIds = (userIds ?? new List<int>()).Where(id => id > 0).Distinct().ToList();

            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);
            if (conn.State != ConnectionState.Open)
                await conn.OpenAsync();

            using var tx = conn.BeginTransaction();
            try
            {
                await conn.ExecuteAsync(
                    "DELETE FROM dbo.TemplateAssignedUsers WHERE EvaluatorID = @EvalID",
                    new { EvalID = evaluatorId }, tx);

                foreach (int uid in distinctIds)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.TemplateAssignedUsers (EvaluatorID, UserID, AssignedAt)
                        VALUES (@EvalID, @UserID, SYSDATETIME())",
                        new { EvalID = evaluatorId, UserID = uid }, tx);
                }

                await tx.CommitAsync();
                return (true, distinctIds.Count == 0
                    ? "تم إلغاء إسناد القالب من جميع المستخدمين العاديين (يظهر لحساب المدير فقط)."
                    : $"تم حفظ ({distinctIds.Count}) مستخدم مسؤول عن القالب بنجاح.");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return (false, $"تعذر حفظ المستخدمين المسؤولين عن القالب: {ex.Message}");
            }
        }

        public static async Task EnsureMonthlyTemplatesSeededAsync(SqlConnection conn, SqlTransaction? tx = null)
        {
            await conn.ExecuteAsync(@"
                INSERT INTO dbo.MonthlyEvaluatorTemplates (EvalYear, EvalMonth, EvaluatorID, TemplateID, UpdatedAt)
                SELECT src.EvalYear, src.EvalMonth, src.EvaluatorID, src.TemplateID, SYSDATETIME()
                FROM (
                    SELECT er.EvalYear, er.EvalMonth, er.EvaluatorID, er.TemplateID,
                           ROW_NUMBER() OVER (PARTITION BY er.EvalYear, er.EvalMonth, er.EvaluatorID ORDER BY er.RecordID DESC) AS rn
                    FROM dbo.EvaluationRecords er
                    INNER JOIN dbo.Templates t ON er.TemplateID = t.TemplateID
                    WHERE t.IsDeleted = 0
                ) src
                WHERE src.rn = 1
                  AND NOT EXISTS (
                      SELECT 1 FROM dbo.MonthlyEvaluatorTemplates met
                      WHERE met.EvalYear = src.EvalYear
                        AND met.EvalMonth = src.EvalMonth
                        AND met.EvaluatorID = src.EvaluatorID
                  );", transaction: tx);

            var activeCurrents = (await conn.QueryAsync<dynamic>(@"
                SELECT t.EvaluatorID, t.TemplateID
                FROM dbo.Templates t
                INNER JOIN dbo.Evaluators ev ON t.EvaluatorID = ev.EvaluatorID
                LEFT JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE t.IsCurrentVersion = 1 AND t.IsDeleted = 0 AND ISNULL(eg.IsSystemGroup, 0) = 0", transaction: tx)).ToList();

            foreach (var cur in activeCurrents)
            {
                int evalId = (int)cur.EvaluatorID;
                int curTid = (int)cur.TemplateID;

                var latestMapped = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT TOP 1 EvalYear, EvalMonth, TemplateID
                    FROM dbo.MonthlyEvaluatorTemplates
                    WHERE EvaluatorID = @EvalID
                    ORDER BY (EvalYear * 100 + EvalMonth) DESC",
                    new { EvalID = evalId }, tx);

                if (latestMapped != null && (int)latestMapped.TemplateID != curTid)
                {
                    var nextMonthDate = new DateTime((int)latestMapped.EvalYear, (int)latestMapped.EvalMonth, 1).AddMonths(1);
                    await conn.ExecuteAsync(@"
                        IF NOT EXISTS (
                            SELECT 1 FROM dbo.MonthlyEvaluatorTemplates
                            WHERE EvalYear = @Y AND EvalMonth = @M AND EvaluatorID = @EvalID
                        )
                        BEGIN
                            INSERT INTO dbo.MonthlyEvaluatorTemplates (EvalYear, EvalMonth, EvaluatorID, TemplateID, UpdatedAt)
                            VALUES (@Y, @M, @EvalID, @TID, SYSDATETIME());
                        END",
                        new { Y = nextMonthDate.Year, M = nextMonthDate.Month, EvalID = evalId, TID = curTid }, tx);
                }
            }
        }

        public static async Task<(int TemplateID, int? EffYear, int? EffMonth, bool IsOwnMonthVersion)> ResolveEffectiveTemplateIdForMonthAsync(
            SqlConnection conn,
            int evaluatorId,
            int year,
            int month,
            SqlTransaction? tx = null)
        {
            await EnsureMonthlyTemplatesSeededAsync(conn, tx);
            int reqKey = year * 100 + month;

            var matched = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT TOP 1 met.TemplateID, met.EvalYear, met.EvalMonth
                FROM dbo.MonthlyEvaluatorTemplates met
                INNER JOIN dbo.Templates t ON met.TemplateID = t.TemplateID
                WHERE met.EvaluatorID = @EvalID
                  AND t.IsDeleted = 0
                  AND (met.EvalYear * 100 + met.EvalMonth) <= @ReqKey
                ORDER BY (met.EvalYear * 100 + met.EvalMonth) DESC",
                new { EvalID = evaluatorId, ReqKey = reqKey }, tx);

            if (matched != null)
            {
                int ey = (int)matched.EvalYear;
                int em = (int)matched.EvalMonth;
                return ((int)matched.TemplateID, ey, em, ey == year && em == month);
            }

            var earliest = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT TOP 1 met.TemplateID, met.EvalYear, met.EvalMonth
                FROM dbo.MonthlyEvaluatorTemplates met
                INNER JOIN dbo.Templates t ON met.TemplateID = t.TemplateID
                WHERE met.EvaluatorID = @EvalID
                  AND t.IsDeleted = 0
                ORDER BY (met.EvalYear * 100 + met.EvalMonth) ASC",
                new { EvalID = evaluatorId }, tx);

            if (earliest != null)
            {
                return ((int)earliest.TemplateID, (int)earliest.EvalYear, (int)earliest.EvalMonth, false);
            }

            int? fallbackId = await conn.ExecuteScalarAsync<int?>(@"
                SELECT TOP 1 TemplateID
                FROM dbo.Templates
                WHERE EvaluatorID = @EvalID AND IsCurrentVersion = 1 AND IsDeleted = 0
                ORDER BY TemplateID DESC",
                new { EvalID = evaluatorId }, tx);

            return (fallbackId ?? 0, null, null, false);
        }

        public async Task<(bool IsMonthLocked, string LockedBy, string StatusBadgeText)> GetTemplatesPeriodStatusAsync(int year, int month)
        {
            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);
            await EnsureMonthlyTemplatesSeededAsync(conn);

            string reqName = $"{IncentiveCalculatorService.GetArabicMonthName(month)} {year} م";
            var (isLocked, lockedBy, _) = await MonthlyPeriodConfigService.GetMonthLockStatusAsync(conn, year, month);
            if (isLocked)
            {
                return (true, lockedBy, $"🔒 شهر ({reqName}) معتمد ومغلق نهائياً بواسطة ({lockedBy}) — قوالب هذا الشهر محفوظة للقراءة فقط");
            }

            int reqKey = year * 100 + month;
            int ownCount = await conn.ExecuteScalarAsync<int>(@"
                SELECT COUNT(1)
                FROM dbo.MonthlyEvaluatorTemplates met
                INNER JOIN dbo.Templates t ON met.TemplateID = t.TemplateID
                INNER JOIN dbo.Evaluators ev ON met.EvaluatorID = ev.EvaluatorID
                LEFT JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE met.EvalYear = @Y AND met.EvalMonth = @M AND t.IsDeleted = 0 AND ISNULL(eg.IsSystemGroup, 0) = 0",
                new { Y = year, M = month });

            var latestPrior = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT TOP 1 met.EvalYear, met.EvalMonth
                FROM dbo.MonthlyEvaluatorTemplates met
                INNER JOIN dbo.Templates t ON met.TemplateID = t.TemplateID
                INNER JOIN dbo.Evaluators ev ON met.EvaluatorID = ev.EvaluatorID
                LEFT JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE (met.EvalYear * 100 + met.EvalMonth) < @ReqKey AND t.IsDeleted = 0 AND ISNULL(eg.IsSystemGroup, 0) = 0
                ORDER BY (met.EvalYear * 100 + met.EvalMonth) DESC",
                new { ReqKey = reqKey });

            if (ownCount > 0)
            {
                return (false, string.Empty, $"✔ توجد نسخ قوالب مستقلة محفوظة لشهر ({reqName}) ({ownCount} قالب) — وأي تعديل سيُحفظ لهذا الشهر دون المساس بالأشهر السابقة أو اللاحقة المعتمدة");
            }

            if (latestPrior != null)
            {
                string effName = $"{IncentiveCalculatorService.GetArabicMonthName((int)latestPrior.EvalMonth)} {(int)latestPrior.EvalYear} م";
                return (false, string.Empty, $"🔗 قوالب شهر ({reqName}) ترث تلقائياً من ({effName}) — أي تعديل سيحفظ نسخة مستقلة لشهر ({reqName})");
            }

            return (false, string.Empty, $"⚙ قوالب شهر ({reqName}) — التهيئة الافتراضية");
        }

        public async Task<int> ResolveTemplateIdForEvaluatorInMonthAsync(int evaluatorId, int year, int month)
        {
            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);
            var res = await ResolveEffectiveTemplateIdForMonthAsync(conn, evaluatorId, year, month);
            return res.TemplateID;
        }

        public async Task<List<TemplateSummaryDto>> GetCurrentTemplatesAsync(
            string evaluatorSearch = "",
            bool syncSystemTemplates = true,
            int? evalYear = null,
            int? evalMonth = null)
        {
            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);
            if (syncSystemTemplates && string.IsNullOrWhiteSpace(evaluatorSearch))
            {
                await SyncAllSystemGroupTemplatesAsync(conn);
            }

            await EnsureMonthlyTemplatesSeededAsync(conn);

            var (year, month) = await ResolvePeriodOrLatestAsync(conn, evalYear, evalMonth);
            var (isMonthLocked, _, _) = await MonthlyPeriodConfigService.GetMonthLockStatusAsync(conn, year, month);

            bool isAdmin = AuthService.CurrentSession?.IsAdmin == true;
            int currentUserId = AuthService.CurrentSession?.UserID ?? 0;

            var candidateEvaluators = (await conn.QueryAsync<dynamic>(@"
                SELECT DISTINCT t.EvaluatorID, ev.EvaluatorName, ISNULL(eg.GroupName, N'') AS GroupName,
                       ISNULL(eg.IsSystemGroup, 0) AS IsSystemGroup
                FROM dbo.Templates t
                INNER JOIN dbo.Evaluators ev ON t.EvaluatorID = ev.EvaluatorID
                LEFT JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE t.IsCurrentVersion = 1 AND t.IsDeleted = 0
                  AND (@Search = '' OR ev.EvaluatorName LIKE @Pattern OR eg.GroupName LIKE @Pattern)
                  AND (
                        @IsAdmin = 1
                        OR EXISTS (
                            SELECT 1 FROM dbo.TemplateAssignedUsers tau
                            WHERE tau.EvaluatorID = t.EvaluatorID AND tau.UserID = @CurrentUserID
                        )
                      )
                ORDER BY ISNULL(eg.IsSystemGroup, 0) DESC, ev.EvaluatorName ASC", new
            {
                Search = (evaluatorSearch ?? string.Empty).Trim(),
                Pattern = $"%{(evaluatorSearch ?? string.Empty).Trim()}%",
                IsAdmin = isAdmin ? 1 : 0,
                CurrentUserID = currentUserId
            })).ToList();

            var resolvedByEval = new Dictionary<int, (int TemplateID, int? EffYear, int? EffMonth, bool IsOwn)>();
            var resolvedTemplateIds = new List<int>();

            foreach (var ce in candidateEvaluators)
            {
                int evId = (int)ce.EvaluatorID;
                bool isSys = Convert.ToBoolean(ce.IsSystemGroup);
                var res = await ResolveEffectiveTemplateIdForMonthAsync(conn, evId, year, month);
                if (res.TemplateID <= 0) continue;

                resolvedByEval[evId] = (res.TemplateID, res.EffYear, res.EffMonth, res.IsOwnMonthVersion);
                resolvedTemplateIds.Add(res.TemplateID);

                if (syncSystemTemplates && string.IsNullOrWhiteSpace(evaluatorSearch) && !isSys && !isMonthLocked)
                {
                    int linkedEvals = await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(1) FROM dbo.EvaluationRecords WHERE TemplateID = @TID",
                        new { TID = res.TemplateID });
                    if (linkedEvals == 0)
                    {
                        await EnsureMissingCategorySheetsForTemplateAsync(conn, res.TemplateID, evId, year, month);
                    }
                }
            }

            if (resolvedTemplateIds.Count == 0)
                return new List<TemplateSummaryDto>();

            string sql = @"
                SELECT t.TemplateID, t.TemplateGroupGuid, t.EvaluatorID, ev.EvaluatorName, ISNULL(eg.GroupName, N'') AS GroupName,
                       t.VersionNumber, t.CreatedAt,
                       (SELECT COUNT(1) FROM dbo.TemplateSheets ts WHERE ts.TemplateID = t.TemplateID) AS SheetsCount,
                       0 AS TotalEmployeesCount,
                       (SELECT COUNT(1) FROM dbo.EvaluationRecords er WHERE er.TemplateID = t.TemplateID) AS LinkedEvaluationsCount
                FROM dbo.Templates t
                INNER JOIN dbo.Evaluators ev ON t.EvaluatorID = ev.EvaluatorID
                LEFT JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE t.TemplateID IN @TemplateIDs
                ORDER BY ISNULL(eg.IsSystemGroup, 0) DESC, t.CreatedAt DESC";

            var list = (await conn.QueryAsync<TemplateSummaryDto>(sql, new { TemplateIDs = resolvedTemplateIds })).ToList();

            var assignedUsersRows = (await conn.QueryAsync<dynamic>(@"
                SELECT tau.EvaluatorID, u.UserID, u.Username, u.FullName, u.IsAdmin, u.IsActive
                FROM dbo.TemplateAssignedUsers tau
                INNER JOIN dbo.Users u ON tau.UserID = u.UserID
                ORDER BY u.IsActive DESC, u.FullName ASC")).ToList();

            var assignedUsersByEval = assignedUsersRows
                .GroupBy(x => (int)x.EvaluatorID)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => new TemplateAssignedUserDto
                    {
                        UserID = (int)x.UserID,
                        Username = (string)x.Username,
                        FullName = (string)x.FullName,
                        IsAdmin = Convert.ToBoolean(x.IsAdmin),
                        IsActive = Convert.ToBoolean(x.IsActive)
                    }).ToList());

            var sheets = (await conn.QueryAsync<dynamic>(@"
                SELECT ts.TemplateID, ts.SheetID, ts.SheetName, ts.TargetCategoryID, ts.TargetBranchID, ts.TargetCategoryName
                FROM dbo.TemplateSheets ts
                WHERE ts.TemplateID IN @TemplateIDs
                ORDER BY ts.SortOrder ASC", new { TemplateIDs = resolvedTemplateIds })).ToList();

            var evalGroupLookup = (await conn.QueryAsync<dynamic>(@"
                SELECT ev.EvaluatorID, ev.GroupID, ISNULL(eg.IsSystemGroup, 0) AS IsSystemGroup
                FROM dbo.Evaluators ev
                LEFT JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID"))
                .ToDictionary(x => (int)x.EvaluatorID, x => (GroupID: (int)x.GroupID, IsSys: Convert.ToBoolean(x.IsSystemGroup)));

            var contEmps = await LoadEffectiveContinuingEmployeesAsync(conn, year, month);
            var effWeights = await MonthlyPeriodConfigService.GetEffectiveCategoryWeightsAsync(conn, year, month);
            var effAssigns = await MonthlyPeriodConfigService.GetEffectiveEmployeeAssignmentsAsync(conn, year, month);

            var sysExcluded = (await conn.QueryAsync<dynamic>("SELECT GroupID, EmployeeID FROM dbo.SystemTemplateExcludedEmployees"))
                .Select(x => ((int)x.GroupID, (int)x.EmployeeID)).ToHashSet();
            var tmplExcluded = (await conn.QueryAsync<dynamic>("SELECT EvaluatorID, EmployeeID FROM dbo.TemplateExcludedEmployees"))
                .Select(x => ((int)x.EvaluatorID, (int)x.EmployeeID)).ToHashSet();

            var assignsByEval = effAssigns
                .GroupBy(a => a.EvaluatorID)
                .ToDictionary(g => g.Key, g => g.Select(x => x.EmployeeID).ToHashSet());

            var catsByGroup = effWeights
                .GroupBy(w => w.GroupID)
                .ToDictionary(g => g.Key, g => g.Select(x => x.CategoryID).ToHashSet());

            var sheetsByTemplate = sheets.GroupBy(x => (int)x.TemplateID).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var item in list)
            {
                if (assignedUsersByEval.TryGetValue(item.EvaluatorID, out var evalAssignedUsers))
                {
                    item.AssignedUsers = evalAssignedUsers;
                }

                item.RequestedYear = year;
                item.RequestedMonth = month;
                item.IsPeriodMonthLocked = isMonthLocked;
                if (resolvedByEval.TryGetValue(item.EvaluatorID, out var mRes))
                {
                    item.EffectiveTemplateYear = mRes.EffYear;
                    item.EffectiveTemplateMonth = mRes.EffMonth;
                    item.IsOwnMonthVersion = mRes.IsOwn;
                }

                sheetsByTemplate.TryGetValue(item.TemplateID, out var tSheets);
                tSheets ??= new List<dynamic>();

                item.SheetsSummary = tSheets.Count > 0
                    ? string.Join("  |  ", tSheets.Select(s => $"{s.SheetName} ({s.TargetCategoryName})"))
                    : "بدون أوراق";

                evalGroupLookup.TryGetValue(item.EvaluatorID, out var gInfo);
                if (gInfo.IsSys)
                {
                    catsByGroup.TryGetValue(gInfo.GroupID, out var sysCats);
                    sysCats ??= new HashSet<int>();
                    item.TotalEmployeesCount = contEmps.Count(e =>
                        sysCats.Contains(e.CategoryID) && !sysExcluded.Contains((gInfo.GroupID, e.EmployeeID)));
                }
                else
                {
                    assignsByEval.TryGetValue(item.EvaluatorID, out var assignedSet);
                    assignedSet ??= new HashSet<int>();

                    int count = 0;
                    foreach (var emp in contEmps)
                    {
                        if (!assignedSet.Contains(emp.EmployeeID)) continue;
                        if (tmplExcluded.Contains((item.EvaluatorID, emp.EmployeeID))) continue;

                        bool fitsAnySheet = tSheets.Any(sh =>
                            (int)sh.TargetCategoryID == emp.CategoryID &&
                            (sh.TargetBranchID == null || (int)sh.TargetBranchID == emp.BranchID));

                        if (fitsAnySheet) count++;
                    }
                    item.TotalEmployeesCount = count;
                }
            }

            return list;
        }

        public async Task<TemplateFullModel?> GetTemplateFullDetailsAsync(
            int templateId, int? evalYear = null, int? evalMonth = null)
        {
            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);
            var (year, month) = await ResolvePeriodOrLatestAsync(conn, evalYear, evalMonth);

            var header = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT t.TemplateID, t.TemplateGroupGuid, t.EvaluatorID, ev.EvaluatorName,
                       ev.GroupID,
                       ISNULL(eg.GroupName, N'') AS GroupName, t.VersionNumber, t.CreatedAt,
                       ISNULL(eg.IsSystemGroup, 0) AS IsSystemGroup,
                       ISNULL(eg.SystemGroupCode, N'') AS SystemGroupCode,
                       ISNULL(eg.MaxAllowedCount, 3) AS MaxAllowedCount
                FROM dbo.Templates t
                INNER JOIN dbo.Evaluators ev ON t.EvaluatorID = ev.EvaluatorID
                LEFT JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE t.TemplateID = @ID", new { ID = templateId });

            if (header == null) return null;

            bool isSys = Convert.ToBoolean(header.IsSystemGroup);
            int groupId = (int)header.GroupID;
            int evaluatorId = (int)header.EvaluatorID;

            if (!await CanCurrentUserAccessEvaluatorAsync(conn, evaluatorId))
                return null;

            await EnsureMonthlyTemplatesSeededAsync(conn);
            var (isMonthLocked, lockedByUser, _) = await MonthlyPeriodConfigService.GetMonthLockStatusAsync(conn, year, month);
            var monthRes = await ResolveEffectiveTemplateIdForMonthAsync(conn, evaluatorId, year, month);

            if (!isSys && !isMonthLocked)
            {
                int linkedEvals = await conn.ExecuteScalarAsync<int>(
                    "SELECT COUNT(1) FROM dbo.EvaluationRecords WHERE TemplateID = @TID",
                    new { TID = templateId });
                if (linkedEvals == 0)
                {
                    await EnsureMissingCategorySheetsForTemplateAsync(conn, templateId, evaluatorId, year, month);
                }
            }

            var model = new TemplateFullModel
            {
                TemplateID = header.TemplateID,
                TemplateGroupGuid = header.TemplateGroupGuid,
                EvaluatorID = evaluatorId,
                EvaluatorName = header.EvaluatorName,
                GroupName = header.GroupName,
                VersionNumber = header.VersionNumber,
                CreatedAt = header.CreatedAt,
                IsSystemTemplate = isSys,
                SystemGroupCode = (header.SystemGroupCode as string) ?? string.Empty,
                MaxAllowedCount = (int)header.MaxAllowedCount,
                RequestedYear = year,
                RequestedMonth = month,
                EffectiveTemplateYear = monthRes.EffYear,
                EffectiveTemplateMonth = monthRes.EffMonth,
                IsOwnMonthVersion = monthRes.IsOwnMonthVersion && monthRes.TemplateID == templateId,
                IsMonthLocked = isMonthLocked,
                LockedByUserName = lockedByUser
            };

            var sheets = (await conn.QueryAsync<TemplateSheetModel>(@"
                SELECT ts.SheetID,
                       ts.SheetName,
                       ts.TargetCategoryID,
                       CASE WHEN @IsSys = 1 THEN ts.TargetCategoryName ELSE ISNULL(c.CategoryName, ts.TargetCategoryName) END AS TargetCategoryName,
                       ts.TargetBranchID,
                       ISNULL(b.BranchName, N'جميع الفروع (كلاهما)') AS TargetBranchName,
                       ts.SortOrder
                FROM dbo.TemplateSheets ts
                LEFT JOIN dbo.Categories c ON ts.TargetCategoryID = c.CategoryID
                LEFT JOIN dbo.Branches b ON ts.TargetBranchID = b.BranchID
                WHERE ts.TemplateID = @ID
                ORDER BY ts.SortOrder ASC", new { ID = templateId, IsSys = isSys ? 1 : 0 })).ToList();

            var contEmps = await LoadEffectiveContinuingEmployeesAsync(conn, year, month);
            var contEmpMap = contEmps.ToDictionary(e => e.EmployeeID);
            var effWeights = await MonthlyPeriodConfigService.GetEffectiveCategoryWeightsAsync(conn, year, month);
            var weightMap = effWeights.ToDictionary(w => (w.CategoryID, w.GroupID), w => w.WeightScore);
            var effAssigns = await MonthlyPeriodConfigService.GetEffectiveEmployeeAssignmentsAsync(conn, year, month);
            var assignedToEval = effAssigns.Where(a => a.EvaluatorID == evaluatorId).Select(a => a.EmployeeID).ToHashSet();
            var assignedCountByEmpGroup = effAssigns
                .GroupBy(a => (a.EmployeeID, a.GroupID))
                .ToDictionary(g => g.Key, g => g.Select(x => x.EvaluatorID).Distinct().Count());

            HashSet<int> excludedEmpIds;
            HashSet<int> eligibleEmpIds;
            if (isSys)
            {
                excludedEmpIds = (await conn.QueryAsync<int>(
                    "SELECT EmployeeID FROM dbo.SystemTemplateExcludedEmployees WHERE GroupID = @GID",
                    new { GID = groupId })).ToHashSet();
                var sysCatIds = effWeights.Where(w => w.GroupID == groupId).Select(w => w.CategoryID).ToHashSet();
                eligibleEmpIds = contEmps
                    .Where(e => sysCatIds.Contains(e.CategoryID) && !excludedEmpIds.Contains(e.EmployeeID))
                    .Select(e => e.EmployeeID).ToHashSet();
            }
            else
            {
                excludedEmpIds = (await conn.QueryAsync<int>(
                    "SELECT EmployeeID FROM dbo.TemplateExcludedEmployees WHERE EvaluatorID = @EvalID",
                    new { EvalID = evaluatorId })).ToHashSet();
                eligibleEmpIds = contEmps
                    .Where(e => assignedToEval.Contains(e.EmployeeID) && !excludedEmpIds.Contains(e.EmployeeID))
                    .Select(e => e.EmployeeID).ToHashSet();
            }

            var placedEmpIds = new HashSet<int>();
            var sheetRowsDict = new Dictionary<int, List<SheetEmployeeRowModel>>();

            // 1. تحميل المعايير والموظفين المحفوظين في كل ورقة مع الفلترة الديناميكية حسب إسناد الشهر
            foreach (var sheet in sheets)
            {
                var criteria = await conn.QueryAsync<CriteriaColumnModel>(@"
                    SELECT CriteriaID, CriteriaTitle, MaxScore, ColumnOrder
                    FROM dbo.SheetCriteria
                    WHERE SheetID = @SheetID
                    ORDER BY ColumnOrder ASC", new { SheetID = sheet.SheetID });

                sheet.CriteriaColumns = new ObservableCollection<CriteriaColumnModel>(criteria);

                var savedEmpIds = (await conn.QueryAsync<int>(@"
                    SELECT EmployeeID
                    FROM dbo.SheetEmployees
                    WHERE SheetID = @SheetID
                    ORDER BY RowOrder ASC", new { SheetID = sheet.SheetID })).ToList();

                var rows = new List<SheetEmployeeRowModel>();
                foreach (int empId in savedEmpIds)
                {
                    if (!eligibleEmpIds.Contains(empId) || !contEmpMap.TryGetValue(empId, out var emp))
                        continue;

                    if (!isSys)
                    {
                        if (sheet.TargetCategoryID.HasValue && sheet.TargetCategoryID.Value != emp.CategoryID)
                            continue;
                        if (sheet.TargetBranchID.HasValue && sheet.TargetBranchID.Value > 0 && sheet.TargetBranchID.Value != emp.BranchID)
                            continue;
                    }

                    if (!placedEmpIds.Add(empId))
                        continue;

                    rows.Add(new SheetEmployeeRowModel
                    {
                        EmployeeID = emp.EmployeeID,
                        EvaluationName = emp.EvaluationName,
                        SystemName = emp.SystemName,
                        BranchID = emp.BranchID,
                        BranchName = emp.BranchName,
                        JobTitle = emp.JobTitle,
                        JobTypeName = emp.JobTypeName,
                        CategoryName = emp.CategoryName,
                        HasIncentive = emp.HasIncentive,
                        IsContinuing = emp.IsContinuing,
                        EvaluatorWeightForEmployee = ComputeEvaluatorWeightForEmployee(
                            emp.EmployeeID, emp.CategoryID, groupId, isSys, weightMap, assignedCountByEmpGroup)
                    });
                }

                sheetRowsDict[sheet.SheetID] = rows;
            }

            // 2. إدراج الموظفين الجدد المسندين لهذا المقيم في الشهر المختار تلقائياً داخل الورقة المطابقة
            var remainingEmps = eligibleEmpIds
                .Where(id => !placedEmpIds.Contains(id) && contEmpMap.ContainsKey(id))
                .Select(id => contEmpMap[id])
                .OrderBy(e => e.CategoryID).ThenBy(e => e.BranchName).ThenBy(e => e.EvaluationName)
                .ToList();

            foreach (var emp in remainingEmps)
            {
                TemplateSheetModel? matchedSheet = isSys
                    ? sheets.FirstOrDefault()
                    : (sheets.FirstOrDefault(s => s.TargetCategoryID == emp.CategoryID && s.TargetBranchID.HasValue && s.TargetBranchID.Value == emp.BranchID)
                       ?? sheets.FirstOrDefault(s => s.TargetCategoryID == emp.CategoryID && (!s.TargetBranchID.HasValue || s.TargetBranchID.Value <= 0)));

                if (matchedSheet == null) continue;

                placedEmpIds.Add(emp.EmployeeID);
                sheetRowsDict[matchedSheet.SheetID].Add(new SheetEmployeeRowModel
                {
                    EmployeeID = emp.EmployeeID,
                    EvaluationName = emp.EvaluationName,
                    SystemName = emp.SystemName,
                    BranchID = emp.BranchID,
                    BranchName = emp.BranchName,
                    JobTitle = emp.JobTitle,
                    JobTypeName = emp.JobTypeName,
                    CategoryName = emp.CategoryName,
                    HasIncentive = emp.HasIncentive,
                    IsContinuing = emp.IsContinuing,
                    EvaluatorWeightForEmployee = ComputeEvaluatorWeightForEmployee(
                        emp.EmployeeID, emp.CategoryID, groupId, isSys, weightMap, assignedCountByEmpGroup)
                });
            }

            bool hasAnyNonEmptySheet = sheets.Any(s => sheetRowsDict[s.SheetID].Count > 0);
            foreach (var sheet in sheets)
            {
                var rows = sheetRowsDict[sheet.SheetID];
                if (!isSys && hasAnyNonEmptySheet && rows.Count == 0)
                    continue;

                for (int i = 0; i < rows.Count; i++)
                    rows[i].RowOrder = i + 1;

                sheet.EmployeeRows = new ObservableCollection<SheetEmployeeRowModel>(rows);
                model.Sheets.Add(sheet);
            }

            return model;
        }

        public async Task<(bool IsSuccess, string Message, int SavedTemplateId)> SaveTemplateAsync(TemplateFullModel model, int? evalYear = null, int? evalMonth = null)
        {
            bool isNew = model.TemplateID == 0;
            if (isNew && AuthService.CurrentSession?.IsAdmin != true)
                return (false, "عذراً، إنشاء قوالب التقييم الجديدة مخصص لمدير النظام فقط.", 0);

            if (!CheckPermission(isNew ? "insert" : "update", out string permErr))
                return (false, permErr, 0);

            if (model.EvaluatorID <= 0)
                return (false, "يرجى اختيار المقيم صاحب القالب.", 0);

            if (model.Sheets.Count == 0)
                return (false, "يجب أن يحتوي القالب على ورقة تقييم واحدة على الأقل.", 0);

            if (!model.IsSystemTemplate && model.Sheets.Count > 1 && model.Sheets.Any(s => s.EmployeeRows.Count > 0))
            {
                var emptySheets = model.Sheets.Where(s => s.EmployeeRows.Count == 0).ToList();
                foreach (var emptySheet in emptySheets)
                {
                    model.Sheets.Remove(emptySheet);
                }
            }

            foreach (var sheet in model.Sheets)
            {
                if (string.IsNullOrWhiteSpace(sheet.SheetName))
                    return (false, "يرجى التأكد من تسمية جميع أوراق التقييم داخل القالب.", 0);

                if (sheet.TargetCategoryID == null || sheet.TargetCategoryID <= 0)
                    return (false, $"يرجى تحديد الفئة الخاصة بالورقة ({sheet.SheetName}).", 0);

                if (sheet.CriteriaColumns.Count == 0)
                    return (false, $"الورقة ({sheet.SheetName}) لا تحتوي على أي أعمدة لمبادئ التقييم.", 0);

                decimal totalScore = sheet.CriteriaColumns.Sum(c => c.MaxScore);
                if (totalScore != 100m)
                {
                    return (false,
                        $"شرط النظام في الورقة ({sheet.SheetName}):\nيجب أن يكون مجموع درجات أعمدة مبادئ التقييم يساوي 100 درجة بالضبط.\nالمجموع الحالي في هذه الورقة هو ({totalScore:0}) درجة.",
                        0);
                }

                if (sheet.EmployeeRows.Count == 0)
                {
                    return (false, $"الورقة ({sheet.SheetName}) لا تحتوي على أي موظفين. يرجى إضافة موظف واحد على الأقل.", 0);
                }
            }

            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync();
            }

            if (!isNew && !await CanCurrentUserAccessEvaluatorAsync(conn, model.EvaluatorID))
                return (false, "عذراً، لا تملك صلاحية تعديل هذا القالب لأنه غير مسند لحسابك.", 0);

            await EnsureMonthlyTemplatesSeededAsync(conn);
            var (saveYear, saveMonth) = await ResolvePeriodOrLatestAsync(conn, evalYear, evalMonth);
            var (isMonthLocked, lockedByUser, _) = await MonthlyPeriodConfigService.GetMonthLockStatusAsync(conn, saveYear, saveMonth);
            if (isMonthLocked)
            {
                string lockName = $"{IncentiveCalculatorService.GetArabicMonthName(saveMonth)} {saveYear} م";
                return (false, $"لا يمكن تعديل قالب شهر ({lockName}) لأنه معتمد ومغلق نهائياً بواسطة ({lockedByUser}).", 0);
            }

            var evalGroupInfo = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT ev.GroupID, ISNULL(eg.IsSystemGroup, 0) AS IsSystemGroup
                FROM dbo.Evaluators ev
                INNER JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE ev.EvaluatorID = @EvalID", new { EvalID = model.EvaluatorID });

            bool isSystemGroup = evalGroupInfo != null && Convert.ToBoolean(evalGroupInfo.IsSystemGroup);
            int sysGroupId = evalGroupInfo != null ? (int)evalGroupInfo.GroupID : 0;

            using var tx = conn.BeginTransaction();

            try
            {
                int targetTemplateId = model.TemplateID;
                int newVersionNumber = model.VersionNumber;
                bool createdNewVersion = false;
                bool savedForPastMonthOnly = false;
                int savePeriodKey = saveYear * 100 + saveMonth;

                if (isSystemGroup)
                {
                    var canonicalId = await conn.ExecuteScalarAsync<int?>(@"
                        SELECT TOP 1 TemplateID
                        FROM dbo.Templates
                        WHERE EvaluatorID = @EvalID
                        ORDER BY
                            (SELECT COUNT(1) FROM dbo.EvaluationRecords er WHERE er.TemplateID = dbo.Templates.TemplateID) DESC,
                            TemplateID ASC",
                        new { EvalID = model.EvaluatorID }, tx);

                    if (canonicalId.HasValue && canonicalId.Value > 0)
                    {
                        targetTemplateId = canonicalId.Value;
                        newVersionNumber = 1;

                        await conn.ExecuteAsync(@"
                            UPDATE dbo.EvaluationRecords
                            SET TemplateID = @TID
                            WHERE EvaluatorID = @EvalID AND TemplateID <> @TID;

                            UPDATE dbo.Templates
                            SET IsCurrentVersion = 1, IsDeleted = 0, VersionNumber = 1
                            WHERE TemplateID = @TID;

                            DELETE FROM dbo.Templates
                            WHERE EvaluatorID = @EvalID AND TemplateID <> @TID;",
                            new { TID = targetTemplateId, EvalID = model.EvaluatorID }, tx);
                    }
                    else
                    {
                        targetTemplateId = await conn.ExecuteScalarAsync<int>(@"
                            INSERT INTO dbo.Templates (TemplateGroupGuid, EvaluatorID, VersionNumber, IsCurrentVersion, IsDeleted, CreatedByUserID)
                            VALUES (@GroupGuid, @EvalID, 1, 1, 0, @UserID);
                            SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new
                            {
                                GroupGuid = model.TemplateGroupGuid == Guid.Empty ? Guid.NewGuid() : model.TemplateGroupGuid,
                                EvalID = model.EvaluatorID,
                                UserID = AuthService.CurrentSession?.UserID
                            }, tx);
                        newVersionNumber = 1;
                    }
                }
                else if (isNew)
                {
                    string insertTemplateSql = @"
                        INSERT INTO dbo.Templates (TemplateGroupGuid, EvaluatorID, VersionNumber, IsCurrentVersion, IsDeleted, CreatedByUserID)
                        VALUES (@GroupGuid, @EvalID, 1, 1, 0, @UserID);
                        SELECT CAST(SCOPE_IDENTITY() AS INT);";

                    targetTemplateId = await conn.ExecuteScalarAsync<int>(insertTemplateSql, new
                    {
                        GroupGuid = model.TemplateGroupGuid == Guid.Empty ? Guid.NewGuid() : model.TemplateGroupGuid,
                        EvalID = model.EvaluatorID,
                        UserID = AuthService.CurrentSession?.UserID
                    }, tx);

                    await conn.ExecuteAsync(@"
                        IF EXISTS (SELECT 1 FROM dbo.MonthlyEvaluatorTemplates WHERE EvalYear = @Y AND EvalMonth = @M AND EvaluatorID = @EvalID)
                            UPDATE dbo.MonthlyEvaluatorTemplates SET TemplateID = @TID, UpdatedAt = SYSDATETIME() WHERE EvalYear = @Y AND EvalMonth = @M AND EvaluatorID = @EvalID;
                        ELSE
                            INSERT INTO dbo.MonthlyEvaluatorTemplates (EvalYear, EvalMonth, EvaluatorID, TemplateID, UpdatedAt) VALUES (@Y, @M, @EvalID, @TID, SYSDATETIME());",
                        new { Y = saveYear, M = saveMonth, EvalID = model.EvaluatorID, TID = targetTemplateId }, tx);
                }
                else
                {
                    var prevEffective = await ResolveEffectiveTemplateIdForMonthAsync(conn, model.EvaluatorID, saveYear, saveMonth, tx);
                    int evalRecordsCount = await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(1) FROM dbo.EvaluationRecords WHERE TemplateID = @ID",
                        new { ID = model.TemplateID }, tx);

                    int usedByOtherMonthsCount = await conn.ExecuteScalarAsync<int>(@"
                        SELECT COUNT(1)
                        FROM dbo.MonthlyEvaluatorTemplates
                        WHERE EvaluatorID = @EvalID AND TemplateID = @ID
                          AND (EvalYear <> @Y OR EvalMonth <> @M)",
                        new { EvalID = model.EvaluatorID, ID = model.TemplateID, Y = saveYear, M = saveMonth }, tx);

                    int hasOwnMonthRow = await conn.ExecuteScalarAsync<int>(@"
                        SELECT COUNT(1)
                        FROM dbo.MonthlyEvaluatorTemplates
                        WHERE EvaluatorID = @EvalID AND EvalYear = @Y AND EvalMonth = @M",
                        new { EvalID = model.EvaluatorID, Y = saveYear, M = saveMonth }, tx);

                    int laterMonthsCount = await conn.ExecuteScalarAsync<int>(@"
                        SELECT
                            (SELECT COUNT(1) FROM dbo.MonthlyEvaluatorTemplates WHERE EvaluatorID = @EvalID AND (EvalYear * 100 + EvalMonth) > @ReqKey)
                          + (SELECT COUNT(1) FROM dbo.EvaluationRecords WHERE EvaluatorID = @EvalID AND (EvalYear * 100 + EvalMonth) > @ReqKey)",
                        new { EvalID = model.EvaluatorID, ReqKey = savePeriodKey }, tx);

                    bool isPastMonth = laterMonthsCount > 0;
                    savedForPastMonthOnly = isPastMonth;

                    if (isPastMonth && prevEffective.TemplateID > 0)
                    {
                        var nextMonthDate = new DateTime(saveYear, saveMonth, 1).AddMonths(1);
                        await conn.ExecuteAsync(@"
                            IF NOT EXISTS (
                                SELECT 1 FROM dbo.MonthlyEvaluatorTemplates
                                WHERE EvalYear = @NY AND EvalMonth = @NM AND EvaluatorID = @EvalID
                            )
                            BEGIN
                                INSERT INTO dbo.MonthlyEvaluatorTemplates (EvalYear, EvalMonth, EvaluatorID, TemplateID, UpdatedAt)
                                VALUES (@NY, @NM, @EvalID, @PrevTID, SYSDATETIME());
                            END",
                            new { NY = nextMonthDate.Year, NM = nextMonthDate.Month, EvalID = model.EvaluatorID, PrevTID = prevEffective.TemplateID }, tx);
                    }

                    bool mustCreateNewVersion = evalRecordsCount > 0 || usedByOtherMonthsCount > 0 || hasOwnMonthRow == 0;

                    if (mustCreateNewVersion)
                    {
                        createdNewVersion = true;
                        int maxVer = await conn.ExecuteScalarAsync<int>(
                            "SELECT ISNULL(MAX(VersionNumber), @Ver) FROM dbo.Templates WHERE TemplateGroupGuid = @GroupGuid",
                            new { Ver = model.VersionNumber, GroupGuid = model.TemplateGroupGuid }, tx);

                        newVersionNumber = maxVer + 1;

                        if (!isPastMonth)
                        {
                            await conn.ExecuteAsync(
                                "UPDATE dbo.Templates SET IsCurrentVersion = 0 WHERE TemplateID = @ID OR TemplateGroupGuid = @GroupGuid",
                                new { ID = model.TemplateID, GroupGuid = model.TemplateGroupGuid }, tx);
                        }

                        string insertNewVerSql = @"
                            INSERT INTO dbo.Templates (TemplateGroupGuid, EvaluatorID, VersionNumber, IsCurrentVersion, IsDeleted, CreatedByUserID)
                            VALUES (@GroupGuid, @EvalID, @Ver, @IsCur, 0, @UserID);
                            SELECT CAST(SCOPE_IDENTITY() AS INT);";

                        targetTemplateId = await conn.ExecuteScalarAsync<int>(insertNewVerSql, new
                        {
                            GroupGuid = model.TemplateGroupGuid,
                            EvalID = model.EvaluatorID,
                            Ver = newVersionNumber,
                            IsCur = isPastMonth ? 0 : 1,
                            UserID = AuthService.CurrentSession?.UserID
                        }, tx);
                    }
                    else
                    {
                        await conn.ExecuteAsync(
                            "DELETE FROM dbo.TemplateSheets WHERE TemplateID = @ID",
                            new { ID = targetTemplateId }, tx);
                    }

                    await conn.ExecuteAsync(@"
                        IF EXISTS (SELECT 1 FROM dbo.MonthlyEvaluatorTemplates WHERE EvalYear = @Y AND EvalMonth = @M AND EvaluatorID = @EvalID)
                            UPDATE dbo.MonthlyEvaluatorTemplates SET TemplateID = @TID, UpdatedAt = SYSDATETIME() WHERE EvalYear = @Y AND EvalMonth = @M AND EvaluatorID = @EvalID;
                        ELSE
                            INSERT INTO dbo.MonthlyEvaluatorTemplates (EvalYear, EvalMonth, EvaluatorID, TemplateID, UpdatedAt) VALUES (@Y, @M, @EvalID, @TID, SYSDATETIME());",
                        new { Y = saveYear, M = saveMonth, EvalID = model.EvaluatorID, TID = targetTemplateId }, tx);
                }

                int sheetOrder = 1;
                var keptEmployeeIdsInTemplate = new HashSet<int>();

                if (isSystemGroup)
                {
                    var sheet = model.Sheets[0];
                    int? dbBranchId = (sheet.TargetBranchID.HasValue && sheet.TargetBranchID.Value > 0)
                        ? sheet.TargetBranchID.Value
                        : null;

                    int? existingSheetId = await conn.ExecuteScalarAsync<int?>(@"
                        SELECT TOP 1 SheetID
                        FROM dbo.TemplateSheets
                        WHERE TemplateID = @TID
                        ORDER BY SortOrder ASC, SheetID ASC",
                        new { TID = targetTemplateId }, tx);

                    int targetSheetId;
                    if (!existingSheetId.HasValue || existingSheetId.Value <= 0)
                    {
                        targetSheetId = await conn.ExecuteScalarAsync<int>(@"
                            INSERT INTO dbo.TemplateSheets (TemplateID, SheetName, TargetCategoryID, TargetBranchID, TargetCategoryName, SortOrder)
                            VALUES (@TemplateID, @SheetName, @TargetCategoryID, @TargetBranchID, @TargetCategoryName, 1);
                            SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new
                            {
                                TemplateID = targetTemplateId,
                                SheetName = sheet.SheetName.Trim(),
                                TargetCategoryID = sheet.TargetCategoryID!.Value,
                                TargetBranchID = dbBranchId,
                                TargetCategoryName = sheet.TargetCategoryName.Trim()
                            }, tx);
                    }
                    else
                    {
                        targetSheetId = existingSheetId.Value;
                        await conn.ExecuteAsync(@"
                            UPDATE dbo.TemplateSheets
                            SET SheetName = @SheetName,
                                TargetCategoryID = @TargetCategoryID,
                                TargetBranchID = @TargetBranchID,
                                TargetCategoryName = @TargetCategoryName,
                                SortOrder = 1
                            WHERE SheetID = @SID;
                            DELETE FROM dbo.TemplateSheets WHERE TemplateID = @TID AND SheetID <> @SID;",
                            new
                            {
                                SheetName = sheet.SheetName.Trim(),
                                TargetCategoryID = sheet.TargetCategoryID!.Value,
                                TargetBranchID = dbBranchId,
                                TargetCategoryName = sheet.TargetCategoryName.Trim(),
                                SID = targetSheetId,
                                TID = targetTemplateId
                            }, tx);
                    }

                    var firstCol = sheet.CriteriaColumns[0];
                    int? existingCritId = await conn.ExecuteScalarAsync<int?>(@"
                        SELECT TOP 1 CriteriaID
                        FROM dbo.SheetCriteria
                        WHERE SheetID = @SID
                        ORDER BY ColumnOrder ASC, CriteriaID ASC",
                        new { SID = targetSheetId }, tx);

                    if (!existingCritId.HasValue || existingCritId.Value <= 0)
                    {
                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.SheetCriteria (SheetID, CriteriaTitle, MaxScore, ColumnOrder)
                            VALUES (@SID, @Title, @MaxScore, 1)",
                            new
                            {
                                SID = targetSheetId,
                                Title = firstCol.CriteriaTitle.Trim(),
                                MaxScore = Math.Round(firstCol.MaxScore, 0, MidpointRounding.AwayFromZero)
                            }, tx);
                    }
                    else
                    {
                        await conn.ExecuteAsync(@"
                            UPDATE dbo.SheetCriteria
                            SET CriteriaTitle = @Title, MaxScore = @MaxScore, ColumnOrder = 1
                            WHERE CriteriaID = @CID;
                            DELETE FROM dbo.SheetCriteria WHERE SheetID = @SID AND CriteriaID <> @CID;",
                            new
                            {
                                Title = firstCol.CriteriaTitle.Trim(),
                                MaxScore = Math.Round(firstCol.MaxScore, 0, MidpointRounding.AwayFromZero),
                                CID = existingCritId.Value,
                                SID = targetSheetId
                            }, tx);
                    }

                    await conn.ExecuteAsync("DELETE FROM dbo.SheetEmployees WHERE SheetID = @SID", new { SID = targetSheetId }, tx);

                    int rowOrder = 1;
                    var insertedSheetEmpIds = new HashSet<int>();
                    foreach (var emp in sheet.EmployeeRows)
                    {
                        if (!insertedSheetEmpIds.Add(emp.EmployeeID))
                            continue;

                        keptEmployeeIdsInTemplate.Add(emp.EmployeeID);
                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.SheetEmployees (SheetID, EmployeeID, RowOrder)
                            VALUES (@SheetID, @EmpID, @RowOrder)",
                            new
                            {
                                SheetID = targetSheetId,
                                EmpID = emp.EmployeeID,
                                RowOrder = rowOrder++
                            }, tx);
                    }
                }
                else
                {
                    foreach (var sheet in model.Sheets)
                    {
                        int? dbBranchId = (sheet.TargetBranchID.HasValue && sheet.TargetBranchID.Value > 0)
                            ? sheet.TargetBranchID.Value
                            : null;

                        string insertSheetSql = @"
                            INSERT INTO dbo.TemplateSheets (TemplateID, SheetName, TargetCategoryID, TargetBranchID, TargetCategoryName, SortOrder)
                            VALUES (@TemplateID, @SheetName, @TargetCategoryID, @TargetBranchID, @TargetCategoryName, @SortOrder);
                            SELECT CAST(SCOPE_IDENTITY() AS INT);";

                        int newSheetId = await conn.ExecuteScalarAsync<int>(insertSheetSql, new
                        {
                            TemplateID = targetTemplateId,
                            SheetName = sheet.SheetName.Trim(),
                            TargetCategoryID = sheet.TargetCategoryID!.Value,
                            TargetBranchID = dbBranchId,
                            TargetCategoryName = sheet.CategoryWithBranchDisplay.Trim(),
                            SortOrder = sheetOrder++
                        }, tx);

                        int colOrder = 1;
                        foreach (var col in sheet.CriteriaColumns)
                        {
                            await conn.ExecuteAsync(@"
                                INSERT INTO dbo.SheetCriteria (SheetID, CriteriaTitle, MaxScore, ColumnOrder)
                                VALUES (@SheetID, @Title, @MaxScore, @ColOrder)",
                                new
                                {
                                    SheetID = newSheetId,
                                    Title = col.CriteriaTitle.Trim(),
                                    MaxScore = Math.Round(col.MaxScore, 0, MidpointRounding.AwayFromZero),
                                    ColOrder = colOrder++
                                }, tx);
                        }

                        int rowOrder = 1;
                        var insertedSheetEmpIds = new HashSet<int>();
                        foreach (var emp in sheet.EmployeeRows)
                        {
                            if (!insertedSheetEmpIds.Add(emp.EmployeeID))
                                continue;

                            keptEmployeeIdsInTemplate.Add(emp.EmployeeID);
                            await conn.ExecuteAsync(@"
                                INSERT INTO dbo.SheetEmployees (SheetID, EmployeeID, RowOrder)
                                VALUES (@SheetID, @EmpID, @RowOrder)",
                                new
                                {
                                    SheetID = newSheetId,
                                    EmpID = emp.EmployeeID,
                                    RowOrder = rowOrder++
                                }, tx);
                        }
                    }
                }

                // [EXCLUSION_MEMORY_V3] حفظ ذاكرة الموظفين المحذوفين يدوياً أو المُعاد إضافتهم يدوياً لكل من القوالب العادية وقوالب النظام
                var (sYear, sMonth) = await ResolvePeriodOrLatestAsync(conn, evalYear, evalMonth, tx);
                var contEmpsForExcl = await LoadEffectiveContinuingEmployeesAsync(conn, sYear, sMonth, tx);

                if (isSystemGroup && sysGroupId > 0)
                {
                    var effWeightsForSys = await MonthlyPeriodConfigService.GetEffectiveCategoryWeightsAsync(conn, sYear, sMonth, tx);
                    var sysCatIds = effWeightsForSys
                        .Where(w => w.GroupID == sysGroupId)
                        .Select(w => w.CategoryID)
                        .ToHashSet();

                    var allEligibleIds = contEmpsForExcl
                        .Where(e => sysCatIds.Contains(e.CategoryID))
                        .Select(e => e.EmployeeID)
                        .ToList();

                    foreach (int keptId in keptEmployeeIdsInTemplate)
                    {
                        await conn.ExecuteAsync(
                            "DELETE FROM dbo.SystemTemplateExcludedEmployees WHERE GroupID = @GID AND EmployeeID = @EmpID",
                            new { GID = sysGroupId, EmpID = keptId }, tx);
                    }

                    foreach (int empId in allEligibleIds)
                    {
                        if (!keptEmployeeIdsInTemplate.Contains(empId))
                        {
                            await conn.ExecuteAsync(@"
                                IF NOT EXISTS (SELECT 1 FROM dbo.SystemTemplateExcludedEmployees WHERE GroupID = @GID AND EmployeeID = @EmpID)
                                    INSERT INTO dbo.SystemTemplateExcludedEmployees (GroupID, EmployeeID) VALUES (@GID, @EmpID);",
                                new { GID = sysGroupId, EmpID = empId }, tx);
                        }
                    }

                    await conn.ExecuteAsync(@"
                        DELETE ee
                        FROM dbo.EmployeeEvaluators ee
                        INNER JOIN dbo.SystemTemplateExcludedEmployees see
                            ON see.EmployeeID = ee.EmployeeID AND see.GroupID = @GID
                        WHERE ee.EvaluatorID = @EvalID;

                        INSERT INTO dbo.EmployeeEvaluators (EmployeeID, EvaluatorID)
                        SELECT e.EmployeeID, @EvalID
                        FROM dbo.Employees e
                        INNER JOIN dbo.CategoryEvaluatorGroups ceg ON e.CategoryID = ceg.CategoryID
                        WHERE ceg.GroupID = @GID AND e.IsActive = 1 AND e.IsContinuing = 1
                          AND NOT EXISTS (
                              SELECT 1 FROM dbo.SystemTemplateExcludedEmployees see
                              WHERE see.GroupID = @GID AND see.EmployeeID = e.EmployeeID
                          )
                          AND NOT EXISTS (
                              SELECT 1 FROM dbo.EmployeeEvaluators ee
                              WHERE ee.EmployeeID = e.EmployeeID AND ee.EvaluatorID = @EvalID
                          );",
                        new { GID = sysGroupId, EvalID = model.EvaluatorID }, tx);
                }
                else
                {
                    var effAssignsForExcl = await MonthlyPeriodConfigService.GetEffectiveEmployeeAssignmentsAsync(conn, sYear, sMonth, tx);
                    var assignedToThisEval = effAssignsForExcl
                        .Where(a => a.EvaluatorID == model.EvaluatorID)
                        .Select(a => a.EmployeeID)
                        .ToHashSet();

                    foreach (int keptId in keptEmployeeIdsInTemplate)
                    {
                        await conn.ExecuteAsync(
                            "DELETE FROM dbo.TemplateExcludedEmployees WHERE EvaluatorID = @EvalID AND EmployeeID = @EmpID",
                            new { EvalID = model.EvaluatorID, EmpID = keptId }, tx);
                    }

                    foreach (var emp in contEmpsForExcl)
                    {
                        if (!assignedToThisEval.Contains(emp.EmployeeID)) continue;
                        if (keptEmployeeIdsInTemplate.Contains(emp.EmployeeID)) continue;

                        await conn.ExecuteAsync(@"
                            IF NOT EXISTS (SELECT 1 FROM dbo.TemplateExcludedEmployees WHERE EvaluatorID = @EvalID AND EmployeeID = @EmpID)
                                INSERT INTO dbo.TemplateExcludedEmployees (EvaluatorID, EmployeeID) VALUES (@EvalID, @EmpID);",
                            new { EvalID = model.EvaluatorID, EmpID = emp.EmployeeID }, tx);
                    }
                }

                await tx.CommitAsync();

                string saveMonthLabel = $"{IncentiveCalculatorService.GetArabicMonthName(saveMonth)} {saveYear} م";
                string msg = isNew
                    ? $"✔ تم إنشاء وحفظ قالب التقييم لشهر ({saveMonthLabel}) بنجاح."
                    : savedForPastMonthOnly
                        ? $"✔ تم حفظ نسخة قالب مستقلة لشهر ({saveMonthLabel} — النسخة v{newVersionNumber}) فقط، دون أي تأثير على الأشهر اللاحقة المعتمدة أو السابقة."
                        : createdNewVersion
                            ? $"✔ تم حفظ نسخة قالب مستقلة لشهر ({saveMonthLabel} — النسخة v{newVersionNumber}) بنجاح (وسترثها الأشهر القادمة تلقائياً مع بقاء الأشهر السابقة وتقييماتها محفوظة كما هي)."
                            : $"✔ تم حفظ التعديلات على قالب شهر ({saveMonthLabel}) بنجاح.";

                return (true, msg, targetTemplateId);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return (false, $"فشل حفظ القالب: {ex.Message}", 0);
            }
        }

        public async Task<(bool IsSuccess, string Message)> DeleteTemplateAsync(int templateId)
        {
            if (!CheckPermission("delete", out string permErr))
                return (false, permErr);

            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);

            int? ownerEvalId = await conn.ExecuteScalarAsync<int?>(
                "SELECT EvaluatorID FROM dbo.Templates WHERE TemplateID = @ID",
                new { ID = templateId });

            if (!ownerEvalId.HasValue || !await CanCurrentUserAccessEvaluatorAsync(conn, ownerEvalId.Value))
                return (false, "عذراً، لا تملك صلاحية حذف هذا القالب لأنه غير مسند لحسابك.");

            int isSys = await conn.ExecuteScalarAsync<int>(@"
                SELECT ISNULL(eg.IsSystemGroup, 0)
                FROM dbo.Templates t
                INNER JOIN dbo.Evaluators ev ON t.EvaluatorID = ev.EvaluatorID
                INNER JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE t.TemplateID = @ID", new { ID = templateId });

            if (isSys == 1)
            {
                return (false, "هذا القالب يتبع مجموعة نظام افتراضية (الغياب / التأخير) ويتم إدارته تلقائياً وفق الفئات المخصصة له.");
            }

            int evalCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.EvaluationRecords WHERE TemplateID = @ID",
                new { ID = templateId });

            if (evalCount > 0)
            {
                await conn.ExecuteAsync("UPDATE dbo.Templates SET IsDeleted = 1, IsCurrentVersion = 0 WHERE TemplateID = @ID", new { ID = templateId });
                return (true, "تم حذف القالب من القائمة النشطة مع الاحتفاظ بأرشيف التقييمات الشهرية السابقة المرتبطة به.");
            }
            else
            {
                await conn.ExecuteAsync("DELETE FROM dbo.Templates WHERE TemplateID = @ID", new { ID = templateId });
                return (true, "تم حذف قالب التقييم نهائياً بنجاح.");
            }
        }

        public async Task<List<EvaluatorEmployeesSheetExportDto>> GetAllEvaluatorsWithAssignedEmployeesAsync(
            int? evalYear = null, int? evalMonth = null)
        {
            if (AuthService.CurrentSession?.IsAdmin != true)
                return new List<EvaluatorEmployeesSheetExportDto>();

            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);
            var (year, month) = await ResolvePeriodOrLatestAsync(conn, evalYear, evalMonth);

            var evaluators = (await conn.QueryAsync<EvaluatorEmployeesSheetExportDto>(@"
                SELECT ev.EvaluatorID,
                       ev.EvaluatorName,
                       ev.GroupID,
                       ISNULL(eg.GroupName, N'') AS GroupName
                FROM dbo.Evaluators ev
                INNER JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE ev.IsActive = 1 AND eg.IsActive = 1
                ORDER BY eg.GroupID ASC, ev.EvaluatorID ASC")).ToList();

            var sysGroupIds = (await conn.QueryAsync<int>(
                "SELECT GroupID FROM dbo.EvaluatorGroups WHERE IsSystemGroup = 1")).ToHashSet();

            var contEmps = await LoadEffectiveContinuingEmployeesAsync(conn, year, month);
            var effWeights = await MonthlyPeriodConfigService.GetEffectiveCategoryWeightsAsync(conn, year, month);
            var effAssigns = await MonthlyPeriodConfigService.GetEffectiveEmployeeAssignmentsAsync(conn, year, month);

            var assignsByEval = effAssigns
                .GroupBy(a => a.EvaluatorID)
                .ToDictionary(g => g.Key, g => g.Select(x => x.EmployeeID).ToHashSet());
            var catsByGroup = effWeights
                .GroupBy(w => w.GroupID)
                .ToDictionary(g => g.Key, g => g.Select(x => x.CategoryID).ToHashSet());
            var sysExcluded = (await conn.QueryAsync<dynamic>("SELECT GroupID, EmployeeID FROM dbo.SystemTemplateExcludedEmployees"))
                .Select(x => ((int)x.GroupID, (int)x.EmployeeID)).ToHashSet();
            var tmplExcludedForExport = (await conn.QueryAsync<dynamic>("SELECT EvaluatorID, EmployeeID FROM dbo.TemplateExcludedEmployees"))
                .Select(x => ((int)x.EvaluatorID, (int)x.EmployeeID)).ToHashSet();

            foreach (var eval in evaluators)
            {
                bool isSys = sysGroupIds.Contains(eval.GroupID);
                List<ActiveEmployeeBaseRow> matched;

                if (isSys)
                {
                    catsByGroup.TryGetValue(eval.GroupID, out var sysCats);
                    sysCats ??= new HashSet<int>();
                    matched = contEmps
                        .Where(e => sysCats.Contains(e.CategoryID) && !sysExcluded.Contains((eval.GroupID, e.EmployeeID)))
                        .OrderBy(e => e.BranchName).ThenBy(e => e.CategoryID).ThenBy(e => e.EvaluationName)
                        .ToList();
                }
                else
                {
                    assignsByEval.TryGetValue(eval.EvaluatorID, out var assignedSet);
                    assignedSet ??= new HashSet<int>();
                    matched = contEmps
                        .Where(e => assignedSet.Contains(e.EmployeeID) && !tmplExcludedForExport.Contains((eval.EvaluatorID, e.EmployeeID)))
                        .OrderBy(e => e.BranchName).ThenBy(e => e.CategoryID).ThenBy(e => e.EvaluationName)
                        .ToList();
                }

                var rows = new List<EvaluatorEmployeeExportRowDto>();
                for (int i = 0; i < matched.Count; i++)
                {
                    var e = matched[i];
                    rows.Add(new EvaluatorEmployeeExportRowDto
                    {
                        RowNumber = i + 1,
                        EmployeeID = e.EmployeeID,
                        EvaluationName = e.EvaluationName,
                        SystemName = e.SystemName,
                        BranchName = e.BranchName,
                        CategoryName = e.CategoryName,
                        JobTitle = e.JobTitle,
                        JobTypeName = e.JobTypeName,
                        HasIncentive = e.HasIncentive,
                        IsContinuing = e.IsContinuing
                    });
                }
                eval.Employees = rows;
            }

            return evaluators;
        }

        public async Task SyncAllSystemGroupTemplatesAsync(SqlConnection? externalConn = null)
        {
            bool ownConn = externalConn == null;
            var conn = externalConn ?? new SqlConnection(_connectionString);
            try
            {
                if (ownConn)
                {
                    await EnsureSchemaUpToDateAsync(conn);
                }

                var sysGroupIds = (await conn.QueryAsync<int>(
                    "SELECT GroupID FROM dbo.EvaluatorGroups WHERE IsSystemGroup = 1 AND IsActive = 1")).ToList();

                foreach (int gid in sysGroupIds)
                {
                    await EnsureSystemGroupTemplateAsync(gid, conn);
                }
            }
            finally
            {
                if (ownConn) conn.Dispose();
            }
        }

        public async Task EnsureSystemGroupTemplateAsync(int groupID, SqlConnection? externalConn = null)
        {
            await _systemTemplateSyncLock.WaitAsync();
            bool ownConn = externalConn == null;
            var conn = externalConn ?? new SqlConnection(_connectionString);
            try
            {
                if (ownConn)
                {
                    await EnsureSchemaUpToDateAsync(conn);
                }

                var groupInfo = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT GroupID, GroupName, IsSystemGroup, SystemGroupCode FROM dbo.EvaluatorGroups WHERE GroupID = @GID",
                    new { GID = groupID });

                if (groupInfo == null || !Convert.ToBoolean(groupInfo.IsSystemGroup))
                    return;

                string sysCode = (groupInfo.SystemGroupCode as string) ?? string.Empty;
                string groupName = (groupInfo.GroupName as string) ?? (sysCode == "ABSENCE" ? "الغياب" : "التأخير");

                var primaryEvaluator = await conn.QueryFirstOrDefaultAsync<dynamic>(
                    "SELECT TOP 1 EvaluatorID FROM dbo.Evaluators WHERE GroupID = @GID AND IsActive = 1 ORDER BY EvaluatorID ASC",
                    new { GID = groupID });

                if (primaryEvaluator == null) return;
                int evaluatorId = (int)primaryEvaluator.EvaluatorID;

                var assignedCategories = (await conn.QueryAsync<dynamic>(@"
                    SELECT c.CategoryID, c.CategoryName
                    FROM dbo.CategoryEvaluatorGroups ceg
                    INNER JOIN dbo.Categories c ON ceg.CategoryID = c.CategoryID
                    WHERE ceg.GroupID = @GID AND c.IsActive = 1
                    ORDER BY c.CategoryID ASC",
                    new { GID = groupID })).ToList();

                if (assignedCategories.Count == 0)
                {
                    await conn.ExecuteAsync(@"
                        UPDATE dbo.Templates
                        SET IsCurrentVersion = 0, IsDeleted = 1
                        WHERE EvaluatorID = @EvalID AND IsCurrentVersion = 1 AND IsDeleted = 0",
                        new { EvalID = evaluatorId });
                    return;
                }

                var catIds = assignedCategories.Select(c => (int)c.CategoryID).ToList();
                string catsSummary = string.Join("، ", assignedCategories.Select(c => (string)c.CategoryName));
                if (catsSummary.Length > 130)
                {
                    catsSummary = $"جميع الفئات المرتبطة ({assignedCategories.Count} فئات)";
                }

                string criteriaTitle = (sysCode == "ABSENCE" || groupName.Contains("غياب"))
                    ? "عدد أيام الغياب"
                    : "عدد مرات التأخير";

                string sheetTitle = $"ورقة {groupName} الشاملة";

                await conn.ExecuteAsync(@"
                    DELETE ee
                    FROM dbo.EmployeeEvaluators ee
                    INNER JOIN dbo.SystemTemplateExcludedEmployees see
                        ON see.EmployeeID = ee.EmployeeID AND see.GroupID = @GID
                    WHERE ee.EvaluatorID = @EvalID;

                    INSERT INTO dbo.EmployeeEvaluators (EmployeeID, EvaluatorID)
                    SELECT e.EmployeeID, @EvalID
                    FROM dbo.Employees e
                    WHERE e.CategoryID IN @CatIDs
                      AND NOT EXISTS (
                          SELECT 1 FROM dbo.SystemTemplateExcludedEmployees see
                          WHERE see.GroupID = @GID AND see.EmployeeID = e.EmployeeID
                      )
                      AND NOT EXISTS (
                          SELECT 1 FROM dbo.EmployeeEvaluators ee
                          WHERE ee.EmployeeID = e.EmployeeID AND ee.EvaluatorID = @EvalID
                      );",
                    new { EvalID = evaluatorId, CatIDs = catIds, GID = groupID });

                var currentTemplate = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT TOP 1 TemplateID, TemplateGroupGuid, VersionNumber
                    FROM dbo.Templates
                    WHERE EvaluatorID = @EvalID
                    ORDER BY
                        (SELECT COUNT(1) FROM dbo.EvaluationRecords er WHERE er.TemplateID = dbo.Templates.TemplateID) DESC,
                        TemplateID ASC",
                    new { EvalID = evaluatorId });

                int targetTemplateId;

                if (currentTemplate == null)
                {
                    targetTemplateId = await conn.ExecuteScalarAsync<int>(@"
                        INSERT INTO dbo.Templates (TemplateGroupGuid, EvaluatorID, VersionNumber, IsCurrentVersion, IsDeleted, CreatedAt)
                        VALUES (NEWID(), @EvalID, 1, 1, 0, SYSDATETIME());
                        SELECT CAST(SCOPE_IDENTITY() AS INT);",
                        new { EvalID = evaluatorId });
                }
                else
                {
                    targetTemplateId = (int)currentTemplate.TemplateID;
                    await conn.ExecuteAsync(@"
                        UPDATE dbo.EvaluationRecords
                        SET TemplateID = @TID
                        WHERE EvaluatorID = @EvalID AND TemplateID <> @TID;

                        UPDATE dbo.Templates
                        SET IsCurrentVersion = 1, IsDeleted = 0, VersionNumber = 1
                        WHERE TemplateID = @TID;

                        DELETE FROM dbo.Templates
                        WHERE EvaluatorID = @EvalID AND TemplateID <> @TID;",
                        new { TID = targetTemplateId, EvalID = evaluatorId });
                }

                var existingSheet = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT TOP 1 SheetID
                    FROM dbo.TemplateSheets
                    WHERE TemplateID = @TID
                    ORDER BY SortOrder ASC, SheetID ASC",
                    new { TID = targetTemplateId });

                int targetSheetId;
                if (existingSheet == null)
                {
                    targetSheetId = await conn.ExecuteScalarAsync<int>(@"
                        INSERT INTO dbo.TemplateSheets (TemplateID, SheetName, TargetCategoryID, TargetBranchID, TargetCategoryName, SortOrder)
                        VALUES (@TID, @Name, @CatID, NULL, @CatName, 1);
                        SELECT CAST(SCOPE_IDENTITY() AS INT);",
                        new
                        {
                            TID = targetTemplateId,
                            Name = sheetTitle,
                            CatID = catIds[0],
                            CatName = catsSummary
                        });
                }
                else
                {
                    targetSheetId = (int)existingSheet.SheetID;
                    await conn.ExecuteAsync(@"
                        UPDATE dbo.TemplateSheets
                        SET SheetName = @Name, TargetCategoryID = @CatID, TargetBranchID = NULL, TargetCategoryName = @CatName
                        WHERE SheetID = @SID;
                        DELETE FROM dbo.TemplateSheets WHERE TemplateID = @TID AND SheetID <> @SID;",
                        new
                        {
                            Name = sheetTitle,
                            CatID = catIds[0],
                            CatName = catsSummary,
                            SID = targetSheetId,
                            TID = targetTemplateId
                        });
                }

                var existingCriteria = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                    SELECT TOP 1 CriteriaID
                    FROM dbo.SheetCriteria
                    WHERE SheetID = @SID
                    ORDER BY ColumnOrder ASC, CriteriaID ASC",
                    new { SID = targetSheetId });

                if (existingCriteria == null)
                {
                    await conn.ExecuteAsync(@"
                        INSERT INTO dbo.SheetCriteria (SheetID, CriteriaTitle, MaxScore, ColumnOrder)
                        VALUES (@SID, @Title, 100, 1)",
                        new { SID = targetSheetId, Title = criteriaTitle });
                }
                else
                {
                    int critId = (int)existingCriteria.CriteriaID;
                    await conn.ExecuteAsync(@"
                        UPDATE dbo.SheetCriteria SET CriteriaTitle = @Title, MaxScore = 100, ColumnOrder = 1 WHERE CriteriaID = @CID;
                        DELETE FROM dbo.SheetCriteria WHERE SheetID = @SID AND CriteriaID <> @CID;",
                        new { Title = criteriaTitle, CID = critId, SID = targetSheetId });
                }

                await conn.ExecuteAsync(@"
                    BEGIN TRANSACTION;

                    DELETE FROM dbo.SheetEmployees WITH (HOLDLOCK)
                    WHERE SheetID = @SID;

                    INSERT INTO dbo.SheetEmployees (SheetID, EmployeeID, RowOrder)
                    SELECT @SID,
                           e.EmployeeID,
                           CAST(ROW_NUMBER() OVER (ORDER BY e.CategoryID ASC, b.BranchName ASC, e.EvaluationName ASC) AS INT)
                    FROM dbo.Employees e
                    INNER JOIN dbo.Branches b ON e.BranchID = b.BranchID
                    WHERE e.IsActive = 1 AND e.IsContinuing = 1
                      AND e.CategoryID IN @CatIDs
                      AND NOT EXISTS (
                          SELECT 1 FROM dbo.SystemTemplateExcludedEmployees see
                          WHERE see.GroupID = @GID AND see.EmployeeID = e.EmployeeID
                      );

                    COMMIT TRANSACTION;",
                    new { SID = targetSheetId, CatIDs = catIds, GID = groupID });
            }
            finally
            {
                if (ownConn) conn.Dispose();
                _systemTemplateSyncLock.Release();
            }
        }
    }
}

























