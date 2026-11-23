using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public class FilterOptionItem
    {
        public int? Value { get; set; }
        public string DisplayText { get; set; } = string.Empty;
    }

    public partial class HistoryLogView : UserControl
    {
        private readonly WeeklyEvaluationService _service = new();
        private WeeklyEvaluationWorkspaceModel? _currentRecordWorkspace;
        private WeeklyEntrySheetModel? _activeDetailSheet;
        private bool _isInitializingFilters;

        public HistoryLogView()
        {
            _isInitializingFilters = true;
            InitializeComponent();
            _isInitializingFilters = false;

            Loaded += async (_, _) =>
            {
                InitializeFilterCombos();
                await RefreshHistoryRecordsGridAsync();
            };
        }

        private void InitializeFilterCombos()
        {
            _isInitializingFilters = true;

            int currentYear = DateTime.Now.Year;
            var yearItems = new List<FilterOptionItem>
            {
                new() { Value = null, DisplayText = "كل السنوات" },
                new() { Value = currentYear - 1, DisplayText = (currentYear - 1).ToString() },
                new() { Value = currentYear, DisplayText = currentYear.ToString() },
                new() { Value = currentYear + 1, DisplayText = (currentYear + 1).ToString() },
                new() { Value = currentYear + 2, DisplayText = (currentYear + 2).ToString() }
            };
            CmbFilterYear.ItemsSource = yearItems;
            CmbFilterYear.SelectedIndex = 0;

            var monthItems = new List<FilterOptionItem>
            {
                new() { Value = null, DisplayText = "كل الشهور" }
            };
            foreach (var m in MonthOptionItem.GetAllMonths())
            {
                monthItems.Add(new FilterOptionItem { Value = m.MonthNumber, DisplayText = m.DisplayText });
            }
            CmbFilterMonth.ItemsSource = monthItems;
            CmbFilterMonth.SelectedIndex = 0;

            _isInitializingFilters = false;
        }

        private async Task RefreshHistoryRecordsGridAsync()
        {
            if (_isInitializingFilters) return;

            string evalSearch = TxtSearchEvaluator.Text.Trim();
            int? yearFilter = (CmbFilterYear.SelectedItem as FilterOptionItem)?.Value;
            int? monthFilter = (CmbFilterMonth.SelectedItem as FilterOptionItem)?.Value;

            var records = await _service.GetEvaluationHistoryLogsAsync(evalSearch, yearFilter, monthFilter);
            GridHistoryRecords.ItemsSource = records;
            TxtRecordsCountBadge.Text = $"إجمالي السجلات المحفوظة: {records.Count}";
        }

        private async void FilterControl_Changed(object sender, TextChangedEventArgs e)
        {
            await RefreshHistoryRecordsGridAsync();
        }

        private async void FilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await RefreshHistoryRecordsGridAsync();
        }

        private async void BtnResetFilters_Click(object sender, RoutedEventArgs e)
        {
            _isInitializingFilters = true;
            TxtSearchEvaluator.Text = string.Empty;
            CmbFilterYear.SelectedIndex = 0;
            CmbFilterMonth.SelectedIndex = 0;
            _isInitializingFilters = false;

            await RefreshHistoryRecordsGridAsync();
        }

        private async void GridHistoryRecords_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GridHistoryRecords.SelectedItem is EvaluationRecordSummaryDto selected)
            {
                await LoadRecordIntoDetailsAsync(selected.RecordID, switchToDetailsTab: false);
            }
        }

        private async void GridHistoryRecords_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (GridHistoryRecords.SelectedItem is EvaluationRecordSummaryDto selected)
            {
                await LoadRecordIntoDetailsAsync(selected.RecordID, switchToDetailsTab: true);
            }
        }

        private async void BtnViewSelectedRecord_Click(object sender, RoutedEventArgs e)
        {
            if (GridHistoryRecords.SelectedItem is not EvaluationRecordSummaryDto selected)
            {
                MessageBox.Show("يرجى تحديد سجل تقييم شهري من الجدول أولاً لعرض درجاته.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await LoadRecordIntoDetailsAsync(selected.RecordID, switchToDetailsTab: true);
        }

        private async Task LoadRecordIntoDetailsAsync(int recordId, bool switchToDetailsTab)
        {
            var workspace = await _service.LoadSavedRecordWorkspaceAsync(recordId);
            if (workspace == null)
            {
                MessageBox.Show("تعذر تحميل درجات سجل التقييم المحدد.", "خطأ", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _currentRecordWorkspace = workspace;
            TxtFilterSheetEmployee.Text = string.Empty;

            if (switchToDetailsTab)
            {
                MainHistoryTabControl.SelectedItem = TabRecordDetails;
            }

            if (_currentRecordWorkspace.Sheets.Count > 0)
            {
                ActivateDetailSheet(_currentRecordWorkspace.Sheets[0]);
            }
        }

        private void ActivateDetailSheet(WeeklyEntrySheetModel sheet)
        {
            if (_currentRecordWorkspace == null) return;
            _activeDetailSheet = sheet;

            string monthName = IncentiveCalculatorService.GetArabicMonthName(_currentRecordWorkspace.EvalMonth);
            string groupInfo = string.IsNullOrWhiteSpace(_currentRecordWorkspace.GroupName) ? "" : $" [{_currentRecordWorkspace.GroupName}]";
            TxtDetailsHeaderTitle.Text = $"المقيم: {_currentRecordWorkspace.EvaluatorName}{groupInfo}  |  شهر {monthName} {_currentRecordWorkspace.EvalYear} م  |  نسخة القالب: v{_currentRecordWorkspace.TemplateVersionNumber}";

            string lockNote = _currentRecordWorkspace.CanBeModified
                ? "نشط (يمكن تعديله من شاشة 4. إدخال التقييمات الشهرية)"
                : "مقفل تاريخياً (تم حذف القالب أو الموظفين من النظام)";

            TxtDetailsSubInfo.Text = $"أُدخل بواسطة: {_currentRecordWorkspace.CreatedByUserName}   |   تاريخ الإدخال: {_currentRecordWorkspace.CreatedAt:yyyy-MM-dd hh:mm tt}   |   حالة القالب: {lockNote}";

            BadgeDetailsMode.Visibility = Visibility.Visible;
            UpdateTemplateOverallSummaryBadges();

            ApplyEmployeeSearchFilterToDetailGrid();
        }

        private void RebuildDetailScoresDataGrid()
        {
            GridRecordSheetScores.Columns.Clear();
            if (_activeDetailSheet == null) return;

            var wrapStyle = TryFindResource("WrappingCellTextStyle") as Style;

            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "م",
                Binding = new Binding("RowOrder"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 45,
                MinWidth = 45
            });

            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الاسم التقييمي للموظف",
                Binding = new Binding("EvaluationName"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 195,
                MinWidth = 170
            });

            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الفرع",
                Binding = new Binding("BranchName"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 85,
                MinWidth = 75
            });

            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الوظيفة ونوعها",
                Binding = new Binding("FullJobDisplay"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 140,
                MinWidth = 120
            });

            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الحافز",
                Binding = new Binding("IncentiveBadgeDisplay"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 95,
                MinWidth = 85
            });

            var scoreDisplayStyle = new Style(typeof(TextBlock));
            scoreDisplayStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            scoreDisplayStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            scoreDisplayStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            scoreDisplayStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 14.0));
            scoreDisplayStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(30, 64, 175))));

            for (int i = 0; i < _activeDetailSheet.CriteriaColumns.Count; i++)
            {
                var crit = _activeDetailSheet.CriteriaColumns[i];

                var critHdr = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = $"{crit.CriteriaTitle} (من {crit.MaxScore:0} درجة)"
                };
                critHdr.Children.Add(new TextBlock
                {
                    Text = crit.CriteriaTitle,
                    FontSize = 12,
                    LineHeight = 17,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    MaxHeight = 54,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Center,
                    MaxWidth = _activeDetailSheet.CriteriaColumns.Count <= 3 ? 300 : 195,
                    FontWeight = FontWeights.Bold
                });
                critHdr.Children.Add(new TextBlock
                {
                    Text = $"(من {crit.MaxScore:0} درجة)",
                    FontSize = 11,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 1, 0, 0),
                    Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85))
                });

                var col = new DataGridTextColumn
                {
                    Header = critHdr,
                    Binding = new Binding($"[{i}]"),
                    ElementStyle = scoreDisplayStyle,
                    IsReadOnly = true,
                    Width = _activeDetailSheet.CriteriaColumns.Count <= 3
                        ? new DataGridLength(1, DataGridLengthUnitType.Star)
                        : new DataGridLength(205),
                    MinWidth = 140
                };

                GridRecordSheetScores.Columns.Add(col);
            }

            if (_activeDetailSheet.EmployeeRows.Any(r => r.BalancingDelta != 0m))
            {
                var deltaStyle = new Style(typeof(TextBlock));
                deltaStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
                deltaStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
                deltaStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
                deltaStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(180, 83, 9))));

                GridRecordSheetScores.Columns.Add(new DataGridTextColumn
                {
                    Header = "⚖ نسبة الموازنة",
                    Binding = new Binding("BalancingDeltaDisplay"),
                    ElementStyle = deltaStyle,
                    IsReadOnly = true,
                    Width = 105,
                    MinWidth = 95
                });
            }

            var totalStyle = new Style(typeof(TextBlock));
            totalStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            totalStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            totalStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            totalStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(4, 120, 87))));

            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "مجموع الورقة\n(من 100)",
                Binding = new Binding("RawTotalDisplay"),
                ElementStyle = totalStyle,
                IsReadOnly = true,
                Width = 105,
                MinWidth = 95
            });

            var weightedStyle = new Style(typeof(TextBlock));
            weightedStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            weightedStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            weightedStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            weightedStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(15, 23, 42))));

            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "حصة المقيم\nالموزونة",
                Binding = new Binding("WeightedScoreDisplay"),
                ElementStyle = weightedStyle,
                IsReadOnly = true,
                Width = 115,
                MinWidth = 105
            });
        }

        private class CrossSheetHistorySearchRow
        {
            public int RowOrder { get; set; }
            public WeeklyEntrySheetModel SourceSheet { get; set; } = null!;
            public WeeklyScoreRowViewModel SourceEmployee { get; set; } = null!;
            public string SheetDisplay => $"{SourceSheet.SheetName} ({SourceSheet.TargetCategoryName})";
            public string EvaluationName => SourceEmployee.EvaluationName;
            public string BranchName => SourceEmployee.BranchName;
            public string FullJobDisplay => SourceEmployee.FullJobDisplay;
            public string IncentiveBadgeDisplay => SourceEmployee.IncentiveBadgeDisplay;
            public string CriteriaSummary { get; set; } = string.Empty;
            public string BalancingDeltaDisplay => SourceEmployee.BalancingDeltaDisplay;
            public string RawTotalDisplay => SourceEmployee.RawTotalDisplay;
            public string WeightedScoreDisplay => SourceEmployee.WeightedScoreDisplay;
        }

        private static string NormalizeArabicSearch(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            return text.Trim()
                .Replace('أ', 'ا')
                .Replace('إ', 'ا')
                .Replace('آ', 'ا')
                .Replace('ة', 'ه')
                .Replace('ى', 'ي')
                .Replace("ـ", string.Empty);
        }

        private static bool MatchesEmployeeSearch(WeeklyEntrySheetModel sheet, WeeklyScoreRowViewModel emp, string normFilter)
        {
            return NormalizeArabicSearch(emp.EvaluationName).Contains(normFilter, StringComparison.OrdinalIgnoreCase)
                || NormalizeArabicSearch(emp.SystemName).Contains(normFilter, StringComparison.OrdinalIgnoreCase)
                || NormalizeArabicSearch(emp.BranchName).Contains(normFilter, StringComparison.OrdinalIgnoreCase)
                || NormalizeArabicSearch(emp.FullJobDisplay).Contains(normFilter, StringComparison.OrdinalIgnoreCase)
                || NormalizeArabicSearch(emp.CategoryName).Contains(normFilter, StringComparison.OrdinalIgnoreCase)
                || NormalizeArabicSearch(sheet.SheetName).Contains(normFilter, StringComparison.OrdinalIgnoreCase)
                || NormalizeArabicSearch(sheet.TargetCategoryName).Contains(normFilter, StringComparison.OrdinalIgnoreCase);
        }

        private string BuildCriteriaSummary(WeeklyEntrySheetModel sheet, WeeklyScoreRowViewModel emp)
        {
            var parts = new List<string>();
            for (int i = 0; i < Math.Min(sheet.CriteriaColumns.Count, emp.CriteriaCount); i++)
            {
                var crit = sheet.CriteriaColumns[i];
                if (_currentRecordWorkspace != null && _currentRecordWorkspace.IsCountBasedEvaluation)
                {
                    parts.Add($"{crit.CriteriaTitle}: {emp[i]}");
                }
                else
                {
                    parts.Add($"{crit.CriteriaTitle}: {emp[i]}/{crit.MaxScore:0}");
                }
            }
            return string.Join("  |  ", parts);
        }

        private void RebuildCrossSheetSearchDataGrid(bool anyBalancingDelta)
        {
            GridRecordSheetScores.Columns.Clear();
            var wrapStyle = TryFindResource("WrappingCellTextStyle") as Style;

            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "م",
                Binding = new Binding("RowOrder"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 45,
                MinWidth = 45
            });

            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "ورقة التقييم",
                Binding = new Binding("SheetDisplay"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 175,
                MinWidth = 140
            });

            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الاسم التقييمي للموظف",
                Binding = new Binding("EvaluationName"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 190,
                MinWidth = 165
            });

            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الفرع",
                Binding = new Binding("BranchName"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 85,
                MinWidth = 75
            });

            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الوظيفة ونوعها",
                Binding = new Binding("FullJobDisplay"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 135,
                MinWidth = 115
            });

            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الحافز",
                Binding = new Binding("IncentiveBadgeDisplay"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 90,
                MinWidth = 85
            });

            var scoreStyle = new Style(typeof(TextBlock));
            scoreStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            scoreStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            scoreStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            scoreStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(30, 64, 175))));

            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "تفصيل الدرجات",
                Binding = new Binding("CriteriaSummary"),
                ElementStyle = scoreStyle,
                IsReadOnly = true,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 160
            });

            if (anyBalancingDelta)
            {
                var deltaStyle = new Style(typeof(TextBlock));
                deltaStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
                deltaStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
                deltaStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
                deltaStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(180, 83, 9))));

                GridRecordSheetScores.Columns.Add(new DataGridTextColumn
                {
                    Header = "⚖ نسبة الموازنة",
                    Binding = new Binding("BalancingDeltaDisplay"),
                    ElementStyle = deltaStyle,
                    IsReadOnly = true,
                    Width = 105,
                    MinWidth = 95
                });
            }

            var totalStyle = new Style(typeof(TextBlock));
            totalStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            totalStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            totalStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            totalStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(4, 120, 87))));

            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "مجموع الورقة\n(من 100)",
                Binding = new Binding("RawTotalDisplay"),
                ElementStyle = totalStyle,
                IsReadOnly = true,
                Width = 105,
                MinWidth = 95
            });

            var weightedStyle = new Style(typeof(TextBlock));
            weightedStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            weightedStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            weightedStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            weightedStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(15, 23, 42))));

            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "حصة المقيم\nالموزونة",
                Binding = new Binding("WeightedScoreDisplay"),
                ElementStyle = weightedStyle,
                IsReadOnly = true,
                Width = 115,
                MinWidth = 105
            });
        }

        private void ApplyEmployeeSearchFilterToDetailGrid()
        {
            if (_currentRecordWorkspace == null || _activeDetailSheet == null) return;

            string filterText = TxtFilterSheetEmployee.Text.Trim();
            if (string.IsNullOrEmpty(filterText))
            {
                RebuildDetailScoresDataGrid();
                var rows = _activeDetailSheet.EmployeeRows.ToList();
                GridRecordSheetScores.ItemsSource = null;
                GridRecordSheetScores.ItemsSource = rows;
                TxtDetailEmployeesCount.Text = $"عدد الموظفين المعروضين: {rows.Count} من {_activeDetailSheet.EmployeeRows.Count}";
                RebuildDetailBottomSheetTabsBar();
                UpdateDetailSheetAverageBadge();
                return;
            }

            string normFilter = NormalizeArabicSearch(filterText);
            var matchedRows = new List<CrossSheetHistorySearchRow>();
            var matchCountsBySheet = new Dictionary<WeeklyEntrySheetModel, int>();
            int totalWorkspaceEmps = 0;

            foreach (var sheet in _currentRecordWorkspace.Sheets)
            {
                totalWorkspaceEmps += sheet.EmployeeRows.Count;
                int sheetMatches = 0;
                foreach (var emp in sheet.EmployeeRows)
                {
                    if (MatchesEmployeeSearch(sheet, emp, normFilter))
                    {
                        sheetMatches++;
                        matchedRows.Add(new CrossSheetHistorySearchRow
                        {
                            RowOrder = matchedRows.Count + 1,
                            SourceSheet = sheet,
                            SourceEmployee = emp,
                            CriteriaSummary = BuildCriteriaSummary(sheet, emp)
                        });
                    }
                }
                matchCountsBySheet[sheet] = sheetMatches;
            }

            bool anyDelta = matchedRows.Any(r => r.SourceEmployee.BalancingDelta != 0m);
            RebuildCrossSheetSearchDataGrid(anyDelta);

            GridRecordSheetScores.ItemsSource = null;
            GridRecordSheetScores.ItemsSource = matchedRows;

            int matchedSheetsCount = matchCountsBySheet.Values.Count(c => c > 0);
            TxtDetailEmployeesCount.Text =
                $"نتائج البحث في جميع أوراق القالب: {matchedRows.Count} من {totalWorkspaceEmps} موظف (في {matchedSheetsCount} من {_currentRecordWorkspace.Sheets.Count} أوراق — دبل كليك لفتح ورقة الموظف)";

            RebuildDetailBottomSheetTabsBar(matchCountsBySheet);

            if (matchedRows.Count == 0)
            {
                TxtDetailSheetAverageBadge.Text = "متوسط نتائج البحث: 0 / 100";
            }
            else
            {
                var completedMatched = matchedRows.Where(r => !r.SourceEmployee.HasAnyEmptyCriteria).ToList();
                decimal sum = completedMatched.Sum(r => r.SourceEmployee.RawTotalOutOf100);
                decimal avg = completedMatched.Count == 0 ? 0m : completedMatched.Average(r => r.SourceEmployee.RawTotalOutOf100);
                TxtDetailSheetAverageBadge.Text = $"نتائج البحث ({completedMatched.Count}/{matchedRows.Count} مكتمل)  |  مجموع الدرجات: {sum:#,##0.##}  |  المتوسط: {avg:0.##}%";
            }
        }

        private void TxtFilterSheetEmployee_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyEmployeeSearchFilterToDetailGrid();
        }

        private void BtnClearSheetEmployeeFilter_Click(object sender, RoutedEventArgs e)
        {
            TxtFilterSheetEmployee.Text = string.Empty;
            ApplyEmployeeSearchFilterToDetailGrid();
        }

        private void GridRecordSheetScores_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (GridRecordSheetScores.SelectedItem is CrossSheetHistorySearchRow searchRow)
            {
                var targetSheet = searchRow.SourceSheet;
                var targetEmp = searchRow.SourceEmployee;

                TxtFilterSheetEmployee.Text = string.Empty;
                ActivateDetailSheet(targetSheet);

                GridRecordSheetScores.SelectedItem = targetEmp;
                GridRecordSheetScores.ScrollIntoView(targetEmp);
            }
        }

        private void UpdateTemplateOverallSummaryBadges()
        {
            if (_currentRecordWorkspace == null || _currentRecordWorkspace.Sheets.Count == 0)
            {
                PanelTemplateSummaryBadges.Visibility = Visibility.Collapsed;
                return;
            }

            var allRows = _currentRecordWorkspace.Sheets.SelectMany(s => s.EmployeeRows).ToList();
            var completedRows = allRows.Where(r => !r.HasAnyEmptyCriteria).ToList();

            decimal totalEarnedScores = completedRows.Sum(r => r.RawTotalOutOf100);
            decimal totalMaxScores = completedRows.Count * 100m;
            decimal overallAverage = completedRows.Count == 0
                ? 0m
                : Math.Round(completedRows.Average(r => r.RawTotalOutOf100), 2, MidpointRounding.AwayFromZero);

            TxtTemplateTotalEmployeesBadge.Text = completedRows.Count == allRows.Count
                ? $"👥 موظفو القالب المكتملون: {completedRows.Count} موظف ({_currentRecordWorkspace.Sheets.Count} أوراق)"
                : $"👥 موظفو القالب المكتملون: {completedRows.Count} من {allRows.Count} موظف ({_currentRecordWorkspace.Sheets.Count} أوراق)";

            TxtTemplateTotalScoresBadge.Text = $"🏆 مجموع الدرجات التي حصل عليها الموظفون بالقالب: {totalEarnedScores:#,##0.##} من {totalMaxScores:#,##0} درجة";
            TxtTemplateOverallAverageBadge.Text = $"📊 متوسط التقييم العام للقالب: {overallAverage:0.##}% (من 100)";

            PanelTemplateSummaryBadges.Visibility = Visibility.Visible;
        }

        private void UpdateDetailSheetAverageBadge()
        {
            if (_activeDetailSheet == null || _activeDetailSheet.EmployeeRows.Count == 0)
            {
                TxtDetailSheetAverageBadge.Text = "متوسط درجات الورقة: 0 / 100";
                return;
            }

            var completedRows = _activeDetailSheet.EmployeeRows.Where(r => !r.HasAnyEmptyCriteria).ToList();
            decimal sum = completedRows.Sum(r => r.RawTotalOutOf100);
            decimal avg = completedRows.Count == 0 ? 0m : completedRows.Average(r => r.RawTotalOutOf100);
            TxtDetailSheetAverageBadge.Text = $"الورقة: {completedRows.Count}/{_activeDetailSheet.EmployeeRows.Count} مكتمل  |  مجموع الدرجات: {sum:#,##0.##}  |  المتوسط: {avg:0.##}%";
        }

        private void RebuildDetailBottomSheetTabsBar(Dictionary<WeeklyEntrySheetModel, int>? searchMatchCounts = null)
        {
            StackDetailSheetTabs.Children.Clear();
            if (_currentRecordWorkspace == null) return;

            bool isSearchMode = searchMatchCounts != null;

            foreach (var sheet in _currentRecordWorkspace.Sheets)
            {
                bool isCurrent = !isSearchMode && ReferenceEquals(sheet, _activeDetailSheet);
                int matchCnt = isSearchMode && searchMatchCounts!.TryGetValue(sheet, out int c) ? c : 0;

                string tabContent = isSearchMode
                    ? $"📄  {sheet.SheetName} ({sheet.TargetCategoryName}) [🔍 {matchCnt} نتيجة]"
                    : $"📄  {sheet.SheetName} ({sheet.TargetCategoryName}) [{sheet.EmployeeRows.Count} موظف]";

                Brush bg = isCurrent
                    ? new SolidColorBrush(Color.FromRgb(37, 99, 235))
                    : (isSearchMode && matchCnt > 0
                        ? new SolidColorBrush(Color.FromRgb(236, 253, 245))
                        : new SolidColorBrush(Color.FromRgb(255, 255, 255)));

                Brush fg = isCurrent
                    ? Brushes.White
                    : (isSearchMode && matchCnt > 0
                        ? new SolidColorBrush(Color.FromRgb(6, 95, 70))
                        : new SolidColorBrush(Color.FromRgb(30, 41, 59)));

                Brush border = isCurrent
                    ? new SolidColorBrush(Color.FromRgb(29, 78, 216))
                    : (isSearchMode && matchCnt > 0
                        ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
                        : new SolidColorBrush(Color.FromRgb(148, 163, 184)));

                var tabBtn = new Button
                {
                    Content = tabContent,
                    Height = 34,
                    Padding = new Thickness(14, 0, 14, 0),
                    Margin = new Thickness(0, 0, 8, 0),
                    Cursor = Cursors.Hand,
                    FontWeight = FontWeights.Bold,
                    FontSize = 12.5,
                    Background = bg,
                    Foreground = fg,
                    BorderBrush = border,
                    BorderThickness = new Thickness(1.5)
                };

                var capturedSheet = sheet;
                tabBtn.Click += (_, _) =>
                {
                    if (!string.IsNullOrEmpty(TxtFilterSheetEmployee.Text))
                        TxtFilterSheetEmployee.Text = string.Empty;
                    ActivateDetailSheet(capturedSheet);
                };

                StackDetailSheetTabs.Children.Add(tabBtn);
            }
        }
    }
}













