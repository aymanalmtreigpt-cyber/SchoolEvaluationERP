using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Models
{
    public class EmployeeRawMonthlyScoreItem
    {
        public int EmployeeID { get; set; }
        public int CategoryID { get; set; }
        public string BranchName { get; set; } = string.Empty;
        public string SystemName { get; set; } = string.Empty;
        public bool HasIncentive { get; set; }
        public decimal MonthlyScoreOut100 { get; set; }
    }

    public class CategoryIncentiveSettingRow : INotifyPropertyChanged
    {
        private decimal _maxIncentiveAmount = 7000m;
        private decimal _minIncentiveAmount = 1500m;
        private int _deservingEmployeesCount;
        private decimal _categoryTotalIncentiveAmount;
        private decimal _categoryAverageScore;
        private List<EmployeeRawMonthlyScoreItem> _categoryEmployeesScores = new();
        private bool _roundToNearest50 = true;
        private int _evalYear = DateTime.Now.Year;
        private int _evalMonth = DateTime.Now.Month;

        public Action? OnRowAmountsChanged { get; set; }

        public int RowNumber { get; set; }
        public int CategoryID { get; set; }
        public string CategoryName { get; set; } = string.Empty;
        public int TotalContinuingEmployeesCount { get; set; }
        public int IncentiveEligibleEmployeesCount { get; set; }

        public decimal MaxIncentiveAmount
        {
            get => _maxIncentiveAmount;
            set
            {
                decimal clean = value < 0m ? 0m : Math.Round(value, 2, MidpointRounding.AwayFromZero);
                if (_maxIncentiveAmount != clean)
                {
                    _maxIncentiveAmount = clean;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(MaxIncentiveText));
                    RecalculateLiveCategoryTotal();
                }
            }
        }

        public string MaxIncentiveText
        {
            get => _maxIncentiveAmount.ToString("0.##", CultureInfo.InvariantCulture);
            set
            {
                string norm = WeeklyScoreRowViewModel.NormalizeAsciiDigits((value ?? "0").Trim()).Replace(",", ".");
                if (string.IsNullOrWhiteSpace(norm))
                {
                    MaxIncentiveAmount = 0m;
                }
                else if (decimal.TryParse(norm, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsed))
                {
                    MaxIncentiveAmount = parsed;
                }
            }
        }

        public decimal MinIncentiveAmount
        {
            get => _minIncentiveAmount;
            set
            {
                decimal clean = value < 0m ? 0m : Math.Round(value, 2, MidpointRounding.AwayFromZero);
                if (_minIncentiveAmount != clean)
                {
                    _minIncentiveAmount = clean;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(MinIncentiveText));
                    RecalculateLiveCategoryTotal();
                }
            }
        }

        public string MinIncentiveText
        {
            get => _minIncentiveAmount.ToString("0.##", CultureInfo.InvariantCulture);
            set
            {
                string norm = WeeklyScoreRowViewModel.NormalizeAsciiDigits((value ?? "0").Trim()).Replace(",", ".");
                if (string.IsNullOrWhiteSpace(norm))
                {
                    MinIncentiveAmount = 0m;
                }
                else if (decimal.TryParse(norm, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsed))
                {
                    MinIncentiveAmount = parsed;
                }
            }
        }

        public int DeservingEmployeesCount
        {
            get => _deservingEmployeesCount;
            private set
            {
                if (_deservingEmployeesCount != value)
                {
                    _deservingEmployeesCount = value;
                    OnPropertyChanged();
                }
            }
        }

        public decimal CategoryAverageScore
        {
            get => _categoryAverageScore;
            private set
            {
                if (_categoryAverageScore != value)
                {
                    _categoryAverageScore = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CategoryAverageDisplay));
                }
            }
        }

        public decimal CategoryTotalIncentiveAmount
        {
            get => _categoryTotalIncentiveAmount;
            private set
            {
                if (_categoryTotalIncentiveAmount != value)
                {
                    _categoryTotalIncentiveAmount = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CategoryTotalIncentiveDisplay));
                }
            }
        }

        public string CategoryAverageDisplay => $"{CategoryAverageScore:0.##}%";
        public string CategoryTotalIncentiveDisplay => $"{CategoryTotalIncentiveAmount:N0} ريال";

        public void AttachMonthlyEmployeeScores(
            List<EmployeeRawMonthlyScoreItem> employeesScores,
            int year,
            int month,
            bool roundToNearest50)
        {
            _categoryEmployeesScores = employeesScores ?? new List<EmployeeRawMonthlyScoreItem>();
            _evalYear = year;
            _evalMonth = month;
            _roundToNearest50 = roundToNearest50;
            RecalculateLiveCategoryTotal();
        }

        public void SetRoundingMode(bool roundToNearest50)
        {
            if (_roundToNearest50 != roundToNearest50)
            {
                _roundToNearest50 = roundToNearest50;
                RecalculateLiveCategoryTotal();
            }
        }

        public void RecalculateLiveCategoryTotal()
        {
            if (_categoryEmployeesScores == null || _categoryEmployeesScores.Count == 0)
            {
                CategoryAverageScore = 0m;
                DeservingEmployeesCount = 0;
                CategoryTotalIncentiveAmount = 0m;
                OnRowAmountsChanged?.Invoke();
                return;
            }

            CategoryAverageScore = Math.Round(_categoryEmployeesScores.Average(e => e.MonthlyScoreOut100), 2, MidpointRounding.AwayFromZero);

            int deserving = 0;
            decimal sumIncentive = 0m;

            foreach (var emp in _categoryEmployeesScores)
            {
                if (!emp.HasIncentive) continue;

                var calc = IncentiveCalculatorService.CalculateMonthlyIncentive(
                    emp.SystemName,
                    emp.BranchName,
                    emp.MonthlyScoreOut100,
                    _maxIncentiveAmount,
                    _minIncentiveAmount,
                    _evalYear,
                    _evalMonth,
                    roundToNearest50: _roundToNearest50);

                if (calc.FinalAmount > 0m)
                {
                    deserving++;
                    sumIncentive += calc.FinalAmount;
                }
            }

            DeservingEmployeesCount = deserving;
            CategoryTotalIncentiveAmount = sumIncentive;
            OnRowAmountsChanged?.Invoke();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
