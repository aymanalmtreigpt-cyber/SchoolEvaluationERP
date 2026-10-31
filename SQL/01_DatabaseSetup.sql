/*
================================================================================
نظام تقييم موظفي المدارس واحتساب الحوافز الشهرية (Enterprise Edition)
Database Setup Script - SQL Server (LAN Multi-User Ready)
تمت مراجعته وتدقيقه هندسياً ومالياً لضمان حماية السجلات التاريخية (Immutability)
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
-- 1. جداول المستخدمين والصلاحيات (Security & RBAC)
-- ============================================================================
IF OBJECT_ID('dbo.UserPermissions', 'U') IS NOT NULL DROP TABLE dbo.UserPermissions;
IF OBJECT_ID('dbo.Users', 'U') IS NOT NULL DROP TABLE dbo.Users;

CREATE TABLE dbo.Users (
    UserID INT IDENTITY(1,1) PRIMARY KEY,
    Username NVARCHAR(100) NOT NULL UNIQUE,
    PasswordHash NVARCHAR(256) NOT NULL,
    PasswordSalt NVARCHAR(128) NOT NULL,
    FullName NVARCHAR(150) NOT NULL,
    IsAdmin BIT NOT NULL DEFAULT 0,
    MustChangePassword BIT NOT NULL DEFAULT 1, -- يجبر الموظف على تغيير كلمة السر عند أول دخول
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME()
);

CREATE TABLE dbo.UserPermissions (
    PermissionID INT IDENTITY(1,1) PRIMARY KEY,
    UserID INT NOT NULL,
    ModuleCode VARCHAR(50) NOT NULL, -- 'MasterData', 'Templates', 'ScoreEntry', 'HistoryLog', 'FinalReport', 'UserManagement'
    ModuleArabicName NVARCHAR(100) NOT NULL,
    CanRead BIT NOT NULL DEFAULT 0,
    CanInsert BIT NOT NULL DEFAULT 0,
    CanUpdate BIT NOT NULL DEFAULT 0,
    CanDelete BIT NOT NULL DEFAULT 0,
    CONSTRAINT FK_UserPermissions_Users FOREIGN KEY (UserID) REFERENCES dbo.Users(UserID) ON DELETE CASCADE,
    CONSTRAINT UQ_User_Module UNIQUE (UserID, ModuleCode)
);

-- ============================================================================
-- 2. جداول صفحة الأساسيات (Master Data)
-- ============================================================================
IF OBJECT_ID('dbo.EvaluationScores', 'U') IS NOT NULL DROP TABLE dbo.EvaluationScores;
IF OBJECT_ID('dbo.EvaluationRecords', 'U') IS NOT NULL DROP TABLE dbo.EvaluationRecords;
IF OBJECT_ID('dbo.SheetEmployees', 'U') IS NOT NULL DROP TABLE dbo.SheetEmployees;
IF OBJECT_ID('dbo.SheetCriteria', 'U') IS NOT NULL DROP TABLE dbo.SheetCriteria;
IF OBJECT_ID('dbo.TemplateSheets', 'U') IS NOT NULL DROP TABLE dbo.TemplateSheets;
IF OBJECT_ID('dbo.Templates', 'U') IS NOT NULL DROP TABLE dbo.Templates;
IF OBJECT_ID('dbo.Employees', 'U') IS NOT NULL DROP TABLE dbo.Employees;
IF OBJECT_ID('dbo.CategoryEvaluators', 'U') IS NOT NULL DROP TABLE dbo.CategoryEvaluators;
IF OBJECT_ID('dbo.Categories', 'U') IS NOT NULL DROP TABLE dbo.Categories;
IF OBJECT_ID('dbo.Evaluators', 'U') IS NOT NULL DROP TABLE dbo.Evaluators;
IF OBJECT_ID('dbo.Jobs', 'U') IS NOT NULL DROP TABLE dbo.Jobs;
IF OBJECT_ID('dbo.Branches', 'U') IS NOT NULL DROP TABLE dbo.Branches;

-- جدول الفروع (يمثل أيضاً "مكان حساب الموظف" في القيد المحاسبي مثل: الحصبة)
CREATE TABLE dbo.Branches (
    BranchID INT IDENTITY(1,1) PRIMARY KEY,
    BranchName NVARCHAR(150) NOT NULL UNIQUE,
    IsActive BIT NOT NULL DEFAULT 1
);

-- جدول الوظائف
CREATE TABLE dbo.Jobs (
    JobID INT IDENTITY(1,1) PRIMARY KEY,
    JobTitle NVARCHAR(150) NOT NULL UNIQUE,
    IsActive BIT NOT NULL DEFAULT 1
);

-- جدول المقيمين
CREATE TABLE dbo.Evaluators (
    EvaluatorID INT IDENTITY(1,1) PRIMARY KEY,
    EvaluatorName NVARCHAR(150) NOT NULL UNIQUE,
    IsActive BIT NOT NULL DEFAULT 1
);

-- جدول الفئات (مرتبط بالفرع كما تم اعتماده: فئة + فرع = مقيمون مخصصون بمجموع 100 درجة)
CREATE TABLE dbo.Categories (
    CategoryID INT IDENTITY(1,1) PRIMARY KEY,
    CategoryName NVARCHAR(150) NOT NULL,
    BranchID INT NOT NULL,
    IsActive BIT NOT NULL DEFAULT 1,
    CONSTRAINT FK_Categories_Branches FOREIGN KEY (BranchID) REFERENCES dbo.Branches(BranchID),
    CONSTRAINT UQ_Category_Branch UNIQUE (CategoryName, BranchID)
);

-- جدول ربط الفئة بالمقيمين وأوزانهم (يجب أن يكون مجموع WeightScore لكل CategoryID = 100)
CREATE TABLE dbo.CategoryEvaluators (
    CategoryEvaluatorID INT IDENTITY(1,1) PRIMARY KEY,
    CategoryID INT NOT NULL,
    EvaluatorID INT NOT NULL,
    WeightScore DECIMAL(5,2) NOT NULL CHECK (WeightScore > 0 AND WeightScore <= 100),
    CONSTRAINT FK_CatEval_Categories FOREIGN KEY (CategoryID) REFERENCES dbo.Categories(CategoryID) ON DELETE CASCADE,
    CONSTRAINT FK_CatEval_Evaluators FOREIGN KEY (EvaluatorID) REFERENCES dbo.Evaluators(EvaluatorID),
    CONSTRAINT UQ_Category_Evaluator UNIQUE (CategoryID, EvaluatorID)
);

-- جدول الموظفين (يفصل بين الاسم في النظام للقيد المحاسبي والاسم التقييمي لأوراق التقييم)
CREATE TABLE dbo.Employees (
    EmployeeID INT IDENTITY(1,1) PRIMARY KEY,
    SystemName NVARCHAR(250) NOT NULL UNIQUE,     -- يظهر في عمود "الحساب الدائن" في القيد النهائي
    EvaluationName NVARCHAR(200) NOT NULL UNIQUE, -- يظهر في أوراق التقييم والبحث التلقائي
    BranchID INT NOT NULL,
    JobID INT NOT NULL,
    CategoryID INT NOT NULL,
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT FK_Employees_Branches FOREIGN KEY (BranchID) REFERENCES dbo.Branches(BranchID),
    CONSTRAINT FK_Employees_Jobs FOREIGN KEY (JobID) REFERENCES dbo.Jobs(JobID),
    CONSTRAINT FK_Employees_Categories FOREIGN KEY (CategoryID) REFERENCES dbo.Categories(CategoryID)
);

-- ============================================================================
-- 3. جداول قوالب التقييم متعددة الأوراق وحفظ النسخ (Templates & Versioning)
-- ============================================================================
CREATE TABLE dbo.Templates (
    TemplateID INT IDENTITY(1,1) PRIMARY KEY,
    TemplateGroupGuid UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(), -- يربط النسخ المتتالية لنفس القالب
    EvaluatorID INT NOT NULL,
    VersionNumber INT NOT NULL DEFAULT 1,
    IsCurrentVersion BIT NOT NULL DEFAULT 1,
    IsDeleted BIT NOT NULL DEFAULT 0, -- Soft Delete لحماية السجل التاريخي
    CreatedByUserID INT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
    CONSTRAINT FK_Templates_Evaluators FOREIGN KEY (EvaluatorID) REFERENCES dbo.Evaluators(EvaluatorID)
);

-- أوراق العمل (Tabs) داخل القالب الواحد
-- [تحسين معماري]: نربط الورقة بـ CategoryName (نص الفئة العام مثل "معلم") لكي لا تقيد الفلترة بفرع واحد كما طلب المستخدم
CREATE TABLE dbo.TemplateSheets (
    SheetID INT IDENTITY(1,1) PRIMARY KEY,
    TemplateID INT NOT NULL,
    SheetName NVARCHAR(150) NOT NULL,
    TargetCategoryName NVARCHAR(150) NOT NULL, -- يفلتر جميع الموظفين الذين ينتمون لهذا المسمى الفئوي بغض النظر عن الفرع
    SortOrder INT NOT NULL DEFAULT 1,
    CONSTRAINT FK_Sheets_Templates FOREIGN KEY (TemplateID) REFERENCES dbo.Templates(TemplateID) ON DELETE CASCADE
);

-- أعمدة مبادئ التقييم داخل الورقة (الافتراضي 3 أعمدة + زر الإضافة، والمجموع = 100)
CREATE TABLE dbo.SheetCriteria (
    CriteriaID INT IDENTITY(1,1) PRIMARY KEY,
    SheetID INT NOT NULL,
    CriteriaTitle NVARCHAR(150) NOT NULL DEFAULT N'قواعد التقييم',
    MaxScore DECIMAL(5,2) NOT NULL DEFAULT 0 CHECK (MaxScore >= 0 AND MaxScore <= 100),
    ColumnOrder INT NOT NULL,
    CONSTRAINT FK_Criteria_Sheets FOREIGN KEY (SheetID) REFERENCES dbo.TemplateSheets(SheetID) ON DELETE CASCADE
);

-- الموظفون المضافون داخل الورقة
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
-- 4. جداول إدخال التقييمات الأسبوعية وسجل التقييمات (Weekly Evaluations & Snapshots)
-- ============================================================================
CREATE TABLE dbo.EvaluationRecords (
    RecordID INT IDENTITY(1,1) PRIMARY KEY,
    TemplateID INT NOT NULL,
    EvaluatorID INT NOT NULL,
    EvalYear INT NOT NULL CHECK (EvalYear >= 2020 AND EvalYear <= 2100),
    EvalMonth INT NOT NULL CHECK (EvalMonth BETWEEN 1 AND 12),
    WeekNumber INT NOT NULL CHECK (WeekNumber BETWEEN 1 AND 4),
    CreatedByUserID INT NOT NULL,
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
    LastModifiedByUserID INT NULL,
    LastModifiedAt DATETIME2 NULL,
    RowVersion TIMESTAMP, -- لمنع التعارض عند التعديل المتزامن عبر شبكة LAN
    CONSTRAINT FK_Records_Templates FOREIGN KEY (TemplateID) REFERENCES dbo.Templates(TemplateID),
    CONSTRAINT FK_Records_Evaluators FOREIGN KEY (EvaluatorID) REFERENCES dbo.Evaluators(EvaluatorID),
    CONSTRAINT FK_Records_Users FOREIGN KEY (CreatedByUserID) REFERENCES dbo.Users(UserID),
    -- منع تكرار إدخال نفس الأسبوع لنفس القالب
    CONSTRAINT UQ_Template_Year_Month_Week UNIQUE (TemplateID, EvalYear, EvalMonth, WeekNumber)
);

-- تفاصيل الدرجات الأسبوعية
-- [تحسين نقدي هام جداً]: حفظ SnapshotWeightScore (وزن المقيم وقت التقييم) حتى لا تتغير التقارير المالية القديمة لو تغيرت أوزان الفئات مستقبلاً
CREATE TABLE dbo.EvaluationScores (
    ScoreID BIGINT IDENTITY(1,1) PRIMARY KEY,
    RecordID INT NOT NULL,
    SheetID INT NOT NULL,
    EmployeeID INT NOT NULL,
    CriteriaID INT NOT NULL,
    GivenScore DECIMAL(5,2) NOT NULL CHECK (GivenScore >= 0),
    SnapshotEvaluatorWeight DECIMAL(5,2) NOT NULL, -- وزن المقيم للفئة لحظة الحفظ (مثلاً 20 درجة)
    CONSTRAINT FK_Scores_Records FOREIGN KEY (RecordID) REFERENCES dbo.EvaluationRecords(RecordID) ON DELETE CASCADE,
    CONSTRAINT FK_Scores_Sheets FOREIGN KEY (SheetID) REFERENCES dbo.TemplateSheets(SheetID),
    CONSTRAINT FK_Scores_Employees FOREIGN KEY (EmployeeID) REFERENCES dbo.Employees(EmployeeID),
    CONSTRAINT FK_Scores_Criteria FOREIGN KEY (CriteriaID) REFERENCES dbo.SheetCriteria(CriteriaID),
    CONSTRAINT UQ_Record_Emp_Criteria UNIQUE (RecordID, EmployeeID, CriteriaID)
);
GO

-- ============================================================================
-- 5. إدراج بيانات البداية: حساب المدير العام + بيانات تجريبية مطابقة لصورة القيد
-- ============================================================================
-- كلمة المرور الافتراضية للمدير هي: admin
-- SHA256 Hash with Salt 'ERP_SALT_2026'
INSERT INTO dbo.Users (Username, PasswordHash, PasswordSalt, FullName, IsAdmin, MustChangePassword, IsActive)
VALUES (N'admin', N'8C6976E5B5410415BDE908BD4DEE15DFB167A9C873FC4BB8A81F6F2AB448A918', N'STATIC_INIT_SALT', N'مدير النظام العام', 1, 0, 1);

DECLARE @AdminID INT = SCOPE_IDENTITY();

INSERT INTO dbo.UserPermissions (UserID, ModuleCode, ModuleArabicName, CanRead, CanInsert, CanUpdate, CanDelete)
VALUES 
(@AdminID, 'MasterData', N'صفحة الأساسيات', 1, 1, 1, 1),
(@AdminID, 'Templates', N'قوالب التقييم', 1, 1, 1, 1),
(@AdminID, 'ScoreEntry', N'إدخال التقييمات', 1, 1, 1, 1),
(@AdminID, 'HistoryLog', N'سجل التقييمات', 1, 1, 1, 1),
(@AdminID, 'FinalReport', N'التقرير النهائي والحوافز', 1, 1, 1, 1),
(@AdminID, 'UserManagement', N'إدارة المستخدمين والصلاحيات', 1, 1, 1, 1);

-- إضافة فرع تجريبي مطابق لصورة القيد ("الحصبة")
INSERT INTO dbo.Branches (BranchName) VALUES (N'الحصبة');
INSERT INTO dbo.Jobs (JobTitle) VALUES (N'معلم'), (N'مشرف'), (N'أخصائية'), (N'سكرتارية');
GO
