using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SchoolEvaluationERP.Models
{
    public class BranchDto
    {
        public int BranchID { get; set; }
        public string BranchName { get; set; } = string.Empty;
        public int EmployeesCount { get; set; }
    }

    public class DepartmentDto
    {
        public int DepartmentID { get; set; }
        public string DepartmentName { get; set; } = string.Empty;
        public string DepartmentType { get; set; } = "أكاديمي";
        public int CategoriesCount { get; set; }
        public int EmployeesCount { get; set; }

        public string DisplayWithType => $"{DepartmentName} ({DepartmentType})";
    }

    public class CategoryDto
    {
        public int CategoryID { get; set; }
        public int DepartmentID { get; set; }
        public string DepartmentName { get; set; } = string.Empty;
        public string DepartmentType { get; set; } = string.Empty;
        public string CategoryName { get; set; } = string.Empty;
        public int JobsCount { get; set; }
        public int EmployeesCount { get; set; }
        public decimal TotalWeights { get; set; }
        public string EvaluatorsSummary { get; set; } = string.Empty;

        public string DisplayFullName => CategoryName;
        public string WeightsStatusDisplay => TotalWeights == 100m
            ? "مكتمل (100 درجة) ✔"
            : $"غير مكتمل ({TotalWeights:0.##} / 100) ⚠";
    }

    public class JobDto
    {
        public int JobID { get; set; }
        public int CategoryID { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public int DepartmentID { get; set; }
        public string DepartmentName { get; set; } = string.Empty;
        public string JobTitle { get; set; } = string.Empty;
        public int JobTypesCount { get; set; }
        public int EmployeesCount { get; set; }

        public string DisplayWithCategory => $"{JobTitle} — ({CategoryName})";
    }

    public class JobTypeDto
    {
        public int JobTypeID { get; set; }
        public int JobID { get; set; }
        public string JobTitle { get; set; } = string.Empty;
        public int CategoryID { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public string JobTypeName { get; set; } = string.Empty;
        public int EmployeesCount { get; set; }

        public string DisplayWithJob => $"{JobTypeName} — ({JobTitle})";
    }

    public class EvaluatorDto
    {
        public int EvaluatorID { get; set; }
        public int GroupID { get; set; }
        public string GroupName { get; set; } = string.Empty;
        public string EvaluatorName { get; set; } = string.Empty;
        public int AssignedCategoriesCount { get; set; }
        public int AssignedEmployeesCount { get; set; }

        public string DisplayWithGroup => string.IsNullOrWhiteSpace(GroupName)
            ? EvaluatorName
            : $"{EvaluatorName} — [{GroupName}]";
    }

    public class EvaluatorGroupDto
    {
        public int GroupID { get; set; }
        public string GroupName { get; set; } = string.Empty;
        public int MembersCount { get; set; }
        public string MembersSummary { get; set; } = string.Empty;
        public int AssignedCategoriesCount { get; set; }
        public List<EvaluatorDto> Members { get; set; } = new();

        public string GroupTypeDisplay => MembersCount <= 1
            ? "مقيم مسؤول عام (شخص واحد)"
            : $"مجموعة مقيمين ({MembersCount} مقيمين)";
    }

    public class CategoryEvaluatorGroupSelectRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        private decimal _weightScore;

        public int GroupID { get; set; }
        public string GroupName { get; set; } = string.Empty;
        public int MembersCount { get; set; }
        public string MembersSummary { get; set; } = string.Empty;

        public string GroupDisplayInfo => MembersCount <= 1
            ? $"مسؤول عام: {MembersSummary}"
            : $"{MembersCount} مقيمين: {MembersSummary}";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                    if (!_isSelected && _weightScore != 0m)
                    {
                        WeightScore = 0m;
                    }
                }
            }
        }

        public decimal WeightScore
        {
            get => _weightScore;
            set
            {
                decimal clean = value < 0m ? 0m : Math.Round(value, 2, MidpointRounding.AwayFromZero);
                if (_weightScore != clean)
                {
                    _weightScore = clean;
                    OnPropertyChanged();
                    if (_weightScore > 0m && !_isSelected)
                    {
                        _isSelected = true;
                        OnPropertyChanged(nameof(IsSelected));
                    }
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class EmployeeEvaluatorMemberItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public int EvaluatorID { get; set; }
        public int GroupID { get; set; }
        public string EvaluatorName { get; set; } = string.Empty;
        public bool IsSingleMemberGroup { get; set; }

        public bool IsCheckEnabled => !IsSingleMemberGroup;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                bool effective = IsSingleMemberGroup ? true : value;
                if (_isSelected != effective)
                {
                    _isSelected = effective;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class EmployeeEvaluatorGroupSection : INotifyPropertyChanged
    {
        public int GroupID { get; set; }
        public string GroupName { get; set; } = string.Empty;
        public decimal GroupWeight { get; set; }
        public ObservableCollection<EmployeeEvaluatorMemberItem> Members { get; set; } = new();

        public int SelectedCount => Members.Count(m => m.IsSelected);
        public bool IsGroupCovered => SelectedCount > 0;

        public string HeaderDisplay => Members.Count <= 1
            ? $"👤 {GroupName} — الوزن المخصص: ({GroupWeight:0.##} درجة) [تلقائي لكامل الفئة]"
            : $"👥 {GroupName} — الوزن المخصص للمجموعة: ({GroupWeight:0.##} درجة)";

        public string ShareStatusDisplay
        {
            get
            {
                int count = SelectedCount;
                if (count == 0)
                    return "⚠ يرجى تحديد مقيم واحد على الأقل من هذه المجموعة";
                decimal share = Math.Round(GroupWeight / count, 2, MidpointRounding.AwayFromZero);
                return count == 1
                    ? $"✔ مقيم واحد محدد — يحصل على ({GroupWeight:0.##}) درجة كاملة"
                    : $"✔ تم تحديد ({count}) مقيمين — تقسم الدرجة عليهم ({share:0.##} درجة لكل مقيم) ويؤخذ متوسطهم";
            }
        }

        public void WireMemberNotifications(Action? onAnyChanged = null)
        {
            foreach (var m in Members)
            {
                m.PropertyChanged += (_, _) =>
                {
                    OnPropertyChanged(nameof(SelectedCount));
                    OnPropertyChanged(nameof(IsGroupCovered));
                    OnPropertyChanged(nameof(ShareStatusDisplay));
                    onAnyChanged?.Invoke();
                };
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class EmployeeDto
    {
        public int EmployeeID { get; set; }
        public string SystemName { get; set; } = string.Empty;
        public string EvaluationName { get; set; } = string.Empty;

        public int BranchID { get; set; }
        public string BranchName { get; set; } = string.Empty;

        public int DepartmentID { get; set; }
        public string DepartmentName { get; set; } = string.Empty;

        public int CategoryID { get; set; }
        public string CategoryName { get; set; } = string.Empty;

        public int JobID { get; set; }
        public string JobTitle { get; set; } = string.Empty;

        public int JobTypeID { get; set; }
        public string JobTypeName { get; set; } = string.Empty;

        public bool HasIncentive { get; set; } = true;
        public bool IsContinuing { get; set; } = true;

        public List<int> SelectedEvaluatorIds { get; set; } = new();

        public string FullJobDisplay => string.IsNullOrWhiteSpace(JobTypeName)
            ? JobTitle
            : $"{JobTitle} ({JobTypeName})";

        public string IncentiveStatusDisplay => HasIncentive ? "بحافز ✔" : "بدون حافز";
        public string ContinuingStatusDisplay => IsContinuing ? "مستمر 🟢" : "منقطع 🔴";

        public string AssignedEvaluatorsSummary { get; set; } = string.Empty;
        public string TemplateCoverageStatus { get; set; } = string.Empty;
        public bool IsCoverageComplete { get; set; }
    }
}
