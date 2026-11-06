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

    public class MonthlyRecordStatusDto
    {
        public bool IsAlreadyEntered { get; set; }
        public int? ExistingRecordId { get; set; }
    }

    public class WeeklyScoreRowViewModel : INotifyPropertyChanged
    {
        private int[] _scores = Array.Empty<int>();
        private int[] _maxScores = Array.Empty<int>();
        private int[] _criteriaIds = Array.Empty<int>();

        public int EmployeeID { get; set; }
        public int RowOrder { get; set; }
        public string EvaluationName { get; set; } = string.Empty;
        public string SystemName { get; set; } = string.Empty;
        public string BranchName { get; set; } = string.Empty;
        public string JobTitle { get; set; } = string.Empty;
        public string JobTypeName { get; set; } = string.Empty;
        public string CategoryName { get; set; } = string.Empty;
        public bool HasIncentive { get; set; } = true;
        public bool IsContinuing { get; set; } = true;
        public decimal EvaluatorWeight { get; set; }

        public string FullJobDisplay => string.IsNullOrWhiteSpace(JobTypeName)
            ? JobTitle
            : $"{JobTitle} - {JobTypeName}";

        public string IncentiveBadgeDisplay => HasIncentive ? "بحافز ✔" : "بدون حافز (أداء)";

        public void InitializeCriteria(IList<CriteriaColumnModel> criteriaColumns, Dictionary<int, decimal>? savedScoresByCriteriaId = null)
        {
            int count = criteriaColumns.Count;
            _scores = new int[count];
            _maxScores = new int[count];
            _criteriaIds = new int[count];

            for (int i = 0; i < count; i++)
            {
                _criteriaIds[i] = criteriaColumns[i].CriteriaID;
                _maxScores[i] = (int)Math.Round(criteriaColumns[i].MaxScore, 0, MidpointRounding.AwayFromZero);

                if (savedScoresByCriteriaId != null && savedScoresByCriteriaId.TryGetValue(_criteriaIds[i], out decimal val))
                {
                    _scores[i] = (int)Math.Round(val, 0, MidpointRounding.AwayFromZero);
                }
                else
                {
                    _scores[i] = 0;
                }
            }

            NotifyCalculatedFields();
        }

        public int CriteriaCount => _scores.Length;
        public int GetCriteriaIdAt(int index) => _criteriaIds[index];
        public int GetMaxScoreAt(int index) => _maxScores[index];
        public int GetScoreAt(int index) => _scores[index];

        public void SetScoreAt(int index, int value)
        {
            if (index < 0 || index >= _scores.Length) return;

            int maxAllowed = _maxScores[index];
            int clamped = value < 0 ? 0 : (value > maxAllowed ? maxAllowed : value);

            if (_scores[index] != clamped)
            {
                _scores[index] = clamped;
                OnPropertyChanged("Item[]");
                NotifyCalculatedFields();
            }
        }

        [IndexerName("Item")]
        public string this[int index]
        {
            get
            {
                if (index < 0 || index >= _scores.Length) return "0";
                return _scores[index].ToString(CultureInfo.InvariantCulture);
            }
            set
            {
                if (index < 0 || index >= _scores.Length) return;
                string clean = (value ?? "0").Trim();
                if (string.IsNullOrEmpty(clean))
                {
                    SetScoreAt(index, 0);
                    return;
                }

                // تحويل الأرقام الهندية/العربية إن وجدت إلى أرقام قياسية لقبول إدخال مثل ٠٥ -> 5
                clean = NormalizeAsciiDigits(clean);

                if (int.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    int maxAllowed = _maxScores[index];
                    if (parsed > maxAllowed)
                    {
                        System.Windows.MessageBox.Show(
                            $"الدرجة المدخلة ({parsed}) للموظف ({EvaluationName}) تتجاوز الدرجة القصوى المسموحة لهذا المبدأ وهي ({maxAllowed}) درجة.\nسيتم ضبطها على الحد الأقصى المسموح ({maxAllowed}).",
                            "تنبيه تجاوز درجة المبدأ",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                        parsed = maxAllowed;
                    }
                    else if (parsed < 0)
                    {
                        parsed = 0;
                    }

                    SetScoreAt(index, parsed);
                }
                else if (decimal.TryParse(clean.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal decVal))
                {
                    int rounded = (int)Math.Round(decVal, 0, MidpointRounding.AwayFromZero);
                    int maxAllowed = _maxScores[index];
                    if (rounded > maxAllowed) rounded = maxAllowed;
                    if (rounded < 0) rounded = 0;
                    SetScoreAt(index, rounded);
                }
            }
        }

        public static string NormalizeAsciiDigits(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            char[] chars = input.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (chars[i] >= '\u0660' && chars[i] <= '\u0669')
                    chars[i] = (char)('0' + (chars[i] - '\u0660'));
                else if (chars[i] >= '\u06F0' && chars[i] <= '\u06F9')
                    chars[i] = (char)('0' + (chars[i] - '\u06F0'));
            }
            return new string(chars);
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
                _scores[i] = 0;
            }
            OnPropertyChanged("Item[]");
            NotifyCalculatedFields();
        }

        public decimal RawTotalOutOf100 => _scores.Sum();
        public decimal WeightedScore => Math.Round((RawTotalOutOf100 / 100m) * EvaluatorWeight, 2, MidpointRounding.AwayFromZero);

        public string RawTotalDisplay => $"{RawTotalOutOf100:0} / 100";
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
        public string GroupName { get; set; } = string.Empty;
        public int EvalYear { get; set; }
        public int EvalMonth { get; set; }
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
        public string GroupName { get; set; } = string.Empty;
        public int EvalYear { get; set; }
        public int EvalMonth { get; set; }
        public int EvaluatedEmployeesCount { get; set; }
        public decimal AverageRawScore { get; set; }
        public DateTime CreatedAt { get; set; }
        public string CreatedByFullName { get; set; } = string.Empty;
        public DateTime? LastModifiedAt { get; set; }

        public string MonthArabicName => Services.IncentiveCalculatorService.GetArabicMonthName(EvalMonth);
        public string PeriodDisplay => $"شهر {MonthArabicName} {EvalYear} م";
        public string EvaluatorWithGroupDisplay => string.IsNullOrWhiteSpace(GroupName) ? EvaluatorName : $"{EvaluatorName} ({GroupName})";
        public string VersionDisplay => $"v{VersionNumber}";
        public string AverageScoreDisplay => $"{AverageRawScore:0.##} %";
        public string CreatedAtDisplay => CreatedAt.ToString("yyyy-MM-dd hh:mm tt");
    }
}
