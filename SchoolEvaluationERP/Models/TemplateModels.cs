using System.Collections.Generic;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SchoolEvaluationERP.Models
{
    public class TemplateCategoryOptionDto
    {
        public int CategoryID { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public string DepartmentName { get; set; } = string.Empty;
        public int AssignedEmployeesCount { get; set; }

        public string DropdownDisplay => $"{CategoryName} [{AssignedEmployeesCount} موظف مسند]";
    }

    public class TemplateBranchOptionDto
    {
        public int? BranchID { get; set; } // null = جميع الفروع (كلاهما)
        public string BranchName { get; set; } = string.Empty;
        public int AssignedEmployeesCount { get; set; }

        public string DropdownDisplay => $"{BranchName} ({AssignedEmployeesCount} موظف)";
    }

    public class TemplateSummaryDto
    {
        public int TemplateID { get; set; }
        public Guid TemplateGroupGuid { get; set; }
        public int EvaluatorID { get; set; }
        public string EvaluatorName { get; set; } = string.Empty;
        public string GroupName { get; set; } = string.Empty;
        public int VersionNumber { get; set; }
        public DateTime CreatedAt { get; set; }
        public int SheetsCount { get; set; }
        public int TotalEmployeesCount { get; set; }
        public int LinkedEvaluationsCount { get; set; }
        public string SheetsSummary { get; set; } = string.Empty;
        public int RequestedYear { get; set; }
        public int RequestedMonth { get; set; }
        public int? EffectiveTemplateYear { get; set; }
        public int? EffectiveTemplateMonth { get; set; }
        public bool IsOwnMonthVersion { get; set; }
        public bool IsPeriodMonthLocked { get; set; }

        public string EvaluatorWithGroupDisplay => string.IsNullOrWhiteSpace(GroupName)
            ? EvaluatorName
            : $"{EvaluatorName} ({GroupName})";
        public string VersionDisplay => $"النسخة v{VersionNumber}";
        public string CreatedAtDisplay => CreatedAt.ToString("yyyy-MM-dd");
        public string StatusDisplay
        {
            get
            {
                if (IsPeriodMonthLocked)
                    return "🔒 شهر معتمد ومغلق نهائياً (للقراءة فقط)";

                if (EffectiveTemplateYear.HasValue && EffectiveTemplateMonth.HasValue)
                {
                    string effMonthName = Services.IncentiveCalculatorService.GetArabicMonthName(EffectiveTemplateMonth.Value);
                    if (IsOwnMonthVersion)
                    {
                        return LinkedEvaluationsCount > 0
                            ? $"✔ نسخة مستقلة للشهر (مرتبطة بـ {LinkedEvaluationsCount} تقييم)"
                            : "✔ نسخة مستقلة للشهر (جاهزة للتعديل)";
                    }
                    return $"🔗 موروث من ({effMonthName} {EffectiveTemplateYear}) — التعديل يحفظ نسخة مستقلة";
                }

                return LinkedEvaluationsCount > 0
                    ? $"مرتبط بـ ({LinkedEvaluationsCount}) تقييم شهري (التعديل ينشئ نسخة جديدة)"
                    : "جاهز للتعديل المباشر";
            }
        }

        public List<TemplateAssignedUserDto> AssignedUsers { get; set; } = new();
        public int AssignedUsersCount => AssignedUsers.Count;
        public bool HasNoAssignedUsers => AssignedUsers.Count == 0;
        public bool HasInactiveAssignedUsers => AssignedUsers.Any(u => !u.IsActive);

        public string AssignedUsersBadgeText
        {
            get
            {
                if (AssignedUsers.Count == 0)
                    return "⚠ غير مسند لمستخدم";
                if (AssignedUsers.Count == 1)
                    return AssignedUsers[0].IsActive
                        ? $"👤 {AssignedUsers[0].FullName}"
                        : $"⚠ {AssignedUsers[0].FullName} (موقوف)";
                string warn = HasInactiveAssignedUsers ? " ⚠" : string.Empty;
                return $"👥 {AssignedUsers[0].FullName} (+{AssignedUsers.Count - 1}){warn}";
            }
        }

        public string AssignedUsersToolTip
        {
            get
            {
                if (AssignedUsers.Count == 0)
                    return "تحذير: لم يتم إسناد هذا القالب لأي مستخدم بعد — اضغط لتحديد المستخدمين المسؤولين عن القالب";

                var lines = AssignedUsers.Select(u => u.IsActive
                    ? $"• {u.FullName} ({u.Username})"
                    : $"• {u.FullName} ({u.Username}) [⚠ حساب موقوف]");
                return "المستخدمون المسؤولون عن هذا القالب (اضغط للتعديل أو الإضافة أو الحذف):\n" + string.Join("\n", lines);
            }
        }
    }

    public class TemplateAssignedUserDto
    {
        public int UserID { get; set; }
        public string Username { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public bool IsAdmin { get; set; }
        public bool IsActive { get; set; } = true;

        public string DropdownDisplay => $"{FullName} ({Username})";
        public string ListDisplay => IsActive
            ? $"{FullName} ({Username})"
            : $"{FullName} ({Username}) — ⚠ حساب موقوف";
    }

    public class CriteriaColumnModel : INotifyPropertyChanged
    {
        private string _criteriaTitle = "قواعد التقييم";
        private decimal _maxScore = 0m;
        private int _columnOrder;

        public int CriteriaID { get; set; }

        public string CriteriaTitle
        {
            get => _criteriaTitle;
            set
            {
                if (_criteriaTitle != value)
                {
                    _criteriaTitle = string.IsNullOrWhiteSpace(value) ? "قواعد التقييم" : value.Trim();
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HeaderDisplay));
                }
            }
        }

        public decimal MaxScore
        {
            get => _maxScore;
            set
            {
                decimal clean = value < 0m ? 0m : Math.Round(value, 0, MidpointRounding.AwayFromZero);
                if (_maxScore != clean)
                {
                    _maxScore = clean;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HeaderDisplay));
                }
            }
        }

        public int ColumnOrder
        {
            get => _columnOrder;
            set
            {
                if (_columnOrder != value)
                {
                    _columnOrder = value;
                    OnPropertyChanged();
                }
            }
        }

        public string HeaderDisplay => $"{CriteriaTitle}\n({MaxScore:0} درجة)";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class SheetEmployeeRowModel : INotifyPropertyChanged
    {
        private int _rowOrder;

        public int EmployeeID { get; set; }
        public string EvaluationName { get; set; } = string.Empty;
        public string SystemName { get; set; } = string.Empty;
        public int BranchID { get; set; }
        public string BranchName { get; set; } = string.Empty;
        public string JobTitle { get; set; } = string.Empty;
        public string JobTypeName { get; set; } = string.Empty;
        public string CategoryName { get; set; } = string.Empty;
        public bool HasIncentive { get; set; } = true;
        public bool IsContinuing { get; set; } = true;
        public decimal EvaluatorWeightForEmployee { get; set; }

        public string FullJobDisplay => string.IsNullOrWhiteSpace(JobTypeName)
            ? JobTitle
            : $"{JobTitle} - {JobTypeName}";

        public string IncentiveBadgeDisplay => HasIncentive ? "بحافز" : "بدون حافز";

        public int RowOrder
        {
            get => _rowOrder;
            set
            {
                if (_rowOrder != value)
                {
                    _rowOrder = value;
                    OnPropertyChanged();
                }
            }
        }

        public string WeightBadgeDisplay => EvaluatorWeightForEmployee > 0
            ? $"{EvaluatorWeightForEmployee:0.##} درجة"
            : "غير مسند";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class TemplateSheetModel : INotifyPropertyChanged
    {
        private string _sheetName = "ورقة تقييم 1";
        private int? _targetCategoryId;
        private string _targetCategoryName = string.Empty;
        private int? _targetBranchId;
        private string _targetBranchName = "جميع الفروع (كلاهما)";
        private int _sortOrder = 1;

        public int SheetID { get; set; }

        public string SheetName
        {
            get => _sheetName;
            set
            {
                if (_sheetName != value)
                {
                    _sheetName = string.IsNullOrWhiteSpace(value) ? "ورقة تقييم" : value.Trim();
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(TabHeaderDisplay));
                }
            }
        }

        public int? TargetCategoryID
        {
            get => _targetCategoryId;
            set
            {
                if (_targetCategoryId != value)
                {
                    _targetCategoryId = value;
                    OnPropertyChanged();
                }
            }
        }

        public string TargetCategoryName
        {
            get => _targetCategoryName;
            set
            {
                if (_targetCategoryName != value)
                {
                    _targetCategoryName = (value ?? string.Empty).Trim();
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CategoryWithBranchDisplay));
                    OnPropertyChanged(nameof(TabHeaderDisplay));
                }
            }
        }

        /// <summary>
        /// null يعني جميع الفروع (كلاهما)، وأي رقم أكبر من 0 يعني فرعاً محدداً.
        /// </summary>
        public int? TargetBranchID
        {
            get => _targetBranchId;
            set
            {
                int? normalized = (value.HasValue && value.Value > 0) ? value.Value : null;
                if (_targetBranchId != normalized)
                {
                    _targetBranchId = normalized;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CategoryWithBranchDisplay));
                    OnPropertyChanged(nameof(TabHeaderDisplay));
                }
            }
        }

        public string TargetBranchName
        {
            get => _targetBranchName;
            set
            {
                string clean = string.IsNullOrWhiteSpace(value) ? "جميع الفروع (كلاهما)" : value.Trim();
                if (_targetBranchName != clean)
                {
                    _targetBranchName = clean;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CategoryWithBranchDisplay));
                    OnPropertyChanged(nameof(TabHeaderDisplay));
                }
            }
        }

        public int SortOrder
        {
            get => _sortOrder;
            set
            {
                if (_sortOrder != value)
                {
                    _sortOrder = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<CriteriaColumnModel> CriteriaColumns { get; set; } = new();
        public ObservableCollection<SheetEmployeeRowModel> EmployeeRows { get; set; } = new();

        public decimal TotalCriteriaScore => CriteriaColumns.Sum(c => c.MaxScore);
        public bool IsCriteriaTotalValid => TotalCriteriaScore == 100m;

        public string CategoryWithBranchDisplay
        {
            get
            {
                string branchPart = TargetBranchID.HasValue && TargetBranchID.Value > 0
                    ? TargetBranchName
                    : "جميع الفروع";
                if (string.IsNullOrWhiteSpace(TargetCategoryName))
                    return branchPart;
                return TargetCategoryName.Contains(" - ")
                    ? TargetCategoryName
                    : $"{TargetCategoryName} - {branchPart}";
            }
        }

        public string TabHeaderDisplay => $"{SheetName} ({CategoryWithBranchDisplay})";

        public static TemplateSheetModel CreateDefault(
            string sheetName,
            int categoryId,
            string categoryName,
            int? branchId,
            string branchName,
            int sortOrder)
        {
            var sheet = new TemplateSheetModel
            {
                SheetName = sheetName,
                TargetCategoryID = categoryId,
                TargetCategoryName = categoryName,
                TargetBranchID = (branchId.HasValue && branchId.Value > 0) ? branchId.Value : null,
                TargetBranchName = string.IsNullOrWhiteSpace(branchName) ? "جميع الفروع (كلاهما)" : branchName,
                SortOrder = sortOrder
            };

            sheet.CriteriaColumns.Add(new CriteriaColumnModel { CriteriaTitle = "أساسيات التقييم", MaxScore = 100m, ColumnOrder = 1 });

            return sheet;
        }

        public void ReindexOrders()
        {
            for (int i = 0; i < CriteriaColumns.Count; i++)
                CriteriaColumns[i].ColumnOrder = i + 1;

            for (int i = 0; i < EmployeeRows.Count; i++)
                EmployeeRows[i].RowOrder = i + 1;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public void NotifyTotalsChanged()
        {
            OnPropertyChanged(nameof(TotalCriteriaScore));
            OnPropertyChanged(nameof(IsCriteriaTotalValid));
            OnPropertyChanged(nameof(CategoryWithBranchDisplay));
            OnPropertyChanged(nameof(TabHeaderDisplay));
        }

        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class TemplateFullModel
    {
        public int TemplateID { get; set; }
        public Guid TemplateGroupGuid { get; set; } = Guid.NewGuid();
        public int EvaluatorID { get; set; }
        public string EvaluatorName { get; set; } = string.Empty;
        public string GroupName { get; set; } = string.Empty;
        public int VersionNumber { get; set; } = 1;
        public bool IsSystemTemplate { get; set; }
        public string SystemGroupCode { get; set; } = string.Empty;
        public int MaxAllowedCount { get; set; } = 3;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public int RequestedYear { get; set; }
        public int RequestedMonth { get; set; }
        public int? EffectiveTemplateYear { get; set; }
        public int? EffectiveTemplateMonth { get; set; }
        public bool IsOwnMonthVersion { get; set; }
        public bool IsMonthLocked { get; set; }
        public string LockedByUserName { get; set; } = string.Empty;

        public string MonthlyTemplateStatusText
        {
            get
            {
                if (RequestedYear <= 0 || RequestedMonth <= 0)
                    return string.Empty;

                string reqName = $"{Services.IncentiveCalculatorService.GetArabicMonthName(RequestedMonth)} {RequestedYear} م";

                if (IsMonthLocked)
                    return $"🔒 شهر ({reqName}) معتمد ومغلق نهائياً بواسطة ({LockedByUserName}) — شكل القالب محفوظ للقراءة فقط";

                if (IsSystemTemplate)
                    return $"⚙ قالب نظام تلقائي لشهر ({reqName})";

                if (IsOwnMonthVersion)
                    return $"✔ نسخة قالب مستقلة لشهر ({reqName}) — النسخة v{VersionNumber}";

                if (EffectiveTemplateYear.HasValue && EffectiveTemplateMonth.HasValue)
                {
                    string effName = $"{Services.IncentiveCalculatorService.GetArabicMonthName(EffectiveTemplateMonth.Value)} {EffectiveTemplateYear.Value} م";
                    return $"🔗 يرث تلقائياً شكل القالب من ({effName}) — أي تعديل سيحفظ نسخة مستقلة لشهر ({reqName}) دون التأثير على الشهور الأخرى";
                }

                return $"⚙ شكل القالب الافتراضي لشهر ({reqName}) — أي تعديل سيحفظ نسخة مستقلة لهذا الشهر";
            }
        }

        public ObservableCollection<TemplateSheetModel> Sheets { get; set; } = new();
    }

    public class EvaluatorEmployeeExportRowDto
    {
        public int RowNumber { get; set; }
        public int EmployeeID { get; set; }
        public string EvaluationName { get; set; } = string.Empty;
        public string SystemName { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;
        public string CategoryName { get; set; } = string.Empty;
        public string JobTitle { get; set; } = string.Empty;
        public string JobTypeName { get; set; } = string.Empty;
        public bool HasIncentive { get; set; } = true;
        public bool IsContinuing { get; set; } = true;

        public string IncentiveStatusText => HasIncentive ? "بحافز" : "بدون حافز";
        public string ContinuingStatusText => IsContinuing ? "مستمر" : "منقطع";
    }

    public class EvaluatorEmployeesSheetExportDto
    {
        public int EvaluatorID { get; set; }
        public string EvaluatorName { get; set; } = string.Empty;
        public int GroupID { get; set; }
        public string GroupName { get; set; } = string.Empty;
        public List<EvaluatorEmployeeExportRowDto> Employees { get; set; } = new();
    }
}






