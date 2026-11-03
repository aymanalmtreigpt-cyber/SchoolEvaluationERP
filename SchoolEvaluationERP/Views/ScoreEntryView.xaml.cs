using System;
using System.Collections.Generic;
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
    public partial class ScoreEntryView : UserControl
    {
        private readonly WeeklyEvaluationService _service;
        private WeeklyEvaluationWorkspaceModel? _currentWorkspace;
        private WeeklyEntrySheetModel? _activeSheet;
        private bool _isSuppressingSelectionEvents;
        private bool _isReadOnlyMode;

        private bool CanInsert => AuthService.CurrentSession?.HasPermission("ScoreEntry", "insert") == true;
        private bool CanUpdate => AuthService.CurrentSession?.HasPermission("ScoreEntry", "update") == true;

        public ScoreEntryView()
        {
            InitializeComponent();
            _service = new WeeklyEvaluationService();

            Loaded += async (_, _) =>
            {
                await InitializeSelectorsAsync();
            };
        }

        private async Task InitializeSelectorsAsync()
        {
            _isSuppressingSelectionEvents = true;

            int currentYear = DateTime.Now.Year;
            var years = new List<int> { currentYear - 1, currentYear, currentYear + 1, currentYear + 2 };
            CmbYear.ItemsSource = years;
            CmbYear.SelectedItem = currentYear;

            var months = MonthOptionItem.GetAllMonths();
            CmbMonth.ItemsSource = months;
            CmbMonth.SelectedValue = DateTime.Now.Month;

            var templates = await _service.GetActiveTemplatesForEntryAsync();
            CmbTemplate.ItemsSource = templates;

            _isSuppressingSelectionEvents = false;

            if (templates.Count > 0)
            {
                CmbTemplate.SelectedIndex = 0;
            }
            else
            {
                SetEmptyState("لا توجد قوالب تقييم منشأة بعد. يرجى إنشاء قالب تقييم في شاشة (2. قوالب التقييم) أولاً.");
            }
        }

        private void SetEmptyState(string message)
        {
            _currentWorkspace = null;
            _activeSheet = null;
            TxtWorkspaceHeader.Text = message;
            BadgeWeekStatus.Visibility = Visibility.Collapsed;
            
            BtnUnlockSavedWeekEdit.Visibility = Visibility.Collapsed;
            BtnFillFullMarksAll.IsEnabled = false;
            BtnFillFullMarksSelected.IsEnabled = false;
            BtnClearMarksZero.IsEnabled = false;
            BtnSaveWeeklyEvaluation.IsEnabled = false;
            GridWeeklyScores.Columns.Clear();
            GridWeeklyScores.ItemsSource = null;
            StackSheetTabs.Children.Clear();
            TxtSheetAverageBadge.Text = "متوسط درجات الورقة: 0 / 100";
        }

        private async void CmbTemplate_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSuppressingSelectionEvents) return;
            await RefreshWeeksAndLoadWorkspaceAsync(autoPickFirstAvailableWeek: true);
        }

        private async void PeriodFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSuppressingSelectionEvents) return;
            await RefreshWeeksAndLoadWorkspaceAsync(autoPickFirstAvailableWeek: true);
        }

        private async void CmbWeek_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSuppressingSelectionEvents) return;
            await LoadSelectedWeekWorkspaceAsync();
        }

        private async void BtnReloadWorkspace_Click(object sender, RoutedEventArgs e)
        {
            await RefreshWeeksAndLoadWorkspaceAsync(autoPickFirstAvailableWeek: false);
        }

        private async Task RefreshWeeksAndLoadWorkspaceAsync(bool autoPickFirstAvailableWeek)
        {
            if (CmbTemplate.SelectedItem is not TemplateSummaryDto selectedTemplate ||
                CmbYear.SelectedItem is not int year ||
                CmbMonth.SelectedValue is not int month)
            {
                return;
            }

            int previouslySelectedWeek = CmbWeek.SelectedValue is int w ? w : 1;

            _isSuppressingSelectionEvents = true;
            var weeks = await _service.GetWeeksStatusAsync(selectedTemplate.TemplateID, year, month);
            CmbWeek.ItemsSource = weeks;

            if (autoPickFirstAvailableWeek)
            {
                var firstOpen = weeks.FirstOrDefault(x => !x.IsAlreadyEntered) ?? weeks[0];
                CmbWeek.SelectedItem = firstOpen;
            }
            else
            {
                CmbWeek.SelectedValue = previouslySelectedWeek;
                if (CmbWeek.SelectedItem == null) CmbWeek.SelectedIndex = 0;
            }

            _isSuppressingSelectionEvents = false;
            await LoadSelectedWeekWorkspaceAsync();
        }

        private async Task LoadSelectedWeekWorkspaceAsync()
        {
            if (CmbTemplate.SelectedItem is not TemplateSummaryDto selectedTemplate ||
                CmbYear.SelectedItem is not int year ||
                CmbMonth.SelectedValue is not int month ||
                CmbWeek.SelectedItem is not WeekStatusItem selectedWeek)
            {
                return;
            }

            if (selectedWeek.IsAlreadyEntered && selectedWeek.ExistingRecordId.HasValue)
            {
                _currentWorkspace = await _service.LoadSavedRecordWorkspaceAsync(selectedWeek.ExistingRecordId.Value);
                _isReadOnlyMode = true;
            }
            else
            {
                _currentWorkspace = await _service.PrepareNewWorkspaceAsync(selectedTemplate.TemplateID, year, month, selectedWeek.WeekNumber);
                _isReadOnlyMode = !CanInsert;
            }

            if (_currentWorkspace == null || _currentWorkspace.Sheets.Count == 0)
            {
                SetEmptyState("تعذر تحميل أوراق هذا القالب. تأكد من احتواء القالب على ورقة عمل واحدة وموظفين.");
                return;
            }

            ActivateSheet(_currentWorkspace.Sheets[0]);
        }

        private void ActivateSheet(WeeklyEntrySheetModel sheet)
        {
            if (_currentWorkspace == null) return;
            _activeSheet = sheet;

            bool isExistingSavedRecord = _currentWorkspace.RecordID > 0;
            bool hasPermissionToEdit = isExistingSavedRecord ? CanUpdate : CanInsert;
            bool canEditNow = !_isReadOnlyMode && hasPermissionToEdit;

            string monthName = IncentiveCalculatorService.GetArabicMonthName(_currentWorkspace.EvalMonth);
            TxtWorkspaceHeader.Text = $"المقيم: {_currentWorkspace.EvaluatorName}  —  شهر {monthName} {_currentWorkspace.EvalYear} م  —  الأسبوع ({_currentWorkspace.WeekNumber})";

            BadgeWeekStatus.Visibility = Visibility.Visible;
            if (isExistingSavedRecord)
            {
                if (canEditNow)
                {
                    BadgeWeekStatus.Background = new SolidColorBrush(Color.FromRgb(239, 246, 255));
                    BadgeWeekStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(147, 197, 253));
                    TxtBadgeWeekStatus.Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175));
                    TxtBadgeWeekStatus.Text = "✏ وضع تعديل تقييم محفوظ مسبقاً";
                    BtnUnlockSavedWeekEdit.Visibility = Visibility.Collapsed;
                }
                else
                {
                    BadgeWeekStatus.Background = new SolidColorBrush(Color.FromRgb(254, 243, 199));
                    BadgeWeekStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(252, 211, 77));
                    TxtBadgeWeekStatus.Foreground = new SolidColorBrush(Color.FromRgb(146, 64, 14));
                    TxtBadgeWeekStatus.Text = "🔒 تم إدخال هذا الأسبوع مسبقاً (وضع القراءة فقط)";
                    BtnUnlockSavedWeekEdit.Visibility = CanUpdate ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            else
            {
                BadgeWeekStatus.Background = new SolidColorBrush(Color.FromRgb(236, 253, 245));
                BadgeWeekStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(167, 243, 208));
                TxtBadgeWeekStatus.Foreground = new SolidColorBrush(Color.FromRgb(4, 120, 87));
                TxtBadgeWeekStatus.Text = canEditNow ? "✔ أسبوع جديد متاح للإدخال" : "🔒 وضع القراءة فقط (لا تملك صلاحية الإضافة)";
                BtnUnlockSavedWeekEdit.Visibility = Visibility.Collapsed;
            }

            
            

            BtnFillFullMarksAll.IsEnabled = canEditNow;
            BtnFillFullMarksSelected.IsEnabled = canEditNow;
            BtnClearMarksZero.IsEnabled = canEditNow;
            BtnSaveWeeklyEvaluation.IsEnabled = canEditNow;
            BtnSaveWeeklyEvaluation.Content = isExistingSavedRecord
                ? "💾 حفظ التعديلات على هذا الأسبوع"
                : "💾 حفظ واعتماد تقييم الأسبوع";

            RebuildScoresDataGrid(canEditNow);
            RebuildBottomSheetTabsBar();
            UpdateSheetAverageBadge();
        }

        private void BtnUnlockSavedWeekEdit_Click(object sender, RoutedEventArgs e)
        {
            if (!CanUpdate)
            {
                MessageBox.Show("عذراً، لا تملك صلاحية (تعديل) لتحديث درجات التقييمات المحفوظة.", "تنبيه صلاحيات", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _isReadOnlyMode = false;
            if (_activeSheet != null)
            {
                ActivateSheet(_activeSheet);
            }
        }

        private void RebuildScoresDataGrid(bool canEditNow)
        {
            GridWeeklyScores.Columns.Clear();
            if (_activeSheet == null) return;

            var wrapStyle = TryFindResource("WrappingCellTextStyle") as Style;

            GridWeeklyScores.Columns.Add(new DataGridTextColumn
            {
                Header = "م",
                Binding = new Binding("RowOrder"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 45
            });

            GridWeeklyScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الاسم التقييمي للموظف",
                Binding = new Binding("EvaluationName"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 210
            });

            GridWeeklyScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الفرع",
                Binding = new Binding("BranchName"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 90
            });

            GridWeeklyScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الوظيفة",
                Binding = new Binding("JobTitle"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 90
            });

            var scoreDisplayStyle = new Style(typeof(TextBlock));
            scoreDisplayStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            scoreDisplayStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            scoreDisplayStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            scoreDisplayStyle.Setters.Add(new Setter(TextBlock.FontSizeProperty, 14.0));
            scoreDisplayStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(30, 64, 175))));

            var scoreEditStyle = new Style(typeof(TextBox));
            scoreEditStyle.Setters.Add(new Setter(TextBox.TextAlignmentProperty, TextAlignment.Center));
            scoreEditStyle.Setters.Add(new Setter(TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            scoreEditStyle.Setters.Add(new Setter(TextBox.FontWeightProperty, FontWeights.Bold));
            scoreEditStyle.Setters.Add(new Setter(TextBox.FontSizeProperty, 14.0));
            scoreEditStyle.Setters.Add(new Setter(TextBox.BackgroundProperty, new SolidColorBrush(Color.FromRgb(239, 246, 255))));

            for (int i = 0; i < _activeSheet.CriteriaColumns.Count; i++)
            {
                var crit = _activeSheet.CriteriaColumns[i];

                var col = new DataGridTextColumn
                {
                    Header = $"{crit.CriteriaTitle}\n(من {crit.MaxScore:0.##})",
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

                GridWeeklyScores.Columns.Add(col);
            }

            var totalStyle = new Style(typeof(TextBlock));
            totalStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            totalStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            totalStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            totalStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(4, 120, 87))));

            GridWeeklyScores.Columns.Add(new DataGridTextColumn
            {
                Header = "مجموع الورقة\n(من 100)",
                Binding = new Binding("RawTotalDisplay"),
                ElementStyle = totalStyle,
                IsReadOnly = true,
                Width = 105
            });

            var weightedStyle = new Style(typeof(TextBlock));
            weightedStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            weightedStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            weightedStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            weightedStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(15, 23, 42))));

            GridWeeklyScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الدرجة الموزونة\nبوزن المقيم",
                Binding = new Binding("WeightedScoreDisplay"),
                ElementStyle = weightedStyle,
                IsReadOnly = true,
                Width = 120
            });

            GridWeeklyScores.ItemsSource = null;
            GridWeeklyScores.ItemsSource = _activeSheet.EmployeeRows;

            foreach (var row in _activeSheet.EmployeeRows)
            {
                row.PropertyChanged -= EmployeeRow_PropertyChanged;
                row.PropertyChanged += EmployeeRow_PropertyChanged;
            }
        }

        private void EmployeeRow_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(WeeklyScoreRowViewModel.RawTotalOutOf100))
            {
                UpdateSheetAverageBadge();
            }
        }

        private void GridWeeklyScores_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
        {
            if (_isReadOnlyMode)
            {
                e.Cancel = true;
            }
        }

        private void GridWeeklyScores_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(UpdateSheetAverageBadge), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void UpdateSheetAverageBadge()
        {
            if (_activeSheet == null || _activeSheet.EmployeeRows.Count == 0)
            {
                TxtSheetAverageBadge.Text = "متوسط درجات الورقة: 0 / 100";
                return;
            }

            decimal avg = _activeSheet.EmployeeRows.Average(r => r.RawTotalOutOf100);
            TxtSheetAverageBadge.Text = $"متوسط درجات الورقة الحالية ({_activeSheet.EmployeeRows.Count} موظف): {avg:0.##} من 100";
        }

        private void BtnFillFullMarksAll_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSheet == null || _isReadOnlyMode) return;

            foreach (var emp in _activeSheet.EmployeeRows)
            {
                emp.FillFullMarks();
            }

            GridWeeklyScores.Items.Refresh();
            UpdateSheetAverageBadge();
        }

        private void BtnFillFullMarksSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSheet == null || _isReadOnlyMode) return;

            var selectedRows = GridWeeklyScores.SelectedItems.Cast<WeeklyScoreRowViewModel>().ToList();
            if (selectedRows.Count == 0)
            {
                MessageBox.Show("يرجى تحديد موظف واحد أو أكثر من الجدول (بالماوس أو Ctrl أو Shift) لمنحهم الدرجة الكاملة.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var emp in selectedRows)
            {
                emp.FillFullMarks();
            }

            GridWeeklyScores.Items.Refresh();
            UpdateSheetAverageBadge();
        }

        private void BtnClearMarksZero_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSheet == null || _isReadOnlyMode) return;

            var confirm = MessageBox.Show("هل تريد تصفير جميع الدرجات في الورقة الحالية إلى (0)؟", "تأكيد التصفير", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            foreach (var emp in _activeSheet.EmployeeRows)
            {
                emp.ClearMarksToZero();
            }

            GridWeeklyScores.Items.Refresh();
            UpdateSheetAverageBadge();
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

        private async void BtnSaveWeeklyEvaluation_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWorkspace == null || _isReadOnlyMode) return;

            GridWeeklyScores.CommitEdit(DataGridEditingUnit.Cell, true);
            GridWeeklyScores.CommitEdit(DataGridEditingUnit.Row, true);

            BtnSaveWeeklyEvaluation.IsEnabled = false;
            var (isSuccess, message, savedRecordId) = await _service.SaveWeeklyEvaluationAsync(_currentWorkspace, "ScoreEntry");
            BtnSaveWeeklyEvaluation.IsEnabled = true;

            MessageBox.Show(
                message,
                isSuccess ? "تم حفظ التقييم الأسبوعي" : "تنبيه التحقق من التقييم",
                MessageBoxButton.OK,
                isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (isSuccess && savedRecordId > 0)
            {
                await RefreshWeeksAndLoadWorkspaceAsync(autoPickFirstAvailableWeek: false);
            }
        }
    }
}

