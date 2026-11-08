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
                WHERE t.IsCurrentVersion = 1 AND t.IsDeleted = 0
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

        /// <summary>
        /// يتحقق هل تم إدخال التقييم الشهري لهذا القالب (أو مجموعة نسخه) في الشهر والسنة المحددين.
        /// </summary>
        public async Task<MonthlyRecordStatusDto> GetMonthStatusAsync(int templateId, int year, int month)
        {
            if (templateId <= 0)
                return new MonthlyRecordStatusDto { IsAlreadyEntered = false };

            using var conn = await OpenReadyConnectionAsync();
            int? existingRecordId = await conn.ExecuteScalarAsync<int?>(@"
                SELECT TOP 1 er.RecordID
                FROM dbo.EvaluationRecords er
                INNER JOIN dbo.Templates t ON er.TemplateID = t.TemplateID
                WHERE t.TemplateGroupGuid = (SELECT TemplateGroupGuid FROM dbo.Templates WHERE TemplateID = @TemplateID)
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
                SELECT t.TemplateID, t.VersionNumber, t.EvaluatorID, ev.EvaluatorName, ISNULL(eg.GroupName, N'') AS GroupName
                FROM dbo.Templates t
                INNER JOIN dbo.Evaluators ev ON t.EvaluatorID = ev.EvaluatorID
                LEFT JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE t.TemplateID = @ID", new { ID = templateId });

            if (header == null) return null;

            var workspace = new WeeklyEvaluationWorkspaceModel
            {
                RecordID = 0,
                TemplateID = header.TemplateID,
                TemplateVersionNumber = header.VersionNumber,
                EvaluatorID = header.EvaluatorID,
                EvaluatorName = header.EvaluatorName,
                GroupName = header.GroupName,
                EvalYear = year,
                EvalMonth = month
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

                // يظهر في الاستمارة الشهرية فقط الموظفون المستمرون (بغض النظر عن بحافز/بدون حافز، بينما يختفي المنقطع)
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
                    empRows[i].InitializeCriteria(criteria, null);
                }

                sheet.EmployeeRows = new ObservableCollection<WeeklyScoreRowViewModel>(empRows);
                workspace.Sheets.Add(sheet);
            }

            return workspace;
        }

        public async Task<WeeklyEvaluationWorkspaceModel?> LoadSavedRecordWorkspaceAsync(int recordId)
        {
            using var conn = await OpenReadyConnectionAsync();
            var rec = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT er.RecordID, er.TemplateID, t.VersionNumber, er.EvaluatorID, ev.EvaluatorName, ISNULL(eg.GroupName, N'') AS GroupName,
                       er.EvalYear, er.EvalMonth, er.CreatedAt, u.FullName AS CreatedByUserName
                FROM dbo.EvaluationRecords er
                INNER JOIN dbo.Templates t ON er.TemplateID = t.TemplateID
                INNER JOIN dbo.Evaluators ev ON er.EvaluatorID = ev.EvaluatorID
                LEFT JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                INNER JOIN dbo.Users u ON er.CreatedByUserID = u.UserID
                WHERE er.RecordID = @ID", new { ID = recordId });

            if (rec == null) return null;

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
                CreatedByUserName = rec.CreatedByUserName
            };

            var savedScores = (await conn.QueryAsync<dynamic>(@"
                SELECT SheetID, EmployeeID, CriteriaID, GivenScore, SnapshotEvaluatorWeight
                FROM dbo.EvaluationScores
                WHERE RecordID = @RecordID", new { RecordID = recordId })).ToList();

            var savedAdjustments = (await conn.QueryAsync<dynamic>(@"
                SELECT SheetID, EmployeeID, BalancingDelta
                FROM dbo.EvaluationEmployeeAdjustments
                WHERE RecordID = @RecordID", new { RecordID = recordId }))
                .ToDictionary(k => ((int)k.SheetID, (int)k.EmployeeID), v => (decimal)v.BalancingDelta);

            var evaluatedEmpIds = savedScores.Select(s => (int)s.EmployeeID).ToHashSet();

            var sheets = (await conn.QueryAsync<WeeklyEntrySheetModel>(@"
                SELECT SheetID, SheetName, TargetCategoryName, SortOrder
                FROM dbo.TemplateSheets
                WHERE TemplateID = @TemplateID
                ORDER BY SortOrder ASC", new { TemplateID = workspace.TemplateID })).ToList();

            foreach (var sheet in sheets)
            {
                var criteria = (await conn.QueryAsync<CriteriaColumnModel>(@"
                    SELECT CriteriaID, CriteriaTitle, MaxScore, ColumnOrder
                    FROM dbo.SheetCriteria
                    WHERE SheetID = @SheetID
                    ORDER BY ColumnOrder ASC", new { SheetID = sheet.SheetID })).ToList();

                sheet.CriteriaColumns = criteria;

                // يعرض الموظفين الذين تم تقييمهم فعلياً في هذا السجل التاريخي أو المستمرين حالياً
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
                           ), 0) AS EvaluatorWeight
                    FROM dbo.SheetEmployees se
                    INNER JOIN dbo.Employees e ON se.EmployeeID = e.EmployeeID
                    INNER JOIN dbo.Branches b ON e.BranchID = b.BranchID
                    INNER JOIN dbo.Jobs j ON e.JobID = j.JobID
                    INNER JOIN dbo.JobTypes jt ON e.JobTypeID = jt.JobTypeID
                    INNER JOIN dbo.Categories c ON e.CategoryID = c.CategoryID
                    WHERE se.SheetID = @SheetID
                    ORDER BY se.RowOrder ASC",
                    new { SheetID = sheet.SheetID, EvalID = workspace.EvaluatorID })).ToList();

                var filteredRows = empRows
                    .Where(e => evaluatedEmpIds.Contains(e.EmployeeID) || e.IsContinuing)
                    .ToList();

                int order = 1;
                foreach (var emp in filteredRows)
                {
                    emp.RowOrder = order++;
                    var empSavedRows = savedScores.Where(s => (int)s.EmployeeID == emp.EmployeeID && (int)s.SheetID == sheet.SheetID).ToList();
                    var scoreDict = new Dictionary<int, decimal>();

                    if (empSavedRows.Count > 0)
                    {
                        emp.EvaluatorWeight = (decimal)empSavedRows[0].SnapshotEvaluatorWeight;
                        foreach (var s in empSavedRows)
                        {
                            scoreDict[(int)s.CriteriaID] = (decimal)s.GivenScore;
                        }
                    }

                    savedAdjustments.TryGetValue((sheet.SheetID, emp.EmployeeID), out decimal savedDelta);
                    emp.InitializeCriteria(criteria, scoreDict, savedDelta);
                }

                sheet.EmployeeRows = new ObservableCollection<WeeklyScoreRowViewModel>(filteredRows);
                workspace.Sheets.Add(sheet);
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

            using var conn = await OpenReadyConnectionAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                int targetRecordId = workspace.RecordID;
                int currentUserId = AuthService.CurrentSession?.UserID ?? 1;

                if (isNew)
                {
                    int duplicateCount = await conn.ExecuteScalarAsync<int>(@"
                        SELECT COUNT(1)
                        FROM dbo.EvaluationRecords er
                        INNER JOIN dbo.Templates t ON er.TemplateID = t.TemplateID
                        WHERE t.TemplateGroupGuid = (SELECT TemplateGroupGuid FROM dbo.Templates WHERE TemplateID = @TemplateID)
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
                        INSERT INTO dbo.EvaluationRecords (TemplateID, EvaluatorID, EvalYear, EvalMonth, CreatedByUserID, CreatedAt)
                        VALUES (@TemplateID, @EvaluatorID, @EvalYear, @EvalMonth, @UserID, SYSDATETIME());
                        SELECT CAST(SCOPE_IDENTITY() AS INT);";

                    targetRecordId = await conn.ExecuteScalarAsync<int>(insertRecordSql, new
                    {
                        workspace.TemplateID,
                        workspace.EvaluatorID,
                        workspace.EvalYear,
                        workspace.EvalMonth,
                        UserID = currentUserId
                    }, tx);
                }
                else
                {
                    await conn.ExecuteAsync(@"
                        UPDATE dbo.EvaluationRecords
                        SET LastModifiedByUserID = @UserID, LastModifiedAt = SYSDATETIME()
                        WHERE RecordID = @RecordID",
                        new { UserID = currentUserId, RecordID = targetRecordId }, tx);

                    await conn.ExecuteAsync(
                        "DELETE FROM dbo.EvaluationScores WHERE RecordID = @RecordID",
                        new { RecordID = targetRecordId }, tx);
                }

                string insertScoreSql = @"
                    INSERT INTO dbo.EvaluationScores (RecordID, SheetID, EmployeeID, CriteriaID, GivenScore, SnapshotEvaluatorWeight)
                    VALUES (@RecordID, @SheetID, @EmployeeID, @CriteriaID, @GivenScore, @SnapshotWeight)";

                foreach (var sheet in workspace.Sheets)
                {
                    foreach (var emp in sheet.EmployeeRows)
                    {
                        for (int c = 0; c < emp.CriteriaCount; c++)
                        {
                            await conn.ExecuteAsync(insertScoreSql, new
                            {
                                RecordID = targetRecordId,
                                sheet.SheetID,
                                emp.EmployeeID,
                                CriteriaID = emp.GetCriteriaIdAt(c),
                                GivenScore = (decimal)emp.GetScoreAt(c),
                                SnapshotWeight = emp.EvaluatorWeight
                            }, tx);
                        }
                    }
                }

                await tx.CommitAsync();

                string arMonth = IncentiveCalculatorService.GetArabicMonthName(workspace.EvalMonth);
                string successMsg = isNew
                    ? $"تم حفظ واعتماد التقييم الشهري لشهر ({arMonth} {workspace.EvalYear} م) للمقيم ({workspace.EvaluatorName}) بنجاح."
                    : $"تم حفظ التعديلات على التقييم الشهري لشهر ({arMonth} {workspace.EvalYear} م) بنجاح.";

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
                SELECT er.RecordID, er.TemplateID, t.VersionNumber, er.EvaluatorID, ev.EvaluatorName, ISNULL(eg.GroupName, N'') AS GroupName,
                       er.EvalYear, er.EvalMonth, er.CreatedAt, er.LastModifiedAt,
                       u.FullName AS CreatedByFullName,
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
                INNER JOIN dbo.Templates t ON er.TemplateID = t.TemplateID
                INNER JOIN dbo.Evaluators ev ON er.EvaluatorID = ev.EvaluatorID
                LEFT JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                INNER JOIN dbo.Users u ON er.CreatedByUserID = u.UserID
                WHERE (@Search = '' OR ev.EvaluatorName LIKE @Pattern OR eg.GroupName LIKE @Pattern)
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

        public async Task<(bool IsSuccess, string Message)> DeleteEvaluationRecordAsync(
            int recordId,
            string permissionModuleCode = "HistoryLog")
        {
            if (!CheckPermission(permissionModuleCode, "delete", out string permErr))
                return (false, permErr);

            using var conn = await OpenReadyConnectionAsync();
            await conn.ExecuteAsync("DELETE FROM dbo.EvaluationRecords WHERE RecordID = @ID", new { ID = recordId });
            return (true, "تم حذف سجل التقييم الشهري المحدد نهائياً من قاعدة البيانات.");
        }
    }
}

