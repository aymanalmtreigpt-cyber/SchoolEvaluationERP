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
    public class TemplateService
    {
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

        /// <summary>
        /// يعرض جميع المقيمين بشكل فردي (مع اسم مجموعتهم) لإنشاء قالب مستقل لكل مقيم.
        /// </summary>
        public async Task<List<EvaluatorDto>> SearchEvaluatorsAsync(string query = "")
        {
            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);

            string sql = @"
                SELECT ev.EvaluatorID, ev.GroupID, eg.GroupName, ev.EvaluatorName,
                       (SELECT COUNT(1) FROM dbo.CategoryEvaluatorGroups ceg WHERE ceg.GroupID = ev.GroupID) AS AssignedCategoriesCount,
                       (SELECT COUNT(1) FROM dbo.EmployeeEvaluators ee INNER JOIN dbo.Employees e ON ee.EmployeeID = e.EmployeeID WHERE ee.EvaluatorID = ev.EvaluatorID AND e.IsActive = 1 AND e.IsContinuing = 1) AS AssignedEmployeesCount
                FROM dbo.Evaluators ev
                INNER JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE ev.IsActive = 1 AND eg.IsActive = 1
                  AND (@Q = '' OR ev.EvaluatorName LIKE @Pattern OR eg.GroupName LIKE @Pattern)
                ORDER BY eg.GroupName ASC, ev.EvaluatorName ASC";

            var list = await conn.QueryAsync<EvaluatorDto>(sql, new
            {
                Q = (query ?? string.Empty).Trim(),
                Pattern = $"%{(query ?? string.Empty).Trim()}%"
            });
            return list.ToList();
        }

        public async Task<bool> HasAnyActiveCategoriesAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);
            int count = await conn.ExecuteScalarAsync<int>("SELECT COUNT(1) FROM dbo.Categories WHERE IsActive = 1");
            return count > 0;
        }

        /// <summary>
        /// يجلب فقط الفئات المرتبطة بمجموعة المقيم المحدد (بدون الفرع).
        /// </summary>
        public async Task<List<TemplateCategoryOptionDto>> GetCategoriesForEvaluatorAsync(int evaluatorId)
        {
            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);

            string sql = @"
                SELECT c.CategoryID,
                       c.CategoryName,
                       (SELECT COUNT(1)
                        FROM dbo.Employees e
                        INNER JOIN dbo.EmployeeEvaluators ee ON ee.EmployeeID = e.EmployeeID
                        WHERE e.IsActive = 1
                          AND e.IsContinuing = 1
                          AND e.CategoryID = c.CategoryID
                          AND ee.EvaluatorID = @EvalID) AS AssignedEmployeesCount
                FROM dbo.Categories c
                INNER JOIN dbo.CategoryEvaluatorGroups ceg ON ceg.CategoryID = c.CategoryID
                INNER JOIN dbo.Evaluators ev ON ev.GroupID = ceg.GroupID
                WHERE c.IsActive = 1 AND ev.EvaluatorID = @EvalID
                ORDER BY c.CategoryID ASC";

            var list = await conn.QueryAsync<TemplateCategoryOptionDto>(sql, new { EvalID = evaluatorId });
            return list.ToList();
        }

        /// <summary>
        /// يجلب خيارات الفروع لورقة التقييم: خيار (جميع الفروع / كلاهما) + الفروع المنشأة مع عدد الموظفين المستمرين المسندين لهذا المقيم في الفئة المحددة.
        /// </summary>
        public async Task<List<TemplateBranchOptionDto>> GetBranchOptionsForEvaluatorCategoryAsync(int evaluatorId, int categoryId)
        {
            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);

            var branches = (await conn.QueryAsync<TemplateBranchOptionDto>(@"
                SELECT b.BranchID,
                       b.BranchName,
                       (SELECT COUNT(1)
                        FROM dbo.Employees e
                        INNER JOIN dbo.EmployeeEvaluators ee ON ee.EmployeeID = e.EmployeeID
                        WHERE e.IsActive = 1
                          AND e.IsContinuing = 1
                          AND e.CategoryID = @CatID
                          AND e.BranchID = b.BranchID
                          AND ee.EvaluatorID = @EvalID) AS AssignedEmployeesCount
                FROM dbo.Branches b
                WHERE b.IsActive = 1
                ORDER BY b.BranchID ASC",
                new { CatID = categoryId, EvalID = evaluatorId })).ToList();

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

        /// <summary>
        /// يجلب الموظفين المستمرين المسندين لهذا المقيم في الفئة المحددة والفرع المحدد (أو جميع الفروع عند TargetBranchID == null).
        /// </summary>
        public async Task<List<SheetEmployeeRowModel>> GetEligibleEmployeesForSheetAsync(
            int? targetCategoryId,
            int? targetBranchId,
            int evaluatorId)
        {
            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);

            if (targetCategoryId == null || targetCategoryId <= 0)
                return new List<SheetEmployeeRowModel>();

            int? effectiveBranchId = (targetBranchId.HasValue && targetBranchId.Value > 0) ? targetBranchId.Value : null;

            string sql = @"
                SELECT e.EmployeeID, e.EvaluationName, e.SystemName,
                       e.BranchID, b.BranchName,
                       j.JobTitle, jt.JobTypeName,
                       c.CategoryName,
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
                       ), 0) AS EvaluatorWeightForEmployee
                FROM dbo.Employees e
                INNER JOIN dbo.EmployeeEvaluators ee ON ee.EmployeeID = e.EmployeeID AND ee.EvaluatorID = @EvalID
                INNER JOIN dbo.Categories c ON e.CategoryID = c.CategoryID
                INNER JOIN dbo.Branches b ON e.BranchID = b.BranchID
                INNER JOIN dbo.Jobs j ON e.JobID = j.JobID
                INNER JOIN dbo.JobTypes jt ON e.JobTypeID = jt.JobTypeID
                WHERE e.IsActive = 1
                  AND e.IsContinuing = 1
                  AND e.CategoryID = @CatID
                  AND (@BranchID IS NULL OR e.BranchID = @BranchID)
                ORDER BY b.BranchName ASC, e.EvaluationName ASC";

            var list = await conn.QueryAsync<SheetEmployeeRowModel>(sql, new
            {
                CatID = targetCategoryId.Value,
                BranchID = effectiveBranchId,
                EvalID = evaluatorId
            });
            return list.ToList();
        }

        public async Task<List<TemplateSummaryDto>> GetCurrentTemplatesAsync(string evaluatorSearch = "")
        {
            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);

            string sql = @"
                SELECT t.TemplateID, t.TemplateGroupGuid, t.EvaluatorID, ev.EvaluatorName, eg.GroupName,
                       t.VersionNumber, t.CreatedAt,
                       (SELECT COUNT(1) FROM dbo.TemplateSheets ts WHERE ts.TemplateID = t.TemplateID) AS SheetsCount,
                       (SELECT COUNT(1)
                        FROM dbo.SheetEmployees se
                        INNER JOIN dbo.TemplateSheets ts2 ON se.SheetID = ts2.SheetID
                        INNER JOIN dbo.Employees e ON se.EmployeeID = e.EmployeeID
                        WHERE ts2.TemplateID = t.TemplateID AND e.IsActive = 1 AND e.IsContinuing = 1) AS TotalEmployeesCount,
                       (SELECT COUNT(1) FROM dbo.EvaluationRecords er WHERE er.TemplateID = t.TemplateID) AS LinkedEvaluationsCount
                FROM dbo.Templates t
                INNER JOIN dbo.Evaluators ev ON t.EvaluatorID = ev.EvaluatorID
                LEFT JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE t.IsCurrentVersion = 1 AND t.IsDeleted = 0
                  AND (@Search = '' OR ev.EvaluatorName LIKE @Pattern OR eg.GroupName LIKE @Pattern)
                ORDER BY t.CreatedAt DESC";

            var list = (await conn.QueryAsync<TemplateSummaryDto>(sql, new
            {
                Search = (evaluatorSearch ?? string.Empty).Trim(),
                Pattern = $"%{(evaluatorSearch ?? string.Empty).Trim()}%"
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
            await EnsureSchemaUpToDateAsync(conn);

            var header = await conn.QueryFirstOrDefaultAsync<dynamic>(@"
                SELECT t.TemplateID, t.TemplateGroupGuid, t.EvaluatorID, ev.EvaluatorName, ISNULL(eg.GroupName, N'') AS GroupName, t.VersionNumber, t.CreatedAt
                FROM dbo.Templates t
                INNER JOIN dbo.Evaluators ev ON t.EvaluatorID = ev.EvaluatorID
                LEFT JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE t.TemplateID = @ID", new { ID = templateId });

            if (header == null) return null;

            var model = new TemplateFullModel
            {
                TemplateID = header.TemplateID,
                TemplateGroupGuid = header.TemplateGroupGuid,
                EvaluatorID = header.EvaluatorID,
                EvaluatorName = header.EvaluatorName,
                GroupName = header.GroupName,
                VersionNumber = header.VersionNumber,
                CreatedAt = header.CreatedAt
            };

            var sheets = (await conn.QueryAsync<TemplateSheetModel>(@"
                SELECT ts.SheetID,
                       ts.SheetName,
                       ts.TargetCategoryID,
                       ISNULL(c.CategoryName, ts.TargetCategoryName) AS TargetCategoryName,
                       ts.TargetBranchID,
                       ISNULL(b.BranchName, N'جميع الفروع (كلاهما)') AS TargetBranchName,
                       ts.SortOrder
                FROM dbo.TemplateSheets ts
                LEFT JOIN dbo.Categories c ON ts.TargetCategoryID = c.CategoryID
                LEFT JOIN dbo.Branches b ON ts.TargetBranchID = b.BranchID
                WHERE ts.TemplateID = @ID
                ORDER BY ts.SortOrder ASC", new { ID = templateId })).ToList();

            foreach (var sheet in sheets)
            {
                var criteria = await conn.QueryAsync<CriteriaColumnModel>(@"
                    SELECT CriteriaID, CriteriaTitle, MaxScore, ColumnOrder
                    FROM dbo.SheetCriteria
                    WHERE SheetID = @SheetID
                    ORDER BY ColumnOrder ASC", new { SheetID = sheet.SheetID });

                sheet.CriteriaColumns = new ObservableCollection<CriteriaColumnModel>(criteria);

                // يظهر في معاينة وتصميم القالب الموظفون المستمرون فقط؛ أما المنقطع فيُخفى تلقائياً
                var empRows = await conn.QueryAsync<SheetEmployeeRowModel>(@"
                    SELECT se.EmployeeID, e.EvaluationName, e.SystemName,
                           e.BranchID, b.BranchName,
                           j.JobTitle, jt.JobTypeName,
                           c.CategoryName,
                           e.HasIncentive, e.IsContinuing,
                           se.RowOrder,
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
                           ), 0) AS EvaluatorWeightForEmployee
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
                    new { SheetID = sheet.SheetID, EvalID = model.EvaluatorID });

                var orderedList = empRows.ToList();
                for (int i = 0; i < orderedList.Count; i++)
                    orderedList[i].RowOrder = i + 1;

                sheet.EmployeeRows = new ObservableCollection<SheetEmployeeRowModel>(orderedList);
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
                        ? $"نظراً لوجود تقييمات شهرية سابقة مرتبطة بهذا القالب، تم حفظ التعديلات كـ (نسخة جديدة v{newVersionNumber}) مع بقاء التقييمات السابقة محفوظة على النسخة القديمة دون أي تأثر."
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
            await EnsureSchemaUpToDateAsync(conn);

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
    
        public async Task<List<EvaluatorEmployeesSheetExportDto>> GetAllEvaluatorsWithAssignedEmployeesAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            await EnsureSchemaUpToDateAsync(conn);

            var evaluators = (await conn.QueryAsync<EvaluatorEmployeesSheetExportDto>(@"
                SELECT ev.EvaluatorID,
                       ev.EvaluatorName,
                       ev.GroupID,
                       ISNULL(eg.GroupName, N'') AS GroupName
                FROM dbo.Evaluators ev
                INNER JOIN dbo.EvaluatorGroups eg ON ev.GroupID = eg.GroupID
                WHERE ev.IsActive = 1 AND eg.IsActive = 1
                ORDER BY eg.GroupID ASC, ev.EvaluatorID ASC")).ToList();

            string empSql = @"
                SELECT e.EmployeeID,
                       e.EvaluationName,
                       e.SystemName,
                       b.BranchName,
                       c.CategoryName,
                       j.JobTitle,
                       jt.JobTypeName,
                       e.HasIncentive,
                       e.IsContinuing
                FROM dbo.Employees e
                INNER JOIN dbo.Branches b ON e.BranchID = b.BranchID
                INNER JOIN dbo.Categories c ON e.CategoryID = c.CategoryID
                INNER JOIN dbo.Jobs j ON e.JobID = j.JobID
                INNER JOIN dbo.JobTypes jt ON e.JobTypeID = jt.JobTypeID
                WHERE e.IsActive = 1
                  AND (
                      EXISTS (
                          SELECT 1 FROM dbo.EmployeeEvaluators ee
                          WHERE ee.EmployeeID = e.EmployeeID AND ee.EvaluatorID = @EvalID
                      )
                      OR EXISTS (
                          SELECT 1 FROM dbo.CategoryEvaluatorGroups ceg
                          INNER JOIN dbo.Evaluators ev_single ON ev_single.GroupID = ceg.GroupID
                          WHERE ceg.CategoryID = e.CategoryID
                            AND ev_single.EvaluatorID = @EvalID
                            AND (SELECT COUNT(1) FROM dbo.Evaluators ev_cnt WHERE ev_cnt.GroupID = ceg.GroupID AND ev_cnt.IsActive = 1) = 1
                      )
                      OR EXISTS (
                          SELECT 1 FROM dbo.SheetEmployees se
                          INNER JOIN dbo.TemplateSheets ts ON se.SheetID = ts.SheetID
                          INNER JOIN dbo.Templates t ON ts.TemplateID = t.TemplateID
                          WHERE se.EmployeeID = e.EmployeeID
                            AND t.EvaluatorID = @EvalID
                            AND t.IsCurrentVersion = 1
                            AND t.IsDeleted = 0
                      )
                  )
                ORDER BY b.BranchName ASC, c.CategoryID ASC, e.EvaluationName ASC";

            foreach (var eval in evaluators)
            {
                var emps = (await conn.QueryAsync<EvaluatorEmployeeExportRowDto>(empSql, new { EvalID = eval.EvaluatorID })).ToList();
                for (int i = 0; i < emps.Count; i++)
                {
                    emps[i].RowNumber = i + 1;
                }
                eval.Employees = emps;
            }

            return evaluators;
        }
    }
}
