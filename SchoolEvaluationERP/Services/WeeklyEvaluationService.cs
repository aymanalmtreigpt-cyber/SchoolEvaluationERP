#nullable disable warnings
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using SchoolEvaluationERP.Models;

namespace SchoolEvaluationERP.Services
{
    public class WeeklyEvaluationService
    {
        private readonly string _connectionString;

        public WeeklyEvaluationService() : this(AppConfigService.ConnectionString) { }

        public WeeklyEvaluationService(string connectionString)
        {
            _connectionString = connectionString;
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

        public async Task<List<TemplateSummaryDto>> GetActiveTemplatesForEntryAsync(string search = "")
        {
            using var conn = await OpenReadyConnectionAsync();
            string sql = @"
                SELECT t.TemplateID, t.TemplateGroupGuid, t.EvaluatorID, ev.EvaluatorName, ISNULL(eg.GroupName, N'') AS GroupName,
                       t.VersionNumber, t.CreatedAt,
                       (SELECT COUNT(1) FROM dbo.TemplateSheets ts WHERE ts.TemplateID = t.TemplateID) AS SheetsCount,
                       (SELECT COUNT(1)
                        FROM dbo.SheetEmployees se
                        INNER JOIN dbo.TemplateSheets ts2 ON se.SheetID = ts2.SheetID
                        INNER JOIN dbo.Employees e ON se.EmployeeID = e.EmployeeID
                        WHERE ts2.TemplateID = t.TemplateID AND e.IsActive = 1 AND e.IsContinuing = 1) AS TotalEmployeesCount
                FROM dbo.Templates t
                INNER JOIN dbo.Evaluators ev ON t.EvaluatorID = ev.EvaluatorID
                LEFT JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE t.IsCurrentVersion = 1 AND t.IsDeleted = 0 AND ev.IsActive = 1
                  AND (@Search = '' OR ev.EvaluatorName LIKE @Pattern OR eg.GroupName LIKE @Pattern)
                ORDER BY ev.EvaluatorName ASC, t.TemplateID DESC";

            var list = (await conn.QueryAsync<TemplateSummaryDto>(sql, new
            {
                Search = (search ?? string.Empty).Trim(),
                Pattern = $"%{(search ?? string.Empty).Trim()}%"
            })).ToList();

            var sheets = await conn.QueryAsync<dynamic>(@"
                SELECT ts.TemplateID, ts.SheetName, ts.TargetCategoryName
                FROM dbo.TemplateSheets ts
                INNER JOIN dbo.Templates t ON ts.TemplateID = t.TemplateID
                WHERE t.IsCurrentVersion = 1 AND t.IsDeleted = 0
                ORDER BY ts.SortOrder ASC");

            var sheetsGroup = sheets.GroupBy(x => (int)x.TemplateID)
                                    .ToDictionary(g => g.Key, g => string.Join(" | ", g.Select(s => $"{s.SheetName} ({s.TargetCategoryName})")));

            foreach (var item in list)
            {
                item.SheetsSummary = sheetsGroup.TryGetValue(item.TemplateID, out var sum) ? sum : string.Empty;
            }

            return list;
        }

        public async Task<MonthlyRecordStatusDto> GetMonthStatusAsync(int templateId, int year, int month)
        {
            if (templateId <= 0)
                return new MonthlyRecordStatusDto { IsAlreadyEntered = false };

            using var conn = await OpenReadyConnectionAsync();
            int? existingRecordId = await conn.ExecuteScalarAsync<int?>(@"
                SELECT TOP 1 er.RecordID
                FROM dbo.EvaluationRecords er
                LEFT JOIN dbo.Templates t ON er.TemplateID = t.TemplateID
                WHERE (
                        er.TemplateID = @TemplateID
                        OR (
                            t.TemplateGroupGuid IS NOT NULL
                            AND t.TemplateGroupGuid = (SELECT TemplateGroupGuid FROM dbo.Templates WHERE TemplateID = @TemplateID)
                        )
                      )
                  AND er.EvalYear = @Year
                  AND er.EvalMonth = @Month
                ORDER BY er.RecordID DESC",
                new { TemplateID = templateId, Year = year, Month = month });

            return new MonthlyRecordStatusDto
            {
                IsAlreadyEntered = existingRecordId.HasValue,
                ExistingRecordId = existingRecordId
            };
        }

        public async Task<WeeklyEvaluationWorkspaceModel?> PrepareNewWorkspaceAsync(int templateId, int year, int month)
        {
            using var conn = await OpenReadyConnectionAsync();
            var header = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT t.TemplateID, t.VersionNumber, t.EvaluatorID, ev.EvaluatorName, ISNULL(eg.GroupName, N'') AS GroupName,
                       ISNULL(eg.IsSystemGroup, 0) AS IsSystemGroup, ISNULL(eg.SystemGroupCode, N'') AS SystemGroupCode, ISNULL(eg.MaxAllowedCount, 3) AS MaxAllowedCount
                FROM dbo.Templates t
                INNER JOIN dbo.Evaluators ev ON t.EvaluatorID = ev.EvaluatorID
                LEFT JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE t.TemplateID = @ID AND t.IsDeleted = 0", new { ID = templateId });

            if (header == null) return null;

            bool isCountBased = ((int)header.IsSystemGroup) == 1;
            string sysCode = header.SystemGroupCode ?? string.Empty;
            int maxAllowed = (int)header.MaxAllowedCount;

            var workspace = new WeeklyEvaluationWorkspaceModel
            {
                RecordID = 0,
                TemplateID = header.TemplateID,
                TemplateVersionNumber = header.VersionNumber,
                EvaluatorID = header.EvaluatorID,
                EvaluatorName = header.EvaluatorName,
                GroupName = header.GroupName,
                EvalYear = year,
                EvalMonth = month,
                CanBeModified = true,
                IsCountBasedEvaluation = isCountBased,
                SystemGroupCode = sysCode,
                MaxAllowedCount = maxAllowed > 0 ? maxAllowed : 3
            };

            var sheets = (await conn.QueryAsync<WeeklyEntrySheetModel>(@"
                SELECT SheetID, SheetName, TargetCategoryName, SortOrder
                FROM dbo.TemplateSheets
                WHERE TemplateID = @ID
                ORDER BY SortOrder ASC", new { ID = templateId })).ToList();

            foreach (var sheet in sheets)
            {
                var criteria = (await conn.QueryAsync<CriteriaColumnModel>(@"
                    SELECT CriteriaID, CriteriaTitle, MaxScore, ColumnOrder
                    FROM dbo.SheetCriteria
                    WHERE SheetID = @SheetID
                    ORDER BY ColumnOrder ASC", new { SheetID = sheet.SheetID })).ToList();

                sheet.CriteriaColumns = criteria;

                var empRows = (await conn.QueryAsync<WeeklyScoreRowViewModel>(@"
                    SELECT se.EmployeeID, se.RowOrder, e.EvaluationName, e.SystemName,
                           b.BranchName, j.JobTitle, jt.JobTypeName, c.CategoryName,
                           e.HasIncentive, e.IsContinuing,
                           ISNULL((
                               SELECT CAST(ROUND(ceg.WeightScore / NULLIF((
                                   SELECT COUNT(1)
                                   FROM dbo.EmployeeEvaluators ee2
                                   INNER JOIN dbo.Evaluators ev2 ON ee2.EvaluatorID = ev2.EvaluatorID
                                   WHERE ee2.EmployeeID = e.EmployeeID
                                     AND ev2.GroupID = ev_curr.GroupID
                                     AND ev2.IsActive = 1
                               ), 0), 2) AS DECIMAL(5,2))
                               FROM dbo.Evaluators ev_curr
                               INNER JOIN dbo.CategoryEvaluatorGroups ceg
                                   ON ceg.GroupID = ev_curr.GroupID AND ceg.CategoryID = e.CategoryID
                               WHERE ev_curr.EvaluatorID = @EvalID
                                 AND EXISTS (
                                     SELECT 1 FROM dbo.EmployeeEvaluators ee_chk
                                     WHERE ee_chk.EmployeeID = e.EmployeeID AND ee_chk.EvaluatorID = @EvalID
                                 )
                           ), 0) AS EvaluatorWeight
                    FROM dbo.SheetEmployees se
                    INNER JOIN dbo.Employees e ON se.EmployeeID = e.EmployeeID
                    INNER JOIN dbo.Branches b ON e.BranchID = b.BranchID
                    INNER JOIN dbo.Jobs j ON e.JobID = j.JobID
                    INNER JOIN dbo.JobTypes jt ON e.JobTypeID = jt.JobTypeID
                    INNER JOIN dbo.Categories c ON e.CategoryID = c.CategoryID
                    WHERE se.SheetID = @SheetID
                      AND e.IsActive = 1
                      AND e.IsContinuing = 1
                    ORDER BY se.RowOrder ASC",
                    new { SheetID = sheet.SheetID, EvalID = workspace.EvaluatorID })).ToList();

                for (int i = 0; i < empRows.Count; i++)
                {
                    empRows[i].RowOrder = i + 1;
                    empRows[i].InitializeCriteria(criteria, null, 0m, workspace.IsCountBasedEvaluation, workspace.MaxAllowedCount);
                }

                sheet.EmployeeRows = new ObservableCollection<WeeklyScoreRowViewModel>(empRows);
                workspace.Sheets.Add(sheet);
            }

            return workspace;
        }

        private class HistoricalScoreRowRaw
        {
            public long ScoreID { get; set; }
            public int SheetID { get; set; }
            public int EmployeeID { get; set; }
            public int CriteriaID { get; set; }
            public decimal GivenScore { get; set; }
            public int? RawEnteredCount { get; set; }
            public decimal SnapshotEvaluatorWeight { get; set; }
            public string EvaluationName { get; set; } = string.Empty;
            public string SystemName { get; set; } = string.Empty;
            public string BranchName { get; set; } = string.Empty;
            public string CategoryName { get; set; } = string.Empty;
            public string JobTitle { get; set; } = string.Empty;
            public string JobTypeName { get; set; } = string.Empty;
            public bool HasIncentive { get; set; } = true;
            public string SheetName { get; set; } = string.Empty;
            public string TargetCategoryName { get; set; } = string.Empty;
            public int SheetSortOrder { get; set; } = 1;
            public string CriteriaTitle { get; set; } = string.Empty;
            public decimal CriteriaMaxScore { get; set; } = 100m;
            public int CriteriaOrder { get; set; } = 1;
            public bool IsEmployeeStillActive { get; set; }
        }

        public async Task<WeeklyEvaluationWorkspaceModel?> LoadSavedRecordWorkspaceAsync(int recordId)
        {
            using var conn = await OpenReadyConnectionAsync();
            var rec = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT er.RecordID,
                       er.TemplateID,
                       ISNULL(t.VersionNumber, ISNULL(er.SnapshotVersionNumber, 1)) AS VersionNumber,
                       er.EvaluatorID,
                       ISNULL(NULLIF(er.SnapshotEvaluatorName, N''), ISNULL(ev.EvaluatorName, N'مقيم')) AS EvaluatorName,
                       ISNULL(NULLIF(er.SnapshotGroupName, N''), ISNULL(eg.GroupName, N'')) AS GroupName,
                       er.EvalYear,
                       er.EvalMonth,
                       er.CreatedAt,
                       ISNULL(NULLIF(er.SnapshotCreatedByFullName, N''), ISNULL(u.FullName, N'مدير النظام العام')) AS CreatedByUserName,
                       CASE WHEN t.TemplateID IS NOT NULL AND t.IsDeleted = 0 THEN 1 ELSE 0 END AS IsTemplateStillActive,
                       ISNULL(eg.IsSystemGroup, 0) AS IsSystemGroup,
                       ISNULL(eg.SystemGroupCode, N'') AS SystemGroupCode,
                       ISNULL(eg.MaxAllowedCount, 3) AS MaxAllowedCount
                FROM dbo.EvaluationRecords er
                LEFT JOIN dbo.Templates t ON er.TemplateID = t.TemplateID
                LEFT JOIN dbo.Evaluators ev ON er.EvaluatorID = ev.EvaluatorID
                LEFT JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                LEFT JOIN dbo.Users u ON er.CreatedByUserID = u.UserID
                WHERE er.RecordID = @ID", new { ID = recordId });

            if (rec == null) return null;

            bool isTemplateStillActive = ((int)rec.IsTemplateStillActive) == 1;

            bool isSystemGrp = rec.SystemGroupCode != null && (rec.SystemGroupCode == "ABSENCE" || rec.SystemGroupCode == "DELAY");
            int maxAllowedCnt = rec.MaxAllowedCount != null ? (int)rec.MaxAllowedCount : 3;

            var workspace = new WeeklyEvaluationWorkspaceModel
            {
                RecordID = rec.RecordID,
                TemplateID = rec.TemplateID,
                TemplateVersionNumber = rec.VersionNumber,
                EvaluatorID = rec.EvaluatorID,
                EvaluatorName = rec.EvaluatorName,
                GroupName = rec.GroupName,
                EvalYear = rec.EvalYear,
                EvalMonth = rec.EvalMonth,
                CreatedAt = rec.CreatedAt,
                CreatedByUserName = rec.CreatedByUserName,
                IsCountBasedEvaluation = isSystemGrp,
                SystemGroupCode = rec.SystemGroupCode ?? string.Empty,
                MaxAllowedCount = maxAllowedCnt > 0 ? maxAllowedCnt : 3
            };

            var savedScores = (await conn.QueryAsync<HistoricalScoreRowRaw>(@"
                SELECT es.ScoreID,
                       es.SheetID,
                       es.EmployeeID,
                       es.CriteriaID,
                       es.GivenScore, es.RawEnteredCount,
                       es.SnapshotEvaluatorWeight,
                       ISNULL(NULLIF(es.SnapshotEvaluationName, N''), ISNULL(e.EvaluationName, CONCAT(N'موظف #', es.EmployeeID))) AS EvaluationName,
                       ISNULL(NULLIF(es.SnapshotSystemName, N''), ISNULL(e.SystemName, N'')) AS SystemName,
                       ISNULL(NULLIF(es.SnapshotBranchName, N''), ISNULL(b.BranchName, N'—')) AS BranchName,
                       ISNULL(NULLIF(es.SnapshotCategoryName, N''), ISNULL(c.CategoryName, N'—')) AS CategoryName,
                       ISNULL(NULLIF(es.SnapshotJobTitle, N''), ISNULL(j.JobTitle, N'—')) AS JobTitle,
                       ISNULL(NULLIF(es.SnapshotJobTypeName, N''), ISNULL(jt.JobTypeName, N'')) AS JobTypeName,
                       ISNULL(es.SnapshotHasIncentive, ISNULL(e.HasIncentive, 1)) AS HasIncentive,
                       ISNULL(NULLIF(es.SnapshotSheetName, N''), ISNULL(ts.SheetName, CONCAT(N'ورقة تقييم ', es.SheetID))) AS SheetName,
                       ISNULL(NULLIF(es.SnapshotTargetCategoryName, N''), ISNULL(ts.TargetCategoryName, ISNULL(c.CategoryName, N'عام'))) AS TargetCategoryName,
                       ISNULL(es.SnapshotSheetSortOrder, ISNULL(ts.SortOrder, 1)) AS SheetSortOrder,
                       ISNULL(NULLIF(es.SnapshotCriteriaTitle, N''), ISNULL(sc.CriteriaTitle, N'قواعد التقييم')) AS CriteriaTitle,
                       ISNULL(es.SnapshotCriteriaMaxScore, ISNULL(sc.MaxScore, 100)) AS CriteriaMaxScore,
                       ISNULL(es.SnapshotCriteriaOrder, ISNULL(sc.ColumnOrder, 1)) AS CriteriaOrder,
                       CASE WHEN e.EmployeeID IS NOT NULL AND e.IsActive = 1 AND e.IsContinuing = 1 THEN 1 ELSE 0 END AS IsEmployeeStillActive
                FROM dbo.EvaluationScores es
                LEFT JOIN dbo.Employees e ON es.EmployeeID = e.EmployeeID
                LEFT JOIN dbo.Branches b ON e.BranchID = b.BranchID
                LEFT JOIN dbo.Categories c ON e.CategoryID = c.CategoryID
                LEFT JOIN dbo.Jobs j ON e.JobID = j.JobID
                LEFT JOIN dbo.JobTypes jt ON e.JobTypeID = jt.JobTypeID
                LEFT JOIN dbo.TemplateSheets ts ON es.SheetID = ts.SheetID
                LEFT JOIN dbo.SheetCriteria sc ON es.CriteriaID = sc.CriteriaID
                WHERE es.RecordID = @RecordID
                ORDER BY SheetSortOrder ASC, es.SheetID ASC, EvaluationName ASC, CriteriaOrder ASC, es.CriteriaID ASC",
                new { RecordID = recordId })).ToList();

            var savedAdjustments = (await conn.QueryAsync<dynamic>(@"
                SELECT SheetID, EmployeeID, BalancingDelta
                FROM dbo.EvaluationEmployeeAdjustments
                WHERE RecordID = @RecordID", new { RecordID = recordId }))
                .ToDictionary(k => ((int)k.SheetID, (int)k.EmployeeID), v => (decimal)v.BalancingDelta);

            bool anyActiveEmployeeInRecord = savedScores.Any(s => s.IsEmployeeStillActive);
            workspace.CanBeModified = isTemplateStillActive && anyActiveEmployeeInRecord;

            var sheetGroups = savedScores
                .GroupBy(s => s.SheetID)
                .OrderBy(g => g.Min(x => x.SheetSortOrder))
                .ThenBy(g => g.Key)
                .ToList();

            foreach (var sheetGrp in sheetGroups)
            {
                var firstRow = sheetGrp.First();
                var sheetModel = new WeeklyEntrySheetModel
                {
                    SheetID = sheetGrp.Key,
                    SheetName = firstRow.SheetName,
                    TargetCategoryName = firstRow.TargetCategoryName,
                    SortOrder = firstRow.SheetSortOrder
                };

                var criteriaList = sheetGrp
                    .GroupBy(s => s.CriteriaID)
                    .OrderBy(g => g.Min(x => x.CriteriaOrder))
                    .ThenBy(g => g.Key)
                    .Select((g, idx) => new CriteriaColumnModel
                    {
                        CriteriaID = g.Key,
                        CriteriaTitle = g.First().CriteriaTitle,
                        MaxScore = g.First().CriteriaMaxScore,
                        ColumnOrder = idx + 1
                    })
                    .ToList();

                sheetModel.CriteriaColumns = criteriaList;

                var empGroups = sheetGrp
                    .GroupBy(s => s.EmployeeID)
                    .OrderBy(g => g.First().EvaluationName)
                    .ToList();

                var empRows = new List<WeeklyScoreRowViewModel>();
                int rowNum = 1;

                foreach (var empGrp in empGroups)
                {
                    var empInfo = empGrp.First();
                    var empVm = new WeeklyScoreRowViewModel
                    {
                        EmployeeID = empGrp.Key,
                        RowOrder = rowNum++,
                        EvaluationName = empInfo.EvaluationName,
                        SystemName = empInfo.SystemName,
                        BranchName = empInfo.BranchName,
                        CategoryName = empInfo.CategoryName,
                        JobTitle = empInfo.JobTitle,
                        JobTypeName = empInfo.JobTypeName,
                        HasIncentive = empInfo.HasIncentive,
                        IsContinuing = true,
                        EvaluatorWeight = empGrp.Max(x => x.SnapshotEvaluatorWeight)
                    };

                    var scoreDict = new Dictionary<int, decimal>();
                    foreach (var s in empGrp)
                    {
                        scoreDict[s.CriteriaID] = s.GivenScore;
                    }

                    savedAdjustments.TryGetValue((sheetModel.SheetID, empVm.EmployeeID), out decimal savedDelta);
                    var rawCountsDict = empGrp.Where(x => x.RawEnteredCount != null).ToDictionary(k => (int)k.CriteriaID, v => (int)v.RawEnteredCount!);
                    empVm.InitializeCriteria(criteriaList, scoreDict, savedDelta, workspace.IsCountBasedEvaluation, workspace.MaxAllowedCount, rawCountsDict);
                    empRows.Add(empVm);
                }

                sheetModel.EmployeeRows = new ObservableCollection<WeeklyScoreRowViewModel>(empRows);
                workspace.Sheets.Add(sheetModel);
            }

            return workspace;
        }

        public async Task<(bool IsSuccess, string Message, int SavedRecordId)> SaveMonthlyEvaluationAsync(
            WeeklyEvaluationWorkspaceModel workspace,
            string permissionModuleCode = "ScoreEntry")
        {
            bool isNew = workspace.RecordID == 0;
            if (!CheckPermission(permissionModuleCode, isNew ? "insert" : "update", out string permErr))
                return (false, permErr, 0);

            if (workspace.TemplateID <= 0 || workspace.EvaluatorID <= 0)
                return (false, "يرجى اختيار قالب المقيم أولاً.", 0);

            if (workspace.EvalYear < 2020 || workspace.EvalYear > 2100)
                return (false, "يرجى إدخال سنة تقييم صحيحة.", 0);

            if (workspace.EvalMonth < 1 || workspace.EvalMonth > 12)
                return (false, "يرجى اختيار شهر التقييم (من 1 إلى 12).", 0);

            if (workspace.Sheets.Count == 0)
                return (false, "لا توجد أوراق تقييم داخل هذا القالب.", 0);

            using var conn = await OpenReadyConnectionAsync();

            int activeTemplateCheck = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.Templates WHERE TemplateID = @TID AND IsDeleted = 0",
                new { TID = workspace.TemplateID });
            if (activeTemplateCheck == 0)
            {
                return (false, "لا يمكن تعديل هذا التقييم لأن القالب المرتبط به تم حذفه من النظام؛ السجل محفوظ في الأرشيف للقراءة فقط.", 0);
            }

            foreach (var sheet in workspace.Sheets)
            {
                foreach (var emp in sheet.EmployeeRows)
                {
                    for (int i = 0; i < emp.CriteriaCount; i++)
                    {
                        int given = emp.GetScoreAt(i);
                        int max = emp.GetMaxScoreAt(i);
                        if (given < 0 || given > max)
                        {
                            return (false,
                                $"درجة الموظف ({emp.EvaluationName}) في الورقة ({sheet.SheetName}) هي ({given}) بينما الحد الأقصى المسموح هو ({max}).",
                                0);
                        }
                    }
                }
            }

            using var tx = conn.BeginTransaction();

            try
            {
                int targetRecordId = workspace.RecordID;
                int currentUserId = AuthService.CurrentSession?.UserID ?? 1;
                string currentUserFullName = AuthService.CurrentSession?.FullName ?? "مدير النظام العام";

                if (isNew)
                {
                    int duplicateCount = await conn.ExecuteScalarAsync<int>(@"
                        SELECT COUNT(1)
                        FROM dbo.EvaluationRecords er
                        LEFT JOIN dbo.Templates t ON er.TemplateID = t.TemplateID
                        WHERE (
                                er.TemplateID = @TemplateID
                                OR (
                                    t.TemplateGroupGuid IS NOT NULL
                                    AND t.TemplateGroupGuid = (SELECT TemplateGroupGuid FROM dbo.Templates WHERE TemplateID = @TemplateID)
                                )
                              )
                          AND er.EvalYear = @Year
                          AND er.EvalMonth = @Month",
                        new
                        {
                            workspace.TemplateID,
                            Year = workspace.EvalYear,
                            Month = workspace.EvalMonth
                        }, tx);

                    if (duplicateCount > 0)
                    {
                        await tx.RollbackAsync();
                        return (false,
                            $"تم إدخال تقييم شهر ({workspace.EvalMonth}/{workspace.EvalYear}) لهذا القالب مسبقاً!\nالتقييم يُدخل مرة واحدة في الشهر؛ يمكنك تفعيل تعديل الشهر الحالي لتحديث الدرجات.",
                            0);
                    }

                    string insertRecordSql = @"
                        INSERT INTO dbo.EvaluationRecords
                            (TemplateID, EvaluatorID, EvalYear, EvalMonth, CreatedByUserID, CreatedAt,
                             SnapshotEvaluatorName, SnapshotGroupName, SnapshotVersionNumber, SnapshotCreatedByFullName)
                        VALUES
                            (@TemplateID, @EvaluatorID, @EvalYear, @EvalMonth, @UserID, SYSDATETIME(),
                             @SnapEvalName, @SnapGroupName, @SnapVer, @SnapUserFull);
                        SELECT CAST(SCOPE_IDENTITY() AS INT);";

                    targetRecordId = await conn.ExecuteScalarAsync<int>(insertRecordSql, new
                    {
                        workspace.TemplateID,
                        workspace.EvaluatorID,
                        workspace.EvalYear,
                        workspace.EvalMonth,
                        UserID = currentUserId,
                        SnapEvalName = workspace.EvaluatorName,
                        SnapGroupName = workspace.GroupName,
                        SnapVer = workspace.TemplateVersionNumber > 0 ? workspace.TemplateVersionNumber : 1,
                        SnapUserFull = currentUserFullName
                    }, tx);
                }
                else
                {
                    await conn.ExecuteAsync(@"
                        UPDATE dbo.EvaluationRecords
                        SET LastModifiedByUserID = @UserID,
                            LastModifiedAt = SYSDATETIME(),
                            SnapshotEvaluatorName = @SnapEvalName,
                            SnapshotGroupName = @SnapGroupName
                        WHERE RecordID = @RecordID",
                        new
                        {
                            UserID = currentUserId,
                            RecordID = targetRecordId,
                            SnapEvalName = workspace.EvaluatorName,
                            SnapGroupName = workspace.GroupName
                        }, tx);

                    await conn.ExecuteAsync(
                        "DELETE FROM dbo.EvaluationScores WHERE RecordID = @RecordID",
                        new { RecordID = targetRecordId }, tx);
                }

                // string insertScoreSql = @"
                    INSERT INTO dbo.EvaluationScores
                        (RecordID, SheetID, EmployeeID, CriteriaID, GivenScore, SnapshotEvaluatorWeight,
                         SnapshotEvaluationName, SnapshotSystemName, SnapshotBranchName, SnapshotCategoryName,
                         SnapshotJobTitle, SnapshotJobTypeName, SnapshotHasIncentive,
                         SnapshotSheetName, SnapshotTargetCategoryName, SnapshotSheetSortOrder,
                         SnapshotCriteriaTitle, SnapshotCriteriaMaxScore, SnapshotCriteriaOrder)
                    VALUES
                        (@RecordID, @SheetID, @EmployeeID, @CriteriaID, @GivenScore, @SnapshotWeight,
                         @SnapEvalName, @SnapSysName, @SnapBranch, @SnapCat,
                         @SnapJob, @SnapJobType, @SnapHasInc,
                         @SnapSheetName, @SnapTargetCat, @SnapSheetOrder,
                         @SnapCritTitle, @SnapCritMax, @SnapCritOrder)";

                foreach (var sheet in workspace.Sheets)
                {
                    foreach (var emp in sheet.EmployeeRows)
                    {
                        for (int c = 0; c < emp.CriteriaCount; c++)
                        {
                            var critCol = sheet.CriteriaColumns[c];
                            decimal effectiveScore = emp.GetEffectiveCriteriaScoreForSave(c);
                            int? rawCount = workspace.IsCountBasedEvaluation ? emp.GetScoreAt(c) : (int?)null;

                            await conn.ExecuteAsync(@"
                                INSERT INTO dbo.EvaluationScores
                                    (RecordID, SheetID, EmployeeID, CriteriaID, GivenScore, SnapshotEvaluatorWeight,
                                     SnapshotEvaluationName, SnapshotSystemName, SnapshotBranchName, SnapshotCategoryName,
                                     SnapshotJobTitle, SnapshotJobTypeName, SnapshotHasIncentive,
                                     SnapshotSheetName, SnapshotTargetCategoryName, SnapshotSheetSortOrder,
                                     SnapshotCriteriaTitle, SnapshotCriteriaMaxScore, SnapshotCriteriaOrder, RawEnteredCount)
                                VALUES
                                    (@RecordID, @SheetID, @EmployeeID, @CriteriaID, @GivenScore, @SnapshotWeight,
                                     @SnapEvalName, @SnapSysName, @SnapBranch, @SnapCat,
                                     @SnapJob, @SnapJobType, @SnapHasInc,
                                     @SnapSheetName, @SnapTargetCat, @SnapSheetOrder,
                                     @SnapCritTitle, @SnapCritMax, @SnapCritOrder, @RawCnt)",
                            new
                            {
                                RecordID = targetRecordId,
                                sheet.SheetID,
                                emp.EmployeeID,
                                CriteriaID = emp.GetCriteriaIdAt(c),
                                GivenScore = effectiveScore,
                                SnapshotWeight = emp.EvaluatorWeight,
                                SnapEvalName = emp.EvaluationName,
                                SnapSysName = emp.SystemName,
                                SnapBranch = emp.BranchName,
                                SnapCat = string.IsNullOrWhiteSpace(emp.CategoryName) ? sheet.TargetCategoryName : emp.CategoryName,
                                SnapJob = emp.JobTitle,
                                SnapJobType = emp.JobTypeName,
                                SnapHasInc = emp.HasIncentive,
                                SnapSheetName = sheet.SheetName,
                                SnapTargetCat = sheet.TargetCategoryName,
                                SnapSheetOrder = sheet.SortOrder,
                                SnapCritTitle = critCol.CriteriaTitle,
                                SnapCritMax = critCol.MaxScore,
                                SnapCritOrder = c + 1,
                                RawCnt = rawCount
                            }, tx);
                        }
                    }
                }

                await tx.CommitAsync();

                string arMonth = IncentiveCalculatorService.GetArabicMonthName(workspace.EvalMonth);
                string successMsg = isNew
                    ? $"تم حفظ واعتماد التقييم الشهري لشهر ({arMonth} {workspace.EvalYear} م) للمقيم ({workspace.EvaluatorName}) بنجاح."
                    : $"تم حفظ التعديلات على التقييم الشهري لشهر ({arMonth} {workspace.EvalYear} م) وانعكست في سجل التقييمات بنجاح.";

                return (true, successMsg, targetRecordId);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return (false, $"حدث خطأ أثناء حفظ التقييم الشهري: {ex.Message}", 0);
            }
        }

        public async Task<List<EvaluationRecordSummaryDto>> GetEvaluationHistoryLogsAsync(
            string evaluatorSearch = "",
            int? filterYear = null,
            int? filterMonth = null)
        {
            using var conn = await OpenReadyConnectionAsync();
            string sql = @"
                SELECT er.RecordID,
                       er.TemplateID,
                       ISNULL(t.VersionNumber, ISNULL(er.SnapshotVersionNumber, 1)) AS VersionNumber,
                       er.EvaluatorID,
                       ISNULL(NULLIF(er.SnapshotEvaluatorName, N''), ISNULL(ev.EvaluatorName, N'مقيم')) AS EvaluatorName,
                       ISNULL(NULLIF(er.SnapshotGroupName, N''), ISNULL(eg.GroupName, N'')) AS GroupName,
                       er.EvalYear,
                       er.EvalMonth,
                       er.CreatedAt,
                       er.LastModifiedAt,
                       ISNULL(NULLIF(er.SnapshotCreatedByFullName, N''), ISNULL(u.FullName, N'مدير النظام العام')) AS CreatedByFullName,
                       (SELECT COUNT(DISTINCT es.EmployeeID) FROM dbo.EvaluationScores es WHERE es.RecordID = er.RecordID) AS EvaluatedEmployeesCount,
                       ISNULL((
                           SELECT AVG(EmpSum.TotalRaw)
                           FROM (
                               SELECT CASE
                                          WHEN SUM(es2.GivenScore) + ISNULL(MAX(adj2.BalancingDelta), 0) > 100 THEN 100
                                          WHEN SUM(es2.GivenScore) + ISNULL(MAX(adj2.BalancingDelta), 0) < 0 THEN 0
                                          ELSE SUM(es2.GivenScore) + ISNULL(MAX(adj2.BalancingDelta), 0)
                                      END AS TotalRaw
                               FROM dbo.EvaluationScores es2
                               LEFT JOIN dbo.EvaluationEmployeeAdjustments adj2
                                   ON adj2.RecordID = es2.RecordID AND adj2.SheetID = es2.SheetID AND adj2.EmployeeID = es2.EmployeeID
                               WHERE es2.RecordID = er.RecordID
                               GROUP BY es2.SheetID, es2.EmployeeID
                           ) AS EmpSum
                       ), 0) AS AverageRawScore
                FROM dbo.EvaluationRecords er
                LEFT JOIN dbo.Templates t ON er.TemplateID = t.TemplateID
                LEFT JOIN dbo.Evaluators ev ON er.EvaluatorID = ev.EvaluatorID
                LEFT JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                LEFT JOIN dbo.Users u ON er.CreatedByUserID = u.UserID
                WHERE (
                        @Search = ''
                        OR ISNULL(er.SnapshotEvaluatorName, ev.EvaluatorName) LIKE @Pattern
                        OR ISNULL(er.SnapshotGroupName, eg.GroupName) LIKE @Pattern
                      )
                  AND (@Year IS NULL OR er.EvalYear = @Year)
                  AND (@Month IS NULL OR er.EvalMonth = @Month)
                ORDER BY er.EvalYear DESC, er.EvalMonth DESC, er.CreatedAt DESC";

            var list = await conn.QueryAsync<EvaluationRecordSummaryDto>(sql, new
            {
                Search = (evaluatorSearch ?? string.Empty).Trim(),
                Pattern = $"%{(evaluatorSearch ?? string.Empty).Trim()}%",
                Year = filterYear,
                Month = filterMonth
            });

            return list.ToList();
        }
    
        private sealed class BulkExcelScoreRow
        {
            public int RowNumber { get; set; }
            public int Month { get; set; }
            public string EmployeeName { get; set; } = string.Empty;
            public string BranchName { get; set; } = string.Empty;
            public string CategoryName { get; set; } = string.Empty;
            public string EvaluatorName { get; set; } = string.Empty;
            public int Score { get; set; }
        }

        private static string NormalizeArabicForBulkMatch(string? input, bool unifyDhadThaa = false)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            var sb = new System.Text.StringBuilder(input.Length);
            foreach (char ch in input.Trim())
            {
                if (ch == 'ـ' || (ch >= '\u064B' && ch <= '\u065F') || ch == '\u0670')
                    continue;

                switch (ch)
                {
                    case 'أ':
                    case 'إ':
                    case 'آ':
                    case 'ٱ':
                        sb.Append('ا');
                        break;
                    case 'ة':
                        sb.Append('ه');
                        break;
                    case 'ى':
                    case 'ئ':
                        sb.Append('ي');
                        break;
                    case 'ؤ':
                        sb.Append('و');
                        break;
                    case 'ظ':
                        sb.Append(unifyDhadThaa ? 'ض' : 'ظ');
                        break;
                    default:
                        sb.Append(char.ToLowerInvariant(ch));
                        break;
                }
            }

            string normalized = string.Join(" ", sb.ToString().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            normalized = normalized.Replace("عبد ", "عبد");
            return normalized;
        }

        private static string StripArabicDefiniteArticle(string normalizedText)
        {
            if (string.IsNullOrWhiteSpace(normalizedText)) return string.Empty;
            var words = normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.StartsWith("ال") && w.Length > 3 ? w[2..] : w);
            return string.Join(" ", words);
        }

        private static bool IsCategoryMatch(string excelCat, string targetCatOrSheetName)
        {
            string normExcel = StripArabicDefiniteArticle(NormalizeArabicForBulkMatch(excelCat, unifyDhadThaa: true));
            string normTarget = StripArabicDefiniteArticle(NormalizeArabicForBulkMatch(targetCatOrSheetName, unifyDhadThaa: true));

            if (string.IsNullOrEmpty(normExcel) || string.IsNullOrEmpty(normTarget))
                return false;

            return normExcel == normTarget || normTarget.Contains(normExcel) || normExcel.Contains(normTarget);
        }

        private static bool IsBranchMatch(string excelBranch, string empBranch)
        {
            string normExcel = StripArabicDefiniteArticle(NormalizeArabicForBulkMatch(excelBranch));
            string normEmp = StripArabicDefiniteArticle(NormalizeArabicForBulkMatch(empBranch));

            if (string.IsNullOrEmpty(normExcel) || string.IsNullOrEmpty(normEmp))
                return false;

            return normExcel == normEmp || normEmp.Contains(normExcel) || normExcel.Contains(normEmp);
        }

        private static bool IsEmployeeNameMatch(string excelName, string evalName, string sysName)
        {
            string normExcel = NormalizeArabicForBulkMatch(excelName);
            if (string.IsNullOrEmpty(normExcel)) return false;

            string normEval = NormalizeArabicForBulkMatch(evalName);
            if (normExcel == normEval) return true;

            string normSys = NormalizeArabicForBulkMatch(sysName);
            if (!string.IsNullOrEmpty(normSys))
            {
                if (normExcel == normSys) return true;
                int dashIdx = normSys.IndexOf('-');
                if (dashIdx > 0)
                {
                    string sysBeforeDash = normSys[..dashIdx].Trim();
                    if (normExcel == sysBeforeDash) return true;
                }
            }

            return false;
        }

        private static void AssignTotalScoreToEmployeeRow(
            WeeklyEntrySheetModel sheet,
            WeeklyScoreRowViewModel empRow,
            int totalScoreOutOf100)
        {
            int clampedTotal = Math.Clamp(totalScoreOutOf100, 0, 100);
            int count = sheet.CriteriaColumns.Count;
            if (count == 0) return;

            if (count == 1)
            {
                empRow.SetScoreAt(0, clampedTotal);
                return;
            }

            int totalColIdx = -1;
            for (int i = 0; i < count; i++)
            {
                string normTitle = NormalizeArabicForBulkMatch(sheet.CriteriaColumns[i].CriteriaTitle);
                if (normTitle.Contains("كلي") || normTitle.Contains("كليه") || sheet.CriteriaColumns[i].MaxScore >= 100m)
                {
                    totalColIdx = i;
                    break;
                }
            }

            if (totalColIdx >= 0)
            {
                for (int i = 0; i < count; i++)
                {
                    empRow.SetScoreAt(i, i == totalColIdx ? clampedTotal : 0);
                }
                return;
            }

            int remaining = clampedTotal;
            for (int i = 0; i < count; i++)
            {
                int maxCol = empRow.GetMaxScoreAt(i);
                if (i == count - 1)
                {
                    empRow.SetScoreAt(i, Math.Clamp(remaining, 0, maxCol));
                }
                else
                {
                    int share = (int)Math.Round((clampedTotal * (decimal)maxCol) / 100m, 0, MidpointRounding.AwayFromZero);
                    share = Math.Clamp(share, 0, Math.Min(maxCol, remaining));
                    empRow.SetScoreAt(i, share);
                    remaining -= share;
                }
            }
        }

        /// <summary>
        /// ميزة سحب التقييمات المجمعة لعدة أشهر (مثلاً 7 و 8 و 9 و 10) ولجميع المقيمين والموظفين من ملف إكسل واحد.
        /// يحدد القالب من عمود (اسم جهة التقييم) ويطابق الموظف عبر (اسم المعلم + مكان التقييم + اسم الفئة) ويدخل (الدرجة الكلية).
        /// </summary>
        public async Task<(bool IsSuccess, string SummaryMessage)> ImportBulkMultiMonthEvaluationsFromExcelAsync(
            string filePath,
            int targetYear)
        {
            if (!CheckPermission("ScoreEntry", "insert", out string permErr) &&
                !CheckPermission("ScoreEntry", "update", out permErr))
            {
                return (false, permErr);
            }

            if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
                return (false, "ملف الإكسل المحدد غير موجود.");

            try
            {
                var parsedRows = new List<BulkExcelScoreRow>();

                using (var fs = new System.IO.FileStream(filePath, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                using (var ms = new System.IO.MemoryStream())
                {
                    await fs.CopyToAsync(ms);
                    ms.Position = 0;

                    using var wb = new ClosedXML.Excel.XLWorkbook(ms);
                    foreach (var ws in wb.Worksheets)
                    {
                        var rangeUsed = ws.RangeUsed();
                        if (rangeUsed == null) continue;

                        int lastRow = rangeUsed.LastRow().RowNumber();
                        int lastCol = Math.Max(rangeUsed.LastColumn().ColumnNumber(), 10);

                        int headerRow = 1;
                        int colMonth = 1;
                        int colEmpName = 3;
                        int colBranch = 4;
                        int colCategory = 8;
                        int colEvaluator = 9;
                        int colScore = 10;

                        for (int r = 1; r <= Math.Min(5, lastRow); r++)
                        {
                            bool foundHeader = false;
                            for (int c = 1; c <= lastCol; c++)
                            {
                                string h = NormalizeArabicForBulkMatch(ws.Cell(r, c).GetString());
                                if (h.Contains("شهر") || h.Contains("اسم المعلم") || h.Contains("جهه التقييم"))
                                {
                                    foundHeader = true;
                                    break;
                                }
                            }

                            if (foundHeader)
                            {
                                headerRow = r;
                                for (int c = 1; c <= lastCol; c++)
                                {
                                    string h = NormalizeArabicForBulkMatch(ws.Cell(r, c).GetString());
                                    if (string.IsNullOrEmpty(h)) continue;

                                    if (h.Contains("شهر")) colMonth = c;
                                    else if (h.Contains("اسم المعلم") || h.Contains("اسم الموظف") || h.Contains("الاسم التقييمي")) colEmpName = c;
                                    else if (h.Contains("مكان التقييم") || h == "الفرع" || h.Contains("اسم الفرع")) colBranch = c;
                                    else if (h.Contains("اسم الفئه") || (h == "الفئه" && !h.Contains("رقم"))) colCategory = c;
                                    else if (h.Contains("جهه التقييم") || h.Contains("اسم المقيم") || h == "المقيم") colEvaluator = c;
                                    else if (h.Contains("الدرجه الكليه") || h.Contains("التقييم الكلي") || h.Contains("الدرجه")) colScore = c;
                                }
                                break;
                            }
                        }

                        for (int r = headerRow + 1; r <= lastRow; r++)
                        {
                            string rawMonth = WeeklyScoreRowViewModel.NormalizeAsciiDigits(ws.Cell(r, colMonth).GetString().Trim());
                            string empName = ws.Cell(r, colEmpName).GetString().Trim();
                            string branchName = ws.Cell(r, colBranch).GetString().Trim();
                            string catName = ws.Cell(r, colCategory).GetString().Trim();
                            string evalName = ws.Cell(r, colEvaluator).GetString().Trim();
                            string rawScore = WeeklyScoreRowViewModel.NormalizeAsciiDigits(ws.Cell(r, colScore).GetString().Trim()).Replace(',', '.');

                            if (string.IsNullOrWhiteSpace(empName) && string.IsNullOrWhiteSpace(evalName))
                                continue;

                            if (!int.TryParse(rawMonth, out int monthNum) || monthNum < 1 || monthNum > 12)
                                continue;

                            if (string.IsNullOrWhiteSpace(empName) || string.IsNullOrWhiteSpace(evalName))
                                continue;

                            if (!decimal.TryParse(rawScore, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal decScore))
                                continue;

                            int scoreInt = Math.Clamp((int)Math.Round(decScore, 0, MidpointRounding.AwayFromZero), 0, 100);

                            parsedRows.Add(new BulkExcelScoreRow
                            {
                                RowNumber = r,
                                Month = monthNum,
                                EmployeeName = empName,
                                BranchName = branchName,
                                CategoryName = catName,
                                EvaluatorName = evalName,
                                Score = scoreInt
                            });
                        }
                    }
                }

                if (parsedRows.Count == 0)
                    return (false, "لم يتم العثور على صفوف بيانات صالحة داخل ملف الإكسل. تأكد من وجود أعمدة (رقم الشهر، اسم المعلم، مكان التقييم، اسم الفئة، اسم جهة التقييم، الدرجة الكلية).");

                var activeTemplates = await GetActiveTemplatesForEntryAsync();
                if (activeTemplates.Count == 0)
                    return (false, "لا توجد قوالب تقييم فعالة في النظام حالياً. يرجى التأكد من إنشاء القوالب أولاً.");

                var templateByEvaluatorKey = new Dictionary<string, TemplateSummaryDto?>(StringComparer.OrdinalIgnoreCase);
                var unmatchedEvaluators = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string distinctEval in parsedRows.Select(x => x.EvaluatorName).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string normEval = StripArabicDefiniteArticle(NormalizeArabicForBulkMatch(distinctEval));

                    var matchedTemplate = activeTemplates.FirstOrDefault(t =>
                            StripArabicDefiniteArticle(NormalizeArabicForBulkMatch(t.EvaluatorName)) == normEval)
                        ?? activeTemplates.FirstOrDefault(t =>
                            StripArabicDefiniteArticle(NormalizeArabicForBulkMatch(t.GroupName)) == normEval)
                        ?? activeTemplates.FirstOrDefault(t =>
                        {
                            string tEval = StripArabicDefiniteArticle(NormalizeArabicForBulkMatch(t.EvaluatorName));
                            return !string.IsNullOrEmpty(tEval) && (tEval.Contains(normEval) || normEval.Contains(tEval));
                        })
                        ?? activeTemplates.FirstOrDefault(t =>
                        {
                            string tGrp = StripArabicDefiniteArticle(NormalizeArabicForBulkMatch(t.GroupName));
                            return !string.IsNullOrEmpty(tGrp) && (tGrp.Contains(normEval) || normEval.Contains(tGrp));
                        });

                    templateByEvaluatorKey[distinctEval] = matchedTemplate;
                    if (matchedTemplate == null)
                    {
                        unmatchedEvaluators.Add(distinctEval);
                    }
                }

                var validRowsWithTemplate = parsedRows
                    .Where(r => templateByEvaluatorKey.TryGetValue(r.EvaluatorName, out var t) && t != null)
                    .Select(r => (Row: r, Template: templateByEvaluatorKey[r.EvaluatorName]!))
                    .ToList();

                if (validRowsWithTemplate.Count == 0)
                {
                    return (false,
                        $"لم يتطابق أي (اسم جهة التقييم) في الملف مع قوالب المقيمين في النظام.\n" +
                        $"الجهات الموجودة في الملف: {string.Join("، ", unmatchedEvaluators.Take(10))}");
                }

                var groupedByTemplateAndMonth = validRowsWithTemplate
                    .GroupBy(x => (x.Template.TemplateID, x.Row.Month))
                    .OrderBy(g => g.Key.Month)
                    .ThenBy(g => g.Key.TemplateID)
                    .ToList();

                int savedWorkspacesCount = 0;
                int matchedEmployeesScoresCount = 0;
                var unmatchedEmployeeSamples = new List<string>();
                var touchedMonths = new SortedSet<int>();
                var touchedEvaluators = new HashSet<string>();

                foreach (var group in groupedByTemplateAndMonth)
                {
                    int templateId = group.Key.TemplateID;
                    int month = group.Key.Month;
                    var templateDto = group.First().Template;

                    var workspace = await PrepareNewWorkspaceAsync(templateId, targetYear, month);
                    if (workspace == null || workspace.Sheets.Count == 0)
                        continue;

                    var monthStatus = await GetMonthStatusAsync(templateId, targetYear, month);
                    if (monthStatus.IsAlreadyEntered && monthStatus.ExistingRecordId.HasValue)
                    {
                        workspace.RecordID = monthStatus.ExistingRecordId.Value;
                        var existingWs = await LoadSavedRecordWorkspaceAsync(monthStatus.ExistingRecordId.Value);
                        if (existingWs != null)
                        {
                            foreach (var targetSheet in workspace.Sheets)
                            {
                                var savedSheet = existingWs.Sheets.FirstOrDefault(s => s.SheetID == targetSheet.SheetID);
                                if (savedSheet == null) continue;

                                foreach (var targetEmp in targetSheet.EmployeeRows)
                                {
                                    var savedEmp = savedSheet.EmployeeRows.FirstOrDefault(e => e.EmployeeID == targetEmp.EmployeeID);
                                    if (savedEmp != null)
                                    {
                                        targetEmp.BalancingDelta = savedEmp.BalancingDelta;
                                        for (int c = 0; c < Math.Min(targetEmp.CriteriaCount, savedEmp.CriteriaCount); c++)
                                        {
                                            targetEmp.SetScoreAt(c, savedEmp.GetScoreAt(c));
                                        }
                                    }
                                }
                            }
                        }
                    }

                    int groupMatchedCount = 0;

                    foreach (var item in group)
                    {
                        var excelRow = item.Row;
                        WeeklyEntrySheetModel? matchedSheet = null;
                        WeeklyScoreRowViewModel? matchedEmp = null;

                        // المستوى 1: مطابقة (اسم المعلم + مكان التقييم/الفرع + اسم الفئة)
                        foreach (var sheet in workspace.Sheets)
                        {
                            foreach (var emp in sheet.EmployeeRows)
                            {
                                if (IsEmployeeNameMatch(excelRow.EmployeeName, emp.EvaluationName, emp.SystemName) &&
                                    IsBranchMatch(excelRow.BranchName, emp.BranchName) &&
                                    (IsCategoryMatch(excelRow.CategoryName, emp.CategoryName) ||
                                     IsCategoryMatch(excelRow.CategoryName, sheet.TargetCategoryName) ||
                                     IsCategoryMatch(excelRow.CategoryName, sheet.SheetName)))
                                {
                                    matchedSheet = sheet;
                                    matchedEmp = emp;
                                    break;
                                }
                            }
                            if (matchedEmp != null) break;
                        }

                        // المستوى 2: مطابقة (اسم المعلم + مكان التقييم/الفرع)
                        if (matchedEmp == null)
                        {
                            foreach (var sheet in workspace.Sheets)
                            {
                                foreach (var emp in sheet.EmployeeRows)
                                {
                                    if (IsEmployeeNameMatch(excelRow.EmployeeName, emp.EvaluationName, emp.SystemName) &&
                                        IsBranchMatch(excelRow.BranchName, emp.BranchName))
                                    {
                                        matchedSheet = sheet;
                                        matchedEmp = emp;
                                        break;
                                    }
                                }
                                if (matchedEmp != null) break;
                            }
                        }

                        // المستوى 3: مطابقة (اسم المعلم) إذا كان فريداً داخل قالب هذا المقيم
                        if (matchedEmp == null)
                        {
                            var nameMatches = new List<(WeeklyEntrySheetModel Sheet, WeeklyScoreRowViewModel Emp)>();
                            foreach (var sheet in workspace.Sheets)
                            {
                                foreach (var emp in sheet.EmployeeRows)
                                {
                                    if (IsEmployeeNameMatch(excelRow.EmployeeName, emp.EvaluationName, emp.SystemName))
                                    {
                                        nameMatches.Add((sheet, emp));
                                    }
                                }
                            }

                            if (nameMatches.Count == 1)
                            {
                                matchedSheet = nameMatches[0].Sheet;
                                matchedEmp = nameMatches[0].Emp;
                            }
                        }

                        if (matchedSheet != null && matchedEmp != null)
                        {
                            AssignTotalScoreToEmployeeRow(matchedSheet, matchedEmp, excelRow.Score);
                            groupMatchedCount++;
                            matchedEmployeesScoresCount++;
                        }
                        else
                        {
                            if (unmatchedEmployeeSamples.Count < 10)
                            {
                                unmatchedEmployeeSamples.Add(
                                    $"سطر {excelRow.RowNumber}: ({excelRow.EmployeeName} - {excelRow.BranchName} - {excelRow.CategoryName}) لدى المقيم ({excelRow.EvaluatorName})");
                            }
                        }
                    }

                    if (groupMatchedCount > 0)
                    {
                        var (savedOk, _, _) = await SaveMonthlyEvaluationAsync(workspace, "ScoreEntry");
                        if (savedOk)
                        {
                            savedWorkspacesCount++;
                            touchedMonths.Add(month);
                            touchedEvaluators.Add(templateDto.EvaluatorName);
                        }
                    }
                }

                string monthsText = string.Join(" ، ", touchedMonths.Select(m => $"{m} ({IncentiveCalculatorService.GetArabicMonthName(m)})"));
                var summary = new System.Text.StringBuilder();
                summary.AppendLine($"✔ اكتمل سحب واعتماد التقييمات المجمعة لعام ({targetYear} م) بنجاح!");
                summary.AppendLine($"• الأشهر التي تم رصدها وحفظها: {monthsText}");
                summary.AppendLine($"• عدد المقيمين (القوالب) الذين تم رصدهم: {touchedEvaluators.Count} مقيم");
                summary.AppendLine($"• إجمالي سجلات التقييم الشهرية المحفوظة/المحدثة: {savedWorkspacesCount} سجل شهري");
                summary.AppendLine($"• إجمالي درجات الموظفين المرصودة بنجاح: {matchedEmployeesScoresCount} درجة موظف");

                if (unmatchedEvaluators.Count > 0)
                {
                    summary.AppendLine($"\n⚠ جهات تقييم في الإكسل لم يُعثر لها على قالب فعال ({unmatchedEvaluators.Count}):");
                    summary.AppendLine(string.Join(" ، ", unmatchedEvaluators.Take(8)));
                }

                if (unmatchedEmployeeSamples.Count > 0)
                {
                    summary.AppendLine($"\n⚠ أمثلة لأسماء في الإكسل لم توجد داخل قالب المقيم المحدد:");
                    summary.AppendLine(string.Join("\n", unmatchedEmployeeSamples.Take(6)));
                }

                return (matchedEmployeesScoresCount > 0, summary.ToString());
            }
            catch (Exception ex)
            {
                return (false, $"حدث خطأ أثناء سحب ملف التقييمات المجمعة:\n{ex.Message}");
            }
        }
    }
}




