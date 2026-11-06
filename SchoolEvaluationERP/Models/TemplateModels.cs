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

        public string EvaluatorWithGroupDisplay => string.IsNullOrWhiteSpace(GroupName)
            ? EvaluatorName
            : $"{EvaluatorName} ({GroupName})";
        public string VersionDisplay => $"النسخة v{VersionNumber}";
        public string CreatedAtDisplay => CreatedAt.ToString("yyyy-MM-dd");
        public string StatusDisplay => LinkedEvaluationsCount > 0
            ? $"مرتبط بـ ({LinkedEvaluationsCount}) تقييم شهري (التعديل ينشئ نسخة جديدة)"
            : "جاهز للتعديل المباشر";
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

            sheet.CriteriaColumns.Add(new CriteriaColumnModel { CriteriaTitle = "قواعد التقييم 1", MaxScore = 0m, ColumnOrder = 1 });
            sheet.CriteriaColumns.Add(new CriteriaColumnModel { CriteriaTitle = "قواعد التقييم 2", MaxScore = 0m, ColumnOrder = 2 });
            sheet.CriteriaColumns.Add(new CriteriaColumnModel { CriteriaTitle = "قواعد التقييم 3", MaxScore = 0m, ColumnOrder = 3 });

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
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public ObservableCollection<TemplateSheetModel> Sheets { get; set; } = new();
    }
}
