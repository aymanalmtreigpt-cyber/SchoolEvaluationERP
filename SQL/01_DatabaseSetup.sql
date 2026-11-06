/*
================================================================================
نظام تقييم موظفي المدارس واحتساب الحوافز الشهرية (Enterprise Edition V2)
Database Setup Script - SQL Server (LAN Multi-User High Performance)
يشمل:
- الهيكل التنظيمي الهرمي (الأقسام -> الفئات -> الوظائف -> نوع الوظيفة)
- مجموعات المقيمين وتخصيص أوزان الفئات (100 درجة)
- ربط الموظفين بالمقيمين وحالتي (بحافز/بدون حافز) و(مستمر/منقطع)
- قوالب التقييم متعددة الأوراق والفروع والتقييم الشهري
================================================================================
*/

IF NOT EXISTS (SELECT * FROM sys.databases WHERE name = 'SchoolEvaluationDB')
BEGIN
    CREATE DATABASE SchoolEvaluationDB;
END
GO

USE SchoolEvaluationDB;
GO

-- ============================================================================
-- حذف الجداول السابقة بالترتيب العكسي للعلاقات لإعادة التهيئة النظيفة
-- ============================================================================
IF OBJECT_ID('dbo.EvaluationScores', 'U') IS NOT NULL DROP TABLE dbo.EvaluationScores;
IF OBJECT_ID('dbo.EvaluationRecords', 'U') IS NOT NULL DROP TABLE dbo.EvaluationRecords;
IF OBJECT_ID('dbo.SheetEmployees', 'U') IS NOT NULL DROP TABLE dbo.SheetEmployees;
IF OBJECT_ID('dbo.SheetCriteria', 'U') IS NOT NULL DROP TABLE dbo.SheetCriteria;
IF OBJECT_ID('dbo.TemplateSheets', 'U') IS NOT NULL DROP TABLE dbo.TemplateSheets;
IF OBJECT_ID('dbo.Templates', 'U') IS NOT NULL DROP TABLE dbo.Templates;
IF OBJECT_ID('dbo.EmployeeEvaluators', 'U') IS NOT NULL DROP TABLE dbo.EmployeeEvaluators;
IF OBJECT_ID('dbo.Employees', 'U') IS NOT NULL DROP TABLE dbo.Employees;
IF OBJECT_ID('dbo.CategoryEvaluatorGroups', 'U') IS NOT NULL DROP TABLE dbo.CategoryEvaluatorGroups;
IF OBJECT_ID('dbo.CategoryEvaluators', 'U') IS NOT NULL DROP TABLE dbo.CategoryEvaluators;
IF OBJECT_ID('dbo.Evaluators', 'U') IS NOT NULL DROP TABLE dbo.Evaluators;
IF OBJECT_ID('dbo.EvaluatorGroups', 'U') IS NOT NULL DROP TABLE dbo.EvaluatorGroups;
IF OBJECT_ID('dbo.JobTypes', 'U') IS NOT NULL DROP TABLE dbo.JobTypes;
IF OBJECT_ID('dbo.Jobs', 'U') IS NOT NULL DROP TABLE dbo.Jobs;
IF OBJECT_ID('dbo.Categories', 'U') IS NOT NULL DROP TABLE dbo.Categories;
IF OBJECT_ID('dbo.Departments', 'U') IS NOT NULL DROP TABLE dbo.Departments;
IF OBJECT_ID('dbo.Branches', 'U') IS NOT NULL DROP TABLE dbo.Branches;
IF OBJECT_ID('dbo.UserPermissions', 'U') IS NOT NULL DROP TABLE dbo.UserPermissions;
IF OBJECT_ID('dbo.Users', 'U') IS NOT NULL DROP TABLE dbo.Users;
GO

-- ============================================================================
-- 1. جداول المستخدمين والصلاحيات (Security & RBAC)
-- ============================================================================
CREATE TABLE dbo.Users (
    UserID INT IDENTITY(1,1) PRIMARY KEY,
    Username NVARCHAR(100) NOT NULL UNIQUE,
    PasswordHash NVARCHAR(256) NOT NULL,
    PasswordSalt NVARCHAR(128) NOT NULL,
    FullName NVARCHAR(150) NOT NULL,
    IsAdmin BIT NOT NULL DEFAULT 0,
    MustChangePassword BIT NOT NULL DEFAULT 1,
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME()
);

