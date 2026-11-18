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

        private async void BtnRowViewReadOnly_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is EvaluationRecordSummaryDto rowDto)
            {
                GridHistoryRecords.SelectedItem = rowDto;
                await LoadRecordIntoDetailsAsync(rowDto.RecordID, switchToDetailsTab: true);
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

            RebuildDetailScoresDataGrid();
            ApplyEmployeeSearchFilterToDetailGrid();
            RebuildDetailBottomSheetTabsBar();
            UpdateDetailSheetAverageBadge();
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

                var col = new DataGridTextColumn
                {
                    Header = $"{crit.CriteriaTitle}\n(من {crit.MaxScore:0})",
                    Binding = new Binding($"[{i}]"),
                    ElementStyle = scoreDisplayStyle,
                    IsReadOnly = true,
                    Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                    MinWidth = 125
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

        private void ApplyEmployeeSearchFilterToDetailGrid()
        {
            if (_activeDetailSheet == null) return;

            string filterText = TxtFilterSheetEmployee.Text.Trim();
            var rows = string.IsNullOrEmpty(filterText)
                ? _activeDetailSheet.EmployeeRows.ToList()
                : _activeDetailSheet.EmployeeRows.Where(e =>
                    e.EvaluationName.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
                    e.BranchName.Contains(filterText, StringComparison.OrdinalIgnoreCase) ||
                    e.FullJobDisplay.Contains(filterText, StringComparison.OrdinalIgnoreCase)).ToList();

            GridRecordSheetScores.ItemsSource = null;
            GridRecordSheetScores.ItemsSource = rows;
            TxtDetailEmployeesCount.Text = $"عدد الموظفين المعروضين: {rows.Count} من {_activeDetailSheet.EmployeeRows.Count}";
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

        private void UpdateDetailSheetAverageBadge()
        {
            if (_activeDetailSheet == null || _activeDetailSheet.EmployeeRows.Count == 0)
            {
                TxtDetailSheetAverageBadge.Text = "متوسط درجات الورقة: 0 / 100";
                return;
            }

            decimal avg = _activeDetailSheet.EmployeeRows.Average(r => r.RawTotalOutOf100);
            TxtDetailSheetAverageBadge.Text = $"متوسط درجات الورقة الحالية ({_activeDetailSheet.EmployeeRows.Count} موظف): {avg:0.##} من 100";
        }

        private void RebuildDetailBottomSheetTabsBar()
        {
            StackDetailSheetTabs.Children.Clear();
            if (_currentRecordWorkspace == null) return;

            foreach (var sheet in _currentRecordWorkspace.Sheets)
            {
                bool isCurrent = ReferenceEquals(sheet, _activeDetailSheet);

                var tabBtn = new Button
                {
                    Content = $"📄  {sheet.SheetName} ({sheet.TargetCategoryName}) [{sheet.EmployeeRows.Count} موظف]",
                    Height = 34,
                    Padding = new Thickness(14, 0, 14, 0),
                    Margin = new Thickness(0, 0, 8, 0),
                    Cursor = Cursors.Hand,
                    FontWeight = FontWeights.Bold,
                    FontSize = 12.5,
                    Background = isCurrent
                        ? new SolidColorBrush(Color.FromRgb(37, 99, 235))
                        : new SolidColorBrush(Color.FromRgb(255, 255, 255)),
                    Foreground = isCurrent
                        ? Brushes.White
                        : new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                    BorderBrush = isCurrent
                        ? new SolidColorBrush(Color.FromRgb(29, 78, 216))
                        : new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                    BorderThickness = new Thickness(1.5)
                };

                var capturedSheet = sheet;
                tabBtn.Click += (_, _) => ActivateDetailSheet(capturedSheet);

                StackDetailSheetTabs.Children.Add(tabBtn);
            }
        }
    }
}
