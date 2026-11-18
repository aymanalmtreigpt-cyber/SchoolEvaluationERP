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
        private decimal _balancingDelta;

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

        // خصائص وضع الغياب والتأخير (إدخال عدد الأيام/المرات واحتساب الدرجة بالخصم التدريجي المتساوي)
        public bool IsCountBasedMode { get; set; }
        public int MaxAllowedCount { get; set; } = 3;
        public decimal? DirectCalculatedScoreOverride { get; set; }

        public string FullJobDisplay => string.IsNullOrWhiteSpace(JobTypeName)
            ? JobTitle
            : $"{JobTitle} - {JobTypeName}";

        public string IncentiveBadgeDisplay => HasIncentive ? "بحافز ✔" : "بدون حافز (أداء)";

        public decimal BalancingDelta
        {
            get => _balancingDelta;
            set
            {
                decimal clamped = Math.Clamp(Math.Round(value, 2, MidpointRounding.AwayFromZero), -100m, 100m);
                if (_balancingDelta != clamped)
                {
                    _balancingDelta = clamped;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(BalancingDeltaText));
                    OnPropertyChanged(nameof(BalancingDeltaDisplay));
                    NotifyCalculatedFields();
                }
            }
        }

        public string BalancingDeltaText
        {
            get => _balancingDelta.ToString("0.##", CultureInfo.InvariantCulture);
            set
            {
                string clean = NormalizeAsciiDigits((value ?? "0").Trim()).Replace(",", ".");
                if (clean.StartsWith("+")) clean = clean[1..].Trim();
                if (string.IsNullOrWhiteSpace(clean) || clean == "-")
                {
                    BalancingDelta = 0m;
                    return;
                }
                if (decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsed))
                {
                    BalancingDelta = parsed;
                }
            }
        }

        public string BalancingDeltaDisplay => _balancingDelta > 0m
            ? $"+{_balancingDelta:0.##}"
            : $"{_balancingDelta:0.##}";

        /// <summary>
        /// معادلة الخصم التدريجي المتساوي للغياب والتأخير:
        /// 0 -> 100% | أكبر من الحد المسموح -> 0% | بين 1 والحد المسموح -> تقسيم متساوٍ على (الحد المسموح + 1)
        /// </summary>
        public static decimal CalculateGradualScoreFromCount(int enteredCount, int maxAllowedCount)
        {
            if (enteredCount <= 0) return 100m;
            if (maxAllowedCount <= 0) return 0m;
            if (enteredCount > maxAllowedCount) return 0m;

            decimal steps = maxAllowedCount + 1m;
            decimal remainingSteps = steps - enteredCount;
            return Math.Round((remainingSteps / steps) * 100m, 2, MidpointRounding.AwayFromZero);
        }

        public void InitializeCriteria(
            IList<CriteriaColumnModel> criteriaColumns,
            Dictionary<int, decimal>? savedScoresByCriteriaId = null,
            decimal savedBalancingDelta = 0m,
            bool isCountBased = false,
            int maxAllowedCount = 3,
            Dictionary<int, int>? savedRawCountsByCriteriaId = null)
        {
            IsCountBasedMode = isCountBased;
            MaxAllowedCount = maxAllowedCount;
            DirectCalculatedScoreOverride = null;

            int count = criteriaColumns.Count;
            _scores = new int[count];
            _maxScores = new int[count];
            _criteriaIds = new int[count];
            _balancingDelta = Math.Round(savedBalancingDelta, 2, MidpointRounding.AwayFromZero);

            for (int i = 0; i < count; i++)
            {
                _criteriaIds[i] = criteriaColumns[i].CriteriaID;
                _maxScores[i] = isCountBased
                    ? 365
                    : (int)Math.Round(criteriaColumns[i].MaxScore, 0, MidpointRounding.AwayFromZero);

                if (isCountBased)
                {
                    if (savedRawCountsByCriteriaId != null && savedRawCountsByCriteriaId.TryGetValue(_criteriaIds[i], out int savedCnt))
                    {
                        _scores[i] = Math.Max(0, savedCnt);
                    }
                    else if (savedScoresByCriteriaId != null && savedScoresByCriteriaId.TryGetValue(_criteriaIds[i], out decimal savedScore))
                    {
                        // إذا كان مسجلاً من قبل بدرجة مئوية قديمة نحافظ على الدرجة المحفوظة حتى يعدل المستخدم العدد
                        DirectCalculatedScoreOverride = savedScore;
                        _scores[i] = 0;
                    }
                    else
                    {
                        _scores[i] = 0;
                    }
                }
                else
                {
                    if (savedScoresByCriteriaId != null && savedScoresByCriteriaId.TryGetValue(_criteriaIds[i], out decimal val))
                    {
                        _scores[i] = (int)Math.Round(val, 0, MidpointRounding.AwayFromZero);
                    }
                    else
                    {
                        _scores[i] = 0;
                    }
                }
            }

            OnPropertyChanged(nameof(BalancingDelta));
            OnPropertyChanged(nameof(BalancingDeltaText));
            OnPropertyChanged(nameof(BalancingDeltaDisplay));
            NotifyCalculatedFields();
        }

        public int CriteriaCount => _scores.Length;
        public int GetCriteriaIdAt(int index) => _criteriaIds[index];
        public int GetMaxScoreAt(int index) => _maxScores[index];
        public int GetScoreAt(int index) => _scores[index];

        /// <summary>
        /// يُرجع الدرجة الفعلية من 100 التي تُحفظ في جدول الدرجات (سواء كانت درجة عادية أو ناتج تحويل أيام الغياب/التأخير).
        /// </summary>
        public decimal GetEffectiveCriteriaScoreForSave(int index)
        {
            if (index < 0 || index >= _scores.Length) return 0m;
            if (IsCountBasedMode)
            {
                if (DirectCalculatedScoreOverride.HasValue)
                    return DirectCalculatedScoreOverride.Value;
                return CalculateGradualScoreFromCount(_scores[index], MaxAllowedCount);
            }
            return _scores[index];
        }

        public void SetScoreAt(int index, int value)
        {
            if (index < 0 || index >= _scores.Length) return;

            DirectCalculatedScoreOverride = null;
            int maxAllowed = IsCountBasedMode ? 365 : _maxScores[index];
            int clamped = value < 0 ? 0 : (value > maxAllowed ? maxAllowed : value);

            if (_scores[index] != clamped)
            {
                _scores[index] = clamped;
                OnPropertyChanged("Item[]");
                NotifyCalculatedFields();
            }
            else
            {
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

                clean = NormalizeAsciiDigits(clean);

                if (int.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    if (!IsCountBasedMode)
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
                    }

                    if (parsed < 0) parsed = 0;
                    SetScoreAt(index, parsed);
                }
                else if (decimal.TryParse(clean.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal decVal))
                {
                    int rounded = (int)Math.Round(decVal, 0, MidpointRounding.AwayFromZero);
                    if (!IsCountBasedMode)
                    {
                        int maxAllowed = _maxScores[index];
                        if (rounded > maxAllowed) rounded = maxAllowed;
                    }
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
            DirectCalculatedScoreOverride = null;
            for (int i = 0; i < _scores.Length; i++)
            {
                // في وضع الغياب والتأخير: الدرجة الكاملة (100%) تعني أن عدد أيام الغياب/التأخير = 0
                _scores[i] = IsCountBasedMode ? 0 : _maxScores[i];
            }
            OnPropertyChanged("Item[]");
            NotifyCalculatedFields();
        }

        public void ClearMarksToZero()
        {
            DirectCalculatedScoreOverride = null;
            for (int i = 0; i < _scores.Length; i++)
            {
                // في وضع الغياب والتأخير: تصفير الدرجة (0%) يعني تجاوز الحد المسموح (MaxAllowedCount + 1)
                _scores[i] = IsCountBasedMode ? (MaxAllowedCount + 1) : 0;
            }
            OnPropertyChanged("Item[]");
            NotifyCalculatedFields();
        }

        public decimal RawCriteriaSumOutOf100
        {
            get
            {
                if (IsCountBasedMode)
                {
                    if (DirectCalculatedScoreOverride.HasValue)
                        return DirectCalculatedScoreOverride.Value;
                    int countVal = _scores.Length > 0 ? _scores[0] : 0;
                    return CalculateGradualScoreFromCount(countVal, MaxAllowedCount);
                }
                return _scores.Sum();
            }
        }

        public decimal RawTotalOutOf100 => Math.Clamp(
            Math.Round(RawCriteriaSumOutOf100 + _balancingDelta, 2, MidpointRounding.AwayFromZero),
            0m,
            100m);

        public decimal WeightedScore => Math.Round((RawTotalOutOf100 / 100m) * EvaluatorWeight, 2, MidpointRounding.AwayFromZero);

        public string RawCriteriaSumDisplay => $"{RawCriteriaSumOutOf100:0.##} / 100";
        public string RawTotalDisplay => $"{RawTotalOutOf100:0.##} / 100";
        public string WeightedScoreDisplay => $"{WeightedScore:0.##} من {EvaluatorWeight:0.##}";

        private void NotifyCalculatedFields()
        {
            OnPropertyChanged(nameof(RawCriteriaSumOutOf100));
            OnPropertyChanged(nameof(RawCriteriaSumDisplay));
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
        public bool CanBeModified { get; set; } = true;
        public bool IsCountBasedEvaluation { get; set; }
        public string SystemGroupCode { get; set; } = string.Empty;
        public int MaxAllowedCount { get; set; } = 3;
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
        public string DropdownRecordDisplay => $"{EvaluatorWithGroupDisplay} — [{EvaluatedEmployeesCount} موظف | المتوسط: {AverageRawScore:0.##}%]";
        public string VersionDisplay => $"v{VersionNumber}";
        public string AverageScoreDisplay => $"{AverageRawScore:0.##} %";
        public string CreatedAtDisplay => CreatedAt.ToString("yyyy-MM-dd hh:mm tt");
    }
}