CREATE TABLE dbo.UserPermissions (
    PermissionID INT IDENTITY(1,1) PRIMARY KEY,
    UserID INT NOT NULL,
    ModuleCode VARCHAR(50) NOT NULL,
    ModuleArabicName NVARCHAR(100) NOT NULL,
    CanRead BIT NOT NULL DEFAULT 0,
    CanInsert BIT NOT NULL DEFAULT 0,
    CanUpdate BIT NOT NULL DEFAULT 0,
    CanDelete BIT NOT NULL DEFAULT 0,
    CONSTRAINT FK_UserPermissions_Users FOREIGN KEY (UserID) REFERENCES dbo.Users(UserID) ON DELETE CASCADE,
    CONSTRAINT UQ_User_Module UNIQUE (UserID, ModuleCode)
);

-- ============================================================================
-- 2. جداول صفحة الأساسيات والهيكل التنظيمي الهرمي (Master Data & Hierarchy)
-- ============================================================================
CREATE TABLE dbo.Branches (
    BranchID INT IDENTITY(1,1) PRIMARY KEY,
    BranchName NVARCHAR(150) NOT NULL UNIQUE,
    IsActive BIT NOT NULL DEFAULT 1
);

CREATE TABLE dbo.Departments (
    DepartmentID INT IDENTITY(1,1) PRIMARY KEY,
    DepartmentName NVARCHAR(150) NOT NULL UNIQUE,
    DepartmentType NVARCHAR(100) NOT NULL DEFAULT N'أكاديمي',
    IsActive BIT NOT NULL DEFAULT 1
);

CREATE TABLE dbo.Categories (
    CategoryID INT IDENTITY(1,1) PRIMARY KEY,
    DepartmentID INT NOT NULL,
    CategoryName NVARCHAR(150) NOT NULL UNIQUE,
    IsActive BIT NOT NULL DEFAULT 1,
    CONSTRAINT FK_Categories_Departments FOREIGN KEY (DepartmentID) REFERENCES dbo.Departments(DepartmentID)
);

CREATE TABLE dbo.Jobs (
    JobID INT IDENTITY(1,1) PRIMARY KEY,
    CategoryID INT NOT NULL,
    JobTitle NVARCHAR(150) NOT NULL,
    IsActive BIT NOT NULL DEFAULT 1,
    CONSTRAINT FK_Jobs_Categories FOREIGN KEY (CategoryID) REFERENCES dbo.Categories(CategoryID),
    CONSTRAINT UQ_Category_Job UNIQUE (CategoryID, JobTitle)
);

CREATE TABLE dbo.JobTypes (
    JobTypeID INT IDENTITY(1,1) PRIMARY KEY,
    JobID INT NOT NULL,
    JobTypeName NVARCHAR(150) NOT NULL,
    IsActive BIT NOT NULL DEFAULT 1,
    CONSTRAINT FK_JobTypes_Jobs FOREIGN KEY (JobID) REFERENCES dbo.Jobs(JobID),
    CONSTRAINT UQ_Job_JobType UNIQUE (JobID, JobTypeName)
);

-- ============================================================================
-- 3. مجموعات المقيمين وتخصيص أوزان الـ 100 درجة للفئات
-- ============================================================================
CREATE TABLE dbo.EvaluatorGroups (
    GroupID INT IDENTITY(1,1) PRIMARY KEY,
    GroupName NVARCHAR(150) NOT NULL UNIQUE,
    IsActive BIT NOT NULL DEFAULT 1
);

CREATE TABLE dbo.Evaluators (
    EvaluatorID INT IDENTITY(1,1) PRIMARY KEY,
    GroupID INT NOT NULL,
    EvaluatorName NVARCHAR(150) NOT NULL UNIQUE,
    IsActive BIT NOT NULL DEFAULT 1,
    CONSTRAINT FK_Evaluators_Groups FOREIGN KEY (GroupID) REFERENCES dbo.EvaluatorGroups(GroupID)
);

CREATE TABLE dbo.CategoryEvaluatorGroups (
    CategoryEvaluatorGroupID INT IDENTITY(1,1) PRIMARY KEY,
    CategoryID INT NOT NULL,
    GroupID INT NOT NULL,
    WeightScore DECIMAL(5,2) NOT NULL CHECK (WeightScore > 0 AND WeightScore <= 100),
    CONSTRAINT FK_CatEvalGroup_Categories FOREIGN KEY (CategoryID) REFERENCES dbo.Categories(CategoryID) ON DELETE CASCADE,
    CONSTRAINT FK_CatEvalGroup_Groups FOREIGN KEY (GroupID) REFERENCES dbo.EvaluatorGroups(GroupID),
    CONSTRAINT UQ_Category_EvalGroup UNIQUE (CategoryID, GroupID)
);

