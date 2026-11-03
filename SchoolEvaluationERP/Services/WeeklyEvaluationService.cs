using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

        public async Task<List<TemplateSummaryDto>> GetActiveTemplatesForEntryAsync(string search = "")
        {
            using var conn = new SqlConnection(_connectionString);
            string sql = @"
                SELECT t.TemplateID, t.TemplateGroupGuid, t.EvaluatorID, ev.EvaluatorName,
                       t.VersionNumber, t.CreatedAt,
                       (SELECT COUNT(1) FROM dbo.TemplateSheets ts WHERE ts.TemplateID = t.TemplateID) AS SheetsCount,
                       (SELECT COUNT(1) FROM dbo.SheetEmployees se INNER JOIN dbo.TemplateSheets ts2 ON se.SheetID = ts2.SheetID WHERE ts2.TemplateID = t.TemplateID) AS TotalEmployeesCount
                FROM dbo.Templates t
                INNER JOIN dbo.Evaluators ev ON t.EvaluatorID = ev.EvaluatorID
                WHERE t.IsCurrentVersion = 1 AND t.IsDeleted = 0
                  AND (@Search = '' OR ev.EvaluatorName LIKE @Pattern)
                ORDER BY ev.EvaluatorName ASC, t.TemplateID DESC";

            var list = (await conn.QueryAsync<TemplateSummaryDto>(sql, new
            {
                Search = search.Trim(),
                Pattern = $"%{search.Trim()}%"
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

        public async Task<List<WeekStatusItem>> GetWeeksStatusAsync(int templateId, int year, int month)
        {
            var weeks = new List<WeekStatusItem>
            {
                new() { WeekNumber = 1 },
                new() { WeekNumber = 2 },
                new() { WeekNumber = 3 },
                new() { WeekNumber = 4 }
            };

            if (templateId <= 0) return weeks;

            using var conn = new SqlConnection(_connectionString);
            string sql = @"
                SELECT er.WeekNumber, er.RecordID
                FROM dbo.EvaluationRecords er
                INNER JOIN dbo.Templates t ON er.TemplateID = t.TemplateID
                WHERE t.TemplateGroupGuid = (SELECT TemplateGroupGuid FROM dbo.Templates WHERE TemplateID = @TemplateID)
                  AND er.EvalYear = @Year
                  AND er.EvalMonth = @Month";

            var existing = await conn.QueryAsync<dynamic>(sql, new
            {
                TemplateID = templateId,
                Year = year,
                Month = month
            });

            var map = existing.GroupBy(x => (int)x.WeekNumber)
                              .ToDictionary(g => g.Key, g => (int)g.First().RecordID);

            foreach (var w in weeks)
            {
                if (map.TryGetValue(w.WeekNumber, out int recId))
                {
                    w.IsAlreadyEntered = true;
                    w.ExistingRecordId = recId;
                }
            }

            return weeks;
        }

        public async Task<WeeklyEvaluationWorkspaceModel?> PrepareNewWorkspaceAsync(int templateId, int year, int month, int weekNumber)
        {
            using var conn = new SqlConnection(_connectionString);
            var header = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT t.TemplateID, t.VersionNumber, t.EvaluatorID, ev.EvaluatorName
                FROM dbo.Templates t
                INNER JOIN dbo.Evaluators ev ON t.EvaluatorID = ev.EvaluatorID
                WHERE t.TemplateID = @ID", new { ID = templateId });

            if (header == null) return null;

            var workspace = new WeeklyEvaluationWorkspaceModel
            {
                RecordID = 0,
                TemplateID = header.TemplateID,
                TemplateVersionNumber = header.VersionNumber,
                EvaluatorID = header.EvaluatorID,
                EvaluatorName = header.EvaluatorName,
                EvalYear = year,
                EvalMonth = month,
                WeekNumber = weekNumber
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
                           b.BranchName, j.JobTitle, c.CategoryName,
                           ISNULL((
                               SELECT TOP 1 ce.WeightScore
                               FROM dbo.CategoryEvaluators ce
                               WHERE ce.CategoryID = e.CategoryID AND ce.EvaluatorID = @EvalID
                           ), 0) AS EvaluatorWeight
                    FROM dbo.SheetEmployees se
                    INNER JOIN dbo.Employees e ON se.EmployeeID = e.EmployeeID
                    INNER JOIN dbo.Branches b ON e.BranchID = b.BranchID
                    INNER JOIN dbo.Jobs j ON e.JobID = j.JobID
                    INNER JOIN dbo.Categories c ON e.CategoryID = c.CategoryID
                    WHERE se.SheetID = @SheetID
                    ORDER BY se.RowOrder ASC",
                    new { SheetID = sheet.SheetID, EvalID = workspace.EvaluatorID })).ToList();

                foreach (var emp in empRows)
                {
                    emp.InitializeCriteria(criteria, null);
                }

                sheet.EmployeeRows = new ObservableCollection<WeeklyScoreRowViewModel>(empRows);
                workspace.Sheets.Add(sheet);
            }

            return workspace;
        }

        public async Task<WeeklyEvaluationWorkspaceModel?> LoadSavedRecordWorkspaceAsync(int recordId)
        {
            using var conn = new SqlConnection(_connectionString);
            var rec = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT er.RecordID, er.TemplateID, t.VersionNumber, er.EvaluatorID, ev.EvaluatorName,
                       er.EvalYear, er.EvalMonth, er.WeekNumber, er.CreatedAt, u.FullName AS CreatedByUserName
                FROM dbo.EvaluationRecords er
                INNER JOIN dbo.Templates t ON er.TemplateID = t.TemplateID
                INNER JOIN dbo.Evaluators ev ON er.EvaluatorID = ev.EvaluatorID
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
                EvalYear = rec.EvalYear,
                EvalMonth = rec.EvalMonth,
                WeekNumber = rec.WeekNumber,
                CreatedAt = rec.CreatedAt,
                CreatedByUserName = rec.CreatedByUserName
            };

            var savedScores = (await conn.QueryAsync<dynamic>(@"
                SELECT SheetID, EmployeeID, CriteriaID, GivenScore, SnapshotEvaluatorWeight
                FROM dbo.EvaluationScores
                WHERE RecordID = @RecordID", new { RecordID = recordId })).ToList();

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

                var empRows = (await conn.QueryAsync<WeeklyScoreRowViewModel>(@"
                    SELECT se.EmployeeID, se.RowOrder, e.EvaluationName, e.SystemName,
                           b.BranchName, j.JobTitle, c.CategoryName,
                           ISNULL((
                               SELECT TOP 1 ce.WeightScore
                               FROM dbo.CategoryEvaluators ce
                               WHERE ce.CategoryID = e.CategoryID AND ce.EvaluatorID = @EvalID
                           ), 0) AS EvaluatorWeight
                    FROM dbo.SheetEmployees se
                    INNER JOIN dbo.Employees e ON se.EmployeeID = e.EmployeeID
                    INNER JOIN dbo.Branches b ON e.BranchID = b.BranchID
                    INNER JOIN dbo.Jobs j ON e.JobID = j.JobID
                    INNER JOIN dbo.Categories c ON e.CategoryID = c.CategoryID
                    WHERE se.SheetID = @SheetID
                    ORDER BY se.RowOrder ASC",
                    new { SheetID = sheet.SheetID, EvalID = workspace.EvaluatorID })).ToList();

                foreach (var emp in empRows)
                {
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

                    emp.InitializeCriteria(criteria, scoreDict);
                }

                sheet.EmployeeRows = new ObservableCollection<WeeklyScoreRowViewModel>(empRows);
                workspace.Sheets.Add(sheet);
            }

            return workspace;
        }

        public async Task<(bool IsSuccess, string Message, int SavedRecordId)> SaveWeeklyEvaluationAsync(
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

            if (workspace.WeekNumber < 1 || workspace.WeekNumber > 4)
                return (false, "يرجى اختيار رقم الأسبوع (من الأسبوع 1 إلى الأسبوع 4).", 0);

            if (workspace.Sheets.Count == 0)
                return (false, "لا توجد أوراق تقييم داخل هذا القالب.", 0);

            foreach (var sheet in workspace.Sheets)
            {
                foreach (var emp in sheet.EmployeeRows)
                {
                    for (int i = 0; i < emp.CriteriaCount; i++)
                    {
                        decimal given = emp.GetScoreAt(i);
                        decimal max = emp.GetMaxScoreAt(i);
                        if (given < 0m || given > max)
                        {
                            return (false,
                                $"درجة الموظف ({emp.EvaluationName}) في الورقة ({sheet.SheetName}) هي ({given:0.##}) بينما الحد الأقصى المسموح هو ({max:0.##}).",
                                0);
                        }
                    }
                }
            }

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
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
                          AND er.EvalMonth = @Month
                          AND er.WeekNumber = @Week",
                        new
                        {
                            workspace.TemplateID,
                            Year = workspace.EvalYear,
                            Month = workspace.EvalMonth,
                            Week = workspace.WeekNumber
                        }, tx);

                    if (duplicateCount > 0)
                    {
                        await tx.RollbackAsync();
                        return (false,
                            $"تم إدخال تقييم الأسبوع ({workspace.WeekNumber}) لشهر ({workspace.EvalMonth}/{workspace.EvalYear}) لهذا القالب مسبقاً!\nلا يمكن تكرار نفس الأسبوع لنفس القالب في نفس الشهر؛ يمكنك فتحه للتعديل أو اختيار أسبوع آخر.",
                            0);
                    }

                    string insertRecordSql = @"
                        INSERT INTO dbo.EvaluationRecords (TemplateID, EvaluatorID, EvalYear, EvalMonth, WeekNumber, CreatedByUserID, CreatedAt)
                        VALUES (@TemplateID, @EvaluatorID, @EvalYear, @EvalMonth, @WeekNumber, @UserID, SYSDATETIME());
                        SELECT CAST(SCOPE_IDENTITY() AS INT);";

                    targetRecordId = await conn.ExecuteScalarAsync<int>(insertRecordSql, new
                    {
                        workspace.TemplateID,
                        workspace.EvaluatorID,
                        workspace.EvalYear,
                        workspace.EvalMonth,
                        workspace.WeekNumber,
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
                                GivenScore = emp.GetScoreAt(c),
                                SnapshotWeight = emp.EvaluatorWeight
                            }, tx);
                        }
                    }
                }

                await tx.CommitAsync();

                string arMonth = IncentiveCalculatorService.GetArabicMonthName(workspace.EvalMonth);
                string successMsg = isNew
                    ? $"تم حفظ واعتماد تقييم الأسبوع ({workspace.WeekNumber}) لشهر ({arMonth} {workspace.EvalYear} م) للمقيم ({workspace.EvaluatorName}) بنجاح."
                    : $"تم حفظ التعديلات على تقييم الأسبوع ({workspace.WeekNumber}) لشهر ({arMonth} {workspace.EvalYear} م) بنجاح.";

                return (true, successMsg, targetRecordId);
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return (false, $"حدث خطأ أثناء حفظ التقييم الأسبوعي: {ex.Message}", 0);
            }
        }

        public async Task<List<EvaluationRecordSummaryDto>> GetEvaluationHistoryLogsAsync(
            string evaluatorSearch = "",
            int? filterYear = null,
            int? filterMonth = null,
            int? filterWeek = null)
        {
            using var conn = new SqlConnection(_connectionString);
            string sql = @"
                SELECT er.RecordID, er.TemplateID, t.VersionNumber, er.EvaluatorID, ev.EvaluatorName,
                       er.EvalYear, er.EvalMonth, er.WeekNumber, er.CreatedAt, er.LastModifiedAt,
                       u.FullName AS CreatedByFullName,
                       (SELECT COUNT(DISTINCT es.EmployeeID) FROM dbo.EvaluationScores es WHERE es.RecordID = er.RecordID) AS EvaluatedEmployeesCount,
                       ISNULL((
                           SELECT AVG(EmpSum.TotalRaw)
                           FROM (
                               SELECT SUM(es2.GivenScore) AS TotalRaw
                               FROM dbo.EvaluationScores es2
                               WHERE es2.RecordID = er.RecordID
                               GROUP BY es2.SheetID, es2.EmployeeID
                           ) AS EmpSum
                       ), 0) AS AverageRawScore
                FROM dbo.EvaluationRecords er
                INNER JOIN dbo.Templates t ON er.TemplateID = t.TemplateID
                INNER JOIN dbo.Evaluators ev ON er.EvaluatorID = ev.EvaluatorID
                INNER JOIN dbo.Users u ON er.CreatedByUserID = u.UserID
                WHERE (@Search = '' OR ev.EvaluatorName LIKE @Pattern)
                  AND (@Year IS NULL OR er.EvalYear = @Year)
                  AND (@Month IS NULL OR er.EvalMonth = @Month)
                  AND (@Week IS NULL OR er.WeekNumber = @Week)
                ORDER BY er.EvalYear DESC, er.EvalMonth DESC, er.WeekNumber DESC, er.CreatedAt DESC";

            var list = await conn.QueryAsync<EvaluationRecordSummaryDto>(sql, new
            {
                Search = evaluatorSearch.Trim(),
                Pattern = $"%{evaluatorSearch.Trim()}%",
                Year = filterYear,
                Month = filterMonth,
                Week = filterWeek
            });

            return list.ToList();
        }

        public async Task<(bool IsSuccess, string Message)> DeleteEvaluationRecordAsync(int recordId, string permissionModuleCode = "HistoryLog")
        {
            if (!CheckPermission(permissionModuleCode, "delete", out string permErr))
                return (false, permErr);

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            await conn.ExecuteAsync("DELETE FROM dbo.EvaluationRecords WHERE RecordID = @ID", new { ID = recordId });
            return (true, "تم حذف سجل التقييم الأسبوعي ودرجاته بنجاح.");
        }
    }
}

