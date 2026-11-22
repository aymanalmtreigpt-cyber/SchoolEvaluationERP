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
        private readonly TemplateService _service = new();
        private readonly MonthlyPeriodConfigService _periodService = new();
        private TemplateFullModel? _currentTemplate;
        private TemplateSheetModel? _activeSheet;
        private List<SheetEmployeeRowModel> _availableCategoryEmployees = new();

        private bool _isReadOnlyViewMode = true;
        private bool _isInitializingPeriod;
        private int _searchRequestVersion;

        private int SelectedTemplateYear => CmbTemplatePreviewYear?.SelectedItem is int y ? y : DateTime.Now.Year;
        private int SelectedTemplateMonth => CmbTemplatePreviewMonth?.SelectedValue is int m ? m : DateTime.Now.Month;

        private bool CanInsert => AuthService.CurrentSession?.HasPermission("Templates", "insert") == true;
        private bool CanUpdate => AuthService.CurrentSession?.HasPermission("Templates", "update") == true;
        private bool CanDelete => AuthService.CurrentSession?.HasPermission("Templates", "delete") == true;

        public TemplatesView()
        {
            InitializeComponent();

            Loaded += async (_, _) =>
            {
                ApplyPermissionsToControls();
                await InitializeTemplatePeriodSelectorsAsync();
                await RefreshSavedTemplatesGridAsync();
            };
        }

        private void ApplyPermissionsToControls()
        {
            BtnCreateNewTemplate.Visibility = CanInsert ? Visibility.Visible : Visibility.Collapsed;
            BtnOpenSelectedTemplate.Visibility = CanUpdate ? Visibility.Visible : Visibility.Collapsed;
            BtnDeleteSelectedTemplate.Visibility = CanDelete ? Visibility.Visible : Visibility.Collapsed;
        }

        private Task InitializeTemplatePeriodSelectorsAsync()
        {
            _isInitializingPeriod = true;

            int currentYear = DateTime.Now.Year;
            int currentMonth = DateTime.Now.Month;
            var years = new List<int> { currentYear - 1, currentYear, currentYear + 1, currentYear + 2 };

            CmbTemplatePreviewYear.ItemsSource = years;
            CmbTemplatePreviewYear.SelectedItem = currentYear;

            CmbTemplatePreviewMonth.ItemsSource = MonthOptionItem.GetAllMonths();
            CmbTemplatePreviewMonth.SelectedValue = currentMonth;

            _isInitializingPeriod = false;
            return Task.CompletedTask;
        }

        private async void TemplatePeriodFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingPeriod || !IsLoaded) return;
            await RefreshSavedTemplatesGridAsync(syncSystemTemplates: false);

            if (_currentTemplate != null && _currentTemplate.TemplateID > 0 && _isReadOnlyViewMode)
            {
                await LoadTemplateIntoDesignerAsync(_currentTemplate.TemplateID, openInReadOnlyMode: true, switchToDesignerTab: false);
            }
        }

        private async Task RefreshSavedTemplatesGridAsync(bool syncSystemTemplates = true)
        {
            if (!IsLoaded) return;
            int requestVersion = ++_searchRequestVersion;
            string search = TxtSearchTemplateEvaluator.Text.Trim();
            var list = await _service.GetCurrentTemplatesAsync(search, syncSystemTemplates, SelectedTemplateYear, SelectedTemplateMonth);
            if (requestVersion != _searchRequestVersion)
                return;

            GridSavedTemplates.ItemsSource = list;
            string mName = IncentiveCalculatorService.GetArabicMonthName(SelectedTemplateMonth);
            TxtTemplatesCountBadge.Text = $"إجمالي القوالب الفعالة ({mName} {SelectedTemplateYear}): {list.Count}";
        }

        private async void TxtSearchTemplateEvaluator_TextChanged(object sender, TextChangedEventArgs e)
        {
            await RefreshSavedTemplatesGridAsync(syncSystemTemplates: false);
        }

        private async void BtnResetTemplateSearch_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(TxtSearchTemplateEvaluator.Text))
            {
                TxtSearchTemplateEvaluator.Text = string.Empty;
            }
            else
            {
                await RefreshSavedTemplatesGridAsync(syncSystemTemplates: false);
            }
        }

        private async void GridSavedTemplates_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MainTemplatesTabControl.SelectedItem == TabTemplateDesigner && !_isReadOnlyViewMode)
                return;

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
            var fullModel = await _service.GetTemplateFullDetailsAsync(templateId, SelectedTemplateYear, SelectedTemplateMonth);
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
                MessageBox.Show("يرجى إضافة مجموعات وأسماء المقيمين في (1. صفحة الأساسيات) أولاً قبل إنشاء قوالب التقييم.", "تنبيه خطوة سابقة", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool hasAnyCategories = await _service.HasAnyActiveCategoriesAsync();
            if (!hasAnyCategories)
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

            if (chosenEvaluator.HasExistingTemplate)
            {
                MessageBox.Show(
                    $"المقيم ({chosenEvaluator.EvaluatorName}) لديه قالب تقييم فعّال مسبقاً.\nسيتم فتح قالبه مباشرة في وضع التعديل مع تحديث الأوراق والموظفين تلقائياً.",
                    "فتح القالب الموجود",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                await LoadTemplateIntoDesignerAsync(chosenEvaluator.ExistingTemplateID, openInReadOnlyMode: false, switchToDesignerTab: true);
                return;
            }

            var autoTemplate = await _service.BuildAutoTemplateForEvaluatorAsync(chosenEvaluator, SelectedTemplateYear, SelectedTemplateMonth);
            if (autoTemplate == null || autoTemplate.Sheets.Count == 0)
            {
                MessageBox.Show(
                    $"لا يوجد موظفون مستمرون مسندون للمقيم ({chosenEvaluator.EvaluatorName}) في الشهر المحدد حالياً.\nيرجى تخصيص الدرجات للفئات وإسناد الموظفين له من (1. صفحة الأساسيات) أولاً.",
                    "لا يوجد موظفون مسندون للمقيم",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var (isSaved, saveMsg, savedId) = await _service.SaveTemplateAsync(autoTemplate, SelectedTemplateYear, SelectedTemplateMonth);
            if (!isSaved || savedId <= 0)
            {
                MessageBox.Show(saveMsg, "تعذر إنشاء القالب", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int totalAutoEmps = autoTemplate.Sheets.Sum(s => s.EmployeeRows.Count);
            await RefreshSavedTemplatesGridAsync(syncSystemTemplates: false);
            await LoadTemplateIntoDesignerAsync(savedId, openInReadOnlyMode: false, switchToDesignerTab: true);

            MessageBox.Show(
                $"✔ تم إنشاء وحفظ قالب المقيم ({chosenEvaluator.EvaluatorName}) تلقائياً بنجاح:\n" +
                $"• عدد الأوراق المنشأة تلقائياً (بأسماء الفئات): {autoTemplate.Sheets.Count} ورقة\n" +
                $"• إجمالي الموظفين المدرجين من جميع الفروع: {totalAutoEmps} موظف\n" +
                $"• العمود الافتراضي لكل ورقة: (أساسيات التقييم — 100 درجة)\n\n" +
                "يمكنك تعديل أعمدة التقييم أو استثناء أي موظف يدوياً ثم الضغط على (💾 حفظ واعتماد القالب بالكامل) متى شئت.",
                "تم إنشاء القالب تلقائياً",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private async void BtnPrintSelectedTemplatePdf_Click(object sender, RoutedEventArgs e)
        {
            if (GridSavedTemplates.SelectedItem is not TemplateSummaryDto selected)
            {
                MessageBox.Show("يرجى تحديد قالب من الجدول لطباعته.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var fullModel = await _service.GetTemplateFullDetailsAsync(selected.TemplateID, SelectedTemplateYear, SelectedTemplateMonth);
            if (fullModel != null)
            {
                ExportTemplateToPdfWithDialog(fullModel);
            }
        }

        private async void BtnExportAllEvaluatorsEmployeesExcel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var evaluatorSheets = await _service.GetAllEvaluatorsWithAssignedEmployeesAsync(SelectedTemplateYear, SelectedTemplateMonth);
                if (evaluatorSheets.Count == 0)
                {
                    MessageBox.Show(
                        "لا يوجد مقيمون مسجلون في النظام حالياً. يرجى إضافة المقيمين في (1. صفحة الأساسيات) أولاً.",
                        "تنبيه",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var sfd = new SaveFileDialog
                {
                    Title = "استخراج ملف إكسل شامل ببيانات الموظفين لكل مقيم (كل مقيم في ورقة)",
                    Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                    FileName = $"بيانات_الموظفين_لكل_مقيم_{DateTime.Now:yyyy-MM-dd}.xlsx"
                };

                if (sfd.ShowDialog() == true)
                {
                    TemplateExcelExportService.GenerateAllEvaluatorsEmployeesExcel(evaluatorSheets, sfd.FileName);

                    int totalSheets = evaluatorSheets.Count;
                    var openNow = MessageBox.Show(
                        $"تم استخراج ملف الإكسل الشامل بنجاح!\n• عدد أوراق المقيمين في الملف: ({totalSheets}) ورقة\n• المسار: {sfd.FileName}\n\nهل تريد فتح ملف الإكسل الآن؟",
                        "تم استخراج بيانات الموظفين لكل مقيم",
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
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"حدث خطأ أثناء استخراج ملف بيانات الموظفين لكل مقيم:\n{ex.Message}",
                    "خطأ تصدير",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void BtnExportSelectedTemplateExcel_Click(object sender, RoutedEventArgs e)
        {
            if (GridSavedTemplates.SelectedItem is not TemplateSummaryDto selected)
            {
                MessageBox.Show("يرجى تحديد قالب من الجدول لاستخراجه بصيغة Excel.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var fullModel = await _service.GetTemplateFullDetailsAsync(selected.TemplateID, SelectedTemplateYear, SelectedTemplateMonth);
            if (fullModel != null)
            {
                ExportTemplateToExcelWithDialog(fullModel);
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
            BtnDesignerExportExcel.IsEnabled = false;
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
            BtnDesignerExportExcel.IsEnabled = true;

            BadgeReadOnlyMode.Visibility = canEditNow ? Visibility.Collapsed : Visibility.Visible;

            string groupInfo = string.IsNullOrWhiteSpace(_currentTemplate.GroupName) ? "" : $" [{_currentTemplate.GroupName}]";
            string modeTitle = _currentTemplate.TemplateID == 0
                ? $"إنشاء قالب جديد — المقيم: {_currentTemplate.EvaluatorName}{groupInfo}"
                : canEditNow
                    ? $"تعديل قالب المقيم: {_currentTemplate.EvaluatorName}{groupInfo} (النسخة الحالية v{_currentTemplate.VersionNumber})"
                    : $"عرض قالب المقيم: {_currentTemplate.EvaluatorName}{groupInfo} (النسخة v{_currentTemplate.VersionNumber})";

            TxtDesignerHeaderTitle.Text = modeTitle;
            TxtActiveSheetCategoryBadge.Text = $"الورقة النشطة: {_activeSheet.SheetName}  |  الفئة والفرع: {_activeSheet.CategoryWithBranchDisplay}";
            BadgeSheetCategory.Visibility = Visibility.Visible;

            _availableCategoryEmployees = await _service.GetEligibleEmployeesForSheetAsync(
                _activeSheet.TargetCategoryID,
                _activeSheet.TargetBranchID,
                _currentTemplate.EvaluatorID,
                SelectedTemplateYear,
                SelectedTemplateMonth);

            RefreshAvailableEmployeesCombo();
            RebuildInteractiveCriteriaStrip(canEditNow);
            RebuildPreviewDataGrid(canEditNow);
            RebuildBottomSheetTabsBar();
        }

        private void RefreshAvailableEmployeesCombo()
        {
            if (_activeSheet == null) return;
            var addedIds = _currentTemplate != null
                ? _currentTemplate.Sheets.SelectMany(s => s.EmployeeRows).Select(e => e.EmployeeID).ToHashSet()
                : _activeSheet.EmployeeRows.Select(e => e.EmployeeID).ToHashSet();

            var remaining = _availableCategoryEmployees.Where(e => !addedIds.Contains(e.EmployeeID)).ToList();

            CmbSearchCategoryEmployees.ItemsSource = remaining;
            if (remaining.Count > 0)
                CmbSearchCategoryEmployees.SelectedIndex = 0;
            else
                CmbSearchCategoryEmployees.Text = string.Empty;

            BtnAddAllCategoryEmployees.Content = $"⚡ إدراج جميع الموظفين المتبقين ({remaining.Count})";
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
                TxtSheetCriteriaSum.Text = $"مجموع درجات أعمدة الورقة: {total:0} من 100 (المتبقي: {diff:0} درجة)";
            }
            else
            {
                BannerSheetCriteriaSum.Background = new SolidColorBrush(Color.FromRgb(254, 242, 242));
                BannerSheetCriteriaSum.BorderBrush = new SolidColorBrush(Color.FromRgb(254, 202, 202));
                TxtSheetCriteriaSum.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                TxtSheetCriteriaSum.Text = $"⚠ المجموع تجاوز 100! الحالي: {total:0} من 100 (الزيادة: {Math.Abs(diff):0})";
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
                    Text = $"الدرجة: {colModel.MaxScore:0} من 100",
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
            int baseShare = 100 / count;
            int distributed = 0;

            for (int i = 0; i < count; i++)
            {
                if (i == count - 1)
                {
                    _activeSheet.CriteriaColumns[i].MaxScore = 100 - distributed;
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

        private void BtnCopyCriteriaToAllSheets_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTemplate == null || _activeSheet == null || _isReadOnlyViewMode || _currentTemplate.IsSystemTemplate)
                return;

            if (_currentTemplate.Sheets.Count <= 1)
            {
                MessageBox.Show("القالب يحتوي على ورقة واحدة فقط.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int otherSheetsCount = _currentTemplate.Sheets.Count - 1;
            var confirm = MessageBox.Show(
                $"سيتم نسخ أعمدة مبادئ التقييم ودرجاتها من الورقة الحالية ({_activeSheet.SheetName}) وتعميمها على جميع أوراق القالب الأخرى ({otherSheetsCount} ورقة).\n\nهل تريد المتابعة؟",
                "تأكيد تعميم أعمدة التقييم",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            var sourceCols = _activeSheet.CriteriaColumns.ToList();
            foreach (var sheet in _currentTemplate.Sheets)
            {
                if (ReferenceEquals(sheet, _activeSheet)) continue;

                sheet.CriteriaColumns.Clear();
                for (int i = 0; i < sourceCols.Count; i++)
                {
                    sheet.CriteriaColumns.Add(new CriteriaColumnModel
                    {
                        CriteriaTitle = sourceCols[i].CriteriaTitle,
                        MaxScore = sourceCols[i].MaxScore,
                        ColumnOrder = i + 1
                    });
                }
                sheet.NotifyTotalsChanged();
            }

            RebuildBottomSheetTabsBar();
            MessageBox.Show(
                $"✔ تم تعميم أعمدة التقييم بنجاح على جميع أوراق القالب ({_currentTemplate.Sheets.Count} ورقة).\nاضغط (💾 حفظ واعتماد القالب بالكامل) لاعتماد التغيير.",
                "تم التعميم بنجاح",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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
                Width = 200
            });

            GridSheetPreview.Columns.Add(new DataGridTextColumn
            {
                Header = "الفرع",
                Binding = new System.Windows.Data.Binding("BranchName"),
                ElementStyle = wrapStyle,
                Width = 85
            });

            GridSheetPreview.Columns.Add(new DataGridTextColumn
            {
                Header = "الوظيفة ونوعها",
                Binding = new System.Windows.Data.Binding("FullJobDisplay"),
                ElementStyle = wrapStyle,
                Width = 140
            });

            GridSheetPreview.Columns.Add(new DataGridTextColumn
            {
                Header = "الحافز",
                Binding = new System.Windows.Data.Binding("IncentiveBadgeDisplay"),
                ElementStyle = wrapStyle,
                Width = 80
            });

            for (int i = 0; i < _activeSheet.CriteriaColumns.Count; i++)
            {
                int capturedIndex = i;
                var crit = _activeSheet.CriteriaColumns[i];

                var headerTextBlock = new TextBlock
                {
                    Text = $"{crit.CriteriaTitle} ({crit.MaxScore:0})",
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
                    Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 125
                };

                GridSheetPreview.Columns.Add(col);
            }

            GridSheetPreview.ItemsSource = null;
            GridSheetPreview.ItemsSource = _activeSheet.EmployeeRows;
        }

        private async Task SaveSystemTemplateChangesImmediatelyAsync()
        {
            if (_currentTemplate != null && _currentTemplate.IsSystemTemplate && _currentTemplate.TemplateID > 0)
            {
                var (isSuccess, _, savedId) = await _service.SaveTemplateAsync(_currentTemplate, SelectedTemplateYear, SelectedTemplateMonth);
                if (isSuccess && savedId > 0)
                {
                    _currentTemplate.TemplateID = savedId;
                    _currentTemplate.VersionNumber = 1;
                    await RefreshSavedTemplatesGridAsync(syncSystemTemplates: false);
                }
            }
        }

        private async void BtnAddSelectedEmployeeToSheet_Click(object sender, RoutedEventArgs e)
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
                MessageBox.Show($"يرجى اختيار موظف مسند من قائمة ({_activeSheet.CategoryWithBranchDisplay}).", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            bool alreadyInAnySheet = _currentTemplate != null
                ? _currentTemplate.Sheets.Any(s => s.EmployeeRows.Any(x => x.EmployeeID == empToAdd.EmployeeID))
                : _activeSheet.EmployeeRows.Any(x => x.EmployeeID == empToAdd.EmployeeID);

            if (alreadyInAnySheet)
            {
                MessageBox.Show($"الموظف ({empToAdd.EvaluationName}) مضاف بالفعل داخل هذا القالب.", "موجود مسبقاً", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            empToAdd.RowOrder = _activeSheet.EmployeeRows.Count + 1;
            _activeSheet.EmployeeRows.Add(empToAdd);

            await SaveSystemTemplateChangesImmediatelyAsync();

            RefreshAvailableEmployeesCombo();
            RebuildPreviewDataGrid(true);
            RebuildBottomSheetTabsBar();
        }

        private async void BtnAddAllCategoryEmployees_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSheet == null || _isReadOnlyViewMode) return;

            var existingIds = _currentTemplate != null
                ? _currentTemplate.Sheets.SelectMany(s => s.EmployeeRows).Select(x => x.EmployeeID).ToHashSet()
                : _activeSheet.EmployeeRows.Select(x => x.EmployeeID).ToHashSet();

            var toAdd = _availableCategoryEmployees.Where(x => !existingIds.Contains(x.EmployeeID)).ToList();

            if (toAdd.Count == 0)
            {
                MessageBox.Show($"جميع الموظفين المستمرين المسندين لهذا المقيم في ({_activeSheet.CategoryWithBranchDisplay}) مضافون بالفعل في هذا القالب.", "مكتمل", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            foreach (var emp in toAdd)
            {
                emp.RowOrder = _activeSheet.EmployeeRows.Count + 1;
                _activeSheet.EmployeeRows.Add(emp);
            }

            await SaveSystemTemplateChangesImmediatelyAsync();

            RefreshAvailableEmployeesCombo();
            RebuildPreviewDataGrid(true);
            RebuildBottomSheetTabsBar();
        }

        private async void BtnRemoveEmployeeFromSheet_Click(object sender, RoutedEventArgs e)
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

            await SaveSystemTemplateChangesImmediatelyAsync();

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
                    Content = $"{statusIcon}  {sheet.SheetName} ({sheet.CategoryWithBranchDisplay}) [{sheet.EmployeeRows.Count} موظف]",
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

            var existingCatIds = _currentTemplate.Sheets
                .Where(s => s.TargetCategoryID.HasValue)
                .Select(s => s.TargetCategoryID!.Value)
                .ToHashSet();

            var dlg = new AddSheetDialog(_currentTemplate.EvaluatorID, existingCatIds, SelectedTemplateYear, SelectedTemplateMonth)
            {
                Owner = Window.GetWindow(this)
            };

            if (dlg.ShowDialog() == true)
            {
                var newSheet = TemplateSheetModel.CreateDefault(
                    dlg.SelectedCategoryName,
                    dlg.SelectedCategoryId,
                    dlg.SelectedCategoryName,
                    null,
                    "جميع الفروع (كلاهما)",
                    _currentTemplate.Sheets.Count + 1);

                var eligible = await _service.GetEligibleEmployeesForSheetAsync(
                    dlg.SelectedCategoryId,
                    null,
                    _currentTemplate.EvaluatorID,
                    SelectedTemplateYear,
                    SelectedTemplateMonth);

                var alreadyInTemplateIds = _currentTemplate.Sheets
                    .SelectMany(s => s.EmployeeRows)
                    .Select(x => x.EmployeeID)
                    .ToHashSet();

                int rowNum = 1;
                foreach (var emp in eligible)
                {
                    if (alreadyInTemplateIds.Contains(emp.EmployeeID)) continue;
                    emp.RowOrder = rowNum++;
                    newSheet.EmployeeRows.Add(emp);
                }

                _currentTemplate.Sheets.Add(newSheet);
                await ActivateSheetInDesignerAsync(newSheet);
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
            var (isSuccess, message, savedId) = await _service.SaveTemplateAsync(_currentTemplate, SelectedTemplateYear, SelectedTemplateMonth);
            BtnDesignerSaveTemplate.IsEnabled = true;

            MessageBox.Show(message, isSuccess ? "تم حفظ القالب" : "تنبيه التحقق من شروط القالب", MessageBoxButton.OK, isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (isSuccess && savedId > 0)
            {
                var reloaded = await _service.GetTemplateFullDetailsAsync(savedId, SelectedTemplateYear, SelectedTemplateMonth);
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

        private void BtnDesignerExportExcel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTemplate == null) return;
            ExportTemplateToExcelWithDialog(_currentTemplate);
        }

        private void ExportTemplateToExcelWithDialog(TemplateFullModel templateToExport)
        {
            if (templateToExport.Sheets.Count == 0)
            {
                MessageBox.Show("لا توجد أوراق في القالب لاستخراجها.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var sfd = new SaveFileDialog
            {
                Title = "استخراج قالب التقييم الشهري بصيغة Excel",
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                FileName = $"قالب_تقييم_{templateToExport.EvaluatorName}_v{templateToExport.VersionNumber}.xlsx"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    TemplateExcelExportService.GenerateTemplateExcel(templateToExport, sfd.FileName);

                    var openNow = MessageBox.Show(
                        $"تم استخراج قالب التقييم بصيغة Excel بنجاح في المسار:\n{sfd.FileName}\n\n(يمكنك تعبئة الدرجات في هذا الملف ثم رفعه مباشرة في شاشة إدخال التقييمات الشهرية).\nهل تريد فتح ملف الإكسل الآن؟",
                        "تم استخراج القالب (Excel)",
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
                    MessageBox.Show($"حدث خطأ أثناء توليد ملف الإكسل:\n{ex.Message}", "خطأ تصدير", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
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
                Title = "حفظ وطباعة استمارة التقييم الشهري بحجم A4",
                Filter = "PDF Document (*.pdf)|*.pdf",
                FileName = $"استمارة_تقييم_{templateToPrint.EvaluatorName}_v{templateToPrint.VersionNumber}.pdf"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    TemplatePdfExportService.GenerateTemplateA4Pdf(templateToPrint, sfd.FileName);

                    var openNow = MessageBox.Show(
                        $"تم توليد ملف الطباعة بحجم A4 بنجاح في المسار:\n{sfd.FileName}\n\nهل تريد فتح ملف الـ PDF الآن لمعاينته وطباعته؟",
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












