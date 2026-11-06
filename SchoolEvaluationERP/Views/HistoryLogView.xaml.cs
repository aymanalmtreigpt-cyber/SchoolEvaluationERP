using System;
using System.Collections.Generic;
using System.ComponentModel;
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
        private const int FirstCriteriaColIndex = 5;

        private readonly WeeklyEvaluationService _service = new();
        private WeeklyEvaluationWorkspaceModel? _currentRecordWorkspace;
        private WeeklyEntrySheetModel? _activeDetailSheet;
        private bool _isReadOnlyViewMode = true;
        private bool _isInitializingFilters;
        private bool _isHorizontalAdvance = true;

        private bool CanUpdate => AuthService.CurrentSession?.HasPermission("HistoryLog", "update") == true;
        private bool CanDelete => AuthService.CurrentSession?.HasPermission("HistoryLog", "delete") == true;

        public HistoryLogView()
        {
            _isInitializingFilters = true;
            InitializeComponent();
            _isInitializingFilters = false;
            UpdateDirectionButtonsVisual();

            Loaded += async (_, _) =>
            {
                ApplyPermissionsToControls();
                InitializeFilterCombos();
                await RefreshHistoryRecordsGridAsync();
            };
        }

        private void BtnDetailNavHorizontal_Click(object sender, RoutedEventArgs e)
        {
            _isHorizontalAdvance = true;
            UpdateDirectionButtonsVisual();
        }

        private void BtnDetailNavVertical_Click(object sender, RoutedEventArgs e)
        {
            _isHorizontalAdvance = false;
            UpdateDirectionButtonsVisual();
        }

        private void UpdateDirectionButtonsVisual()
        {
            var primaryStyle = TryFindResource("PrimaryButton") as Style;
            var secondaryStyle = TryFindResource("SecondaryButton") as Style;

            if (primaryStyle != null && secondaryStyle != null)
            {
                BtnDetailNavHorizontal.Style = _isHorizontalAdvance ? primaryStyle : secondaryStyle;
                BtnDetailNavVertical.Style = !_isHorizontalAdvance ? primaryStyle : secondaryStyle;
            }
        }

        private void ApplyPermissionsToControls()
        {
            BtnEditSelectedRecord.Visibility = CanUpdate ? Visibility.Visible : Visibility.Collapsed;
            BtnDeleteSelectedRecords.Visibility = CanDelete ? Visibility.Visible : Visibility.Collapsed;
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
            TxtRecordsCountBadge.Text = $"إجمالي السجلات: {records.Count}";
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
                await LoadRecordIntoDetailsAsync(selected.RecordID, openInReadOnlyMode: true, switchToDetailsTab: false);
            }
        }

        private async void GridHistoryRecords_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (GridHistoryRecords.SelectedItem is EvaluationRecordSummaryDto selected)
            {
                await LoadRecordIntoDetailsAsync(selected.RecordID, openInReadOnlyMode: true, switchToDetailsTab: true);
            }
        }

        private async void BtnRowViewReadOnly_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is EvaluationRecordSummaryDto rowDto)
            {
                GridHistoryRecords.SelectedItem = rowDto;
                await LoadRecordIntoDetailsAsync(rowDto.RecordID, openInReadOnlyMode: true, switchToDetailsTab: true);
            }
        }

        private async void BtnViewSelectedRecord_Click(object sender, RoutedEventArgs e)
        {
            if (GridHistoryRecords.SelectedItem is not EvaluationRecordSummaryDto selected)
            {
                MessageBox.Show("يرجى تحديد سجل تقييم شهري من الجدول أولاً لعرض درجاته.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await LoadRecordIntoDetailsAsync(selected.RecordID, openInReadOnlyMode: true, switchToDetailsTab: true);
        }

        private async void BtnEditSelectedRecord_Click(object sender, RoutedEventArgs e)
        {
            if (!CanUpdate)
            {
                MessageBox.Show("عذراً، لا تملك صلاحية (تعديل) في شاشة سجل التقييمات والأرشيف.", "تنبيه صلاحيات", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (GridHistoryRecords.SelectedItem is not EvaluationRecordSummaryDto selected)
            {
                MessageBox.Show("يرجى تحديد سجل تقييم شهري من الجدول أولاً ثم الضغط على (✏ فتح وتعديل التقييم).", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await LoadRecordIntoDetailsAsync(selected.RecordID, openInReadOnlyMode: false, switchToDetailsTab: true);
        }

        private async void BtnDeleteSelectedRecords_Click(object sender, RoutedEventArgs e)
        {
            var selectedList = GridHistoryRecords.SelectedItems.Cast<EvaluationRecordSummaryDto>().ToList();
            if (selectedList.Count == 0)
            {
                MessageBox.Show("يرجى تحديد سجل تقييم واحد أو أكثر من الجدول لحذفه.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string confirmText = selectedList.Count == 1
                ? $"هل أنت متأكد من حذف تقييم المقيم ({selectedList[0].EvaluatorName}) لفترة ({selectedList[0].PeriodDisplay})؟"
                : $"هل أنت متأكد من حذف ({selectedList.Count}) سجلات تقييم محددة دفعة واحدة؟";

            var confirm = MessageBox.Show(confirmText, "تأكيد الحذف من الأرشيف", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            int deletedCount = 0;
            string lastMsg = string.Empty;

            foreach (var rec in selectedList)
            {
                var (isSuccess, msg) = await _service.DeleteEvaluationRecordAsync(rec.RecordID, "HistoryLog");
                lastMsg = msg;
                if (isSuccess)
                {
                    deletedCount++;
                    if (_currentRecordWorkspace != null && _currentRecordWorkspace.RecordID == rec.RecordID)
                    {
                        ResetDetailsToEmpty();
                    }
                }
            }

            await RefreshHistoryRecordsGridAsync();
            MessageBox.Show(
                selectedList.Count == 1 ? lastMsg : $"تم حذف ({deletedCount}) من أصل ({selectedList.Count}) سجلات تقييم بنجاح.",
                "نتيجة الحذف",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void ResetDetailsToEmpty()
        {
            _currentRecordWorkspace = null;
            _activeDetailSheet = null;
            _isReadOnlyViewMode = true;
            TxtDetailsHeaderTitle.Text = "لم يتم اختيار سجل تقييم بعد — اختر تقييماً شهرياً من تبويب (1. سجل التقييمات الشهرية والأرشيف)";
            BadgeDetailsMode.Visibility = Visibility.Collapsed;

            BtnUnlockEditInDetails.Visibility = Visibility.Collapsed;
            BtnDetailFillAll.IsEnabled = false;
            BtnDetailFillSelected.IsEnabled = false;
            BtnDetailSaveChanges.IsEnabled = false;
            GridRecordSheetScores.Columns.Clear();
            GridRecordSheetScores.ItemsSource = null;
            StackDetailSheetTabs.Children.Clear();
            TxtDetailSheetAverageBadge.Text = "متوسط درجات الورقة: 0 / 100";
            TxtDetailEmployeesCount.Text = "عدد الموظفين: 0";
        }

        private async Task LoadRecordIntoDetailsAsync(int recordId, bool openInReadOnlyMode, bool switchToDetailsTab)
        {
            var workspace = await _service.LoadSavedRecordWorkspaceAsync(recordId);
            if (workspace == null)
            {
                MessageBox.Show("تعذر تحميل درجات سجل التقييم المحدد.", "خطأ", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _currentRecordWorkspace = workspace;
            _isReadOnlyViewMode = openInReadOnlyMode;

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

            bool canEditNow = !_isReadOnlyViewMode && CanUpdate;

            string monthName = IncentiveCalculatorService.GetArabicMonthName(_currentRecordWorkspace.EvalMonth);
            string groupInfo = string.IsNullOrWhiteSpace(_currentRecordWorkspace.GroupName) ? "" : $" [{_currentRecordWorkspace.GroupName}]";
            TxtDetailsHeaderTitle.Text = $"المقيم: {_currentRecordWorkspace.EvaluatorName}{groupInfo}  |  شهر {monthName} {_currentRecordWorkspace.EvalYear} م  |  نسخة القالب: v{_currentRecordWorkspace.TemplateVersionNumber}";

            TxtDetailsSubInfo.Text = $"أُدخل بواسطة: {_currentRecordWorkspace.CreatedByUserName}   |   تاريخ الإدخال: {_currentRecordWorkspace.CreatedAt:yyyy-MM-dd hh:mm tt}";

            BadgeDetailsMode.Visibility = Visibility.Visible;
            if (canEditNow)
            {
                BadgeDetailsMode.Background = new SolidColorBrush(Color.FromRgb(239, 246, 255));
                BadgeDetailsMode.BorderBrush = new SolidColorBrush(Color.FromRgb(147, 197, 253));
                TxtBadgeDetailsMode.Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175));
                TxtBadgeDetailsMode.Text = "✏ وضع التعديل مفعل (يمكنك تعديل الدرجات والضغط على حفظ)";
                BtnUnlockEditInDetails.Visibility = Visibility.Collapsed;
            }
            else
            {
                BadgeDetailsMode.Background = new SolidColorBrush(Color.FromRgb(254, 243, 199));
                BadgeDetailsMode.BorderBrush = new SolidColorBrush(Color.FromRgb(252, 211, 77));
                TxtBadgeDetailsMode.Foreground = new SolidColorBrush(Color.FromRgb(146, 64, 14));
                TxtBadgeDetailsMode.Text = "🔒 وضع القراءة فقط (للتعديل اضغط تفعيل التعديل)";
                BtnUnlockEditInDetails.Visibility = CanUpdate ? Visibility.Visible : Visibility.Collapsed;
            }

            BtnDetailFillAll.IsEnabled = canEditNow;
            BtnDetailFillSelected.IsEnabled = canEditNow;
            BtnDetailSaveChanges.IsEnabled = canEditNow;
            BtnDetailSaveChanges.Visibility = canEditNow ? Visibility.Visible : Visibility.Collapsed;

            RebuildDetailScoresDataGrid(canEditNow);
            ApplyEmployeeSearchFilterToDetailGrid();
            RebuildDetailBottomSheetTabsBar();
            UpdateDetailSheetAverageBadge();
        }

        private void BtnUnlockEditInDetails_Click(object sender, RoutedEventArgs e)
        {
            if (!CanUpdate)
            {
                MessageBox.Show("عذراً، لا تملك صلاحية (تعديل) في شاشة سجل التقييمات والأرشيف.", "تنبيه صلاحيات", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _isReadOnlyViewMode = false;
            if (_activeDetailSheet != null)
            {
                ActivateDetailSheet(_activeDetailSheet);
            }
        }

        private void RebuildDetailScoresDataGrid(bool canEditNow)
        {
            GridRecordSheetScores.Columns.Clear();
            if (_activeDetailSheet == null) return;

            var wrapStyle = TryFindResource("WrappingCellTextStyle") as Style;

            // 0: م
            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "م",
                Binding = new Binding("RowOrder"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 45
            });

            // 1: الاسم التقييمي
            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الاسم التقييمي للموظف",
                Binding = new Binding("EvaluationName"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 195
            });

            // 2: الفرع
            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الفرع",
                Binding = new Binding("BranchName"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 85
            });

            // 3: الوظيفة ونوعها
            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الوظيفة ونوعها",
                Binding = new Binding("FullJobDisplay"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 135
            });

            // 4: الحافز
            GridRecordSheetScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الحافز",
                Binding = new Binding("IncentiveBadgeDisplay"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 95
            });

            var scoreDisplayStyle = new Style(typeof(TextBlock));
            scoreDisplayStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            scoreDisplayStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            scoreDisplayStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            scoreDisplayStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 14.5));
            scoreDisplayStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(30, 64, 175))));

            var scoreEditStyle = new Style(typeof(TextBox));
            scoreEditStyle.Setters.Add(new Setter(TextBox.TextAlignmentProperty, TextAlignment.Center));
            scoreEditStyle.Setters.Add(new Setter(TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            scoreEditStyle.Setters.Add(new Setter(TextBox.FontWeightProperty, FontWeights.Bold));
            scoreEditStyle.Setters.Add(new Setter(TextBox.FontSizeProperty, 14.5));
            scoreEditStyle.Setters.Add(new Setter(TextBox.BackgroundProperty, new SolidColorBrush(Color.FromRgb(239, 246, 255))));

            for (int i = 0; i < _activeDetailSheet.CriteriaColumns.Count; i++)
            {
                var crit = _activeDetailSheet.CriteriaColumns[i];

                var col = new DataGridTextColumn
                {
                    Header = $"{crit.CriteriaTitle}\n(من {crit.MaxScore:0})",
                    Binding = new Binding($"[{i}]")
                    {
                        Mode = BindingMode.TwoWay,
                        UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
                    },
                    ElementStyle = scoreDisplayStyle,
                    EditingElementStyle = scoreEditStyle,
                    IsReadOnly = !canEditNow,
                    Width = new DataGridLength(1, DataGridLengthUnitType.Star)
                };

                GridRecordSheetScores.Columns.Add(col);
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
                Width = 100
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
                Width = 115
            });

            foreach (var row in _activeDetailSheet.EmployeeRows)
            {
                row.PropertyChanged -= DetailRow_PropertyChanged;
                row.PropertyChanged += DetailRow_PropertyChanged;
            }
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

        private void DetailRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WeeklyScoreRowViewModel.RawTotalOutOf100))
            {
                UpdateDetailSheetAverageBadge();
            }
        }

        private void GridRecordSheetScores_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            if (_isReadOnlyViewMode || !CanUpdate)
            {
                e.Cancel = true;
            }
        }

        private void GridRecordSheetScores_PreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
        {
            if (_activeDetailSheet == null || _isReadOnlyViewMode || !CanUpdate) return;

            ScoreGridNavigationHelper.AttachEditorHandlers(
                GridRecordSheetScores,
                e,
                FirstCriteriaColIndex,
                _activeDetailSheet.CriteriaColumns.Count,
                () => _isHorizontalAdvance,
                UpdateDetailSheetAverageBadge);
        }

        private void GridRecordSheetScores_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(UpdateDetailSheetAverageBadge), System.Windows.Threading.DispatcherPriority.Background);
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

        private void BtnDetailFillAll_Click(object sender, RoutedEventArgs e)
        {
            if (_activeDetailSheet == null || _isReadOnlyViewMode || !CanUpdate) return;

            foreach (var emp in _activeDetailSheet.EmployeeRows)
            {
                emp.FillFullMarks();
            }

            GridRecordSheetScores.Items.Refresh();
            UpdateDetailSheetAverageBadge();
        }

        private void BtnDetailFillSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_activeDetailSheet == null || _isReadOnlyViewMode || !CanUpdate) return;

            var selectedRows = GridRecordSheetScores.SelectedItems.Cast<WeeklyScoreRowViewModel>().ToList();
            if (selectedRows.Count == 0)
            {
                MessageBox.Show("يرجى تحديد موظف واحد أو أكثر من الجدول لمنحهم الدرجة الكاملة.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var emp in selectedRows)
            {
                emp.FillFullMarks();
            }

            GridRecordSheetScores.Items.Refresh();
            UpdateDetailSheetAverageBadge();
        }

        private async void BtnDetailSaveChanges_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRecordWorkspace == null || _isReadOnlyViewMode || !CanUpdate) return;

            GridRecordSheetScores.CommitEdit(DataGridEditingUnit.Cell, true);
            GridRecordSheetScores.CommitEdit(DataGridEditingUnit.Row, true);

            BtnDetailSaveChanges.IsEnabled = false;
            var (isSuccess, message, _) = await _service.SaveMonthlyEvaluationAsync(_currentRecordWorkspace, "HistoryLog");
            BtnDetailSaveChanges.IsEnabled = true;

            MessageBox.Show(
                message,
                isSuccess ? "تم حفظ التعديلات" : "تنبيه التحقق من التقييم",
                MessageBoxButton.OK,
                isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (isSuccess)
            {
                _isReadOnlyViewMode = true;
                if (_activeDetailSheet != null)
                {
                    ActivateDetailSheet(_activeDetailSheet);
                }
                await RefreshHistoryRecordsGridAsync();
            }
        }
    }
}
