using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Dapper;
using Microsoft.Data.SqlClient;
using SchoolEvaluationERP.Models;

namespace SchoolEvaluationERP.Services
{
    public class MasterDataService
    {
        private readonly string _connectionString;

        public MasterDataService() : this(AppConfigService.ConnectionString) { }

        public MasterDataService(string connectionString)
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
            if (!AuthService.CurrentSession.HasPermission("MasterData", action))
            {
                string arAction = action switch
                {
                    "insert" => "إضافة وكتابة",
                    "update" => "تعديل",
                    "delete" => "حذف",
                    _ => "قراءة"
                };
                errorMsg = $"عذراً، لا تملك صلاحية ({arAction}) في شاشة الأساسيات.";
                return false;
            }
            return true;
        }

        // =========================================================================
        // 1. إدارة الفروع (Branches)
        // =========================================================================
        public async Task<List<BranchDto>> GetBranchesAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            string sql = @"
                SELECT b.BranchID, b.BranchName,
                       (SELECT COUNT(1) FROM dbo.Categories c WHERE c.BranchID = b.BranchID AND c.IsActive = 1) AS CategoriesCount,
                       (SELECT COUNT(1) FROM dbo.Employees e WHERE e.BranchID = b.BranchID AND e.IsActive = 1) AS EmployeesCount
                FROM dbo.Branches b
                WHERE b.IsActive = 1
                ORDER BY b.BranchID ASC";
            return (await conn.QueryAsync<BranchDto>(sql)).ToList();
        }

        public async Task<(bool IsSuccess, string Message)> SaveBranchAsync(int branchId, string branchName)
        {
            string cleanName = branchName.Trim();
            if (string.IsNullOrWhiteSpace(cleanName))
                return (false, "يرجى إدخال اسم الفرع.");

            if (!CheckPermission(branchId == 0 ? "insert" : "update", out string permErr))
                return (false, permErr);

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            int dup = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.Branches WHERE BranchName = @Name AND BranchID <> @ID",
                new { Name = cleanName, ID = branchId });

            if (dup > 0)
                return (false, $"الفرع ({cleanName}) مسجل مسبقاً.");

            if (branchId == 0)
            {
                await conn.ExecuteAsync("INSERT INTO dbo.Branches (BranchName, IsActive) VALUES (@Name, 1)", new { Name = cleanName });
                return (true, "تمت إضافة الفرع بنجاح.");
            }
            else
            {
                await conn.ExecuteAsync("UPDATE dbo.Branches SET BranchName = @Name WHERE BranchID = @ID", new { Name = cleanName, ID = branchId });
                return (true, "تم تعديل اسم الفرع بنجاح.");
            }
        }

        public async Task<(bool IsSuccess, string Message)> DeleteBranchAsync(int branchId)
        {
            if (!CheckPermission("delete", out string permErr))
                return (false, permErr);

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            try
            {
                await conn.ExecuteAsync("DELETE FROM dbo.Branches WHERE BranchID = @ID", new { ID = branchId });
                return (true, "تم حذف الفرع بنجاح.");
            }
            catch (SqlException)
            {
                return (false, "لا يمكن حذف هذا الفرع لوجود فئات أو موظفين مرتبطين به. قم بنقلهم أو حذفهم أولاً.");
            }
        }

        // =========================================================================
        // 2. إدارة الوظائف (Jobs)
        // =========================================================================
        public async Task<List<JobDto>> GetJobsAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            string sql = @"
                SELECT j.JobID, j.JobTitle,
                       (SELECT COUNT(1) FROM dbo.Employees e WHERE e.JobID = j.JobID AND e.IsActive = 1) AS EmployeesCount
                FROM dbo.Jobs j
                WHERE j.IsActive = 1
                ORDER BY j.JobID ASC";
            return (await conn.QueryAsync<JobDto>(sql)).ToList();
        }

        public async Task<(bool IsSuccess, string Message)> SaveJobAsync(int jobId, string jobTitle)
        {
            string cleanTitle = jobTitle.Trim();
            if (string.IsNullOrWhiteSpace(cleanTitle))
                return (false, "يرجى إدخال المسمى الوظيفي.");

            if (!CheckPermission(jobId == 0 ? "insert" : "update", out string permErr))
                return (false, permErr);

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            int dup = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.Jobs WHERE JobTitle = @Title AND JobID <> @ID",
                new { Title = cleanTitle, ID = jobId });

            if (dup > 0)
                return (false, $"الوظيفة ({cleanTitle}) مسجلة مسبقاً.");

            if (jobId == 0)
            {
                await conn.ExecuteAsync("INSERT INTO dbo.Jobs (JobTitle, IsActive) VALUES (@Title, 1)", new { Title = cleanTitle });
                return (true, "تمت إضافة الوظيفة بنجاح.");
            }
            else
            {
                await conn.ExecuteAsync("UPDATE dbo.Jobs SET JobTitle = @Title WHERE JobID = @ID", new { Title = cleanTitle, ID = jobId });
                return (true, "تم تعديل المسمى الوظيفي بنجاح.");
            }
        }

        public async Task<(bool IsSuccess, string Message)> DeleteJobAsync(int jobId)
        {
            if (!CheckPermission("delete", out string permErr))
                return (false, permErr);

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            try
            {
                await conn.ExecuteAsync("DELETE FROM dbo.Jobs WHERE JobID = @ID", new { ID = jobId });
                return (true, "تم حذف الوظيفة بنجاح.");
            }
            catch (SqlException)
            {
                return (false, "لا يمكن حذف هذه الوظيفة لوجود موظفين مسجلين عليها حالياً.");
            }
        }

        // =========================================================================
        // 3. إدارة المقيمين (Evaluators)
        // =========================================================================
        public async Task<List<EvaluatorDto>> GetEvaluatorsAsync()
        {
            using var conn = new SqlConnection(_connectionString);
            string sql = @"
                SELECT ev.EvaluatorID, ev.EvaluatorName,
                       (SELECT COUNT(1) FROM dbo.CategoryEvaluators ce WHERE ce.EvaluatorID = ev.EvaluatorID) AS AssignedCategoriesCount
                FROM dbo.Evaluators ev
                WHERE ev.IsActive = 1
                ORDER BY ev.EvaluatorID ASC";
            return (await conn.QueryAsync<EvaluatorDto>(sql)).ToList();
        }

        public async Task<(bool IsSuccess, string Message)> SaveEvaluatorAsync(int evaluatorId, string evaluatorName)
        {
            string cleanName = evaluatorName.Trim();
            if (string.IsNullOrWhiteSpace(cleanName))
                return (false, "يرجى إدخال اسم المقيم.");

            if (!CheckPermission(evaluatorId == 0 ? "insert" : "update", out string permErr))
                return (false, permErr);

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            int dup = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.Evaluators WHERE EvaluatorName = @Name AND EvaluatorID <> @ID",
                new { Name = cleanName, ID = evaluatorId });

            if (dup > 0)
                return (false, $"المقيم ({cleanName}) مسجل مسبقاً.");

            if (evaluatorId == 0)
            {
                await conn.ExecuteAsync("INSERT INTO dbo.Evaluators (EvaluatorName, IsActive) VALUES (@Name, 1)", new { Name = cleanName });
                return (true, "تمت إضافة المقيم بنجاح.");
            }
            else
            {
                await conn.ExecuteAsync("UPDATE dbo.Evaluators SET EvaluatorName = @Name WHERE EvaluatorID = @ID", new { Name = cleanName, ID = evaluatorId });
                return (true, "تم تعديل اسم المقيم بنجاح.");
            }
        }

        public async Task<(bool IsSuccess, string Message)> DeleteEvaluatorAsync(int evaluatorId)
        {
            if (!CheckPermission("delete", out string permErr))
                return (false, permErr);

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            try
            {
                await conn.ExecuteAsync("DELETE FROM dbo.Evaluators WHERE EvaluatorID = @ID", new { ID = evaluatorId });
                return (true, "تم حذف المقيم بنجاح.");
            }
            catch (SqlException)
            {
                return (false, "لا يمكن حذف هذا المقيم لارتباطه بفئات أو قوالب تقييم حالية.");
            }
        }

        // =========================================================================
        // 4. إدارة الفئات وربطها بالفرع والمقيمين بشرط (المجموع = 100 درجة)
        // =========================================================================
        public async Task<List<CategoryDto>> GetCategoriesAsync(int? filterBranchId = null)
        {
            using var conn = new SqlConnection(_connectionString);
            string sql = @"
                SELECT c.CategoryID, c.CategoryName, c.BranchID, b.BranchName,
                       ISNULL((SELECT SUM(ce.WeightScore) FROM dbo.CategoryEvaluators ce WHERE ce.CategoryID = c.CategoryID), 0) AS TotalWeights,
                       (SELECT COUNT(1) FROM dbo.Employees e WHERE e.CategoryID = c.CategoryID AND e.IsActive = 1) AS EmployeesCount
                FROM dbo.Categories c
                INNER JOIN dbo.Branches b ON c.BranchID = b.BranchID
                WHERE c.IsActive = 1 AND (@BranchID IS NULL OR c.BranchID = @BranchID)
                ORDER BY b.BranchName, c.CategoryName";

            var categories = (await conn.QueryAsync<CategoryDto>(sql, new { BranchID = filterBranchId })).ToList();

            var evalLinks = await conn.QueryAsync<dynamic>(@"
                SELECT ce.CategoryID, ev.EvaluatorName, ce.WeightScore
                FROM dbo.CategoryEvaluators ce
                INNER JOIN dbo.Evaluators ev ON ce.EvaluatorID = ev.EvaluatorID
                ORDER BY ce.WeightScore DESC");

            var grouped = evalLinks.GroupBy(x => (int)x.CategoryID)
                                   .ToDictionary(g => g.Key, g => string.Join("  |  ", g.Select(i => $"{i.EvaluatorName}: {((decimal)i.WeightScore):0.##} درجة")));

            foreach (var cat in categories)
            {
                cat.EvaluatorsSummary = grouped.TryGetValue(cat.CategoryID, out var summary)
                    ? summary
                    : "غير محدد";
            }

            return categories;
        }

        public async Task<List<CategoryEvaluatorSelectRow>> GetCategoryEvaluatorMatrixAsync(int categoryId)
        {
            using var conn = new SqlConnection(_connectionString);
            var evaluators = await GetEvaluatorsAsync();
            var existingWeights = new Dictionary<int, decimal>();

            if (categoryId > 0)
            {
                var rows = await conn.QueryAsync<dynamic>(
                    "SELECT EvaluatorID, WeightScore FROM dbo.CategoryEvaluators WHERE CategoryID = @CatID",
                    new { CatID = categoryId });
                foreach (var r in rows)
                {
                    existingWeights[(int)r.EvaluatorID] = (decimal)r.WeightScore;
                }
            }

            return evaluators.Select(ev =>
            {
                bool hasWeight = existingWeights.TryGetValue(ev.EvaluatorID, out decimal w) && w > 0m;
                return new CategoryEvaluatorSelectRow
                {
                    EvaluatorID = ev.EvaluatorID,
                    EvaluatorName = ev.EvaluatorName,
                    IsSelected = hasWeight,
                    WeightScore = hasWeight ? w : 0m
                };
            }).ToList();
        }

        public async Task<(bool IsSuccess, string Message)> SaveCategoryWithEvaluatorsAsync(
            int categoryId,
            string categoryName,
            int branchId,
            List<CategoryEvaluatorSelectRow> selectedEvaluators)
        {
            string cleanName = categoryName.Trim();
            if (string.IsNullOrWhiteSpace(cleanName))
                return (false, "يرجى إدخال اسم الفئة (مثلاً: معلم، إداري، مشرف).");

            if (branchId <= 0)
                return (false, "يرجى اختيار الفرع الذي تنتمي إليه هذه الفئة.");

            if (!CheckPermission(categoryId == 0 ? "insert" : "update", out string permErr))
                return (false, permErr);

            var activeRows = selectedEvaluators.Where(x => x.IsSelected && x.WeightScore > 0m).ToList();
            if (activeRows.Count == 0)
                return (false, "يرجى اختيار مقيم واحد على الأقل وتحديد درجته لهذه الفئة.");

            decimal totalWeight = activeRows.Sum(x => x.WeightScore);
            if (totalWeight != 100m)
                return (false, $"شرط النظام: يجب أن يكون مجموع درجات المقيمين لهذه الفئة يساوي 100 درجة بالضبط.\nالمجموع الحالي هو ({totalWeight:0.##}) درجة.");

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            int dup = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.Categories WHERE CategoryName = @Name AND BranchID = @BranchID AND CategoryID <> @ID",
                new { Name = cleanName, BranchID = branchId, ID = categoryId });

            if (dup > 0)
                return (false, $"الفئة ({cleanName}) مسجلة مسبقاً في هذا الفرع.");

            using var tx = conn.BeginTransaction();
            try
            {
                int targetCatId = categoryId;
                if (targetCatId == 0)
                {
                    string insertCatSql = @"
                        INSERT INTO dbo.Categories (CategoryName, BranchID, IsActive)
                        VALUES (@Name, @BranchID, 1);
                        SELECT CAST(SCOPE_IDENTITY() AS INT);";
                    targetCatId = await conn.ExecuteScalarAsync<int>(insertCatSql, new { Name = cleanName, BranchID = branchId }, tx);
                }
                else
                {
                    await conn.ExecuteAsync(
                        "UPDATE dbo.Categories SET CategoryName = @Name, BranchID = @BranchID WHERE CategoryID = @ID",
                        new { Name = cleanName, BranchID = branchId, ID = targetCatId }, tx);

                    await conn.ExecuteAsync("DELETE FROM dbo.CategoryEvaluators WHERE CategoryID = @ID", new { ID = targetCatId }, tx);
                }

                string insertLinkSql = @"
                    INSERT INTO dbo.CategoryEvaluators (CategoryID, EvaluatorID, WeightScore)
                    VALUES (@CatID, @EvalID, @Weight)";

                foreach (var row in activeRows)
                {
                    await conn.ExecuteAsync(insertLinkSql, new
                    {
                        CatID = targetCatId,
                        EvalID = row.EvaluatorID,
                        Weight = row.WeightScore
                    }, tx);
                }

                await tx.CommitAsync();
                return (true, categoryId == 0 ? "تم إنشاء الفئة وربطها بالمقيمين (100 درجة) بنجاح." : "تم تحديث بيانات الفئة وأوزان المقيمين بنجاح.");
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return (false, $"فشل حفظ الفئة: {ex.Message}");
            }
        }

        public async Task<(bool IsSuccess, string Message)> DeleteCategoryAsync(int categoryId)
        {
            if (!CheckPermission("delete", out string permErr))
                return (false, permErr);

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            try
            {
                await conn.ExecuteAsync("DELETE FROM dbo.Categories WHERE CategoryID = @ID", new { ID = categoryId });
                return (true, "تم حذف الفئة بنجاح.");
            }
            catch (SqlException)
            {
                return (false, "لا يمكن حذف هذه الفئة لوجود موظفين مسجلين عليها حالياً.");
            }
        }

        // =========================================================================
        // 5. إدارة الموظفين + مؤشر اكتمال نصاب القوالب + استيراد وتصدير Excel
        // =========================================================================
        public async Task<List<EmployeeDto>> GetEmployeesAsync(string search = "", int? branchId = null, int? categoryId = null)
        {
            using var conn = new SqlConnection(_connectionString);
            string sql = @"
                SELECT e.EmployeeID, e.SystemName, e.EvaluationName,
                       e.BranchID, b.BranchName,
                       e.JobID, j.JobTitle,
                       e.CategoryID, c.CategoryName
                FROM dbo.Employees e
                INNER JOIN dbo.Branches b ON e.BranchID = b.BranchID
                INNER JOIN dbo.Jobs j ON e.JobID = j.JobID
                INNER JOIN dbo.Categories c ON e.CategoryID = c.CategoryID
                WHERE e.IsActive = 1
                  AND (@Search = '' OR e.SystemName LIKE @Pattern OR e.EvaluationName LIKE @Pattern OR j.JobTitle LIKE @Pattern)
                  AND (@BranchID IS NULL OR e.BranchID = @BranchID)
                  AND (@CategoryID IS NULL OR e.CategoryID = @CategoryID)
                ORDER BY b.BranchName, e.EvaluationName";

            var employees = (await conn.QueryAsync<EmployeeDto>(sql, new
            {
                Search = search.Trim(),
                Pattern = $"%{search.Trim()}%",
                BranchID = branchId,
                CategoryID = categoryId
            })).ToList();

            var catEvals = (await conn.QueryAsync<dynamic>(@"
                SELECT ce.CategoryID, ce.EvaluatorID, ev.EvaluatorName, ce.WeightScore
                FROM dbo.CategoryEvaluators ce
                INNER JOIN dbo.Evaluators ev ON ce.EvaluatorID = ev.EvaluatorID")).ToList();

            var empInTemplates = (await conn.QueryAsync<dynamic>(@"
                SELECT DISTINCT se.EmployeeID, t.EvaluatorID
                FROM dbo.SheetEmployees se
                INNER JOIN dbo.TemplateSheets ts ON se.SheetID = ts.SheetID
                INNER JOIN dbo.Templates t ON ts.TemplateID = t.TemplateID
                WHERE t.IsCurrentVersion = 1 AND t.IsDeleted = 0")).ToList();

            var empTemplateMap = empInTemplates
                .GroupBy(x => (int)x.EmployeeID)
                .ToDictionary(g => g.Key, g => g.Select(i => (int)i.EvaluatorID).ToHashSet());

            var catEvalMap = catEvals
                .GroupBy(x => (int)x.CategoryID)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var emp in employees)
            {
                if (catEvalMap.TryGetValue(emp.CategoryID, out var requiredEvaluators) && requiredEvaluators.Count > 0)
                {
                    emp.AssignedEvaluatorsSummary = string.Join(" | ", requiredEvaluators.Select(r => $"{r.EvaluatorName} ({((decimal)r.WeightScore):0.##})"));

                    empTemplateMap.TryGetValue(emp.EmployeeID, out var coveredEvalIds);
                    coveredEvalIds ??= new HashSet<int>();

                    var missingNames = requiredEvaluators
                        .Where(r => !coveredEvalIds.Contains((int)r.EvaluatorID))
                        .Select(r => (string)r.EvaluatorName)
                        .ToList();

                    if (missingNames.Count == 0)
                    {
                        emp.IsCoverageComplete = true;
                        emp.TemplateCoverageStatus = "مكتمل في القوالب ✔";
                    }
                    else
                    {
                        emp.IsCoverageComplete = false;
                        emp.TemplateCoverageStatus = $"ينقصه قالب: {string.Join("، ", missingNames)} ⚠";
                    }
                }
                else
                {
                    emp.AssignedEvaluatorsSummary = "لا يوجد مقيمون للفئة";
                    emp.IsCoverageComplete = false;
                    emp.TemplateCoverageStatus = "الفئة بلا مقيمين";
                }
            }

            return employees;
        }

        public async Task<(bool IsSuccess, string Message)> SaveEmployeeAsync(EmployeeDto dto)
        {
            string sysName = dto.SystemName.Trim();
            string evalName = dto.EvaluationName.Trim();

            if (string.IsNullOrWhiteSpace(sysName) || string.IsNullOrWhiteSpace(evalName))
                return (false, "يرجى إدخال كل من (الاسم في النظام) و(الاسم التقييمي).");

            if (dto.BranchID <= 0 || dto.JobID <= 0 || dto.CategoryID <= 0)
                return (false, "يرجى اختيار الفرع والوظيفة والفئة من القوائم المنسدلة.");

            if (!CheckPermission(dto.EmployeeID == 0 ? "insert" : "update", out string permErr))
                return (false, permErr);

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            int dupSys = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.Employees WHERE SystemName = @Sys AND EmployeeID <> @ID",
                new { Sys = sysName, ID = dto.EmployeeID });
            if (dupSys > 0)
                return (false, $"الاسم في النظام ({sysName}) مسجل لموظف آخر بالفعل.");

            int dupEval = await conn.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM dbo.Employees WHERE EvaluationName = @Eval AND EmployeeID <> @ID",
                new { Eval = evalName, ID = dto.EmployeeID });
            if (dupEval > 0)
                return (false, $"الاسم التقييمي ({evalName}) مسجل لموظف آخر بالفعل.");

            if (dto.EmployeeID == 0)
            {
                string sql = @"
                    INSERT INTO dbo.Employees (SystemName, EvaluationName, BranchID, JobID, CategoryID, IsActive)
                    VALUES (@Sys, @Eval, @BranchID, @JobID, @CategoryID, 1)";
                await conn.ExecuteAsync(sql, new
                {
                    Sys = sysName,
                    Eval = evalName,
                    dto.BranchID,
                    dto.JobID,
                    dto.CategoryID
                });
                return (true, "تمت إضافة الموظف وربطه بمقيمي الفئة تلقائياً بنجاح.");
            }
            else
            {
                string sql = @"
                    UPDATE dbo.Employees
                    SET SystemName = @Sys, EvaluationName = @Eval, BranchID = @BranchID, JobID = @JobID, CategoryID = @CategoryID
                    WHERE EmployeeID = @ID";
                await conn.ExecuteAsync(sql, new
                {
                    Sys = sysName,
                    Eval = evalName,
                    dto.BranchID,
                    dto.JobID,
                    dto.CategoryID,
                    ID = dto.EmployeeID
                });
                return (true, "تم تحديث بيانات الموظف بنجاح.");
            }
        }

        public async Task<(bool IsSuccess, string Message)> DeleteEmployeeAsync(int employeeId)
        {
            if (!CheckPermission("delete", out string permErr))
                return (false, permErr);

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            try
            {
                await conn.ExecuteAsync("DELETE FROM dbo.Employees WHERE EmployeeID = @ID", new { ID = employeeId });
                return (true, "تم حذف الموظف بنجاح.");
            }
            catch (SqlException)
            {
                await conn.ExecuteAsync("UPDATE dbo.Employees SET IsActive = 0 WHERE EmployeeID = @ID", new { ID = employeeId });
                return (true, "نظراً لوجود تقييمات أو قوالب مرتبطة بهذا الموظف، تم أرشفته (إيقاف نشاطه) حفاظاً على السجلات السابقة.");
            }
        }

        // =========================================================================
        // 6. توليد قالب إكسل جاهز واستيراد الموظفين من Excel دفعة واحدة (ClosedXML)
        // =========================================================================
        public void GenerateEmployeesExcelTemplate(string filePath)
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("بيانات الموظفين");
            ws.RightToLeft = true;

            string[] headers = { "الاسم في النظام", "الاسم التقييمي", "الفرع", "الوظيفة", "الفئة" };
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cell(1, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E293B");
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            ws.Cell(2, 1).Value = "ابتسام ابراهيم علي الزيادي - استاذة/";
            ws.Cell(2, 2).Value = "ابتسام الزيادي";
            ws.Cell(2, 3).Value = "الحصبة";
            ws.Cell(2, 4).Value = "معلم";
            ws.Cell(2, 5).Value = "معلم";

            ws.Cell(3, 1).Value = "ابراهيم عبدالعزيز عبداللطيف المعلمي - استاذ/";
            ws.Cell(3, 2).Value = "ابراهيم المعلمي";
            ws.Cell(3, 3).Value = "الحصبة";
            ws.Cell(3, 4).Value = "معلم";
            ws.Cell(3, 5).Value = "معلم";

            ws.Columns().AdjustToContents();
            wb.SaveAs(filePath);
        }

        public async Task<(bool IsSuccess, string SummaryMessage)> ImportEmployeesFromExcelAsync(string filePath)
        {
            if (!CheckPermission("insert", out string permErr))
                return (false, permErr);

            if (!File.Exists(filePath))
                return (false, "ملف الإكسل المحدد غير موجود.");

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            var branches = (await GetBranchesAsync()).ToDictionary(b => b.BranchName.Trim(), b => b.BranchID);
            var jobs = (await GetJobsAsync()).ToDictionary(j => j.JobTitle.Trim(), j => j.JobID);
            var categories = await GetCategoriesAsync();

            int insertedCount = 0;
            int updatedCount = 0;
            var skippedReasons = new List<string>();

            try
            {
                using var wb = new XLWorkbook(filePath);
                var ws = wb.Worksheets.First();
                var rows = ws.RangeUsed()?.RowsUsed().Skip(1);

                if (rows == null)
                    return (false, "ملف الإكسل فارغ ولا يحتوي على بيانات موظفين.");

                foreach (var row in rows)
                {
                    int rowNum = row.RowNumber();
                    string sysName = row.Cell(1).GetString().Trim();
                    string evalName = row.Cell(2).GetString().Trim();
                    string branchName = row.Cell(3).GetString().Trim();
                    string jobTitle = row.Cell(4).GetString().Trim();
                    string catName = row.Cell(5).GetString().Trim();

                    if (string.IsNullOrWhiteSpace(sysName) && string.IsNullOrWhiteSpace(evalName))
                        continue;

                    if (string.IsNullOrWhiteSpace(evalName)) evalName = sysName;
                    if (string.IsNullOrWhiteSpace(sysName)) sysName = evalName;

                    if (string.IsNullOrWhiteSpace(branchName) || string.IsNullOrWhiteSpace(jobTitle) || string.IsNullOrWhiteSpace(catName))
                    {
                        skippedReasons.Add($"سطر {rowNum} ({evalName}): ينقصه الفرع أو الوظيفة أو الفئة.");
                        continue;
                    }

                    if (!branches.TryGetValue(branchName, out int branchId))
                    {
                        branchId = await conn.ExecuteScalarAsync<int>(
                            "INSERT INTO dbo.Branches (BranchName, IsActive) VALUES (@Name, 1); SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new { Name = branchName });
                        branches[branchName] = branchId;
                    }

                    if (!jobs.TryGetValue(jobTitle, out int jobId))
                    {
                        jobId = await conn.ExecuteScalarAsync<int>(
                            "INSERT INTO dbo.Jobs (JobTitle, IsActive) VALUES (@Title, 1); SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new { Title = jobTitle });
                        jobs[jobTitle] = jobId;
                    }

                    var matchedCat = categories.FirstOrDefault(c =>
                        c.BranchID == branchId &&
                        string.Equals(c.CategoryName.Trim(), catName, StringComparison.OrdinalIgnoreCase));

                    if (matchedCat == null)
                    {
                        skippedReasons.Add($"سطر {rowNum} ({evalName}): الفئة ({catName}) غير منشأة بعد لفرع ({branchName}). أنشئ الفئة وحدد مقيميها الـ 100 درجة أولاً.");
                        continue;
                    }

                    var existingEmpId = await conn.ExecuteScalarAsync<int?>(
                        "SELECT EmployeeID FROM dbo.Employees WHERE SystemName = @Sys OR EvaluationName = @Eval",
                        new { Sys = sysName, Eval = evalName });

                    if (existingEmpId == null)
                    {
                        await conn.ExecuteAsync(@"
                            INSERT INTO dbo.Employees (SystemName, EvaluationName, BranchID, JobID, CategoryID, IsActive)
                            VALUES (@Sys, @Eval, @BranchID, @JobID, @CatID, 1)",
                            new { Sys = sysName, Eval = evalName, BranchID = branchId, JobID = jobId, CatID = matchedCat.CategoryID });
                        insertedCount++;
                    }
                    else
                    {
                        await conn.ExecuteAsync(@"
                            UPDATE dbo.Employees
                            SET SystemName = @Sys, EvaluationName = @Eval, BranchID = @BranchID, JobID = @JobID, CategoryID = @CatID, IsActive = 1
                            WHERE EmployeeID = @ID",
                            new { Sys = sysName, Eval = evalName, BranchID = branchId, JobID = jobId, CatID = matchedCat.CategoryID, ID = existingEmpId.Value });
                        updatedCount++;
                    }
                }

                string report = $"اكتمل استيراد ملف الإكسل:\n• موظفون جدد تمت إضافتهم: {insertedCount}\n• موظفون تم تحديث بياناتهم: {updatedCount}";
                if (skippedReasons.Count > 0)
                {
                    report += $"\n• أسطر تم تخطيها ({skippedReasons.Count}):\n" + string.Join("\n", skippedReasons.Take(6));
                }

                return (true, report);
            }
            catch (Exception ex)
            {
                return (false, $"حدث خطأ أثناء قراءة ملف الإكسل (تأكد من إغلاق الملف في برنامج Excel قبل الاستيراد): {ex.Message}");
            }
        }
    }
}
