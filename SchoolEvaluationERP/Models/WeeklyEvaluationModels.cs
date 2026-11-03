using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace SchoolEvaluationERP.Models
{
    public class MonthOptionItem
    {
        public int MonthNumber { get; set; }
        public string MonthArabicName { get; set; } = string.Empty;
        public string DisplayText => $"{MonthNumber} - {MonthArabicName}";

        public static List<MonthOptionItem> GetAllMonths() => new()
        {
            new() { MonthNumber = 1, MonthArabicName = "يناير" },
            new() { MonthNumber = 2, MonthArabicName = "فبراير" },
            new() { MonthNumber = 3, MonthArabicName = "مارس" },
            new() { MonthNumber = 4, MonthArabicName = "أبريل" },
            new() { MonthNumber = 5, MonthArabicName = "مايو" },
            new() { MonthNumber = 6, MonthArabicName = "يونيو" },
            new() { MonthNumber = 7, MonthArabicName = "يوليو" },
            new() { MonthNumber = 8, MonthArabicName = "أغسطس" },
            new() { MonthNumber = 9, MonthArabicName = "سبتمبر" },
            new() { MonthNumber = 10, MonthArabicName = "أكتوبر" },
            new() { MonthNumber = 11, MonthArabicName = "نوفمبر" },
            new() { MonthNumber = 12, MonthArabicName = "ديسمبر" }
        };
    }

    public class WeekStatusItem : INotifyPropertyChanged
    {
        private bool _isAlreadyEntered;
        private int? _existingRecordId;

        public int WeekNumber { get; set; }

        public bool IsAlreadyEntered
        {
            get => _isAlreadyEntered;
            set
            {
                if (_isAlreadyEntered != value)
                {
                    _isAlreadyEntered = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DisplayText));
                }
            }
        }

        public int? ExistingRecordId
        {
            get => _existingRecordId;
            set
            {
                if (_existingRecordId != value)
                {
                    _existingRecordId = value;
                    OnPropertyChanged();
                }
            }
        }

        public string DisplayText => IsAlreadyEntered
            ? $"الأسبوع ({WeekNumber}) — [ تم إدخاله مسبقاً ✔ ]"
            : $"الأسبوع ({WeekNumber}) — [ متاح للإدخال الجديد ]";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class WeeklyScoreRowViewModel : INotifyPropertyChanged
    {
        private decimal[] _scores = Array.Empty<decimal>();
        private decimal[] _maxScores = Array.Empty<decimal>();
        private int[] _criteriaIds = Array.Empty<int>();

        public int EmployeeID { get; set; }
        public int RowOrder { get; set; }
        public string EvaluationName { get; set; } = string.Empty;
        public string SystemName { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;
        public string JobTitle { get; set; } = string.Empty;
        public string CategoryName { get; set; } = string.Empty;
        public decimal EvaluatorWeight { get; set; }

        public void InitializeCriteria(IList<CriteriaColumnModel> criteriaColumns, Dictionary<int, decimal>? savedScoresByCriteriaId = null)
        {
            int count = criteriaColumns.Count;
            _scores = new decimal[count];
            _maxScores = new decimal[count];
            _criteriaIds = new int[count];

            for (int i = 0; i < count; i++)
            {
                _criteriaIds[i] = criteriaColumns[i].CriteriaID;
                _maxScores[i] = criteriaColumns[i].MaxScore;

                if (savedScoresByCriteriaId != null && savedScoresByCriteriaId.TryGetValue(_criteriaIds[i], out decimal val))
                {
                    _scores[i] = val;
                }
                else
                {
                    _scores[i] = 0m;
                }
            }

            NotifyCalculatedFields();
        }

        public int CriteriaCount => _scores.Length;
        public int GetCriteriaIdAt(int index) => _criteriaIds[index];
        public decimal GetMaxScoreAt(int index) => _maxScores[index];
        public decimal GetScoreAt(int index) => _scores[index];

        public void SetScoreAt(int index, decimal value)
        {
            if (index < 0 || index >= _scores.Length) return;

            decimal maxAllowed = _maxScores[index];
            decimal clamped = value < 0m ? 0m : (value > maxAllowed ? maxAllowed : value);
            clamped = Math.Round(clamped, 2, MidpointRounding.AwayFromZero);

            if (_scores[index] != clamped)
            {
                _scores[index] = clamped;
                OnPropertyChanged("Item[]");
                NotifyCalculatedFields();
            }
        }

        [System.Runtime.CompilerServices.IndexerName("Item")]
        public string this[int index]
        {
            get
            {
                if (index < 0 || index >= _scores.Length) return "0";
                return _scores[index].ToString("0.##", CultureInfo.InvariantCulture);
            }
            set
            {
                if (index < 0 || index >= _scores.Length) return;
                string clean = (value ?? "0").Trim().Replace(',', '.');
                if (string.IsNullOrEmpty(clean))
                {
                    SetScoreAt(index, 0m);
                    return;
                }

                if (decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsed))
                {
                    decimal maxAllowed = _maxScores[index];
                    if (parsed > maxAllowed)
                    {
                        System.Windows.MessageBox.Show(
                            $"الدرجة المدخلة ({parsed:0.##}) للموظف ({EvaluationName}) تتجاوز الدرجة القصوى المسموحة لهذا المبدأ وهي ({maxAllowed:0.##}) درجة.\nسيتم ضبطها على الحد الأقصى المسموح ({maxAllowed:0.##}).",
                            "تنبيه تجاوز درجة المبدأ",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                        parsed = maxAllowed;
                    }
                    else if (parsed < 0m)
                    {
                        parsed = 0m;
                    }

                    SetScoreAt(index, parsed);
                }
            }
        }

        public void FillFullMarks()
        {
            for (int i = 0; i < _scores.Length; i++)
            {
                _scores[i] = _maxScores[i];
            }
            OnPropertyChanged("Item[]");
            NotifyCalculatedFields();
        }

        public void ClearMarksToZero()
        {
            for (int i = 0; i < _scores.Length; i++)
            {
                _scores[i] = 0m;
            }
            OnPropertyChanged("Item[]");
            NotifyCalculatedFields();
        }

        public decimal RawTotalOutOf100 => Math.Round(_scores.Sum(), 2, MidpointRounding.AwayFromZero);
        public decimal WeightedScore => Math.Round((RawTotalOutOf100 / 100m) * EvaluatorWeight, 2, MidpointRounding.AwayFromZero);

        public string RawTotalDisplay => $"{RawTotalOutOf100:0.##} / 100";
        public string WeightedScoreDisplay => $"{WeightedScore:0.##} من {EvaluatorWeight:0.##}";

        private void NotifyCalculatedFields()
        {
            OnPropertyChanged(nameof(RawTotalOutOf100));
            OnPropertyChanged(nameof(WeightedScore));
            OnPropertyChanged(nameof(RawTotalDisplay));
            OnPropertyChanged(nameof(WeightedScoreDisplay));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public class WeeklyEntrySheetModel
    {
        public int SheetID { get; set; }
        public string SheetName { get; set; } = string.Empty;
        public string TargetCategoryName { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public List<CriteriaColumnModel> CriteriaColumns { get; set; } = new();
        public ObservableCollection<WeeklyScoreRowViewModel> EmployeeRows { get; set; } = new();
    }

    public class WeeklyEvaluationWorkspaceModel
    {
        public int RecordID { get; set; }
        public int TemplateID { get; set; }
        public int TemplateVersionNumber { get; set; }
        public int EvaluatorID { get; set; }
        public string EvaluatorName { get; set; } = string.Empty;
        public int EvalYear { get; set; }
        public int EvalMonth { get; set; }
        public int WeekNumber { get; set; }
        public DateTime? CreatedAt { get; set; }
        public string CreatedByUserName { get; set; } = string.Empty;
        public List<WeeklyEntrySheetModel> Sheets { get; set; } = new();
    }

    public class EvaluationRecordSummaryDto
    {
        public int RecordID { get; set; }
        public int TemplateID { get; set; }
        public int VersionNumber { get; set; }
        public int EvaluatorID { get; set; }
        public string EvaluatorName { get; set; } = string.Empty;
        public int EvalYear { get; set; }
        public int EvalMonth { get; set; }
        public int WeekNumber { get; set; }
        public int EvaluatedEmployeesCount { get; set; }
        public decimal AverageRawScore { get; set; }
        public DateTime CreatedAt { get; set; }
        public string CreatedByFullName { get; set; } = string.Empty;
        public DateTime? LastModifiedAt { get; set; }

        public string MonthArabicName => Services.IncentiveCalculatorService.GetArabicMonthName(EvalMonth);
        public string PeriodDisplay => $"{MonthArabicName} {EvalYear} م — الأسبوع ({WeekNumber})";
        public string VersionDisplay => $"v{VersionNumber}";
        public string AverageScoreDisplay => $"{AverageRawScore:0.##} %";
        public string CreatedAtDisplay => CreatedAt.ToString("yyyy-MM-dd hh:mm tt");
    }
}