-- ============================================================================
-- 4. جدول الموظفين والمقيمين المسؤولين عن كل موظف
-- ============================================================================
CREATE TABLE dbo.Employees (
    EmployeeID INT IDENTITY(1,1) PRIMARY KEY,
    SystemName NVARCHAR(250) NOT NULL UNIQUE,
    EvaluationName NVARCHAR(200) NOT NULL UNIQUE,
    BranchID INT NOT NULL,
    DepartmentID INT NOT NULL,
    CategoryID INT NOT NULL,
    JobID INT NOT NULL,
    JobTypeID INT NOT NULL,
    HasIncentive BIT NOT NULL DEFAULT 1,
    IsContinuing BIT NOT NULL DEFAULT 1,
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT FK_Employees_Branches FOREIGN KEY (BranchID) REFERENCES dbo.Branches(BranchID),
    CONSTRAINT FK_Employees_Departments FOREIGN KEY (DepartmentID) REFERENCES dbo.Departments(DepartmentID),
    CONSTRAINT FK_Employees_Categories FOREIGN KEY (CategoryID) REFERENCES dbo.Categories(CategoryID),
    CONSTRAINT FK_Employees_Jobs FOREIGN KEY (JobID) REFERENCES dbo.Jobs(JobID),
    CONSTRAINT FK_Employees_JobTypes FOREIGN KEY (JobTypeID) REFERENCES dbo.JobTypes(JobTypeID)
);

CREATE TABLE dbo.EmployeeEvaluators (
    EmployeeEvaluatorID INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeID INT NOT NULL,
    EvaluatorID INT NOT NULL,
    CONSTRAINT FK_EmpEval_Employees FOREIGN KEY (EmployeeID) REFERENCES dbo.Employees(EmployeeID) ON DELETE CASCADE,
    CONSTRAINT FK_EmpEval_Evaluators FOREIGN KEY (EvaluatorID) REFERENCES dbo.Evaluators(EvaluatorID),
    CONSTRAINT UQ_Employee_Evaluator UNIQUE (EmployeeID, EvaluatorID)
);

-- ============================================================================
-- 5. جداول قوالب التقييم متعددة الأوراق وحفظ النسخ (Templates & Versioning)
-- ============================================================================
CREATE TABLE dbo.Templates (
    TemplateID INT IDENTITY(1,1) PRIMARY KEY,
    TemplateGroupGuid UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    EvaluatorID INT NOT NULL,
    VersionNumber INT NOT NULL DEFAULT 1,
    IsCurrentVersion BIT NOT NULL DEFAULT 1,
    IsDeleted BIT NOT NULL DEFAULT 0,
    CreatedByUserID INT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT FK_Templates_Evaluators FOREIGN KEY (EvaluatorID) REFERENCES dbo.Evaluators(EvaluatorID)
);

CREATE TABLE dbo.TemplateSheets (
    SheetID INT IDENTITY(1,1) PRIMARY KEY,
    TemplateID INT NOT NULL,
    SheetName NVARCHAR(150) NOT NULL,
    TargetCategoryID INT NOT NULL,
    TargetBranchID INT NULL,
    TargetCategoryName NVARCHAR(150) NOT NULL,
    SortOrder INT NOT NULL DEFAULT 1,
    CONSTRAINT FK_Sheets_Templates FOREIGN KEY (TemplateID) REFERENCES dbo.Templates(TemplateID) ON DELETE CASCADE,
    CONSTRAINT FK_Sheets_Categories FOREIGN KEY (TargetCategoryID) REFERENCES dbo.Categories(CategoryID),
    CONSTRAINT FK_Sheets_Branches FOREIGN KEY (TargetBranchID) REFERENCES dbo.Branches(BranchID)
);

CREATE TABLE dbo.SheetCriteria (
    CriteriaID INT IDENTITY(1,1) PRIMARY KEY,
    SheetID INT NOT NULL,
    CriteriaTitle NVARCHAR(150) NOT NULL DEFAULT N'قواعد التقييم',
    MaxScore DECIMAL(5,2) NOT NULL DEFAULT 0 CHECK (MaxScore >= 0 AND MaxScore <= 100),
    ColumnOrder INT NOT NULL,
    CONSTRAINT FK_Criteria_Sheets FOREIGN KEY (SheetID) REFERENCES dbo.TemplateSheets(SheetID) ON DELETE CASCADE
);

