using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class TemplatesView : UserControl
    {
        private readonly TemplateService _service;
        private TemplateFullModel? _currentTemplate;
        private TemplateSheetModel? _activeSheet;
        private List<SheetEmployeeRowModel> _availableCategoryEmployees = new();

        private bool _isReadOnlyViewMode = true;

        private bool CanInsert => AuthService.CurrentSession?.HasPermission("Templates", "insert") == true;
        private bool CanUpdate => AuthService.CurrentSession?.HasPermission("Templates", "update") == true;
        private bool CanDelete => AuthService.CurrentSession?.HasPermission("Templates", "delete") == true;

        public TemplatesView()
        {
            InitializeComponent();
            _service = new TemplateService();

            Loaded += async (_, _) =>
            {
                ApplyPermissionsToControls();
                await RefreshSavedTemplatesGridAsync();
            };
        }

        private void ApplyPermissionsToControls()
        {
            BtnCreateNewTemplate.Visibility = CanInsert ? Visibility.Visible : Visibility.Collapsed;
            BtnOpenSelectedTemplate.Visibility = CanUpdate ? Visibility.Visible : Visibility.Collapsed;
            BtnDeleteSelectedTemplate.Visibility = CanDelete ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task RefreshSavedTemplatesGridAsync()
        {
            string search = TxtSearchTemplateEvaluator.Text.Trim();
            var list = await _service.GetCurrentTemplatesAsync(search);
            GridSavedTemplates.ItemsSource = list;
            TxtTemplatesCountBadge.Text = $"إجمالي القوالب الفعالة: {list.Count}";
        }

        private async void TxtSearchTemplateEvaluator_TextChanged(object sender, TextChangedEventArgs e)
        {
            await RefreshSavedTemplatesGridAsync();
        }

        private async void BtnResetTemplateSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtSearchTemplateEvaluator.Text = string.Empty;
            await RefreshSavedTemplatesGridAsync();
        }

        private async void GridSavedTemplates_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GridSavedTemplates.SelectedItem is TemplateSummaryDto selected)
            {
                await LoadTemplateIntoDesignerAsync(selected.TemplateID, openInReadOnlyMode: true, switchToDesignerTab: false);
            }
        }

        private async void GridSavedTemplates_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (GridSavedTemplates.SelectedItem is TemplateSummaryDto selected)
            {
                await LoadTemplateIntoDesignerAsync(selected.TemplateID, openInReadOnlyMode: true, switchToDesignerTab: true);
            }
        }

        private async void BtnRowViewReadOnly_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is TemplateSummaryDto rowDto)
            {
                GridSavedTemplates.SelectedItem = rowDto;
                await LoadTemplateIntoDesignerAsync(rowDto.TemplateID, openInReadOnlyMode: true, switchToDesignerTab: true);
            }
        }

        private async void BtnOpenSelectedTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (!CanUpdate)
            {
                MessageBox.Show("عذراً، لا تملك صلاحية (تعديل) قوالب التقييم.", "تنبيه صلاحيات", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (GridSavedTemplates.SelectedItem is not TemplateSummaryDto selected)
            {
                MessageBox.Show("يرجى تحديد قالب من الجدول أولاً ثم الضغط على (✏ فتح وتعديل القالب).", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            await LoadTemplateIntoDesignerAsync(selected.TemplateID, openInReadOnlyMode: false, switchToDesignerTab: true);
        }

        private async Task LoadTemplateIntoDesignerAsync(int templateId, bool openInReadOnlyMode, bool switchToDesignerTab)
        {
            var fullModel = await _service.GetTemplateFullDetailsAsync(templateId);
            if (fullModel == null)
            {
                MessageBox.Show("تعذر تحميل تفاصيل القالب المحدد.", "خطأ", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            _currentTemplate = fullModel;
            _isReadOnlyViewMode = openInReadOnlyMode;

            if (switchToDesignerTab)
            {
                MainTemplatesTabControl.SelectedItem = TabTemplateDesigner;
            }

            if (_currentTemplate.Sheets.Count > 0)
            {
                await ActivateSheetInDesignerAsync(_currentTemplate.Sheets[0]);
            }
        }

        private async void BtnCreateNewTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (!CanInsert)
            {
                MessageBox.Show("عذراً، لا تملك صلاحية (إضافة وكتابة) لإنشاء قالب تقييم جديد.", "تنبيه صلاحيات", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var evaluators = await _service.SearchEvaluatorsAsync();
            if (evaluators.Count == 0)
            {
                MessageBox.Show("يرجى إضافة أسماء المقيمين في (1. صفحة الأساسيات) أولاً قبل إنشاء قوالب التقييم.", "تنبيه خطوة سابقة", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var categories = await _service.GetDistinctCategoryNamesForEvaluatorAsync(0);
            if (categories.Count == 0)
            {
                MessageBox.Show("يرجى إنشاء فئة واحدة على الأقل في (1. صفحة الأساسيات) قبل إنشاء قوالب التقييم.", "تنبيه خطوة سابقة", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var evalDlg = new SelectEvaluatorDialog
            {
                Owner = Window.GetWindow(this)
            };

            if (evalDlg.ShowDialog() != true || evalDlg.SelectedEvaluator == null)
                return;

            var chosenEvaluator = evalDlg.SelectedEvaluator;

            var sheetDlg = new AddSheetDialog(chosenEvaluator.EvaluatorID, nextSheetNumber: 1)
            {
                Owner = Window.GetWindow(this)
            };

            if (sheetDlg.ShowDialog() != true)
                return;

            _currentTemplate = new TemplateFullModel
            {
                TemplateID = 0,
                TemplateGroupGuid = Guid.NewGuid(),
                EvaluatorID = chosenEvaluator.EvaluatorID,
                EvaluatorName = chosenEvaluator.EvaluatorName,
                VersionNumber = 1
            };

            _isReadOnlyViewMode = false;

            var firstSheet = TemplateSheetModel.CreateDefault(sheetDlg.EnteredSheetName, sheetDlg.SelectedCategoryName, 1);
            _currentTemplate.Sheets.Add(firstSheet);

            MainTemplatesTabControl.SelectedItem = TabTemplateDesigner;
            await ActivateSheetInDesignerAsync(firstSheet);
        }

        private async void BtnPrintSelectedTemplatePdf_Click(object sender, RoutedEventArgs e)
        {
            if (GridSavedTemplates.SelectedItem is not TemplateSummaryDto selected)
            {
                MessageBox.Show("يرجى تحديد قالب من الجدول لطباعته.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var fullModel = await _service.GetTemplateFullDetailsAsync(selected.TemplateID);
            if (fullModel != null)
            {
                ExportTemplateToPdfWithDialog(fullModel);
            }
        }

        private async void BtnDeleteSelectedTemplate_Click(object sender, RoutedEventArgs e)
        {
            var selectedList = GridSavedTemplates.SelectedItems.Cast<TemplateSummaryDto>().ToList();
            if (selectedList.Count == 0)
            {
                MessageBox.Show("يرجى تحديد قالب واحد أو أكثر من الجدول لحذفه.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string confirmText = selectedList.Count == 1
                ? $"هل أنت متأكد من حذف قالب المقيم ({selectedList[0].EvaluatorName} - {selectedList[0].VersionDisplay})؟"
                : $"هل أنت متأكد من حذف ({selectedList.Count}) قوالب محددة دفعة واحدة؟";

            var confirm = MessageBox.Show(confirmText, "تأكيد الحذف", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            int deletedCount = 0;
            string lastMessage = string.Empty;

            foreach (var item in selectedList)
            {
                var (isSuccess, msg) = await _service.DeleteTemplateAsync(item.TemplateID);
                lastMessage = msg;
                if (isSuccess)
                {
                    deletedCount++;
                    if (_currentTemplate != null && _currentTemplate.TemplateID == item.TemplateID)
                    {
                        ResetDesignerToEmpty();
                    }
                }
            }

            await RefreshSavedTemplatesGridAsync();
            MessageBox.Show(
                selectedList.Count == 1 ? lastMessage : $"تم حذف ({deletedCount}) من أصل ({selectedList.Count}) قوالب بنجاح.",
                "نتيجة الحذف",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void ResetDesignerToEmpty()
        {
            _currentTemplate = null;
            _activeSheet = null;
            _isReadOnlyViewMode = true;
            TxtDesignerHeaderTitle.Text = "لم يتم فتح قالب بعد — اختر قالباً من القائمة أو أنشئ قالباً جديداً";
            BadgeSheetCategory.Visibility = Visibility.Collapsed;
            BadgeReadOnlyMode.Visibility = Visibility.Collapsed;
            PanelCriteriaDesigner.IsEnabled = false;
            PanelEmployeeAdder.IsEnabled = false;
            PanelSheetManageButtons.IsEnabled = false;
            BtnDesignerSaveTemplate.IsEnabled = false;
            BtnDesignerPrintPdf.IsEnabled = false;
            StackInteractiveColumns.Children.Clear();
            StackSheetTabs.Children.Clear();
            GridSheetPreview.Columns.Clear();
            GridSheetPreview.ItemsSource = null;
        }

        private async Task ActivateSheetInDesignerAsync(TemplateSheetModel sheet)
        {
            if (_currentTemplate == null) return;
            _activeSheet = sheet;

            bool hasRolePermission = (_currentTemplate.TemplateID == 0) ? CanInsert : CanUpdate;
            bool canEditNow = !_isReadOnlyViewMode && hasRolePermission;

            PanelCriteriaDesigner.IsEnabled = canEditNow;
            PanelEmployeeAdder.IsEnabled = canEditNow;
            PanelSheetManageButtons.IsEnabled = canEditNow;
            BtnDesignerSaveTemplate.IsEnabled = canEditNow;
            BtnDesignerSaveTemplate.Visibility = canEditNow ? Visibility.Visible : Visibility.Collapsed;
            BtnDesignerPrintPdf.IsEnabled = true;

            BadgeReadOnlyMode.Visibility = canEditNow ? Visibility.Collapsed : Visibility.Visible;

            string modeTitle = _currentTemplate.TemplateID == 0
                ? $"إنشاء قالب جديد — المقيم: {_currentTemplate.EvaluatorName}"
                : canEditNow
                    ? $"تعديل قالب المقيم: {_currentTemplate.EvaluatorName} (النسخة الحالية v{_currentTemplate.VersionNumber})"
                    : $"عرض قالب المقيم: {_currentTemplate.EvaluatorName} (النسخة v{_currentTemplate.VersionNumber})";

            TxtDesignerHeaderTitle.Text = modeTitle;
            TxtActiveSheetCategoryBadge.Text = $"الورقة النشطة: {_activeSheet.SheetName}  |  الفئة: {_activeSheet.TargetCategoryName}";
            BadgeSheetCategory.Visibility = Visibility.Collapsed;

            _availableCategoryEmployees = await _service.GetEmployeesByCategoryNameAsync(
                _activeSheet.TargetCategoryName,
                _currentTemplate.EvaluatorID);

            RefreshAvailableEmployeesCombo();
            RebuildInteractiveCriteriaStrip(canEditNow);
            RebuildPreviewDataGrid(canEditNow);
            RebuildBottomSheetTabsBar();
        }

        private void RefreshAvailableEmployeesCombo()
        {
            if (_activeSheet == null) return;
            var addedIds = _currentTemplate != null ? _currentTemplate.Sheets.SelectMany(s => s.EmployeeRows).Select(e => e.EmployeeID).ToHashSet() : _activeSheet.EmployeeRows.Select(e => e.EmployeeID).ToHashSet();
            var remaining = _availableCategoryEmployees.Where(e => !addedIds.Contains(e.EmployeeID)).ToList();

            CmbSearchCategoryEmployees.ItemsSource = remaining;
            if (remaining.Count > 0)
                CmbSearchCategoryEmployees.SelectedIndex = 0;
            else
                CmbSearchCategoryEmployees.Text = string.Empty;

            BtnAddAllCategoryEmployees.Content = $"⚡ إدراج جميع موظفي فئة ({_activeSheet.TargetCategoryName}) المتبقين ({remaining.Count})";
        }

        private void RebuildInteractiveCriteriaStrip(bool canEditNow)
        {
            StackInteractiveColumns.Children.Clear();
            if (_activeSheet == null) return;

            _activeSheet.ReindexOrders();
            _activeSheet.NotifyTotalsChanged();

            decimal total = _activeSheet.TotalCriteriaScore;
            decimal diff = 100m - total;

            if (total == 100m)
            {
                BannerSheetCriteriaSum.Background = new SolidColorBrush(Color.FromRgb(236, 253, 245));
                BannerSheetCriteriaSum.BorderBrush = new SolidColorBrush(Color.FromRgb(167, 243, 208));
                TxtSheetCriteriaSum.Foreground = new SolidColorBrush(Color.FromRgb(4, 120, 87));
                TxtSheetCriteriaSum.Text = "✔ مجموع درجات أعمدة الورقة مكتمل: 100 من 100 درجة";
            }
            else if (total < 100m)
            {
                BannerSheetCriteriaSum.Background = new SolidColorBrush(Color.FromRgb(255, 251, 235));
                BannerSheetCriteriaSum.BorderBrush = new SolidColorBrush(Color.FromRgb(253, 230, 138));
                TxtSheetCriteriaSum.Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9));
                TxtSheetCriteriaSum.Text = $"مجموع درجات أعمدة الورقة: {total:0.##} من 100 (المتبقي: {diff:0.##} درجة)";
            }
            else
            {
                BannerSheetCriteriaSum.Background = new SolidColorBrush(Color.FromRgb(254, 242, 242));
                BannerSheetCriteriaSum.BorderBrush = new SolidColorBrush(Color.FromRgb(254, 202, 202));
                TxtSheetCriteriaSum.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                TxtSheetCriteriaSum.Text = $"⚠ المجموع تجاوز 100! الحالي: {total:0.##} من 100 (الزيادة: {Math.Abs(diff):0.##})";
            }

            if (canEditNow)
            {
                StackInteractiveColumns.Children.Add(CreateInsertColumnPlusButton(0));
            }

            for (int i = 0; i < _activeSheet.CriteriaColumns.Count; i++)
            {
                int colIndex = i;
                var colModel = _activeSheet.CriteriaColumns[i];

                var colButton = new Button
                {
                    Cursor = canEditNow ? Cursors.Hand : Cursors.Arrow,
                    Margin = new Thickness(4, 0, 4, 0),
                    Padding = new Thickness(14, 6, 14, 6),
                    Background = colModel.MaxScore > 0
                        ? new SolidColorBrush(Color.FromRgb(239, 246, 255))
                        : new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                    BorderBrush = colModel.MaxScore > 0
                        ? new SolidColorBrush(Color.FromRgb(59, 130, 246))
                        : new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                    BorderThickness = new Thickness(1.5),
                    ToolTip = canEditNow ? "اضغط هنا لتسمية مبدأ التقييم وتحديد درجته أو حذفه" : "وضع القراءة فقط"
                };

                var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                stack.Children.Add(new TextBlock
                {
                    Text = canEditNow ? $"✎ {colModel.CriteriaTitle}" : colModel.CriteriaTitle,
                    FontWeight = FontWeights.Bold,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                stack.Children.Add(new TextBlock
                {
                    Text = $"الدرجة: {colModel.MaxScore:0.##} من 100",
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = colModel.MaxScore > 0
                        ? new SolidColorBrush(Color.FromRgb(37, 99, 235))
                        : new SolidColorBrush(Color.FromRgb(180, 83, 9)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 2, 0, 0)
                });

                colButton.Content = stack;
                if (canEditNow)
                {
                    colButton.Click += (_, _) => OpenEditCriteriaPopup(colIndex);
                }

                StackInteractiveColumns.Children.Add(colButton);

                if (canEditNow)
                {
                    StackInteractiveColumns.Children.Add(CreateInsertColumnPlusButton(colIndex + 1));
                }
            }
        }

        private Button CreateInsertColumnPlusButton(int insertAtIndex)
        {
            var plusBtn = new Button
            {
                Content = "+",
                Width = 28,
                Height = 38,
                FontWeight = FontWeights.Bold,
                FontSize = 16,
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(2, 0, 2, 0),
                ToolTip = "اضغط (+) لإدراج عمود مبدأ تقييم جديد في هذا المكان"
            };

            plusBtn.Click += (_, _) =>
            {
                if (_activeSheet == null || _isReadOnlyViewMode) return;
                var newCol = new CriteriaColumnModel
                {
                    CriteriaTitle = "قواعد التقييم",
                    MaxScore = 0m,
                    ColumnOrder = insertAtIndex + 1
                };

                _activeSheet.CriteriaColumns.Insert(insertAtIndex, newCol);
                RebuildInteractiveCriteriaStrip(true);
                RebuildPreviewDataGrid(true);
                RebuildBottomSheetTabsBar();

                OpenEditCriteriaPopup(insertAtIndex);
            };

            return plusBtn;
        }

        private void OpenEditCriteriaPopup(int colIndex)
        {
            if (_activeSheet == null || _isReadOnlyViewMode || colIndex < 0 || colIndex >= _activeSheet.CriteriaColumns.Count)
                return;

            var targetCol = _activeSheet.CriteriaColumns[colIndex];
            decimal otherColsSum = _activeSheet.CriteriaColumns
                .Where((_, idx) => idx != colIndex)
                .Sum(c => c.MaxScore);

            bool allowDelete = _activeSheet.CriteriaColumns.Count > 1;

            var dlg = new EditCriteriaDialog(targetCol.CriteriaTitle, targetCol.MaxScore, otherColsSum, allowDelete)
            {
                Owner = Window.GetWindow(this)
            };

            if (dlg.ShowDialog() == true)
            {
                if (dlg.IsDeleteRequested)
                {
                    _activeSheet.CriteriaColumns.RemoveAt(colIndex);
                }
                else
                {
                    targetCol.CriteriaTitle = dlg.CriteriaTitleResult;
                    targetCol.MaxScore = dlg.MaxScoreResult;
                }

                RebuildInteractiveCriteriaStrip(true);
                RebuildPreviewDataGrid(true);
                RebuildBottomSheetTabsBar();
            }
        }

        private void BtnDistributeCriteriaEqually_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSheet == null || _isReadOnlyViewMode || _activeSheet.CriteriaColumns.Count == 0)
                return;

            int count = _activeSheet.CriteriaColumns.Count;
            decimal baseShare = Math.Floor((100m / count) * 100m) / 100m;
            decimal distributed = 0m;

            for (int i = 0; i < count; i++)
            {
                if (i == count - 1)
                {
                    _activeSheet.CriteriaColumns[i].MaxScore = 100m - distributed;
                }
                else
                {
                    _activeSheet.CriteriaColumns[i].MaxScore = baseShare;
                    distributed += baseShare;
                }
            }

            RebuildInteractiveCriteriaStrip(true);
            RebuildPreviewDataGrid(true);
            RebuildBottomSheetTabsBar();
        }

        private void RebuildPreviewDataGrid(bool canEditNow)
        {
            GridSheetPreview.Columns.Clear();
            if (_activeSheet == null) return;

            _activeSheet.ReindexOrders();
            var wrapStyle = TryFindResource("WrappingCellTextStyle") as Style;

            GridSheetPreview.Columns.Add(new DataGridTextColumn
            {
                Header = "م",
                Binding = new System.Windows.Data.Binding("RowOrder"),
                ElementStyle = wrapStyle,
                Width = 45
            });

            GridSheetPreview.Columns.Add(new DataGridTextColumn
            {
                Header = "الاسم التقييمي للموظف",
                Binding = new System.Windows.Data.Binding("EvaluationName"),
                ElementStyle = wrapStyle,
                Width = 210
            });

            GridSheetPreview.Columns.Add(new DataGridTextColumn
            {
                Header = "الفرع",
                Binding = new System.Windows.Data.Binding("BranchName"),
                ElementStyle = wrapStyle,
                Width = 95
            });

            GridSheetPreview.Columns.Add(new DataGridTextColumn
            {
                Header = "الوظيفة",
                Binding = new System.Windows.Data.Binding("JobTitle"),
                ElementStyle = wrapStyle,
                Width = 95
            });

            for (int i = 0; i < _activeSheet.CriteriaColumns.Count; i++)
            {
                int capturedIndex = i;
                var crit = _activeSheet.CriteriaColumns[i];

                var headerTextBlock = new TextBlock
                {
                    Text = $"{crit.CriteriaTitle} ({crit.MaxScore:0.##})",
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175))
                };

                var headerBtn = new Button
                {
                    Content = headerTextBlock,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = canEditNow ? Cursors.Hand : Cursors.Arrow,
                    ToolTip = canEditNow ? "اضغط لتعديل اسم مبدأ التقييم أو درجته" : "وضع القراءة فقط"
                };

                if (canEditNow)
                {
                    headerBtn.Click += (_, _) => OpenEditCriteriaPopup(capturedIndex);
                }

                var col = new DataGridTextColumn
                {
                    Header = headerBtn,
                    Binding = new System.Windows.Data.Binding
                    {
                        Source = string.Empty
                    },
                    ElementStyle = wrapStyle,
                    Width = new DataGridLength(1, DataGridLengthUnitType.Star)
                };

                GridSheetPreview.Columns.Add(col);
            }

            GridSheetPreview.ItemsSource = null;
            GridSheetPreview.ItemsSource = _activeSheet.EmployeeRows;
        }

        private void BtnAddSelectedEmployeeToSheet_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSheet == null || _isReadOnlyViewMode) return;

            SheetEmployeeRowModel? empToAdd = CmbSearchCategoryEmployees.SelectedItem as SheetEmployeeRowModel;

            if (empToAdd == null && !string.IsNullOrWhiteSpace(CmbSearchCategoryEmployees.Text))
            {
                string typed = CmbSearchCategoryEmployees.Text.Trim();
                empToAdd = _availableCategoryEmployees.FirstOrDefault(x =>
                    x.EvaluationName.Contains(typed, StringComparison.OrdinalIgnoreCase));
            }

            if (empToAdd == null)
            {
                MessageBox.Show($"يرجى اختيار موظف من قائمة موظفي فئة ({_activeSheet.TargetCategoryName}).", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_activeSheet.EmployeeRows.Any(x => x.EmployeeID == empToAdd.EmployeeID))
            {
                MessageBox.Show($"الموظف ({empToAdd.EvaluationName}) مضاف بالفعل في هذه الورقة.", "موجود مسبقاً", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            empToAdd.RowOrder = _activeSheet.EmployeeRows.Count + 1;
            _activeSheet.EmployeeRows.Add(empToAdd);

            RefreshAvailableEmployeesCombo();
            RebuildPreviewDataGrid(true);
            RebuildBottomSheetTabsBar();
        }

        private void BtnAddAllCategoryEmployees_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSheet == null || _isReadOnlyViewMode) return;

            var existingIds = _currentTemplate != null ? _currentTemplate.Sheets.SelectMany(s => s.EmployeeRows).Select(x => x.EmployeeID).ToHashSet() : _activeSheet.EmployeeRows.Select(x => x.EmployeeID).ToHashSet();
            var toAdd = _availableCategoryEmployees.Where(x => !existingIds.Contains(x.EmployeeID)).ToList();

            if (toAdd.Count == 0)
            {
                MessageBox.Show($"جميع موظفي فئة ({_activeSheet.TargetCategoryName}) مضافون بالفعل في هذه الورقة.", "مكتمل", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var emp in toAdd)
            {
                emp.RowOrder = _activeSheet.EmployeeRows.Count + 1;
                _activeSheet.EmployeeRows.Add(emp);
            }

            RefreshAvailableEmployeesCombo();
            RebuildPreviewDataGrid(true);
            RebuildBottomSheetTabsBar();
        }

        private void BtnRemoveEmployeeFromSheet_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSheet == null || _isReadOnlyViewMode) return;

            var selectedEmps = GridSheetPreview.SelectedItems.Cast<SheetEmployeeRowModel>().ToList();
            if (selectedEmps.Count == 0)
            {
                MessageBox.Show("يرجى تحديد موظف واحد أو أكثر من جدول الورقة لإزالته.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var emp in selectedEmps)
            {
                _activeSheet.EmployeeRows.Remove(emp);
            }

            _activeSheet.ReindexOrders();

            RefreshAvailableEmployeesCombo();
            RebuildPreviewDataGrid(true);
            RebuildBottomSheetTabsBar();
        }

        private void RebuildBottomSheetTabsBar()
        {
            StackSheetTabs.Children.Clear();
            if (_currentTemplate == null) return;

            foreach (var sheet in _currentTemplate.Sheets)
            {
                bool isCurrent = ReferenceEquals(sheet, _activeSheet);
                bool isValid100 = sheet.IsCriteriaTotalValid && sheet.EmployeeRows.Count > 0;

                string statusIcon = isValid100 ? "✔" : "⚠";
                var tabBtn = new Button
                {
                    Content = $"{statusIcon}  {sheet.SheetName} ({sheet.TargetCategoryName}) [{sheet.EmployeeRows.Count} موظف]",
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
                tabBtn.Click += async (_, _) => await ActivateSheetInDesignerAsync(capturedSheet);

                StackSheetTabs.Children.Add(tabBtn);
            }
        }

        private async void BtnAddNewSheetTab_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTemplate == null || _isReadOnlyViewMode) return;

            var dlg = new AddSheetDialog(_currentTemplate.EvaluatorID, _currentTemplate.Sheets.Count + 1)
            {
                Owner = Window.GetWindow(this)
            };

            if (dlg.ShowDialog() == true)
            {
                var newSheet = TemplateSheetModel.CreateDefault(
                    dlg.EnteredSheetName,
                    dlg.SelectedCategoryName,
                    _currentTemplate.Sheets.Count + 1);

                _currentTemplate.Sheets.Add(newSheet);
                await ActivateSheetInDesignerAsync(newSheet);
            }
        }

        private async void BtnRenameCurrentSheet_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTemplate == null || _activeSheet == null || _isReadOnlyViewMode) return;

            var dlg = new AddSheetDialog(
                _currentTemplate.EvaluatorID,
                _activeSheet.SortOrder,
                _activeSheet.SheetName,
                _activeSheet.TargetCategoryName)
            {
                Owner = Window.GetWindow(this)
            };

            if (dlg.ShowDialog() == true)
            {
                bool categoryChanged = !string.Equals(_activeSheet.TargetCategoryName, dlg.SelectedCategoryName, StringComparison.OrdinalIgnoreCase);
                if (categoryChanged && _activeSheet.EmployeeRows.Count > 0)
                {
                    var confirm = MessageBox.Show(
                        "تغيير فئة الورقة سيؤدي إلى تفريغ قائمة الموظفين المضافين حالياً في هذه الورقة لأنهم يتبعون الفئة السابقة. هل تريد المتابعة؟",
                        "تأكيد تغيير فئة الورقة",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (confirm != MessageBoxResult.Yes) return;
                    _activeSheet.EmployeeRows.Clear();
                }

                _activeSheet.SheetName = dlg.EnteredSheetName;
                _activeSheet.TargetCategoryName = dlg.SelectedCategoryName;
                await ActivateSheetInDesignerAsync(_activeSheet);
            }
        }

        private async void BtnDeleteCurrentSheet_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTemplate == null || _activeSheet == null || _isReadOnlyViewMode) return;

            if (_currentTemplate.Sheets.Count <= 1)
            {
                MessageBox.Show("لا يمكن حذف الورقة الوحيدة في القالب. يجب أن يحتوي القالب على ورقة تقييم واحدة على الأقل.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                $"هل أنت متأكد من حذف الورقة ({_activeSheet.SheetName}) من هذا القالب؟",
                "تأكيد حذف الورقة",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            _currentTemplate.Sheets.Remove(_activeSheet);
            await ActivateSheetInDesignerAsync(_currentTemplate.Sheets[0]);
        }

        private async void BtnDesignerSaveTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTemplate == null || _isReadOnlyViewMode) return;

            BtnDesignerSaveTemplate.IsEnabled = false;
            var (isSuccess, message, savedId) = await _service.SaveTemplateAsync(_currentTemplate);
            BtnDesignerSaveTemplate.IsEnabled = true;

            MessageBox.Show(message, isSuccess ? "تم حفظ القالب" : "تنبيه التحقق من شروط القالب", MessageBoxButton.OK, isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (isSuccess && savedId > 0)
            {
                var reloaded = await _service.GetTemplateFullDetailsAsync(savedId);
                if (reloaded != null)
                {
                    _currentTemplate = reloaded;
                    if (_currentTemplate.Sheets.Count > 0)
                        await ActivateSheetInDesignerAsync(_currentTemplate.Sheets[0]);
                }
                await RefreshSavedTemplatesGridAsync();
            }
        }

        private void BtnDesignerPrintPdf_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTemplate == null) return;
            ExportTemplateToPdfWithDialog(_currentTemplate);
        }

        private void ExportTemplateToPdfWithDialog(TemplateFullModel templateToPrint)
        {
            if (templateToPrint.Sheets.Count == 0)
            {
                MessageBox.Show("لا توجد أوراق في القالب لطباعتها.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var sfd = new SaveFileDialog
            {
                Title = "حفظ وطباعة استمارة التقييم الأسبوعي بحجم A4",
                Filter = "PDF Document (*.pdf)|*.pdf",
                FileName = $"استمارة_تقييم_{templateToPrint.EvaluatorName}_v{templateToPrint.VersionNumber}.pdf"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    TemplatePdfExportService.GenerateTemplateA4Pdf(templateToPrint, sfd.FileName);

                    var openNow = MessageBox.Show(
                        $"تم توليد ملف الطباعة بحجم A4 الذكي بنجاح في المسار:\n{sfd.FileName}\n\nهل تريد فتح ملف الـ PDF الآن لمعاينته وطباعته؟",
                        "جاهز للطباعة (A4)",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);

                    if (openNow == MessageBoxResult.Yes)
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = sfd.FileName,
                            UseShellExecute = true
                        });
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"حدث خطأ أثناء توليد ملف الـ PDF:\n{ex.Message}", "خطأ طباعة", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}



