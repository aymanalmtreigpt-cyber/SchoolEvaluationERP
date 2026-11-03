using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SchoolEvaluationERP.Models
{
    public class BranchDto
    {
        public int BranchID { get; set; }
        public string BranchName { get; set; } = string.Empty;
        public int CategoriesCount { get; set; }
        public int EmployeesCount { get; set; }
    }

    public class JobDto
    {
        public int JobID { get; set; }
        public string JobTitle { get; set; } = string.Empty;
        public int EmployeesCount { get; set; }
    }

    public class EvaluatorDto
    {
        public int EvaluatorID { get; set; }
        public string EvaluatorName { get; set; } = string.Empty;
        public int AssignedCategoriesCount { get; set; }
    }

    public class CategoryDto
    {
        public int CategoryID { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public int BranchID { get; set; }
        public string BranchName { get; set; } = string.Empty;
        public decimal TotalWeights { get; set; }
        public string EvaluatorsSummary { get; set; } = string.Empty;
        public int EmployeesCount { get; set; }

        public string DisplayFullName => $"{CategoryName} ({BranchName})";
    }

    public class CategoryEvaluatorSelectRow : INotifyPropertyChanged
    {
        private bool _isSelected;
        private decimal _weightScore;

        public int EvaluatorID { get; set; }
        public string EvaluatorName { get; set; } = string.Empty;

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
                if (_weightScore != value)
                {
                    _weightScore = value < 0m ? 0m : value;
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

    public class EmployeeDto
    {
        public int EmployeeID { get; set; }
        public string SystemName { get; set; } = string.Empty;
        public string EvaluationName { get; set; } = string.Empty;
        public int BranchID { get; set; }
        public string BranchName { get; set; } = string.Empty;
        public int JobID { get; set; }
        public string JobTitle { get; set; } = string.Empty;
        public int CategoryID { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public string AssignedEvaluatorsSummary { get; set; } = string.Empty;
        public string TemplateCoverageStatus { get; set; } = string.Empty;
        public bool IsCoverageComplete { get; set; }
    }
}