CREATE TABLE dbo.SheetEmployees (
    SheetEmployeeID INT IDENTITY(1,1) PRIMARY KEY,
    SheetID INT NOT NULL,
    EmployeeID INT NOT NULL,
    RowOrder INT NOT NULL,
    CONSTRAINT FK_SheetEmp_Sheets FOREIGN KEY (SheetID) REFERENCES dbo.TemplateSheets(SheetID) ON DELETE CASCADE,
    CONSTRAINT FK_SheetEmp_Employees FOREIGN KEY (EmployeeID) REFERENCES dbo.Employees(EmployeeID),
    CONSTRAINT UQ_Sheet_Employee UNIQUE (SheetID, EmployeeID)
);

-- ============================================================================
-- 6. جداول إدخال التقييمات الشهرية وسجل التقييمات (Monthly Evaluations)
-- ============================================================================
CREATE TABLE dbo.EvaluationRecords (
    RecordID INT IDENTITY(1,1) PRIMARY KEY,
    TemplateID INT NOT NULL,
    EvaluatorID INT NOT NULL,
    EvalYear INT NOT NULL CHECK (EvalYear >= 2020 AND EvalYear <= 2100),
    EvalMonth INT NOT NULL CHECK (EvalMonth BETWEEN 1 AND 12),
    CreatedByUserID INT NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
    LastModifiedByUserID INT NULL,
    LastModifiedAt DATETIME2 NULL,
    RowVersion TIMESTAMP,
    CONSTRAINT FK_Records_Templates FOREIGN KEY (TemplateID) REFERENCES dbo.Templates(TemplateID),
    CONSTRAINT FK_Records_Evaluators FOREIGN KEY (EvaluatorID) REFERENCES dbo.Evaluators(EvaluatorID),
    CONSTRAINT FK_Records_Users FOREIGN KEY (CreatedByUserID) REFERENCES dbo.Users(UserID),
    CONSTRAINT UQ_Template_Year_Month UNIQUE (TemplateID, EvalYear, EvalMonth)
);

CREATE TABLE dbo.EvaluationScores (
    ScoreID BIGINT IDENTITY(1,1) PRIMARY KEY,
    RecordID INT NOT NULL,
    SheetID INT NOT NULL,
    EmployeeID INT NOT NULL,
    CriteriaID INT NOT NULL,
    GivenScore DECIMAL(5,2) NOT NULL CHECK (GivenScore >= 0),
    SnapshotEvaluatorWeight DECIMAL(5,2) NOT NULL,
    CONSTRAINT FK_Scores_Records FOREIGN KEY (RecordID) REFERENCES dbo.EvaluationRecords(RecordID) ON DELETE CASCADE,
    CONSTRAINT FK_Scores_Sheets FOREIGN KEY (SheetID) REFERENCES dbo.TemplateSheets(SheetID),
    CONSTRAINT FK_Scores_Employees FOREIGN KEY (EmployeeID) REFERENCES dbo.Employees(EmployeeID),
    CONSTRAINT FK_Scores_Criteria FOREIGN KEY (CriteriaID) REFERENCES dbo.SheetCriteria(CriteriaID),
    CONSTRAINT UQ_Record_Emp_Criteria UNIQUE (RecordID, EmployeeID, CriteriaID)
);
GO

-- ============================================================================
-- 7. فهارس الأداء العالي عبر الشبكة المحلية (High-Performance LAN Indexes)
-- ============================================================================
CREATE NONCLUSTERED INDEX IX_Employees_EvaluationName ON dbo.Employees(EvaluationName) INCLUDE (BranchID, CategoryID, HasIncentive, IsContinuing, IsActive);
CREATE NONCLUSTERED INDEX IX_EmployeeEvaluators_Eval ON dbo.EmployeeEvaluators(EvaluatorID, EmployeeID);
CREATE NONCLUSTERED INDEX IX_Templates_Evaluator_Current ON dbo.Templates(EvaluatorID, IsCurrentVersion, IsDeleted);
CREATE NONCLUSTERED INDEX IX_EvalRecords_Period ON dbo.EvaluationRecords(EvalYear, EvalMonth, EvaluatorID);
CREATE NONCLUSTERED INDEX IX_EvalScores_Record_Employee ON dbo.EvaluationScores(RecordID, EmployeeID) INCLUDE (GivenScore, SnapshotEvaluatorWeight);
GO

