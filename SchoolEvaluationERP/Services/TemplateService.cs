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
    public class TemplateService
    {
        private readonly string _connectionString;

        public TemplateService() : this(AppConfigService.ConnectionString) { }

        public TemplateService(string connectionString)
        {
            _connectionString = connectionString;
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

        public async Task<List<EvaluatorDto>> SearchEvaluatorsAsync(string query = "")
        {
            using var conn = new SqlConnection(_connectionString);
            string sql = @"
                SELECT ev.EvaluatorID, ev.EvaluatorName,
                       (SELECT COUNT(1) FROM dbo.CategoryEvaluators ce WHERE ce.EvaluatorID = ev.EvaluatorID) AS AssignedCategoriesCount
                FROM dbo.Evaluators ev
                WHERE ev.IsActive = 1
                  AND (@Q = '' OR ev.EvaluatorName LIKE @Pattern)
                ORDER BY ev.EvaluatorName";

            var list = await conn.QueryAsync<EvaluatorDto>(sql, new
            {
                Q = query.Trim(),
                Pattern = $"%{query.Trim()}%"
            });
            return list.ToList();
        }

        public async Task<List<string>> GetDistinctCategoryNamesForEvaluatorAsync(int evaluatorId)
        {
            using var conn = new SqlConnection(_connectionString);
            string sql = @"
                SELECT DISTINCT c.CategoryName,
                       CASE WHEN EXISTS (
                           SELECT 1 FROM dbo.CategoryEvaluators ce 
                           WHERE ce.CategoryID = c.CategoryID AND ce.EvaluatorID = @EvalID
                       ) THEN 0 ELSE 1 END AS PriorityOrder
                FROM dbo.Categories c
                WHERE c.IsActive = 1
                ORDER BY PriorityOrder ASC, c.CategoryName ASC";

            var rows = await conn.QueryAsync<dynamic>(sql, new { EvalID = evaluatorId });
            return rows.Select(r => (string)r.CategoryName).ToList();
        }

        public async Task<List<SheetEmployeeRowModel>> GetEmployeesByCategoryNameAsync(string targetCategoryName, int evaluatorId)
        {
            using var conn = new SqlConnection(_connectionString);
            string sql = @"
                SELECT e.EmployeeID, e.EvaluationName, e.SystemName,
                       b.BranchName, j.JobTitle, c.CategoryName,
                       ISNULL((
                           SELECT TOP 1 ce.WeightScore 
                           FROM dbo.CategoryEvaluators ce 
                           WHERE ce.CategoryID = e.CategoryID AND ce.EvaluatorID = @EvalID
                       ), 0) AS EvaluatorWeightForEmployee
                FROM dbo.Employees e
                INNER JOIN dbo.Branches b ON e.BranchID = b.BranchID
                INNER JOIN dbo.Jobs j ON e.JobID = j.JobID
                INNER JOIN dbo.Categories c ON e.CategoryID = c.CategoryID
                WHERE e.IsActive = 1
                  AND c.CategoryName = @CatName
                ORDER BY e.EvaluationName ASC";

            var list = await conn.QueryAsync<SheetEmployeeRowModel>(sql, new
            {
                CatName = targetCategoryName.Trim(),
                EvalID = evaluatorId
            });
            return list.ToList();
        }

        public async Task<List<TemplateSummaryDto>> GetCurrentTemplatesAsync(string evaluatorSearch = "")
        {
            using var conn = new SqlConnection(_connectionString);
            string sql = @"
                SELECT t.TemplateID, t.TemplateGroupGuid, t.EvaluatorID, ev.EvaluatorName,
                       t.VersionNumber, t.CreatedAt,
                       (SELECT COUNT(1) FROM dbo.TemplateSheets ts WHERE ts.TemplateID = t.TemplateID) AS SheetsCount,
                       (SELECT COUNT(1) FROM dbo.SheetEmployees se INNER JOIN dbo.TemplateSheets ts2 ON se.SheetID = ts2.SheetID WHERE ts2.TemplateID = t.TemplateID) AS TotalEmployeesCount,
                       (SELECT COUNT(1) FROM dbo.EvaluationRecords er WHERE er.TemplateID = t.TemplateID) AS LinkedEvaluationsCount
                FROM dbo.Templates t
                INNER JOIN dbo.Evaluators ev ON t.EvaluatorID = ev.EvaluatorID
                WHERE t.IsCurrentVersion = 1 AND t.IsDeleted = 0
                  AND (@Search = '' OR ev.EvaluatorName LIKE @Pattern)
                ORDER BY t.CreatedAt DESC";

            var list = (await conn.QueryAsync<TemplateSummaryDto>(sql, new
            {
                Search = evaluatorSearch.Trim(),
                Pattern = $"%{evaluatorSearch.Trim()}%"
            })).ToList();

            var sheets = await conn.QueryAsync<dynamic>(@"
                SELECT ts.TemplateID, ts.SheetName, ts.TargetCategoryName
                FROM dbo.TemplateSheets ts
                INNER JOIN dbo.Templates t ON ts.TemplateID = t.TemplateID
                WHERE t.IsCurrentVersion = 1 AND t.IsDeleted = 0
                ORDER BY ts.SortOrder ASC");

            var sheetsGroup = sheets.GroupBy(x => (int)x.TemplateID)
                                    .ToDictionary(g => g.Key, g => string.Join("  |  ", g.Select(s => $"{s.SheetName} ({s.TargetCategoryName})")));

            foreach (var item in list)
            {
                item.SheetsSummary = sheetsGroup.TryGetValue(item.TemplateID, out var sum) ? sum : "بدون أوراق";
            }

            return list;
        }

        public async Task<TemplateFullModel?> GetTemplateFullDetailsAsync(int templateId)
        {
            using var conn = new SqlConnection(_connectionString);
            var header = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT t.TemplateID, t.TemplateGroupGuid, t.EvaluatorID, ev.EvaluatorName, t.VersionNumber, t.CreatedAt
                FROM dbo.Templates t
                INNER JOIN dbo.Evaluators ev ON t.EvaluatorID = ev.EvaluatorID
                WHERE t.TemplateID = @ID", new { ID = templateId });

            if (header == null) return null;

            var model = new TemplateFullModel
            {
                TemplateID = header.TemplateID,
                TemplateGroupGuid = header.TemplateGroupGuid,
                EvaluatorID = header.EvaluatorID,
                EvaluatorName = header.EvaluatorName,
                VersionNumber = header.VersionNumber,
                CreatedAt = header.CreatedAt
            };

            var sheets = (await conn.QueryAsync<TemplateSheetModel>(@"
                SELECT SheetID, SheetName, TargetCategoryName, SortOrder
                FROM dbo.TemplateSheets
                WHERE TemplateID = @ID
                ORDER BY SortOrder ASC", new { ID = templateId })).ToList();

            foreach (var sheet in sheets)
            {
                var criteria = await conn.QueryAsync<CriteriaColumnModel>(@"
                    SELECT CriteriaID, CriteriaTitle, MaxScore, ColumnOrder
                    FROM dbo.SheetCriteria
                    WHERE SheetID = @SheetID
                    ORDER BY ColumnOrder ASC", new { SheetID = sheet.SheetID });

                sheet.CriteriaColumns = new ObservableCollection<CriteriaColumnModel>(criteria);

                var empRows = await conn.QueryAsync<SheetEmployeeRowModel>(@"
                    SELECT se.EmployeeID, e.EvaluationName, e.SystemName,
                           b.BranchName, j.JobTitle, c.CategoryName, se.RowOrder,
                           ISNULL((
                               SELECT TOP 1 ce.WeightScore 
                               FROM dbo.CategoryEvaluators ce 
                               WHERE ce.CategoryID = e.CategoryID AND ce.EvaluatorID = @EvalID
                           ), 0) AS EvaluatorWeightForEmployee
                    FROM dbo.SheetEmployees se
                    INNER JOIN dbo.Employees e ON se.EmployeeID = e.EmployeeID
                    INNER JOIN dbo.Branches b ON e.BranchID = b.BranchID
                    INNER JOIN dbo.Jobs j ON e.JobID = j.JobID
                    INNER JOIN dbo.Categories c ON e.CategoryID = c.CategoryID
                    WHERE se.SheetID = @SheetID
                    ORDER BY se.RowOrder ASC",
                    new { SheetID = sheet.SheetID, EvalID = model.EvaluatorID });

                sheet.EmployeeRows = new ObservableCollection<SheetEmployeeRowModel>(empRows);
                model.Sheets.Add(sheet);
            }

            return model;
        }

        public async Task<(bool IsSuccess, string Message, int SavedTemplateId)> SaveTemplateAsync(TemplateFullModel model)
        {
            bool isNew = model.TemplateID == 0;
            if (!CheckPermission(isNew ? "insert" : "update", out string permErr))
                return (false, permErr, 0);

            if (model.EvaluatorID <= 0)
                return (false, "يرجى اختيار المقيم صاحب القالب.", 0);

            if (model.Sheets.Count == 0)
                return (false, "يجب أن يحتوي القالب على ورقة تقييم واحدة على الأقل.", 0);

            foreach (var sheet in model.Sheets)
            {
                if (string.IsNullOrWhiteSpace(sheet.SheetName))
                    return (false, "يرجى التأكد من تسمية جميع أوراق التقييم داخل القالب.", 0);

                if (sheet.CriteriaColumns.Count == 0)
                    return (false, $"الورقة ({sheet.SheetName}) لا تحتوي على أي أعمدة لمبادئ التقييم.", 0);

                decimal totalScore = sheet.CriteriaColumns.Sum(c => c.MaxScore);
                if (totalScore != 100m)
                {
                    return (false,
                        $"شرط النظام في الورقة ({sheet.SheetName}):\nيجب أن يكون مجموع درجات أعمدة مبادئ التقييم يساوي 100 درجة بالضبط.\nالمجموع الحالي في هذه الورقة هو ({totalScore:0.##}) درجة.",
                        0);
                }

                if (sheet.EmployeeRows.Count == 0)
                {
                    return (false, $"الورقة ({sheet.SheetName}) لا تحتوي على أي موظفين. يرجى إضافة موظف واحد على الأقل.", 0);
                }
            }

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();
            using var tx = conn.BeginTransaction();

            try
            {
                int targetTemplateId = model.TemplateID;
                int newVersionNumber = model.VersionNumber;
                bool createdNewVersion = false;

                if (isNew)
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
                }
                else
                {
                    int evalRecordsCount = await conn.ExecuteScalarAsync<int>(
                        "SELECT COUNT(1) FROM dbo.EvaluationRecords WHERE TemplateID = @ID",
                        new { ID = model.TemplateID }, tx);

                    if (evalRecordsCount > 0)
                    {
                        createdNewVersion = true;
                        await conn.ExecuteAsync(
                            "UPDATE dbo.Templates SET IsCurrentVersion = 0 WHERE TemplateID = @ID",
                            new { ID = model.TemplateID }, tx);

                        newVersionNumber = model.VersionNumber + 1;
                        string insertNewVerSql = @"
                            INSERT INTO dbo.Templates (TemplateGroupGuid, EvaluatorID, VersionNumber, IsCurrentVersion, IsDeleted, CreatedByUserID)
                            VALUES (@GroupGuid, @EvalID, @Ver, 1, 0, @UserID);
                            SELECT CAST(SCOPE_IDENTITY() AS INT);";

                        targetTemplateId = await conn.ExecuteScalarAsync<int>(insertNewVerSql, new
                        {
                            GroupGuid = model.TemplateGroupGuid,
                            EvalID = model.EvaluatorID,
                            Ver = newVersionNumber,
                            UserID = AuthService.CurrentSession?.UserID
                        }, tx);
                    }
                    else
                    {
                        await conn.ExecuteAsync(
                            "DELETE FROM dbo.TemplateSheets WHERE TemplateID = @ID",
                            new { ID = targetTemplateId }, tx);
                    }
                }

                int sheetOrder = 1;
                foreach (var sheet in model.Sheets)
                {
                    string insertSheetSql = @"
                        INSERT INTO dbo.TemplateSheets (TemplateID, SheetName, TargetCategoryName, SortOrder)
                        VALUES (@TemplateID, @SheetName, @TargetCategoryName, @SortOrder);
                        SELECT CAST(SCOPE_IDENTITY() AS INT);";

                    int newSheetId = await conn.ExecuteScalarAsync<int>(insertSheetSql, new
                    {
                        TemplateID = targetTemplateId,
                        SheetName = sheet.SheetName.Trim(),
                        TargetCategoryName = sheet.TargetCategoryName.Trim(),
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
                                col.MaxScore,
                                ColOrder = colOrder++
                            }, tx);
                    }

                    int rowOrder = 1;
                    foreach (var emp in sheet.EmployeeRows)
                    {
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

                await tx.CommitAsync();

                string msg = isNew
                    ? "تم إنشاء وحفظ قالب التقييم بنجاح."
                    : createdNewVersion
                        ? $"نظراً لوجود تقييمات سابقة مرتبطة بهذا القالب، تم حفظ التعديلات كـ (نسخة جديدة v{newVersionNumber}) مع بقاء التقييمات السابقة محفوظة على النسخة القديمة دون أي تأثر."
                        : "تم حفظ التعديلات على قالب التقييم بنجاح.";

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
            await conn.OpenAsync();

            int evalCount = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.EvaluationRecords WHERE TemplateID = @ID",
                new { ID = templateId });

            if (evalCount > 0)
            {
                await conn.ExecuteAsync("UPDATE dbo.Templates SET IsDeleted = 1, IsCurrentVersion = 0 WHERE TemplateID = @ID", new { ID = templateId });
                return (true, "تم حذف القالب من القائمة النشطة مع الاحتفاظ بأرشيف التقييمات السابقة المرتبطة به.");
            }
            else
            {
                await conn.ExecuteAsync("DELETE FROM dbo.Templates WHERE TemplateID = @ID", new { ID = templateId });
                return (true, "تم حذف قالب التقييم نهائياً بنجاح.");
            }
        }
    }
}
