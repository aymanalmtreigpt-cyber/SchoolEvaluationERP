using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class ScoreBalancingView : UserControl
    {
        private readonly IncentiveAndBalancingService _service = new();
        private readonly WeeklyEvaluationService _evalService = new();
        private WeeklyEvaluationWorkspaceModel? _currentWorkspace;
        private WeeklyEntrySheetModel? _activeSheet;
        private bool _isInitializing;

        private bool CanEditBalancing =>
            AuthService.CurrentSession?.HasPermission("ScoreBalancing", "update") == true ||
            AuthService.CurrentSession?.HasPermission("ScoreBalancing", "insert") == true ||
            AuthService.CurrentSession?.HasPermission("HistoryLog", "update") == true;

        public ScoreBalancingView()
        {
            _isInitializing = true;
            InitializeComponent();
            _isInitializing = false;

            Loaded += async (_, _) =>
            {
                InitializePeriodSelectors();
                await LoadEvaluatorRecordsForPeriodAsync();
            };
        }

        private void InitializePeriodSelectors()
        {
            _isInitializing = true;

            int currentYear = DateTime.Now.Year;
            CmbYear.ItemsSource = new List<int> { currentYear - 1, currentYear, currentYear + 1, currentYear + 2 };
            CmbYear.SelectedItem = currentYear;

            var months = MonthOptionItem.GetAllMonths();
            CmbMonth.ItemsSource = months;
            CmbMonth.SelectedValue = DateTime.Now.Month;

            _isInitializing = false;
        }

        private void SetEmptyWorkspaceState(string message)
        {
            _currentWorkspace = null;
            _activeSheet = null;
            TxtOpenedRecordHeader.Text = message;
            BadgeRecordAverage.Visibility = Visibility.Collapsed;

            BtnSaveBalancing.IsEnabled = false;
            BtnIncreaseAllTemplate.IsEnabled = false;
            BtnDecreaseAllTemplate.IsEnabled = false;
            BtnResetAllTemplateZero.IsEnabled = false;
            BtnIncreaseSelectedEmp.IsEnabled = false;
            BtnDecreaseSelectedEmp.IsEnabled = false;
            BtnResetSelectedEmp.IsEnabled = false;

            GridBalancingScores.Columns.Clear();
            GridBalancingScores.ItemsSource = null;
            StackSheetTabs.Children.Clear();
            TxtSheetAverageComparison.Text = "متوسط الورقة قبل الموازنة: 0%  |  بعد الموازنة: 0%";
            TxtFilteredCountBadge.Text = "الموظفون: 0";
        }

        private async Task LoadEvaluatorRecordsForPeriodAsync()
        {
            if (_isInitializing || !IsLoaded) return;
            if (CmbYear.SelectedItem is not int year || CmbMonth.SelectedValue is not int month)
                return;

            int? prevSelectedRecordId = CmbEvaluatorRecord.SelectedValue as int?;

            _isInitializing = true;
            var records = await _service.GetEvaluatorRecordsForMonthAsync(year, month);
            CmbEvaluatorRecord.ItemsSource = records;

            if (prevSelectedRecordId.HasValue && records.Any(r => r.RecordID == prevSelectedRecordId.Value))
            {
                CmbEvaluatorRecord.SelectedValue = prevSelectedRecordId.Value;
            }
            else if (records.Count > 0)
            {
                CmbEvaluatorRecord.SelectedIndex = 0;
            }
            else
            {
                CmbEvaluatorRecord.SelectedItem = null;
            }
            _isInitializing = false;

            if (records.Count == 0)
            {
                string mName = IncentiveCalculatorService.GetArabicMonthName(month);
                SetEmptyWorkspaceState($"لا توجد سجلات تقييم مدخلة لشهر ({mName} {year} م) بعد. يرجى إدخال التقييم في شاشة الإدخال الشهري أولاً.");
                return;
            }

            await OpenSelectedEvaluatorRecordAsync();
        }

        private async void PeriodFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await LoadEvaluatorRecordsForPeriodAsync();
        }

        private async void CmbEvaluatorRecord_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            await OpenSelectedEvaluatorRecordAsync();
        }

        private async void BtnReloadRecords_Click(object sender, RoutedEventArgs e)
        {
            await LoadEvaluatorRecordsForPeriodAsync();
        }

        private async Task OpenSelectedEvaluatorRecordAsync()
        {
            if (CmbEvaluatorRecord.SelectedItem is not EvaluationRecordSummaryDto selectedRec)
            {
                SetEmptyWorkspaceState("يرجى اختيار سجل مقيم من القائمة العلوية لفتح تقييمه.");
                return;
            }

            _currentWorkspace = await _evalService.LoadSavedRecordWorkspaceAsync(selectedRec.RecordID);
            if (_currentWorkspace == null || _currentWorkspace.Sheets.Count == 0)
            {
                SetEmptyWorkspaceState("تعذر فتح سجل التقييم المحدد.");
                return;
            }

            bool canEdit = CanEditBalancing;
            BtnSaveBalancing.IsEnabled = canEdit;
            BtnIncreaseAllTemplate.IsEnabled = canEdit;
            BtnDecreaseAllTemplate.IsEnabled = canEdit;
            BtnResetAllTemplateZero.IsEnabled = canEdit;
            BtnIncreaseSelectedEmp.IsEnabled = canEdit;
            BtnDecreaseSelectedEmp.IsEnabled = canEdit;
            BtnResetSelectedEmp.IsEnabled = canEdit;

            ActivateSheet(_currentWorkspace.Sheets[0]);
        }

        private void ActivateSheet(WeeklyEntrySheetModel sheet)
        {
            if (_currentWorkspace == null) return;
            _activeSheet = sheet;

            string mName = IncentiveCalculatorService.GetArabicMonthName(_currentWorkspace.EvalMonth);
            string grpText = string.IsNullOrWhiteSpace(_currentWorkspace.GroupName) ? "" : $" [{_currentWorkspace.GroupName}]";
            TxtOpenedRecordHeader.Text = $"سجل المقيم: {_currentWorkspace.EvaluatorName}{grpText}  —  شهر {mName} {_currentWorkspace.EvalYear} م";
            BadgeRecordAverage.Visibility = Visibility.Visible;

            RebuildBalancingDataGrid(CanEditBalancing);
            ApplyEmployeeSearchFilter();
            RebuildBottomSheetTabsBar();
            UpdateLiveAveragesBadges();
        }

        private void RebuildBalancingDataGrid(bool canEdit)
        {
            GridBalancingScores.Columns.Clear();
            if (_activeSheet == null) return;

            var wrapStyle = TryFindResource("WrappingCellTextStyle") as Style;

            GridBalancingScores.Columns.Add(new DataGridTextColumn
            {
                Header = "م",
                Binding = new Binding("RowOrder"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 45
            });

            GridBalancingScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الاسم التقييمي للموظف",
                Binding = new Binding("EvaluationName"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 195
            });

            GridBalancingScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الفرع",
                Binding = new Binding("BranchName"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 80
            });

            GridBalancingScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الوظيفة ونوعها",
                Binding = new Binding("FullJobDisplay"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 130
            });

            var critCellStyle = new Style(typeof(TextBlock));
            critCellStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            critCellStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            critCellStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
            critCellStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(71, 85, 105))));

            for (int i = 0; i < _activeSheet.CriteriaColumns.Count; i++)
            {
                var crit = _activeSheet.CriteriaColumns[i];
                GridBalancingScores.Columns.Add(new DataGridTextColumn
                {
                    Header = $"{crit.CriteriaTitle}\n(من {crit.MaxScore:0})",
                    Binding = new Binding($"[{i}]"),
                    ElementStyle = critCellStyle,
                    IsReadOnly = true,
                    Width = new DataGridLength(0.85, DataGridLengthUnitType.Star)
                });
            }

            var beforeStyle = new Style(typeof(TextBlock));
            beforeStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            beforeStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            beforeStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            beforeStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(51, 65, 85))));

            GridBalancingScores.Columns.Add(new DataGridTextColumn
            {
                Header = "مجموع المبادئ\n(قبل الموازنة)",
                Binding = new Binding("RawCriteriaSumDisplay"),
                ElementStyle = beforeStyle,
                IsReadOnly = true,
                Width = 105
            });

            var deltaDisplayStyle = new Style(typeof(TextBlock));
            deltaDisplayStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            deltaDisplayStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            deltaDisplayStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            deltaDisplayStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 14.5));
            deltaDisplayStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(180, 83, 9))));

            var deltaEditStyle = new Style(typeof(TextBox));
            deltaEditStyle.Setters.Add(new Setter(TextBox.TextAlignmentProperty, TextAlignment.Center));
            deltaEditStyle.Setters.Add(new Setter(TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            deltaEditStyle.Setters.Add(new Setter(TextBox.FontWeightProperty, FontWeights.Bold));
            deltaEditStyle.Setters.Add(new Setter(TextBox.FontSizeProperty, 14.5));
            deltaEditStyle.Setters.Add(new Setter(TextBox.BackgroundProperty, new SolidColorBrush(Color.FromRgb(254, 243, 199))));

            GridBalancingScores.Columns.Add(new DataGridTextColumn
            {
                Header = "⚖ نسبة الموازنة\n(+ زيادة / - تنقيص)",
                Binding = new Binding("BalancingDeltaText")
                {
                    Mode = BindingMode.TwoWay,
                    UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
                },
                ElementStyle = deltaDisplayStyle,
                EditingElementStyle = deltaEditStyle,
                IsReadOnly = !canEdit,
                Width = 135
            });

            var afterStyle = new Style(typeof(TextBlock));
            afterStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            afterStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            afterStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            afterStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 14.0));
            afterStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(4, 120, 87))));

            GridBalancingScores.Columns.Add(new DataGridTextColumn
            {
                Header = "النسبة النهائية\n(بعد الموازنة)",
                Binding = new Binding("RawTotalDisplay"),
                ElementStyle = afterStyle,
                IsReadOnly = true,
                Width = 110
            });

            var weightedStyle = new Style(typeof(TextBlock));
            weightedStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            weightedStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            weightedStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            weightedStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(30, 64, 175))));

            GridBalancingScores.Columns.Add(new DataGridTextColumn
            {
                Header = "حصة المقيم\nالموزونة",
                Binding = new Binding("WeightedScoreDisplay"),
                ElementStyle = weightedStyle,
                IsReadOnly = true,
                Width = 115
            });

            foreach (var row in _activeSheet.EmployeeRows)
            {
                row.PropertyChanged -= EmployeeRow_PropertyChanged;
                row.PropertyChanged += EmployeeRow_PropertyChanged;
            }
        }

        private void EmployeeRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WeeklyScoreRowViewModel.RawTotalOutOf100) ||
                e.PropertyName == nameof(WeeklyScoreRowViewModel.BalancingDelta))
            {
                UpdateLiveAveragesBadges();
            }
        }

        private void ApplyEmployeeSearchFilter()
        {
            if (_activeSheet == null) return;

            string q = TxtSearchEmployee.Text.Trim();
            var filtered = string.IsNullOrEmpty(q)
                ? _activeSheet.EmployeeRows.ToList()
                : _activeSheet.EmployeeRows.Where(e =>
                    e.EvaluationName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    e.BranchName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    e.FullJobDisplay.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

            GridBalancingScores.ItemsSource = null;
            GridBalancingScores.ItemsSource = filtered;
            TxtFilteredCountBadge.Text = $"المعروض: {filtered.Count} من {_activeSheet.EmployeeRows.Count}";
        }

        private void TxtSearchEmployee_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyEmployeeSearchFilter();
        }

        private void BtnClearSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtSearchEmployee.Text = string.Empty;
            ApplyEmployeeSearchFilter();
        }

        private void UpdateLiveAveragesBadges()
        {
            if (_currentWorkspace == null || _activeSheet == null) return;

            if (_activeSheet.EmployeeRows.Count > 0)
            {
                decimal sheetBefore = _activeSheet.EmployeeRows.Average(r => r.RawCriteriaSumOutOf100);
                decimal sheetAfter = _activeSheet.EmployeeRows.Average(r => r.RawTotalOutOf100);
                TxtSheetAverageComparison.Text = $"متوسط الورقة قبل الموازنة: {sheetBefore:0.##}%   |   بعد الموازنة: {sheetAfter:0.##}%";
            }

            var allTemplateRows = _currentWorkspace.Sheets.SelectMany(s => s.EmployeeRows).ToList();
            if (allTemplateRows.Count > 0)
            {
                decimal allBefore = allTemplateRows.Average(r => r.RawCriteriaSumOutOf100);
                decimal allAfter = allTemplateRows.Average(r => r.RawTotalOutOf100);
                TxtBadgeRecordAverage.Text = $"متوسط القالب كاملًا ({allTemplateRows.Count} موظف) — قبل: {allBefore:0.##}%  ⬅  بعد الموازنة: {allAfter:0.##}%";
            }
        }

        private void RebuildBottomSheetTabsBar()
        {
            StackSheetTabs.Children.Clear();
            if (_currentWorkspace == null) return;

            foreach (var sheet in _currentWorkspace.Sheets)
            {
                bool isCurrent = ReferenceEquals(sheet, _activeSheet);

                var tabBtn = new Button
                {
                    Content = $"📄  {sheet.SheetName} ({sheet.TargetCategoryName}) [{sheet.EmployeeRows.Count} موظف]",
                    Height = 34,
                    Padding = new Thickness(14, 0, 14, 0),
                    Margin = new Thickness(0, 0, 8, 0),
                    Cursor = System.Windows.Input.Cursors.Hand,
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
                tabBtn.Click += (_, _) => ActivateSheet(capturedSheet);

                StackSheetTabs.Children.Add(tabBtn);
            }
        }

        private bool TryReadDeltaAmount(TextBox tb, out decimal amount)
        {
            amount = 0m;
            string clean = WeeklyScoreRowViewModel.NormalizeAsciiDigits(tb.Text.Trim()).Replace(",", ".");
            if (!decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal parsed) || parsed <= 0m)
            {
                MessageBox.Show("يرجى إدخال مقدار رقمي أكبر من صفر (مثلاً: 5 أو 10).", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                tb.Focus();
                tb.SelectAll();
                return false;
            }
            amount = Math.Round(parsed, 2, MidpointRounding.AwayFromZero);
            return true;
        }

        private void BtnIncreaseAllTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWorkspace == null || !CanEditBalancing) return;
            if (!TryReadDeltaAmount(TxtBulkDeltaValue, out decimal step)) return;

            foreach (var sheet in _currentWorkspace.Sheets)
            {
                foreach (var emp in sheet.EmployeeRows)
                {
                    emp.BalancingDelta += step;
                }
            }

            GridBalancingScores.Items.Refresh();
            UpdateLiveAveragesBadges();
        }

        private void BtnDecreaseAllTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWorkspace == null || !CanEditBalancing) return;
            if (!TryReadDeltaAmount(TxtBulkDeltaValue, out decimal step)) return;

            foreach (var sheet in _currentWorkspace.Sheets)
            {
                foreach (var emp in sheet.EmployeeRows)
                {
                    emp.BalancingDelta -= step;
                }
            }

            GridBalancingScores.Items.Refresh();
            UpdateLiveAveragesBadges();
        }

        private void BtnResetAllTemplateZero_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWorkspace == null || !CanEditBalancing) return;

            var confirm = MessageBox.Show("هل تريد تصفير عمود نسبة الموازنة (0) لجميع الموظفين في قالب هذا المقيم؟", "تأكيد التصفير", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            foreach (var sheet in _currentWorkspace.Sheets)
            {
                foreach (var emp in sheet.EmployeeRows)
                {
                    emp.BalancingDelta = 0m;
                }
            }

            GridBalancingScores.Items.Refresh();
            UpdateLiveAveragesBadges();
        }

        private List<WeeklyScoreRowViewModel> GetTargetEmployeesForIndividualAction()
        {
            var selected = GridBalancingScores.SelectedItems.Cast<WeeklyScoreRowViewModel>().ToList();
            if (selected.Count > 0)
                return selected;

            if (!string.IsNullOrWhiteSpace(TxtSearchEmployee.Text) && GridBalancingScores.ItemsSource is IEnumerable<WeeklyScoreRowViewModel> filtered)
            {
                return filtered.ToList();
            }

            return new List<WeeklyScoreRowViewModel>();
        }

        private void BtnIncreaseSelectedEmp_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSheet == null || !CanEditBalancing) return;
            if (!TryReadDeltaAmount(TxtIndividualDeltaValue, out decimal step)) return;

            var targets = GetTargetEmployeesForIndividualAction();
            if (targets.Count == 0)
            {
                MessageBox.Show("يرجى البحث عن اسم الموظف أو تحديده من الجدول أولاً لزيادة نسبته.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var emp in targets)
            {
                emp.BalancingDelta += step;
            }

            GridBalancingScores.Items.Refresh();
            UpdateLiveAveragesBadges();
        }

        private void BtnDecreaseSelectedEmp_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSheet == null || !CanEditBalancing) return;
            if (!TryReadDeltaAmount(TxtIndividualDeltaValue, out decimal step)) return;

            var targets = GetTargetEmployeesForIndividualAction();
            if (targets.Count == 0)
            {
                MessageBox.Show("يرجى البحث عن اسم الموظف أو تحديده من الجدول أولاً لتنقيص نسبته.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var emp in targets)
            {
                emp.BalancingDelta -= step;
            }

            GridBalancingScores.Items.Refresh();
            UpdateLiveAveragesBadges();
        }

        private void BtnResetSelectedEmp_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSheet == null || !CanEditBalancing) return;

            var targets = GetTargetEmployeesForIndividualAction();
            if (targets.Count == 0)
            {
                MessageBox.Show("يرجى البحث عن اسم الموظف أو تحديده من الجدول أولاً لتصفير موازنته.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var emp in targets)
            {
                emp.BalancingDelta = 0m;
            }

            GridBalancingScores.Items.Refresh();
            UpdateLiveAveragesBadges();
        }

        private async void BtnSaveBalancing_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWorkspace == null || !CanEditBalancing) return;

            GridBalancingScores.CommitEdit(DataGridEditingUnit.Cell, true);
            GridBalancingScores.CommitEdit(DataGridEditingUnit.Row, true);

            BtnSaveBalancing.IsEnabled = false;
            var (isSuccess, message) = await _service.SaveRecordBalancingAdjustmentsAsync(_currentWorkspace);
            BtnSaveBalancing.IsEnabled = true;

            MessageBox.Show(
                message,
                isSuccess ? "تم حفظ نسب الموازنة" : "تنبيه",
                MessageBoxButton.OK,
                isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (isSuccess)
            {
                await LoadEvaluatorRecordsForPeriodAsync();
            }
        }
    }
}