-- ============================================================================
-- 8. إدراج بيانات البداية: حساب المدير العام (admin / admin) وهيكل مبدئي
-- ============================================================================
INSERT INTO dbo.Users (Username, PasswordHash, PasswordSalt, FullName, IsAdmin, MustChangePassword, IsActive)
VALUES (N'admin', N'8C6976E5B5410415BDE908BD4DEE15DFB167A9C873FC4BB8A81F6F2AB448A918', N'STATIC_INIT_SALT', N'مدير النظام العام', 1, 0, 1);

DECLARE @AdminID INT = SCOPE_IDENTITY();

INSERT INTO dbo.UserPermissions (UserID, ModuleCode, ModuleArabicName, CanRead, CanInsert, CanUpdate, CanDelete)
VALUES 
(@AdminID, 'MasterData', N'صفحة الأساسيات', 1, 1, 1, 1),
(@AdminID, 'Templates', N'قوالب التقييم', 1, 1, 1, 1),
(@AdminID, 'ScoreEntry', N'إدخال التقييمات الشهرية', 1, 1, 1, 1),
(@AdminID, 'HistoryLog', N'سجل التقييمات والأرشيف', 1, 1, 1, 1),
(@AdminID, 'FinalReport', N'التقرير النهائي والحوافز', 1, 1, 1, 1),
(@AdminID, 'UserManagement', N'إدارة المستخدمين والصلاحيات', 1, 1, 1, 1);

INSERT INTO dbo.Branches (BranchName) VALUES (N'الحصبة'), (N'حدة');

INSERT INTO dbo.Departments (DepartmentName, DepartmentType)
VALUES (N'القسم الأكاديمي', N'أكاديمي'),
       (N'القسم الإداري', N'إداري');

DECLARE @AcadDeptID INT = (SELECT DepartmentID FROM dbo.Departments WHERE DepartmentName = N'القسم الأكاديمي');
DECLARE @AdminDeptID INT = (SELECT DepartmentID FROM dbo.Departments WHERE DepartmentName = N'القسم الإداري');

INSERT INTO dbo.Categories (DepartmentID, CategoryName)
VALUES (@AcadDeptID, N'المعلمين'),
       (@AcadDeptID, N'المشرفين'),
       (@AcadDeptID, N'الوكلاء'),
       (@AdminDeptID, N'الإداريين');

DECLARE @TeachersCatID INT = (SELECT CategoryID FROM dbo.Categories WHERE CategoryName = N'المعلمين');
DECLARE @SupervisorsCatID INT = (SELECT CategoryID FROM dbo.Categories WHERE CategoryName = N'المشرفين');
DECLARE @DeputiesCatID INT = (SELECT CategoryID FROM dbo.Categories WHERE CategoryName = N'الوكلاء');
DECLARE @AdminsCatID INT = (SELECT CategoryID FROM dbo.Categories WHERE CategoryName = N'الإداريين');

INSERT INTO dbo.Jobs (CategoryID, JobTitle)
VALUES (@TeachersCatID, N'معلم'),
       (@TeachersCatID, N'مربية'),
       (@SupervisorsCatID, N'مشرف تربوي'),
       (@DeputiesCatID, N'وكيل مرحلة'),
       (@AdminsCatID, N'سكرتارية');

DECLARE @TeacherJobID INT = (SELECT JobID FROM dbo.Jobs WHERE CategoryID = @TeachersCatID AND JobTitle = N'معلم');
DECLARE @NannyJobID INT = (SELECT JobID FROM dbo.Jobs WHERE CategoryID = @TeachersCatID AND JobTitle = N'مربية');
DECLARE @SupJobID INT = (SELECT JobID FROM dbo.Jobs WHERE CategoryID = @SupervisorsCatID AND JobTitle = N'مشرف تربوي');
DECLARE @DepJobID INT = (SELECT JobID FROM dbo.Jobs WHERE CategoryID = @DeputiesCatID AND JobTitle = N'وكيل مرحلة');
DECLARE @SecJobID INT = (SELECT JobID FROM dbo.Jobs WHERE CategoryID = @AdminsCatID AND JobTitle = N'سكرتارية');

INSERT INTO dbo.JobTypes (JobID, JobTypeName)
VALUES (@TeacherJobID, N'معلم لغة عربية'),
       (@TeacherJobID, N'معلم لغة إنجليزية'),
       (@TeacherJobID, N'معلم رياضيات'),
       (@NannyJobID, N'مربية صفوف أولية'),
       (@SupJobID, N'مشرف مواد علمية'),
       (@DepJobID, N'وكيل شؤون طلاب'),
       (@SecJobID, N'سكرتارية شؤون طلاب');
GO
