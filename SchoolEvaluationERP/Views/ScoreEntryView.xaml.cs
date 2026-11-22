using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class ScoreEntryView : UserControl
    {
        private const int FirstCriteriaColIndex = 5;

        private readonly WeeklyEvaluationService _service = new();
        private WeeklyEvaluationWorkspaceModel? _currentWorkspace;
        private WeeklyEntrySheetModel? _activeSheet;
        private bool _isSuppressingSelectionEvents;
        private bool _isReadOnlyMode;
        private bool _isHorizontalAdvance = true;

        private bool CanInsert => AuthService.CurrentSession?.HasPermission("ScoreEntry", "insert") == true;
        private bool CanUpdate => AuthService.CurrentSession?.HasPermission("ScoreEntry", "update") == true;

        public ScoreEntryView()
        {
            _isSuppressingSelectionEvents = true;
            InitializeComponent();
            _isSuppressingSelectionEvents = false;
            UpdateDirectionButtonsVisual();

            Loaded += async (_, _) =>
            {
                await InitializeSelectorsAsync();
            };
        }

        private void BtnNavHorizontal_Click(object sender, RoutedEventArgs e)
        {
            _isHorizontalAdvance = true;
            UpdateDirectionButtonsVisual();
        }

        private void BtnNavVertical_Click(object sender, RoutedEventArgs e)
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
                BtnNavHorizontal.Style = _isHorizontalAdvance ? primaryStyle : secondaryStyle;
                BtnNavVertical.Style = !_isHorizontalAdvance ? primaryStyle : secondaryStyle;
            }
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
            BadgeMonthStatus.Visibility = Visibility.Collapsed;
            BtnMissingScoresAlert.Visibility = Visibility.Collapsed;

            BtnUnlockSavedMonthEdit.Visibility = Visibility.Collapsed;
            BtnSyncTemplateEmployees.Visibility = Visibility.Collapsed;
            BtnImportScoresExcel.IsEnabled = false;
            BtnFillFullMarksAll.IsEnabled = false;
            BtnFillFullMarksSelected.IsEnabled = false;
            BtnClearMarksZero.IsEnabled = false;
            BtnSaveMonthlyEvaluation.IsEnabled = false;
            GridWeeklyScores.Columns.Clear();
            GridWeeklyScores.ItemsSource = null;
            StackSheetTabs.Children.Clear();
            TxtSheetAverageBadge.Text = "متوسط درجات الورقة: 0 / 100";
        }

        private async void CmbTemplate_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSuppressingSelectionEvents) return;
            await LoadSelectedMonthWorkspaceAsync();
        }

        private async void PeriodFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSuppressingSelectionEvents) return;
            await LoadSelectedMonthWorkspaceAsync();
        }

        private async void BtnReloadWorkspace_Click(object sender, RoutedEventArgs e)
        {
            await LoadSelectedMonthWorkspaceAsync();
        }

        private async Task LoadSelectedMonthWorkspaceAsync()
        {
            if (CmbTemplate.SelectedItem is not TemplateSummaryDto selectedTemplate ||
                CmbYear.SelectedItem is not int year ||
                CmbMonth.SelectedValue is not int month)
            {
                return;
            }

            var monthStatus = await _service.GetMonthStatusAsync(selectedTemplate.TemplateID, year, month);

            if (monthStatus.IsAlreadyEntered && monthStatus.ExistingRecordId.HasValue)
            {
                _currentWorkspace = await _service.LoadSavedRecordWorkspaceAsync(monthStatus.ExistingRecordId.Value);
                _isReadOnlyMode = true;
            }
            else
            {
                _currentWorkspace = await _service.PrepareNewWorkspaceAsync(selectedTemplate.TemplateID, year, month);
                _isReadOnlyMode = !CanInsert;
            }

            if (_currentWorkspace == null || _currentWorkspace.Sheets.Count == 0)
            {
                SetEmptyState("تعذر تحميل أوراق هذا القالب. تأكد من احتواء القالب على ورقة عمل واحدة وموظفين مستمرين.");
                return;
            }

            ActivateSheet(_currentWorkspace.Sheets[0]);
        }

        private void ActivateSheet(WeeklyEntrySheetModel sheet)
        {
            if (_currentWorkspace == null) return;
            _activeSheet = sheet;

            bool isExistingSavedRecord = _currentWorkspace.RecordID > 0;
            bool canBeModifiedInSystem = _currentWorkspace.CanBeModified;
            bool hasPermissionToEdit = isExistingSavedRecord ? (CanUpdate && canBeModifiedInSystem) : CanInsert;
            bool canEditNow = !_isReadOnlyMode && hasPermissionToEdit;

            string monthName = IncentiveCalculatorService.GetArabicMonthName(_currentWorkspace.EvalMonth);
            string groupInfo = string.IsNullOrWhiteSpace(_currentWorkspace.GroupName) ? "" : $" [{_currentWorkspace.GroupName}]";
            TxtWorkspaceHeader.Text = $"المقيم: {_currentWorkspace.EvaluatorName}{groupInfo}  —  التقييم الشهري لشهر {monthName} {_currentWorkspace.EvalYear} م";

            BadgeMonthStatus.Visibility = Visibility.Visible;
            if (isExistingSavedRecord)
            {
                if (canEditNow)
                {
                    BadgeMonthStatus.Background = new SolidColorBrush(Color.FromRgb(239, 246, 255));
                    BadgeMonthStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(147, 197, 253));
                    TxtBadgeMonthStatus.Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175));
                    TxtBadgeMonthStatus.Text = "✏ وضع تعديل تقييم الشهر المحفوظ";
                    BtnUnlockSavedMonthEdit.Visibility = Visibility.Collapsed;
                }
                else
                {
                    BadgeMonthStatus.Background = new SolidColorBrush(Color.FromRgb(254, 243, 199));
                    BadgeMonthStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(252, 211, 77));
                    TxtBadgeMonthStatus.Foreground = new SolidColorBrush(Color.FromRgb(146, 64, 14));
                    TxtBadgeMonthStatus.Text = canBeModifiedInSystem
                        ? "🔒 تم إدخال تقييم هذا الشهر مسبقاً (وضع القراءة فقط)"
                        : "🔒 تم حذف القالب أو الموظفين — هذا السجل مقفل تاريخياً للقراءة فقط";
                    BtnUnlockSavedMonthEdit.Visibility = (CanUpdate && canBeModifiedInSystem) ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            else
            {
                BadgeMonthStatus.Background = new SolidColorBrush(Color.FromRgb(236, 253, 245));
                BadgeMonthStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(167, 243, 208));
                TxtBadgeMonthStatus.Foreground = new SolidColorBrush(Color.FromRgb(4, 120, 87));
                TxtBadgeMonthStatus.Text = canEditNow ? "✔ شهر جديد متاح للإدخال" : "🔒 وضع القراءة فقط (لا تملك صلاحية الإضافة)";
                BtnUnlockSavedMonthEdit.Visibility = Visibility.Collapsed;
            }

            BtnSyncTemplateEmployees.Visibility = (isExistingSavedRecord && canEditNow) ? Visibility.Visible : Visibility.Collapsed;
            BtnImportScoresExcel.IsEnabled = canEditNow;
            BtnFillFullMarksAll.IsEnabled = canEditNow;
            BtnFillFullMarksSelected.IsEnabled = canEditNow;
            BtnClearMarksZero.IsEnabled = canEditNow;
            BtnSaveMonthlyEvaluation.IsEnabled = canEditNow;
            if (_currentWorkspace.IsMonthLocked)
            {
                BadgeMonthStatus.Background = new SolidColorBrush(Color.FromRgb(254, 242, 242));
                BadgeMonthStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(254, 202, 202));
                TxtBadgeMonthStatus.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                TxtBadgeMonthStatus.Text = $"🔒 شهر معتمد ومغلق نهائياً ({_currentWorkspace.LockedByUserName}) — للقراءة فقط";
                BtnUnlockSavedMonthEdit.Visibility = Visibility.Collapsed;
                BtnSyncTemplateEmployees.Visibility = Visibility.Collapsed;
            }

            if (!string.IsNullOrWhiteSpace(_currentWorkspace.UnmatchedEmployeesWarning))
            {
                TxtWorkspaceHint.Text = _currentWorkspace.UnmatchedEmployeesWarning;
                TxtWorkspaceHint.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
            }
            else
            {
                TxtWorkspaceHint.Text = "💡 تلميح الإدخال السريع: بمجرد كتابة الدرجة ينتقل المؤشر تلقائياً للخلية التالية حسب الاتجاه المحدد.";
                TxtWorkspaceHint.Foreground = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.SlateGray;
            }

            BtnSaveMonthlyEvaluation.Content = isExistingSavedRecord
                ? "💾 حفظ التعديلات على تقييم الشهر"
                : "💾 حفظ واعتماد تقييم الشهر";

            RebuildScoresDataGrid(canEditNow);
            RebuildBottomSheetTabsBar();
            UpdateSheetAverageBadge();
        }

        private void BtnUnlockSavedMonthEdit_Click(object sender, RoutedEventArgs e)
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

        private async void BtnSyncTemplateEmployees_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWorkspace == null || _currentWorkspace.RecordID <= 0 || _isReadOnlyMode)
                return;

            GridWeeklyScores.CommitEdit(DataGridEditingUnit.Cell, true);
            GridWeeklyScores.CommitEdit(DataGridEditingUnit.Row, true);

            int latestTemplateId = (CmbTemplate.SelectedItem as TemplateSummaryDto)?.TemplateID ?? _currentWorkspace.TemplateID;

            BtnSyncTemplateEmployees.IsEnabled = false;
            string originalContent = BtnSyncTemplateEmployees.Content?.ToString() ?? "🔄 مزامنة الموظفين مع القالب";
            BtnSyncTemplateEmployees.Content = "⏳ جاري المقارنة مع القالب...";

            TemplateSyncComparisonResult comparison;
            try
            {
                comparison = await _service.CompareWorkspaceWithLatestTemplateAsync(_currentWorkspace, latestTemplateId);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"تعذر مقارنة الشهر الحالي مع القالب:\n{ex.Message}", "خطأ", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                BtnSyncTemplateEmployees.Content = originalContent;
                BtnSyncTemplateEmployees.IsEnabled = true;
            }

            if (!comparison.HasAnyDifferences)
            {
                MessageBox.Show(
                    "قائمة الموظفين في جميع أوراق هذا الشهر مطابقة تماماً لأحدث نسخة من القالب — لا توجد فروقات للإضافة أو الإزالة.",
                    "القالب متطابق بالكامل",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            int currentTotalEmps = _currentWorkspace.Sheets.Sum(s => s.EmployeeRows.Count);
            var dlg = new SyncTemplateEmployeesDialog(_currentWorkspace, comparison, currentTotalEmps)
            {
                Owner = Window.GetWindow(this)
            };

            if (dlg.ShowDialog() != true)
                return;

            int addedCount = 0;
            int removedCount = 0;
            WeeklyEntrySheetModel? firstAddedSheet = null;
            var newlyAddedRowsOnFirstSheet = new List<WeeklyScoreRowViewModel>();
            var createdNewSheetsBySourceId = new Dictionary<int, WeeklyEntrySheetModel>();

            // 1. إضافة الموظفين الجدد المختارين إلى أوراقهم المقابلة
            foreach (var item in dlg.SelectedToAdd)
            {
                if (item.SourceLatestEmployeeRow == null || item.SourceLatestSheet == null)
                    continue;

                WeeklyEntrySheetModel targetSheet;
                if (item.MatchedSavedSheet != null)
                {
                    targetSheet = item.MatchedSavedSheet;
                }
                else
                {
                    int srcSheetId = item.SourceLatestSheet.SheetID;
                    if (!createdNewSheetsBySourceId.TryGetValue(srcSheetId, out targetSheet!))
                    {
                        targetSheet = new WeeklyEntrySheetModel
                        {
                            SheetID = item.SourceLatestSheet.SheetID,
                            SheetName = item.SourceLatestSheet.SheetName,
                            TargetCategoryName = item.SourceLatestSheet.TargetCategoryName,
                            SortOrder = _currentWorkspace.Sheets.Count + 1,
                            CriteriaColumns = item.SourceLatestSheet.CriteriaColumns.ToList(),
                            EmployeeRows = new System.Collections.ObjectModel.ObservableCollection<WeeklyScoreRowViewModel>()
                        };
                        _currentWorkspace.Sheets.Add(targetSheet);
                        createdNewSheetsBySourceId[srcSheetId] = targetSheet;
                    }
                }

                if (targetSheet.EmployeeRows.Any(x => x.EmployeeID == item.EmployeeID))
                    continue;

                var src = item.SourceLatestEmployeeRow;
                var newRow = new WeeklyScoreRowViewModel
                {
                    EmployeeID = src.EmployeeID,
                    RowOrder = targetSheet.EmployeeRows.Count + 1,
                    EvaluationName = src.EvaluationName,
                    SystemName = src.SystemName,
                    BranchName = src.BranchName,
                    CategoryName = src.CategoryName,
                    JobTitle = src.JobTitle,
                    JobTypeName = src.JobTypeName,
                    HasIncentive = src.HasIncentive,
                    IsContinuing = src.IsContinuing,
                    EvaluatorWeight = src.EvaluatorWeight
                };

                newRow.InitializeCriteria(
                    targetSheet.CriteriaColumns,
                    savedScoresByCriteriaId: null,
                    savedBalancingDelta: 0m,
                    isCountBased: _currentWorkspace.IsCountBasedEvaluation,
                    maxAllowedCount: _currentWorkspace.MaxAllowedCount,
                    savedRawCountsByCriteriaId: null);

                targetSheet.EmployeeRows.Add(newRow);
                addedCount++;

                if (firstAddedSheet == null)
                {
                    firstAddedSheet = targetSheet;
                }
                if (ReferenceEquals(targetSheet, firstAddedSheet))
                {
                    newlyAddedRowsOnFirstSheet.Add(newRow);
                }
            }

            // 2. إزالة الموظفين المختارين من أوراقهم
            foreach (var item in dlg.SelectedToRemove)
            {
                if (item.ExistingSavedSheet == null || item.ExistingSavedEmployeeRow == null)
                    continue;

                if (item.ExistingSavedSheet.EmployeeRows.Remove(item.ExistingSavedEmployeeRow))
                {
                    removedCount++;
                }
            }

            // 3. تنظيف أي ورقة أصبحت فارغة بالكامل (مع الإبقاء على ورقة واحدة على الأقل) وإعادة الترقيم
            if (_currentWorkspace.Sheets.Count > 1)
            {
                var emptySheets = _currentWorkspace.Sheets.Where(s => s.EmployeeRows.Count == 0).ToList();
                foreach (var emptySheet in emptySheets)
                {
                    if (_currentWorkspace.Sheets.Count > 1)
                    {
                        _currentWorkspace.Sheets.Remove(emptySheet);
                    }
                }
            }

            foreach (var sheet in _currentWorkspace.Sheets)
            {
                for (int i = 0; i < sheet.EmployeeRows.Count; i++)
                {
                    sheet.EmployeeRows[i].RowOrder = i + 1;
                }
            }

            // 4. تنشيط الورقة المناسبة وتحديد الموظفين الجدد مباشرة لتسهيل إدخال درجاتهم
            WeeklyEntrySheetModel sheetToActivate = firstAddedSheet != null && _currentWorkspace.Sheets.Contains(firstAddedSheet)
                ? firstAddedSheet
                : (_activeSheet != null && _currentWorkspace.Sheets.Contains(_activeSheet)
                    ? _activeSheet
                    : _currentWorkspace.Sheets[0]);

            ActivateSheet(sheetToActivate);

            if (newlyAddedRowsOnFirstSheet.Count > 0 && ReferenceEquals(sheetToActivate, firstAddedSheet))
            {
                GridWeeklyScores.SelectedItems.Clear();
                foreach (var addedRow in newlyAddedRowsOnFirstSheet)
                {
                    GridWeeklyScores.SelectedItems.Add(addedRow);
                }
                GridWeeklyScores.ScrollIntoView(newlyAddedRowsOnFirstSheet[0]);
            }

            TxtWorkspaceHint.Text =
                $"⚠ تم تطبيق المزامنة على الجدول (إضافة {addedCount} موظف | إزالة {removedCount} موظف) — راجع/أدخل الدرجات ثم اضغط (💾 حفظ التعديلات على تقييم الشهر) لاعتماد التغيير نهائياً.";
            TxtWorkspaceHint.Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9));
        }

        private void RebuildScoresDataGrid(bool canEditNow)
        {
            GridWeeklyScores.Columns.Clear();
            if (_activeSheet == null) return;

            var wrapStyle = TryFindResource("WrappingCellTextStyle") as Style;

            // العمود 0: م
            GridWeeklyScores.Columns.Add(new DataGridTextColumn
            {
                Header = "م",
                Binding = new Binding("RowOrder"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 45
            });

            // العمود 1: الاسم التقييمي
            GridWeeklyScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الاسم التقييمي للموظف",
                Binding = new Binding("EvaluationName"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 195
            });

            // العمود 2: الفرع
            GridWeeklyScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الفرع",
                Binding = new Binding("BranchName"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 85
            });

            // العمود 3: الوظيفة ونوعها
            GridWeeklyScores.Columns.Add(new DataGridTextColumn
            {
                Header = "الوظيفة ونوعها",
                Binding = new Binding("FullJobDisplay"),
                ElementStyle = wrapStyle,
                IsReadOnly = true,
                Width = 135
            });

            // العمود 4: الحافز
            GridWeeklyScores.Columns.Add(new DataGridTextColumn
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

            // الأعمدة الديناميكية للمبادئ ابتداءً من الفهرس FirstCriteriaColIndex = 5
            for (int i = 0; i < _activeSheet.CriteriaColumns.Count; i++)
            {
                var crit = _activeSheet.CriteriaColumns[i];

                var col = new DataGridTextColumn
                {
                    Header = (_currentWorkspace != null && _currentWorkspace.IsCountBasedEvaluation) ? $"{crit.CriteriaTitle}\n(الحد المسموح: {_currentWorkspace.MaxAllowedCount} — ما زاد = 0)" : $"{crit.CriteriaTitle}\n(من {crit.MaxScore:0})",
                    Binding = new Binding($"[{i}]")
                    {
                        Mode = BindingMode.TwoWay,
                        UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
                    },
                    ElementStyle = scoreDisplayStyle,
                    EditingElementStyle = scoreEditStyle,
                    IsReadOnly = !canEditNow,
                    Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 125
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
                Width = 100
            });

            var weightedStyle = new Style(typeof(TextBlock));
            weightedStyle.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Center));
            weightedStyle.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
            weightedStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.Bold));
            weightedStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromRgb(15, 23, 42))));

            GridWeeklyScores.Columns.Add(new DataGridTextColumn
            {
                Header = "حصة المقيم\nالموزونة",
                Binding = new Binding("WeightedScoreDisplay"),
                ElementStyle = weightedStyle,
                IsReadOnly = true,
                Width = 115
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
            if (e.PropertyName == nameof(WeeklyScoreRowViewModel.RawTotalOutOf100) ||
                e.PropertyName == nameof(WeeklyScoreRowViewModel.HasAnyEmptyCriteria) ||
                e.PropertyName == nameof(WeeklyScoreRowViewModel.IsCompletelyEmpty))
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

        private void GridWeeklyScores_PreparingCellForEdit(object sender, DataGridPreparingCellForEditEventArgs e)
        {
            if (_activeSheet == null || _isReadOnlyMode) return;

            ScoreGridNavigationHelper.AttachEditorHandlers(
                GridWeeklyScores,
                e,
                FirstCriteriaColIndex,
                _activeSheet.CriteriaColumns.Count,
                () => _isHorizontalAdvance,
                UpdateSheetAverageBadge);
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
                UpdateMissingScoresNotification();
                return;
            }

            var enteredRows = _activeSheet.EmployeeRows.Where(r => !r.IsCompletelyEmpty).ToList();
            decimal avg = enteredRows.Count == 0 ? 0m : enteredRows.Average(r => r.RawTotalOutOf100);
            TxtSheetAverageBadge.Text = $"المُقيَّمون بالورقة: {enteredRows.Count} من {_activeSheet.EmployeeRows.Count}  |  متوسط الدرجات: {avg:0.##} من 100";
            UpdateMissingScoresNotification();
        }

        private void UpdateMissingScoresNotification()
        {
            if (_currentWorkspace == null || BtnMissingScoresAlert == null)
            {
                if (BtnMissingScoresAlert != null)
                    BtnMissingScoresAlert.Visibility = Visibility.Collapsed;
                return;
            }

            int totalEmps = _currentWorkspace.Sheets.Sum(s => s.EmployeeRows.Count);
            int totalMissing = _currentWorkspace.Sheets.Sum(s => s.EmployeeRows.Count(r => r.HasAnyEmptyCriteria));
            int activeMissing = _activeSheet?.EmployeeRows.Count(r => r.HasAnyEmptyCriteria) ?? 0;

            BtnMissingScoresAlert.Visibility = totalEmps > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (totalMissing > 0)
            {
                BtnMissingScoresAlert.Background = new SolidColorBrush(Color.FromRgb(254, 242, 242));
                BtnMissingScoresAlert.BorderBrush = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                BtnMissingScoresAlert.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                BtnMissingScoresAlert.Content = _currentWorkspace.Sheets.Count > 1
                    ? $"⚠ لم يتم إدخال تقييم لـ ({totalMissing}) موظف ({activeMissing} في هذه الورقة) — اضغط للانتقال للموظف ⬇"
                    : $"⚠ لم يتم إدخال تقييم لـ ({totalMissing}) موظف — اضغط للانتقال للموظف ⬇";
            }
            else
            {
                BtnMissingScoresAlert.Background = new SolidColorBrush(Color.FromRgb(236, 253, 245));
                BtnMissingScoresAlert.BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                BtnMissingScoresAlert.Foreground = new SolidColorBrush(Color.FromRgb(4, 120, 87));
                BtnMissingScoresAlert.Content = $"✔ تم إدخال التقييم لجميع الموظفين ({totalEmps} / {totalEmps})";
            }

            foreach (var child in StackSheetTabs.Children)
            {
                if (child is Button btn && btn.Tag is WeeklyEntrySheetModel sheet)
                {
                    int sheetMissing = sheet.EmployeeRows.Count(r => r.HasAnyEmptyCriteria);
                    btn.Content = sheetMissing > 0
                        ? $"📄  {sheet.SheetName} ({sheet.TargetCategoryName}) [{sheet.EmployeeRows.Count} موظف | ⚠ {sheetMissing} فارغ]"
                        : $"📄  {sheet.SheetName} ({sheet.TargetCategoryName}) [{sheet.EmployeeRows.Count} موظف ✔]";
                }
            }
        }

        private void BtnMissingScoresAlert_Click(object sender, RoutedEventArgs e)
        {
            NavigateToNextMissingEmployee(startFromFirst: false);
        }

        private void NavigateToNextMissingEmployee(bool startFromFirst)
        {
            if (_currentWorkspace == null || _currentWorkspace.Sheets.Count == 0)
                return;

            GridWeeklyScores.CommitEdit(DataGridEditingUnit.Cell, true);
            GridWeeklyScores.CommitEdit(DataGridEditingUnit.Row, true);

            WeeklyEntrySheetModel? targetSheet = null;
            WeeklyScoreRowViewModel? targetEmp = null;

            int activeSheetIdx = _activeSheet != null ? _currentWorkspace.Sheets.IndexOf(_activeSheet) : 0;
            if (activeSheetIdx < 0) activeSheetIdx = 0;

            int currentRowIdx = -1;
            if (!startFromFirst && _activeSheet != null && GridWeeklyScores.SelectedItem is WeeklyScoreRowViewModel selectedRow)
            {
                currentRowIdx = _activeSheet.EmployeeRows.IndexOf(selectedRow);
            }

            // 1. البحث في الورقة الحالية بعد الموظف المحدد حالياً
            var startSheet = _currentWorkspace.Sheets[activeSheetIdx];
            for (int i = currentRowIdx + 1; i < startSheet.EmployeeRows.Count; i++)
            {
                if (startSheet.EmployeeRows[i].HasAnyEmptyCriteria)
                {
                    targetSheet = startSheet;
                    targetEmp = startSheet.EmployeeRows[i];
                    break;
                }
            }

            // 2. البحث في بقية أوراق القالب
            if (targetEmp == null)
            {
                for (int offset = 1; offset < _currentWorkspace.Sheets.Count; offset++)
                {
                    var sh = _currentWorkspace.Sheets[(activeSheetIdx + offset) % _currentWorkspace.Sheets.Count];
                    var found = sh.EmployeeRows.FirstOrDefault(r => r.HasAnyEmptyCriteria);
                    if (found != null)
                    {
                        targetSheet = sh;
                        targetEmp = found;
                        break;
                    }
                }
            }

            // 3. العودة لبداية الورقة الحالية إذا كان الموظف الفارغ قبل الموضع الحالي
            if (targetEmp == null && currentRowIdx >= 0)
            {
                for (int i = 0; i <= currentRowIdx && i < startSheet.EmployeeRows.Count; i++)
                {
                    if (startSheet.EmployeeRows[i].HasAnyEmptyCriteria)
                    {
                        targetSheet = startSheet;
                        targetEmp = startSheet.EmployeeRows[i];
                        break;
                    }
                }
            }

            if (targetSheet == null || targetEmp == null)
            {
                MessageBox.Show(
                    "✔ تم إدخال التقييم لجميع الموظفين في كافة أوراق القالب بنجاح!",
                    "التقييم مكتمل",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            if (!ReferenceEquals(targetSheet, _activeSheet))
            {
                ActivateSheet(targetSheet);
            }

            int critIdx = targetEmp.FirstEmptyCriteriaIndex;
            if (critIdx < 0) critIdx = 0;
            int targetGridColIndex = FirstCriteriaColIndex + critIdx;

            bool canEditNow = !_isReadOnlyMode && (_currentWorkspace.RecordID > 0
                ? (CanUpdate && _currentWorkspace.CanBeModified)
                : CanInsert);

            var empToFocus = targetEmp;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ScoreGridNavigationHelper.FocusAndEditCell(GridWeeklyScores, empToFocus, targetGridColIndex, beginEdit: canEditNow);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private async void BtnImportBulkMultiMonthExcel_Click(object sender, RoutedEventArgs e)
        {
            if (!CanInsert && !CanUpdate)
            {
                MessageBox.Show("عذراً، لا تملك صلاحية إضافة أو تعديل درجات التقييم.", "تنبيه صلاحيات", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int targetYear = CmbYear.SelectedItem is int y ? y : DateTime.Now.Year;

            var ofd = new OpenFileDialog
            {
                Title = $"اختر ملف الإكسل المجمع لتقييمات الأشهر (7 و 8 و 9 و 10) لعام {targetYear} م",
                Filter = "Excel Workbook (*.xlsx)|*.xlsx"
            };

            if (ofd.ShowDialog() == true)
            {
                BtnImportBulkMultiMonthExcel.IsEnabled = false;
                string originalText = BtnImportBulkMultiMonthExcel.Content?.ToString() ?? "📦 سحب التقييمات المجمعة";
                BtnImportBulkMultiMonthExcel.Content = "⏳ جاري سحب وحفظ تقييمات جميع الأشهر والمقيمين...";

                var (isSuccess, summaryMessage) = await _service.ImportBulkMultiMonthEvaluationsFromExcelAsync(ofd.FileName, targetYear);

                BtnImportBulkMultiMonthExcel.Content = originalText;
                BtnImportBulkMultiMonthExcel.IsEnabled = true;

                MessageBox.Show(
                    summaryMessage,
                    isSuccess ? "تقرير سحب التقييمات المجمعة" : "تنبيه سحب التقييمات",
                    MessageBoxButton.OK,
                    isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);

                if (isSuccess)
                {
                    await LoadSelectedMonthWorkspaceAsync();
                }
            }
        }

        private void BtnImportScoresExcel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWorkspace == null || _isReadOnlyMode) return;

            var ofd = new OpenFileDialog
            {
                Title = "اختر ملف إكسل الدرجات الشهري المستخرج من القالب",
                Filter = "Excel Workbook (*.xlsx)|*.xlsx"
            };

            if (ofd.ShowDialog() == true)
            {
                var (isSuccess, message) = TemplateExcelExportService.ImportScoresIntoWorkspace(_currentWorkspace, ofd.FileName);
                GridWeeklyScores.Items.Refresh();
                UpdateSheetAverageBadge();

                MessageBox.Show(
                    message,
                    isSuccess ? "استيراد الدرجات الشهرية من Excel" : "تنبيه الاستيراد",
                    MessageBoxButton.OK,
                    isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
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

                tabBtn.Tag = sheet;
                var capturedSheet = sheet;
                tabBtn.Click += (_, _) => ActivateSheet(capturedSheet);

                StackSheetTabs.Children.Add(tabBtn);
            }
        }

        private async void BtnSaveMonthlyEvaluation_Click(object sender, RoutedEventArgs e)
        {
            if (_currentWorkspace == null || _isReadOnlyMode) return;

            GridWeeklyScores.CommitEdit(DataGridEditingUnit.Cell, true);
            GridWeeklyScores.CommitEdit(DataGridEditingUnit.Row, true);

            int missingCount = _currentWorkspace.Sheets.Sum(s => s.EmployeeRows.Count(r => r.HasAnyEmptyCriteria));
            if (missingCount > 0)
            {
                var choice = MessageBox.Show(
                    $"⚠ تنبيه: يوجد ({missingCount}) موظف لم يتم إدخال تقييمهم بعد (خلاياهم فارغة).\n\n" +
                    "• اضغط (نعم) للانتقال مباشرة إلى أول موظف بدون تقييم لإكمال درجاته.\n" +
                    "• اضغط (لا) للمتابعة وحفظ الاستمارة مع احتساب الخلايا الفارغة بقيمة (0).\n" +
                    "• اضغط (إلغاء) للتراجع والبقاء في الصفحة.",
                    "يوجد موظفون بدون تقييم",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);

                if (choice == MessageBoxResult.Yes)
                {
                    NavigateToNextMissingEmployee(startFromFirst: true);
                    return;
                }
                if (choice != MessageBoxResult.No)
                {
                    return;
                }
            }

            BtnSaveMonthlyEvaluation.IsEnabled = false;
            var (isSuccess, message, savedRecordId) = await _service.SaveMonthlyEvaluationAsync(_currentWorkspace, "ScoreEntry");
            BtnSaveMonthlyEvaluation.IsEnabled = true;

            MessageBox.Show(
                message,
                isSuccess ? "تم حفظ التقييم الشهري" : "تنبيه التحقق من التقييم",
                MessageBoxButton.OK,
                isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (isSuccess && savedRecordId > 0)
            {
                await LoadSelectedMonthWorkspaceAsync();
            }
        }
    }
}














