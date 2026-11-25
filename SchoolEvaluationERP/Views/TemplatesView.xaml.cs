using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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
        private bool _hasUnsavedChanges;

        public bool HasUnsavedChanges => _hasUnsavedChanges && IsCurrentDesignerEditable();

        private void MarkTemplateDirty()
        {
            if (_currentTemplate == null || _currentTemplate.IsSystemTemplate || !IsCurrentDesignerEditable())
                return;
            if (!_hasUnsavedChanges)
            {
                _hasUnsavedChanges = true;
                UpdateSaveTemplateDirtyLabel();
            }
        }

        private void UpdateSaveTemplateDirtyLabel()
        {
            if (BtnDesignerSaveTemplate == null) return;
            BtnDesignerSaveTemplate.Content = HasUnsavedChanges
                ? "💾 حفظ واعتماد القالب بالكامل *"
                : "💾 حفظ واعتماد القالب بالكامل";
        }

        public bool ConfirmDiscardUnsavedChanges(string actionLabel = "مغادرة الصفحة")
        {
            if (!HasUnsavedChanges) return true;

            var choice = MessageBox.Show(
                $"⚠ تنبيه: توجد تعديلات غير محفوظة على قالب التقييم الحالي!\n\n" +
                $"هل تريد تجاهل التعديلات غير المحفوظة والمتابعة ({actionLabel})؟\n" +
                "• اضغط (لا) للبقاء والضغط على (💾 حفظ واعتماد القالب بالكامل) أولاً.\n" +
                "• اضغط (نعم) لتجاهل التعديلات والمتابعة.",
                "تعديلات غير محفوظة على القالب",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (choice == MessageBoxResult.Yes)
            {
                _hasUnsavedChanges = false;
                UpdateSaveTemplateDirtyLabel();
                return true;
            }
            return false;
        }

        // حافظة نسخ ولصق أعمدة مبادئ التقييم (مشتركة بين الأوراق وبين قوالب المقيمين)
        private static List<(string Title, decimal MaxScore)> _copiedCriteriaBuffer = new();
        private static string _copiedCriteriaSourceLabel = string.Empty;
        private bool _isSelectingCriteriaForCopy;
        private bool _isClearingTemplateSearch;

        public static void ClearCopiedCriteriaBuffer()
        {
            _copiedCriteriaBuffer.Clear();
            _copiedCriteriaSourceLabel = string.Empty;
        }
        private readonly HashSet<int> _selectedCriteriaIndicesForCopy = new();

        // حالة تحريك وإعادة ترتيب أعمدة مبادئ التقييم (بالضغط مرتين أو السحب المباشر بالماوس)
        private const string CriteriaDragDataFormat = "SchoolEvaluationERP_CriteriaColIndex";
        private Point _criteriaDragStartPoint;
        private int? _criteriaDragCandidateIndex;
        private bool _isCriteriaDraggingNow;
        private int? _pendingDoubleClickMoveIndex;
        private int? _movingCriteriaSourceIndex;
        private int? _pendingSingleClickEditIndex;
        private readonly System.Windows.Threading.DispatcherTimer _criteriaSingleClickTimer = new()
        {
            Interval = TimeSpan.FromMilliseconds(240)
        };

        // مراجع البطاقات ورؤوس الجدول لتنفيذ أنيميشن المعاينة والانزلاق (FLIP) عند إعادة الترتيب
        private readonly Dictionary<int, Button> _criteriaCardButtons = new();
        private readonly Dictionary<int, Button> _criteriaGridHeaderButtons = new();
        private readonly Dictionary<int, Button> _criteriaSlotButtons = new();
        private int? _hoverReorderSourceIndex;
        private int? _hoverReorderTargetIndex;
        private bool _hoverReorderIsSlot;
        private bool _isExcelPasswordVisible;
        private bool _isSyncingExcelPassword;

        private int _lastSelectedPeriodYear = DateTime.Now.Year;
        private int _lastSelectedPeriodMonth = DateTime.Now.Month;
        private bool _isSelectedPeriodLocked;

        private int SelectedTemplateYear => CmbTemplatePreviewYear?.SelectedItem is int y ? y : DateTime.Now.Year;
        private int SelectedTemplateMonth => CmbTemplatePreviewMonth?.SelectedValue is int m ? m : DateTime.Now.Month;

        private bool IsAdmin => AuthService.CurrentSession?.IsAdmin == true;
        private bool CanInsert => AuthService.CurrentSession?.HasPermission("Templates", "insert") == true;
        private bool CanUpdate => AuthService.CurrentSession?.HasPermission("Templates", "update") == true;
        private bool CanDelete => AuthService.CurrentSession?.HasPermission("Templates", "delete") == true;

        public TemplatesView()
        {
            InitializeComponent();
            _criteriaSingleClickTimer.Tick += CriteriaSingleClickTimer_Tick;
            PreviewKeyDown += TemplatesView_PreviewKeyDown;

            Loaded += async (_, _) =>
            {
                ApplyPermissionsToControls();
                if (IsAdmin)
                {
                    await LoadAdminExcelPasswordAsync();
                }
                await InitializeTemplatePeriodSelectorsAsync();
                await RefreshSavedTemplatesGridAsync();
            };
        }

        private void ApplyPermissionsToControls()
        {
            BtnCreateNewTemplate.Visibility = IsAdmin ? Visibility.Visible : Visibility.Collapsed;
            BtnExportAllEvaluatorsEmployeesExcel.Visibility = IsAdmin ? Visibility.Visible : Visibility.Collapsed;
            BtnDesignerExportAllEvaluatorsExcel.Visibility = IsAdmin ? Visibility.Visible : Visibility.Collapsed;
            ColAssignedUsers.Visibility = IsAdmin ? Visibility.Visible : Visibility.Collapsed;
            PanelAdminExcelPassword.Visibility = IsAdmin ? Visibility.Visible : Visibility.Collapsed;

            BtnOpenSelectedTemplate.Visibility = CanUpdate ? Visibility.Visible : Visibility.Collapsed;
            BtnDeleteSelectedTemplate.Visibility = CanDelete ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task LoadAdminExcelPasswordAsync()
        {
            if (!IsAdmin) return;
            try
            {
                string savedPwd = await _service.GetSavedExcelProtectionPasswordRawAsync();
                _isSyncingExcelPassword = true;
                PwdExcelTemplateProtection.Password = savedPwd;
                TxtExcelTemplateProtectionVisible.Text = savedPwd;
                _isSyncingExcelPassword = false;
                UpdateExcelPasswordStatusHint(savedPwd);
            }
            catch { }
        }

        private string GetTypedExcelPassword()
        {
            return _isExcelPasswordVisible
                ? (TxtExcelTemplateProtectionVisible.Text ?? string.Empty).Trim()
                : (PwdExcelTemplateProtection.Password ?? string.Empty).Trim();
        }

        private void UpdateExcelPasswordStatusHint(string pwd, bool isUnsavedEdit = false)
        {
            bool hasPassword = !string.IsNullOrWhiteSpace(pwd);

            if (PanelAdminExcelPassword != null)
            {
                PanelAdminExcelPassword.Background = hasPassword
                    ? new SolidColorBrush(Color.FromRgb(236, 253, 245))
                    : new SolidColorBrush(Color.FromRgb(255, 251, 235));
                PanelAdminExcelPassword.BorderBrush = hasPassword
                    ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
                    : new SolidColorBrush(Color.FromRgb(245, 158, 11));
            }

            var textBrush = hasPassword
                ? new SolidColorBrush(Color.FromRgb(6, 95, 70))
                : new SolidColorBrush(Color.FromRgb(146, 64, 14));

            if (TxtExcelPasswordTitle != null)
            {
                TxtExcelPasswordTitle.Foreground = textBrush;
            }

            if (TxtExcelPasswordStatusHint != null)
            {
                TxtExcelPasswordStatusHint.Foreground = textBrush;
                TxtExcelPasswordStatusHint.Text = !hasPassword
                    ? "ℹ لم تُحدد كلمة سر مخصصة بعد (تُستخدم حماية افتراضية داخلية تلقائياً)."
                    : isUnsavedEdit
                        ? "✔ كلمة سر مدخلة (اضغط «💾 حفظ كلمة السر» لتثبيتها)."
                        : "✔ كلمة السر مفعّلة لحماية جميع قوالب الإكسل المستخرجة.";
            }
        }

        private void PwdExcelTemplateProtection_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_isSyncingExcelPassword) return;
            _isSyncingExcelPassword = true;
            TxtExcelTemplateProtectionVisible.Text = PwdExcelTemplateProtection.Password;
            _isSyncingExcelPassword = false;
            UpdateExcelPasswordStatusHint(PwdExcelTemplateProtection.Password, isUnsavedEdit: true);
        }

        private void TxtExcelTemplateProtectionVisible_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSyncingExcelPassword) return;
            _isSyncingExcelPassword = true;
            PwdExcelTemplateProtection.Password = TxtExcelTemplateProtectionVisible.Text;
            _isSyncingExcelPassword = false;
            UpdateExcelPasswordStatusHint(TxtExcelTemplateProtectionVisible.Text, isUnsavedEdit: true);
        }

        private void BtnToggleExcelPasswordVisibility_Click(object sender, RoutedEventArgs e)
        {
            if (!IsAdmin) return;
            _isExcelPasswordVisible = !_isExcelPasswordVisible;

            if (_isExcelPasswordVisible)
            {
                _isSyncingExcelPassword = true;
                TxtExcelTemplateProtectionVisible.Text = PwdExcelTemplateProtection.Password;
                _isSyncingExcelPassword = false;
                PwdExcelTemplateProtection.Visibility = Visibility.Collapsed;
                TxtExcelTemplateProtectionVisible.Visibility = Visibility.Visible;
                BtnToggleExcelPasswordVisibility.Content = "🙈 حجب";
                TxtExcelTemplateProtectionVisible.Focus();
                TxtExcelTemplateProtectionVisible.CaretIndex = TxtExcelTemplateProtectionVisible.Text.Length;
            }
            else
            {
                _isSyncingExcelPassword = true;
                PwdExcelTemplateProtection.Password = TxtExcelTemplateProtectionVisible.Text;
                _isSyncingExcelPassword = false;
                TxtExcelTemplateProtectionVisible.Visibility = Visibility.Collapsed;
                PwdExcelTemplateProtection.Visibility = Visibility.Visible;
                BtnToggleExcelPasswordVisibility.Content = "👁 إظهار";
                PwdExcelTemplateProtection.Focus();
            }
        }

        private async void BtnSaveExcelPassword_Click(object sender, RoutedEventArgs e)
        {
            if (!IsAdmin) return;

            string pwd = GetTypedExcelPassword();
            bool isSuccess;
            string message;
            BtnSaveExcelPassword.IsEnabled = false;
            try
            {
                (isSuccess, message) = await _service.SaveExcelProtectionPasswordAsync(pwd);
            }
            finally
            {
                BtnSaveExcelPassword.IsEnabled = true;
            }

            if (isSuccess)
            {
                UpdateExcelPasswordStatusHint(pwd);
            }

            MessageBox.Show(
                message,
                isSuccess ? "كلمة سر قوالب الإكسل" : "تنبيه",
                MessageBoxButton.OK,
                isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
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

            _lastSelectedPeriodYear = currentYear;
            _lastSelectedPeriodMonth = currentMonth;

            _isInitializingPeriod = false;
            return Task.CompletedTask;
        }

        private async void TemplatePeriodFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializingPeriod || !IsLoaded) return;

            if (!ConfirmDiscardUnsavedChanges("تغيير السنة أو الشهر"))
            {
                _isInitializingPeriod = true;
                CmbTemplatePreviewYear.SelectedItem = _lastSelectedPeriodYear;
                CmbTemplatePreviewMonth.SelectedValue = _lastSelectedPeriodMonth;
                _isInitializingPeriod = false;
                return;
            }

            _lastSelectedPeriodYear = SelectedTemplateYear;
            _lastSelectedPeriodMonth = SelectedTemplateMonth;

            await RefreshSavedTemplatesGridAsync(syncSystemTemplates: false);

            if (_currentTemplate != null && _currentTemplate.EvaluatorID > 0)
            {
                int resolvedId = await _service.ResolveTemplateIdForEvaluatorInMonthAsync(
                    _currentTemplate.EvaluatorID,
                    SelectedTemplateYear,
                    SelectedTemplateMonth);

                if (resolvedId > 0)
                {
                    await LoadTemplateIntoDesignerAsync(resolvedId, openInReadOnlyMode: true, switchToDesignerTab: false);
                }
            }
        }

        private async Task RefreshSavedTemplatesGridAsync(bool syncSystemTemplates = true)
        {
            if (!IsLoaded) return;
            int requestVersion = ++_searchRequestVersion;
            string search = TxtSearchTemplateEvaluator.Text.Trim();
            int y = SelectedTemplateYear;
            int m = SelectedTemplateMonth;

            var periodStatus = await _service.GetTemplatesPeriodStatusAsync(y, m);
            var list = await _service.GetCurrentTemplatesAsync(search, syncSystemTemplates, y, m);
            if (requestVersion != _searchRequestVersion)
                return;

            _isSelectedPeriodLocked = periodStatus.IsMonthLocked;
            TxtTemplatesPeriodStatus.Text = periodStatus.StatusBadgeText;

            if (periodStatus.IsMonthLocked)
            {
                BadgeTemplatesPeriodStatus.Background = new SolidColorBrush(Color.FromRgb(254, 242, 242));
                BadgeTemplatesPeriodStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(254, 202, 202));
                TxtTemplatesPeriodStatus.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
            }
            else if (periodStatus.StatusBadgeText.StartsWith("✔"))
            {
                BadgeTemplatesPeriodStatus.Background = new SolidColorBrush(Color.FromRgb(236, 253, 245));
                BadgeTemplatesPeriodStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(167, 243, 208));
                TxtTemplatesPeriodStatus.Foreground = new SolidColorBrush(Color.FromRgb(4, 120, 87));
            }
            else
            {
                BadgeTemplatesPeriodStatus.Background = new SolidColorBrush(Color.FromRgb(239, 246, 255));
                BadgeTemplatesPeriodStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(191, 219, 254));
                TxtTemplatesPeriodStatus.Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175));
            }

            GridSavedTemplates.ItemsSource = list;
            string mName = IncentiveCalculatorService.GetArabicMonthName(m);
            TxtTemplatesCountBadge.Text = $"إجمالي القوالب الفعالة ({mName} {y}): {list.Count}";
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
            if (!_isReadOnlyViewMode && (MainTemplatesTabControl.SelectedItem == TabTemplateDesigner || HasUnsavedChanges))
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
                if (!ConfirmDiscardUnsavedChanges("فتح قالب آخر")) return;
                await LoadTemplateIntoDesignerAsync(selected.TemplateID, openInReadOnlyMode: true, switchToDesignerTab: true);
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

            if (_isSelectedPeriodLocked || selected.IsPeriodMonthLocked)
            {
                string lockName = $"{IncentiveCalculatorService.GetArabicMonthName(SelectedTemplateMonth)} {SelectedTemplateYear} م";
                MessageBox.Show(
                    $"شهر ({lockName}) معتمد ومغلق نهائياً ولا يمكن تعديل قوالبه.\nسيتم فتح القالب بوضع القراءة فقط.",
                    "شهر مغلق — قراءة فقط",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                if (!ConfirmDiscardUnsavedChanges("فتح قالب آخر")) return;
                await LoadTemplateIntoDesignerAsync(selected.TemplateID, openInReadOnlyMode: true, switchToDesignerTab: true);
                return;
            }

            if (!ConfirmDiscardUnsavedChanges("فتح قالب آخر للتعديل")) return;
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

            _hasUnsavedChanges = false;
            _currentTemplate = fullModel;
            if (TxtSearchTemplateEmployees != null) TxtSearchTemplateEmployees.Text = string.Empty;
            _isReadOnlyViewMode = openInReadOnlyMode;
            UpdateSaveTemplateDirtyLabel();

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
            if (!ConfirmDiscardUnsavedChanges("إنشاء قالب جديد")) return;
            if (!IsAdmin)
            {
                MessageBox.Show("عذراً، إنشاء قوالب التقييم الجديدة مخصص لمدير النظام فقط.", "تنبيه صلاحيات", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                if (evalDlg.AssignedUsersModified)
                {
                    await _service.SaveTemplateAssignedUsersAsync(chosenEvaluator.EvaluatorID, evalDlg.SelectedAssignedUserIds);
                    await RefreshSavedTemplatesGridAsync(syncSystemTemplates: false);
                }

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

            if (evalDlg.SelectedAssignedUserIds.Count > 0)
            {
                await _service.SaveTemplateAssignedUsersAsync(chosenEvaluator.EvaluatorID, evalDlg.SelectedAssignedUserIds);
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


        private async void BtnAssignTemplateUsers_Click(object sender, RoutedEventArgs e)
        {
            if (!IsAdmin)
            {
                MessageBox.Show("عذراً، تحديد المستخدمين المسؤولين عن القوالب مخصص لمدير النظام فقط.", "تنبيه صلاحيات", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (sender is Button btn && btn.DataContext is TemplateSummaryDto rowDto)
            {
                var dlg = new AssignTemplateUsersDialog(rowDto.EvaluatorID, rowDto.EvaluatorWithGroupDisplay)
                {
                    Owner = Window.GetWindow(this)
                };

                if (dlg.ShowDialog() == true)
                {
                    await RefreshSavedTemplatesGridAsync(syncSystemTemplates: false);
                }
            }
        }

        private async void BtnExportAllEvaluatorsEmployeesExcel_Click(object sender, RoutedEventArgs e)
        {
            if (!IsAdmin)
            {
                MessageBox.Show("عذراً، استخراج كشف موظفي جميع المقيمين مخصص لمدير النظام فقط.", "تنبيه صلاحيات", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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

            await PromptAndExportTemplateExcelAsync(
                evaluatorId: selected.EvaluatorID,
                evaluatorName: selected.EvaluatorName,
                fallbackTemplateId: selected.TemplateID,
                preloadedSelectedMonthModel: null);
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
            _hasUnsavedChanges = false;
            _currentTemplate = null;
            _activeSheet = null;
            _isReadOnlyViewMode = true;
            UpdateSaveTemplateDirtyLabel();
            _isSelectingCriteriaForCopy = false;
            _selectedCriteriaIndicesForCopy.Clear();
            _movingCriteriaSourceIndex = null;
            _pendingDoubleClickMoveIndex = null;
            _criteriaDragCandidateIndex = null;
            _criteriaSingleClickTimer.Stop();
            _pendingSingleClickEditIndex = null;
            BannerCriteriaCopyMode.Visibility = Visibility.Collapsed;
            BannerCriteriaMoveMode.Visibility = Visibility.Collapsed;
            TxtDesignerHeaderTitle.Text = "لم يتم فتح قالب بعد — اختر قالباً من القائمة أو أنشئ قالباً جديداً";
            BadgeSheetCategory.Visibility = Visibility.Collapsed;
            BadgeReadOnlyMode.Visibility = Visibility.Collapsed;
            if (BadgeDesignerPeriodStatus != null) BadgeDesignerPeriodStatus.Visibility = Visibility.Collapsed;
            PanelCriteriaDesigner.IsEnabled = false;
            PanelEmployeeAdder.IsEnabled = false;
            if (TxtSearchTemplateEmployees != null) TxtSearchTemplateEmployees.Text = string.Empty;
            if (BtnClearTemplateEmployeeSearch != null) BtnClearTemplateEmployeeSearch.Visibility = Visibility.Collapsed;
            if (TxtTemplateSearchMatchCount != null) TxtTemplateSearchMatchCount.Visibility = Visibility.Collapsed;
            PanelSheetManageButtons.IsEnabled = false;
            BtnDesignerSaveTemplate.IsEnabled = false;
            BtnDesignerExportExcel.IsEnabled = false;
            BtnSwitchToEditMode.Visibility = Visibility.Collapsed;
            StackInteractiveColumns.Children.Clear();
            StackSheetTabs.Children.Clear();
            GridSheetPreview.Columns.Clear();
            GridSheetPreview.ItemsSource = null;
        }

        private async Task ActivateSheetInDesignerAsync(TemplateSheetModel sheet)
        {
            if (_currentTemplate == null) return;
            _activeSheet = sheet;

            _isSelectingCriteriaForCopy = false;
            _selectedCriteriaIndicesForCopy.Clear();
            _movingCriteriaSourceIndex = null;
            _pendingDoubleClickMoveIndex = null;
            _criteriaDragCandidateIndex = null;
            _criteriaSingleClickTimer.Stop();
            _pendingSingleClickEditIndex = null;
            BannerCriteriaCopyMode.Visibility = Visibility.Collapsed;
            BannerCriteriaMoveMode.Visibility = Visibility.Collapsed;

            bool isMonthLocked = _currentTemplate.IsMonthLocked;
            if (isMonthLocked)
                _isReadOnlyViewMode = true;

            bool hasRolePermission = (_currentTemplate.TemplateID == 0) ? CanInsert : CanUpdate;
            bool canEditNow = !_isReadOnlyViewMode && hasRolePermission && !isMonthLocked;

            PanelCriteriaDesigner.IsEnabled = true;
            UpdateCriteriaCopyPasteVisualState(canEditNow);
            PanelEmployeeAdder.IsEnabled = true;
            CmbSearchCategoryEmployees.IsEnabled = canEditNow;
            BtnAddSelectedEmployee.IsEnabled = canEditNow;
            BtnAddAllCategoryEmployees.IsEnabled = canEditNow;
            BtnRemoveEmployeeFromSheet.IsEnabled = canEditNow;
            TxtSearchTemplateEmployees.IsEnabled = true;
            PanelSheetManageButtons.IsEnabled = canEditNow;
            BtnDesignerSaveTemplate.IsEnabled = canEditNow;
            BtnDesignerSaveTemplate.Visibility = canEditNow ? Visibility.Visible : Visibility.Collapsed;
            BtnDesignerExportExcel.IsEnabled = true;
            BtnSwitchToEditMode.Visibility = (!canEditNow && hasRolePermission && !isMonthLocked) ? Visibility.Visible : Visibility.Collapsed;
            TxtReadOnlyBadge.Text = isMonthLocked
                ? "🔒 شهر معتمد ومغلق نهائياً — القالب للقراءة فقط"
                : (hasRolePermission
                    ? "🔒 وضع القراءة فقط (اضغط على «✏ الانتقال إلى وضع التعديل» لتعديل القالب)"
                    : "🔒 وضع القراءة فقط (لا تملك صلاحية تعديل القوالب)");

            if (BadgeDesignerPeriodStatus != null && TxtDesignerPeriodStatus != null)
            {
                string statusText = _currentTemplate.MonthlyTemplateStatusText;
                if (!string.IsNullOrWhiteSpace(statusText))
                {
                    TxtDesignerPeriodStatus.Text = statusText;
                    BadgeDesignerPeriodStatus.Visibility = Visibility.Visible;
                    if (isMonthLocked)
                    {
                        BadgeDesignerPeriodStatus.Background = new SolidColorBrush(Color.FromRgb(254, 242, 242));
                        BadgeDesignerPeriodStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(254, 202, 202));
                        TxtDesignerPeriodStatus.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                    }
                    else if (_currentTemplate.IsOwnMonthVersion)
                    {
                        BadgeDesignerPeriodStatus.Background = new SolidColorBrush(Color.FromRgb(236, 253, 245));
                        BadgeDesignerPeriodStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(167, 243, 208));
                        TxtDesignerPeriodStatus.Foreground = new SolidColorBrush(Color.FromRgb(4, 120, 87));
                    }
                    else
                    {
                        BadgeDesignerPeriodStatus.Background = new SolidColorBrush(Color.FromRgb(239, 246, 255));
                        BadgeDesignerPeriodStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(191, 219, 254));
                        TxtDesignerPeriodStatus.Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175));
                    }
                }
                else
                {
                    BadgeDesignerPeriodStatus.Visibility = Visibility.Collapsed;
                }
            }

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
            if (!string.IsNullOrWhiteSpace(TxtSearchTemplateEmployees?.Text))
            {
                ApplyDesignerEmployeeSearchFilter();
            }
            else
            {
                RebuildPreviewDataGrid(canEditNow);
                RebuildBottomSheetTabsBar();
            }
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
            _criteriaCardButtons.Clear();
            _criteriaSlotButtons.Clear();
            _hoverReorderSourceIndex = null;
            _hoverReorderTargetIndex = null;
            _hoverReorderIsSlot = false;
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

            bool showPlusButtons = canEditNow && !_isSelectingCriteriaForCopy && (_currentTemplate == null || !_currentTemplate.IsSystemTemplate);

            if (showPlusButtons)
            {
                StackInteractiveColumns.Children.Add(CreateInsertColumnPlusButton(0));
            }

            for (int i = 0; i < _activeSheet.CriteriaColumns.Count; i++)
            {
                int colIndex = i;
                var colModel = _activeSheet.CriteriaColumns[i];
                bool isSelectedForCopy = _isSelectingCriteriaForCopy && _selectedCriteriaIndicesForCopy.Contains(colIndex);
                bool isMovingSource = !_isSelectingCriteriaForCopy && _movingCriteriaSourceIndex == colIndex;
                bool isAnyMoving = !_isSelectingCriteriaForCopy && _movingCriteriaSourceIndex.HasValue;

                Brush cardBg;
                Brush cardBorder;
                Thickness borderThick;
                string titlePrefix;
                string tooltipText;

                if (_isSelectingCriteriaForCopy)
                {
                    cardBg = isSelectedForCopy
                        ? new SolidColorBrush(Color.FromRgb(220, 252, 231))
                        : new SolidColorBrush(Color.FromRgb(248, 250, 252));
                    cardBorder = isSelectedForCopy
                        ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
                        : new SolidColorBrush(Color.FromRgb(148, 163, 184));
                    borderThick = isSelectedForCopy ? new Thickness(2.2) : new Thickness(1.5);
                    titlePrefix = isSelectedForCopy ? "☑ " : "☐ ";
                    tooltipText = isSelectedForCopy ? "محدد للنسخ — اضغط لإلغاء التحديد" : "اضغط لتحديد هذا العمود للنسخ";
                }
                else if (isMovingSource)
                {
                    cardBg = new SolidColorBrush(Color.FromRgb(254, 243, 199));
                    cardBorder = new SolidColorBrush(Color.FromRgb(217, 119, 6));
                    borderThick = new Thickness(2.4);
                    titlePrefix = "✋ ";
                    tooltipText = "هذا العمود جاهز للنقل — اضغط على أي عمود آخر أو سهم (⬇) لنقله، أو اضغط عليه مجدداً لإلغاء النقل";
                }
                else
                {
                    cardBg = colModel.MaxScore > 0
                        ? new SolidColorBrush(Color.FromRgb(239, 246, 255))
                        : new SolidColorBrush(Color.FromRgb(248, 250, 252));
                    cardBorder = isAnyMoving
                        ? new SolidColorBrush(Color.FromRgb(245, 158, 11))
                        : (colModel.MaxScore > 0
                            ? new SolidColorBrush(Color.FromRgb(59, 130, 246))
                            : new SolidColorBrush(Color.FromRgb(148, 163, 184)));
                    borderThick = new Thickness(1.5);
                    titlePrefix = canEditNow ? "✎ " : string.Empty;
                    tooltipText = isAnyMoving
                        ? "اضغط هنا لنقل العمود المحدد إلى هذا المكان"
                        : (canEditNow
                            ? "نقرة واحدة للتعديل — أو اضغط مرتين / اسحب بالماوس لتحريك العمود وتغيير ترتيبه"
                            : "وضع القراءة فقط (يمكنك الضغط على نسخ أعمدة لنسخها)");
                }

                var colButton = new Button
                {
                    Cursor = (_isSelectingCriteriaForCopy || canEditNow) ? Cursors.Hand : Cursors.Arrow,
                    Margin = new Thickness(4, 2, 4, 2),
                    Padding = new Thickness(12, 6, 12, 6),
                    MinWidth = 135,
                    MaxWidth = 240,
                    MaxHeight = 104,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = cardBg,
                    BorderBrush = cardBorder,
                    BorderThickness = borderThick,
                    ToolTip = $"{colModel.CriteriaTitle} ({colModel.MaxScore:0} درجة)\n{tooltipText}"
                };

                var stack = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                stack.Children.Add(new TextBlock
                {
                    Text = $"{titlePrefix}{colModel.CriteriaTitle}",
                    FontWeight = FontWeights.Bold,
                    FontSize = 12,
                    LineHeight = 17,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    MaxHeight = 54,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Center,
                    MaxWidth = 215,
                    Foreground = isSelectedForCopy
                        ? new SolidColorBrush(Color.FromRgb(6, 95, 70))
                        : new SolidColorBrush(Color.FromRgb(15, 23, 42)),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                stack.Children.Add(new TextBlock
                {
                    Text = $"الدرجة: {colModel.MaxScore:0} من 100",
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = isSelectedForCopy
                        ? new SolidColorBrush(Color.FromRgb(4, 120, 87))
                        : (colModel.MaxScore > 0
                            ? new SolidColorBrush(Color.FromRgb(37, 99, 235))
                            : new SolidColorBrush(Color.FromRgb(180, 83, 9))),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 3, 0, 0)
                });

                colButton.Content = stack;
                EnsureAnimTransforms(colButton);
                _criteriaCardButtons[colIndex] = colButton;

                var cardHost = new Grid { Background = Brushes.Transparent };
                cardHost.Children.Add(colButton);

                if (isMovingSource)
                {
                    colButton.Effect = new DropShadowEffect
                    {
                        Color = Color.FromRgb(245, 158, 11),
                        BlurRadius = 14,
                        ShadowDepth = 2,
                        Opacity = 0.45
                    };
                    AnimateElementTransform(colButton, 0.0, -3.0, 1.045, 220);
                    Panel.SetZIndex(cardHost, 20);
                }

                if (_isSelectingCriteriaForCopy)
                {
                    colButton.Click += (_, _) => ToggleCriteriaSelectionForCopy(colIndex);
                }
                else if (canEditNow)
                {
                    if (_currentTemplate == null || !_currentTemplate.IsSystemTemplate)
                    {
                        AttachCriteriaReorderHandlers(colButton, colIndex, isTopCard: true, dropHost: cardHost);
                    }
                    else
                    {
                        colButton.Click += (_, _) => OpenEditCriteriaPopup(colIndex);
                    }
                }

                StackInteractiveColumns.Children.Add(cardHost);

                if (showPlusButtons)
                {
                    StackInteractiveColumns.Children.Add(CreateInsertColumnPlusButton(colIndex + 1));
                }
            }

            UpdateCriteriaCopyPasteVisualState(canEditNow);
        }

        private bool IsCurrentDesignerEditable()
        {
            if (_currentTemplate == null || _currentTemplate.IsMonthLocked) return false;
            bool hasRolePermission = (_currentTemplate.TemplateID == 0) ? CanInsert : CanUpdate;
            return !_isReadOnlyViewMode && hasRolePermission;
        }

        private void UpdateCriteriaCopyPasteVisualState(bool canEditNow)
        {
            bool isSysTmpl = _currentTemplate?.IsSystemTemplate == true;
            bool hasColumns = _activeSheet != null && _activeSheet.CriteriaColumns.Count > 0;
            bool hasCopied = _copiedCriteriaBuffer.Count > 0;

            BtnDistributeCriteriaEqually.IsEnabled = canEditNow && !isSysTmpl && !_isSelectingCriteriaForCopy;
            BtnCopyCriteriaToAllSheets.IsEnabled = canEditNow && !isSysTmpl && !_isSelectingCriteriaForCopy;

            BtnCopyCriteriaColumns.IsEnabled = !isSysTmpl && hasColumns;
            BtnCopyCriteriaColumns.Content = _isSelectingCriteriaForCopy ? "✖ إنهاء التحديد" : "📋 نسخ أعمدة";

            BtnPasteCriteriaColumns.IsEnabled = canEditNow && !isSysTmpl && hasCopied && !_isSelectingCriteriaForCopy;
            BtnPasteCriteriaColumns.Content = hasCopied
                ? $"📥 لصق الأعمدة ({_copiedCriteriaBuffer.Count})"
                : "📥 لصق الأعمدة";
            BtnPasteCriteriaColumns.ToolTip = hasCopied
                ? $"لصق ({_copiedCriteriaBuffer.Count}) عمود منسوخ من ({_copiedCriteriaSourceLabel}) في الورقة الحالية (إضافة أو استبدال)"
                : "انسخ أعمدة من أي ورقة أو قالب أولاً لتفعيل زر اللصق";

            BannerCriteriaCopyMode.Visibility = _isSelectingCriteriaForCopy ? Visibility.Visible : Visibility.Collapsed;
            int selectedCount = _selectedCriteriaIndicesForCopy.Count;
            BtnConfirmCopyCriteria.Content = $"✔ نسخ المحدد ({selectedCount})";
            BtnConfirmCopyCriteria.IsEnabled = selectedCount > 0;

            bool showMoveBanner = canEditNow && !isSysTmpl && !_isSelectingCriteriaForCopy
                && _movingCriteriaSourceIndex.HasValue
                && _activeSheet != null
                && _movingCriteriaSourceIndex.Value >= 0
                && _movingCriteriaSourceIndex.Value < _activeSheet.CriteriaColumns.Count;

            BannerCriteriaMoveMode.Visibility = showMoveBanner ? Visibility.Visible : Visibility.Collapsed;
            if (showMoveBanner)
            {
                string movingTitle = _activeSheet!.CriteriaColumns[_movingCriteriaSourceIndex!.Value].CriteriaTitle;
                TxtCriteriaMoveModeHint.Text = $"✋ جاري تحريك العمود ({movingTitle}): حرّك الماوس واضغط على أي عمود آخر أو على سهم (⬇) لنقله إلى ذلك المكان (أو اضغط Esc للإلغاء).";
            }
        }

        private void ToggleCriteriaSelectionForCopy(int colIndex)
        {
            if (!_isSelectingCriteriaForCopy || _activeSheet == null) return;
            if (colIndex < 0 || colIndex >= _activeSheet.CriteriaColumns.Count) return;

            if (!_selectedCriteriaIndicesForCopy.Add(colIndex))
            {
                _selectedCriteriaIndicesForCopy.Remove(colIndex);
            }

            bool canEditNow = IsCurrentDesignerEditable();
            RebuildInteractiveCriteriaStrip(canEditNow);
            RebuildPreviewDataGrid(canEditNow);
        }

        private void BtnCopyCriteriaColumns_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTemplate == null || _activeSheet == null || _currentTemplate.IsSystemTemplate)
                return;

            if (_activeSheet.CriteriaColumns.Count == 0)
            {
                MessageBox.Show("لا توجد أعمدة تقييم في الورقة الحالية لنسخها.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _movingCriteriaSourceIndex = null;
            _criteriaSingleClickTimer.Stop();
            _pendingSingleClickEditIndex = null;
            _isSelectingCriteriaForCopy = !_isSelectingCriteriaForCopy;
            _selectedCriteriaIndicesForCopy.Clear();

            bool canEditNow = IsCurrentDesignerEditable();
            RebuildInteractiveCriteriaStrip(canEditNow);
            RebuildPreviewDataGrid(canEditNow);

            if (_isSelectingCriteriaForCopy)
            {
                MessageBox.Show(
                    "تم تفعيل وضع تحديد أعمدة التقييم للنسخ:\n\n" +
                    "1. انقر على كل عمود تريد نسخه (سيظهر باللون الأخضر وعلامة ☑).\n" +
                    "2. يمكنك إلغاء تحديد أي عمود بالنقر عليه مرة أخرى.\n" +
                    "3. بعد الانتهاء اضغط على زر (✔ نسخ المحدد) في الشريط الأخضر، ثم انتقل إلى أي ورقة أو قالب آخر واضغط (📥 لصق الأعمدة).",
                    "تحديد أعمدة التقييم للنسخ",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        private void BtnSelectAllCriteriaForCopy_Click(object sender, RoutedEventArgs e)
        {
            if (!_isSelectingCriteriaForCopy || _activeSheet == null) return;
            _selectedCriteriaIndicesForCopy.Clear();
            for (int i = 0; i < _activeSheet.CriteriaColumns.Count; i++)
            {
                _selectedCriteriaIndicesForCopy.Add(i);
            }

            bool canEditNow = IsCurrentDesignerEditable();
            RebuildInteractiveCriteriaStrip(canEditNow);
            RebuildPreviewDataGrid(canEditNow);
        }

        private void BtnClearCriteriaSelectionForCopy_Click(object sender, RoutedEventArgs e)
        {
            if (!_isSelectingCriteriaForCopy) return;
            _selectedCriteriaIndicesForCopy.Clear();

            bool canEditNow = IsCurrentDesignerEditable();
            RebuildInteractiveCriteriaStrip(canEditNow);
            RebuildPreviewDataGrid(canEditNow);
        }

        private void BtnCancelCopyCriteria_Click(object sender, RoutedEventArgs e)
        {
            _isSelectingCriteriaForCopy = false;
            _selectedCriteriaIndicesForCopy.Clear();

            bool canEditNow = IsCurrentDesignerEditable();
            RebuildInteractiveCriteriaStrip(canEditNow);
            RebuildPreviewDataGrid(canEditNow);
        }

        private void BtnConfirmCopyCriteria_Click(object sender, RoutedEventArgs e)
        {
            if (_activeSheet == null || _selectedCriteriaIndicesForCopy.Count == 0)
            {
                MessageBox.Show("يرجى تحديد عمود واحد على الأقل لنسخه.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var orderedSelected = _selectedCriteriaIndicesForCopy
                .Where(idx => idx >= 0 && idx < _activeSheet.CriteriaColumns.Count)
                .OrderBy(idx => idx)
                .Select(idx => _activeSheet.CriteriaColumns[idx])
                .Select(c => (Title: c.CriteriaTitle, MaxScore: c.MaxScore))
                .ToList();

            if (orderedSelected.Count == 0) return;

            _copiedCriteriaBuffer = orderedSelected;
            _copiedCriteriaSourceLabel = _currentTemplate != null
                ? $"{_currentTemplate.EvaluatorName} — {_activeSheet.SheetName}"
                : _activeSheet.SheetName;

            decimal copiedSum = orderedSelected.Sum(x => x.MaxScore);

            _isSelectingCriteriaForCopy = false;
            _selectedCriteriaIndicesForCopy.Clear();

            bool canEditNow = IsCurrentDesignerEditable();
            RebuildInteractiveCriteriaStrip(canEditNow);
            RebuildPreviewDataGrid(canEditNow);

            MessageBox.Show(
                $"✔ تم نسخ ({orderedSelected.Count}) عمود تقييم بنجاح (مجموع درجاتها: {copiedSum:0} درجة).\n\n" +
                "يمكنك الآن الانتقال إلى أي ورقة أخرى في هذا القالب أو فتح قالب مقيم آخر في وضع التعديل والضغط على (📥 لصق الأعمدة).",
                "تم نسخ الأعمدة بنجاح",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void BtnPasteCriteriaColumns_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTemplate == null || _activeSheet == null || !IsCurrentDesignerEditable() || _currentTemplate.IsSystemTemplate)
                return;

            if (_copiedCriteriaBuffer.Count == 0)
            {
                MessageBox.Show("لا توجد أعمدة منسوخة حالياً. اضغط (📋 نسخ أعمدة) وحدد الأعمدة المطلوبة أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            decimal copiedSum = _copiedCriteriaBuffer.Sum(x => x.MaxScore);
            var choice = MessageBox.Show(
                $"الأعمدة المنسوخة: ({_copiedCriteriaBuffer.Count}) عمود من ({_copiedCriteriaSourceLabel}) — مجموع درجاتها: ({copiedSum:0} درجة).\n\n" +
                $"كيف تريد لصقها في الورقة الحالية ({_activeSheet.SheetName})؟\n" +
                "• اضغط (نعم) للإضافة (دمج الأعمدة المنسوخة بجانب الأعمدة الموجودة حالياً).\n" +
                "• اضغط (لا) للاستبدال (مسح أعمدة الورقة الحالية ووضع الأعمدة المنسوخة مكانها).\n" +
                "• اضغط (إلغاء) للتراجع.",
                "خيار لصق أعمدة التقييم",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (choice == MessageBoxResult.Cancel)
                return;

            if (choice == MessageBoxResult.No)
            {
                _activeSheet.CriteriaColumns.Clear();
            }

            foreach (var item in _copiedCriteriaBuffer)
            {
                _activeSheet.CriteriaColumns.Add(new CriteriaColumnModel
                {
                    CriteriaTitle = item.Title,
                    MaxScore = item.MaxScore,
                    ColumnOrder = _activeSheet.CriteriaColumns.Count + 1
                });
            }

            _activeSheet.ReindexOrders();
            _activeSheet.NotifyTotalsChanged();
            MarkTemplateDirty();

            RebuildInteractiveCriteriaStrip(true);
            RebuildPreviewDataGrid(true);
            RebuildBottomSheetTabsBar();
        }

        private void ScrollCriteriaColumns_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is ScrollViewer sv && sv.ScrollableWidth > 0)
            {
                double newOffset = Math.Clamp(sv.HorizontalOffset - (e.Delta * 0.5), 0, sv.ScrollableWidth);
                sv.ScrollToHorizontalOffset(newOffset);
                e.Handled = true;
            }
        }

        private void CriteriaSingleClickTimer_Tick(object? sender, EventArgs e)
        {
            _criteriaSingleClickTimer.Stop();
            int? idx = _pendingSingleClickEditIndex;
            _pendingSingleClickEditIndex = null;

            if (idx.HasValue
                && !_isCriteriaDraggingNow
                && !_movingCriteriaSourceIndex.HasValue
                && !_isSelectingCriteriaForCopy)
            {
                OpenEditCriteriaPopup(idx.Value);
            }
        }

        private void TemplatesView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _movingCriteriaSourceIndex.HasValue)
            {
                CancelCriteriaMoveMode();
                e.Handled = true;
            }
        }

        private void StartCriteriaMoveMode(int colIndex)
        {
            if (_activeSheet == null || !IsCurrentDesignerEditable() || _isSelectingCriteriaForCopy) return;
            if (_currentTemplate?.IsSystemTemplate == true) return;
            if (colIndex < 0 || colIndex >= _activeSheet.CriteriaColumns.Count) return;
            if (_activeSheet.CriteriaColumns.Count <= 1) return;

            _criteriaSingleClickTimer.Stop();
            _pendingSingleClickEditIndex = null;
            _movingCriteriaSourceIndex = colIndex;

            bool canEditNow = IsCurrentDesignerEditable();
            RebuildInteractiveCriteriaStrip(canEditNow);
            RebuildPreviewDataGrid(canEditNow);
        }

        private void CancelCriteriaMoveMode()
        {
            _criteriaSingleClickTimer.Stop();
            _pendingSingleClickEditIndex = null;
            _pendingDoubleClickMoveIndex = null;
            _criteriaDragCandidateIndex = null;

            if (_movingCriteriaSourceIndex.HasValue)
            {
                _movingCriteriaSourceIndex = null;
                bool canEditNow = IsCurrentDesignerEditable();
                RebuildInteractiveCriteriaStrip(canEditNow);
                RebuildPreviewDataGrid(canEditNow);
            }
            else if (BannerCriteriaMoveMode != null)
            {
                BannerCriteriaMoveMode.Visibility = Visibility.Collapsed;
            }
        }

        private void BtnCancelMoveCriteria_Click(object sender, RoutedEventArgs e)
        {
            CancelCriteriaMoveMode();
        }

        private static (TranslateTransform Translate, ScaleTransform Scale) EnsureAnimTransforms(UIElement el)
        {
            if (el.RenderTransform is TransformGroup tg && tg.Children.Count >= 2 &&
                tg.Children[0] is ScaleTransform sc && tg.Children[1] is TranslateTransform tr)
            {
                return (tr, sc);
            }

            var newScale = new ScaleTransform(1.0, 1.0);
            var newTranslate = new TranslateTransform(0.0, 0.0);
            var group = new TransformGroup();
            group.Children.Add(newScale);
            group.Children.Add(newTranslate);
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = group;
            return (newTranslate, newScale);
        }

        private static void AnimateElementTransform(
            UIElement el,
            double targetX,
            double targetY,
            double targetScale,
            int durationMs = 190)
        {
            var (tr, sc) = EnsureAnimTransforms(el);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(durationMs);

            tr.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(targetX, dur) { EasingFunction = ease });
            tr.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(targetY, dur) { EasingFunction = ease });
            sc.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(targetScale, dur) { EasingFunction = ease });
            sc.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(targetScale, dur) { EasingFunction = ease });
        }

        private void ApplyHoverReorderPreview(int sourceIndex, int? targetIndex, bool isInsertSlot)
        {
            if (_activeSheet == null || _isSelectingCriteriaForCopy) return;
            int count = _activeSheet.CriteriaColumns.Count;
            if (sourceIndex < 0 || sourceIndex >= count) return;

            if (_hoverReorderSourceIndex == sourceIndex
                && _hoverReorderTargetIndex == targetIndex
                && _hoverReorderIsSlot == isInsertSlot)
            {
                return;
            }

            _hoverReorderSourceIndex = sourceIndex;
            _hoverReorderTargetIndex = targetIndex;
            _hoverReorderIsSlot = isInsertSlot;

            int finalIndex = sourceIndex;
            bool hasValidTarget = false;
            if (targetIndex.HasValue)
            {
                if (isInsertSlot && targetIndex.Value >= 0 && targetIndex.Value <= count)
                {
                    finalIndex = sourceIndex < targetIndex.Value ? targetIndex.Value - 1 : targetIndex.Value;
                    hasValidTarget = finalIndex != sourceIndex && finalIndex >= 0 && finalIndex < count;
                }
                else if (!isInsertSlot && targetIndex.Value >= 0 && targetIndex.Value < count && targetIndex.Value != sourceIndex)
                {
                    finalIndex = targetIndex.Value;
                    hasValidTarget = true;
                }
            }

            foreach (var kv in _criteriaCardButtons)
            {
                int idx = kv.Key;
                Button btn = kv.Value;
                bool isSource = idx == sourceIndex;
                bool isDirectTarget = hasValidTarget && !isInsertSlot && idx == targetIndex;

                double shiftX = 0.0;
                double shiftY = 0.0;
                double scale = isSource ? 1.04 : 1.0;

                if (hasValidTarget)
                {
                    if (isSource)
                    {
                        shiftX = finalIndex > sourceIndex ? 14.0 : -14.0;
                        shiftY = -3.0;
                        scale = 1.04;
                    }
                    else if (sourceIndex < finalIndex && idx > sourceIndex && idx <= finalIndex)
                    {
                        shiftX = -22.0;
                        scale = isDirectTarget ? 1.04 : 0.99;
                    }
                    else if (sourceIndex > finalIndex && idx >= finalIndex && idx < sourceIndex)
                    {
                        shiftX = 22.0;
                        scale = isDirectTarget ? 1.04 : 0.99;
                    }
                }

                AnimateElementTransform(btn, shiftX, shiftY, scale, 180);

                if (btn.Parent is UIElement hostEl)
                {
                    Panel.SetZIndex(hostEl, isSource ? 20 : (isDirectTarget ? 15 : 1));
                }

                if (!isSource)
                {
                    var colModel = idx >= 0 && idx < count ? _activeSheet.CriteriaColumns[idx] : null;
                    if (isDirectTarget)
                    {
                        btn.Background = new SolidColorBrush(Color.FromRgb(254, 243, 199));
                        btn.BorderBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                        btn.BorderThickness = new Thickness(2.4);
                        btn.Effect = new DropShadowEffect
                        {
                            Color = Color.FromRgb(245, 158, 11),
                            BlurRadius = 12,
                            ShadowDepth = 1,
                            Opacity = 0.40
                        };
                    }
                    else
                    {
                        bool inWave = hasValidTarget && ((sourceIndex < finalIndex && idx > sourceIndex && idx <= finalIndex) ||
                                                         (sourceIndex > finalIndex && idx >= finalIndex && idx < sourceIndex));
                        btn.Background = inWave
                            ? new SolidColorBrush(Color.FromRgb(240, 249, 255))
                            : (colModel != null && colModel.MaxScore > 0
                                ? new SolidColorBrush(Color.FromRgb(239, 246, 255))
                                : new SolidColorBrush(Color.FromRgb(248, 250, 252)));
                        btn.BorderBrush = _movingCriteriaSourceIndex.HasValue
                            ? new SolidColorBrush(Color.FromRgb(245, 158, 11))
                            : (colModel != null && colModel.MaxScore > 0
                                ? new SolidColorBrush(Color.FromRgb(59, 130, 246))
                                : new SolidColorBrush(Color.FromRgb(148, 163, 184)));
                        btn.BorderThickness = new Thickness(1.5);
                        btn.Effect = null;
                    }
                }
            }

            foreach (var kv in _criteriaSlotButtons)
            {
                int slotIdx = kv.Key;
                Button slotBtn = kv.Value;
                bool isHoveredSlot = hasValidTarget && isInsertSlot && targetIndex == slotIdx;
                AnimateElementTransform(slotBtn, 0.0, isHoveredSlot ? -2.0 : 0.0, isHoveredSlot ? 1.18 : 1.0, 150);
            }

            foreach (var kv in _criteriaGridHeaderButtons)
            {
                int idx = kv.Key;
                Button hdrBtn = kv.Value;
                bool isSource = idx == sourceIndex;
                bool isDirectTarget = hasValidTarget && !isInsertSlot && idx == targetIndex;

                double shiftX = 0.0;
                double scale = isSource ? 1.03 : 1.0;

                if (hasValidTarget)
                {
                    if (isSource)
                    {
                        shiftX = finalIndex > sourceIndex ? 10.0 : -10.0;
                    }
                    else if (sourceIndex < finalIndex && idx > sourceIndex && idx <= finalIndex)
                    {
                        shiftX = -14.0;
                        scale = isDirectTarget ? 1.04 : 1.0;
                    }
                    else if (sourceIndex > finalIndex && idx >= finalIndex && idx < sourceIndex)
                    {
                        shiftX = 14.0;
                        scale = isDirectTarget ? 1.04 : 1.0;
                    }
                }

                AnimateElementTransform(hdrBtn, shiftX, 0.0, scale, 180);

                if (!isSource)
                {
                    if (isDirectTarget)
                    {
                        hdrBtn.Background = new SolidColorBrush(Color.FromRgb(254, 243, 199));
                        hdrBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                        hdrBtn.BorderThickness = new Thickness(1.6);
                    }
                    else
                    {
                        hdrBtn.Background = Brushes.Transparent;
                        hdrBtn.BorderBrush = Brushes.Transparent;
                        hdrBtn.BorderThickness = new Thickness(0);
                    }
                }
            }

            if (_movingCriteriaSourceIndex.HasValue && BannerCriteriaMoveMode.Visibility == Visibility.Visible)
            {
                string srcTitle = _activeSheet.CriteriaColumns[sourceIndex].CriteriaTitle;
                if (hasValidTarget)
                {
                    string dstTitle = _activeSheet.CriteriaColumns[finalIndex].CriteriaTitle;
                    TxtCriteriaMoveModeHint.Text = $"✋ نقل «{srcTitle}» ⬅ إلى موضع «{dstTitle}» (الترتيب #{finalIndex + 1}) — اضغط للتأكيد أو Esc للإلغاء.";
                }
                else
                {
                    TxtCriteriaMoveModeHint.Text = $"✋ جاري تحريك العمود ({srcTitle}): حرّك الماوس واضغط على أي عمود آخر أو على سهم (⬇) لنقله إلى ذلك المكان (أو اضغط Esc للإلغاء).";
                }
            }
        }

        private void ClearHoverReorderPreview(int? expectedTargetIndex = null, bool? isInsertSlot = null)
        {
            if (expectedTargetIndex.HasValue &&
                (_hoverReorderTargetIndex != expectedTargetIndex || _hoverReorderIsSlot != isInsertSlot))
            {
                return;
            }

            if (_hoverReorderSourceIndex.HasValue)
            {
                int src = _hoverReorderSourceIndex.Value;
                ApplyHoverReorderPreview(src, null, false);
            }
            _hoverReorderSourceIndex = null;
            _hoverReorderTargetIndex = null;
            _hoverReorderIsSlot = false;
        }

        private void PlayFlipReorderAnimation(
            Dictionary<CriteriaColumnModel, double> oldTopX,
            Dictionary<CriteriaColumnModel, double> oldGridX,
            CriteriaColumnModel movedModel)
        {
            if (_activeSheet == null) return;

            StackInteractiveColumns.UpdateLayout();
            GridSheetPreview.UpdateLayout();

            var glideEase = new CubicEase { EasingMode = EasingMode.EaseOut };
            var glideDur = TimeSpan.FromMilliseconds(320);

            foreach (var kv in _criteriaCardButtons)
            {
                int idx = kv.Key;
                Button btn = kv.Value;
                if (idx < 0 || idx >= _activeSheet.CriteriaColumns.Count) continue;
                if (PresentationSource.FromVisual(btn) == null) continue;

                var model = _activeSheet.CriteriaColumns[idx];
                var (tr, sc) = EnsureAnimTransforms(btn);
                bool isMoved = ReferenceEquals(model, movedModel);

                if (btn.Parent is UIElement hostEl)
                {
                    Panel.SetZIndex(hostEl, isMoved ? 30 : 1);
                }

                if (oldTopX.TryGetValue(model, out double prevX))
                {
                    try
                    {
                        double newX = btn.TranslatePoint(new Point(0, 0), StackInteractiveColumns).X;
                        double deltaX = prevX - newX;
                        if (Math.Abs(deltaX) > 1.0)
                        {
                            tr.BeginAnimation(TranslateTransform.XProperty,
                                new DoubleAnimation(deltaX, 0.0, glideDur) { EasingFunction = glideEase });
                        }
                    }
                    catch { }
                }

                if (isMoved)
                {
                    tr.BeginAnimation(TranslateTransform.YProperty,
                        new DoubleAnimation(-10.0, 0.0, TimeSpan.FromMilliseconds(340)) { EasingFunction = glideEase });
                    sc.BeginAnimation(ScaleTransform.ScaleXProperty,
                        new DoubleAnimation(1.07, 1.0, TimeSpan.FromMilliseconds(340)) { EasingFunction = glideEase });
                    sc.BeginAnimation(ScaleTransform.ScaleYProperty,
                        new DoubleAnimation(1.07, 1.0, TimeSpan.FromMilliseconds(340)) { EasingFunction = glideEase });

                    Color endBg = model.MaxScore > 0 ? Color.FromRgb(239, 246, 255) : Color.FromRgb(248, 250, 252);
                    Color endBorder = model.MaxScore > 0 ? Color.FromRgb(59, 130, 246) : Color.FromRgb(148, 163, 184);

                    var animBg = new SolidColorBrush(Color.FromRgb(220, 252, 231));
                    var animBorder = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                    btn.Background = animBg;
                    btn.BorderBrush = animBorder;
                    animBg.BeginAnimation(SolidColorBrush.ColorProperty,
                        new ColorAnimation(Color.FromRgb(220, 252, 231), endBg, TimeSpan.FromMilliseconds(600)) { EasingFunction = glideEase });
                    animBorder.BeginAnimation(SolidColorBrush.ColorProperty,
                        new ColorAnimation(Color.FromRgb(16, 185, 129), endBorder, TimeSpan.FromMilliseconds(600)) { EasingFunction = glideEase });
                }
            }

            foreach (var kv in _criteriaGridHeaderButtons)
            {
                int idx = kv.Key;
                Button hdrBtn = kv.Value;
                if (idx < 0 || idx >= _activeSheet.CriteriaColumns.Count) continue;
                if (PresentationSource.FromVisual(hdrBtn) == null) continue;

                var model = _activeSheet.CriteriaColumns[idx];
                var (tr, sc) = EnsureAnimTransforms(hdrBtn);
                bool isMoved = ReferenceEquals(model, movedModel);

                if (oldGridX.TryGetValue(model, out double prevX))
                {
                    try
                    {
                        double newX = hdrBtn.TranslatePoint(new Point(0, 0), GridSheetPreview).X;
                        double deltaX = prevX - newX;
                        if (Math.Abs(deltaX) > 1.0)
                        {
                            tr.BeginAnimation(TranslateTransform.XProperty,
                                new DoubleAnimation(deltaX, 0.0, glideDur) { EasingFunction = glideEase });
                        }
                    }
                    catch { }
                }

                if (isMoved)
                {
                    sc.BeginAnimation(ScaleTransform.ScaleXProperty,
                        new DoubleAnimation(1.06, 1.0, TimeSpan.FromMilliseconds(340)) { EasingFunction = glideEase });
                    sc.BeginAnimation(ScaleTransform.ScaleYProperty,
                        new DoubleAnimation(1.06, 1.0, TimeSpan.FromMilliseconds(340)) { EasingFunction = glideEase });

                    var flashHdrBg = new SolidColorBrush(Color.FromRgb(220, 252, 231));
                    hdrBtn.Background = flashHdrBg;
                    flashHdrBg.BeginAnimation(SolidColorBrush.ColorProperty,
                        new ColorAnimation(Color.FromRgb(220, 252, 231), Color.FromArgb(0, 220, 252, 231), TimeSpan.FromMilliseconds(600)) { EasingFunction = glideEase });
                }
            }
        }

        private void MoveCriteriaColumn(int sourceIndex, int targetIndex, bool isInsertSlot)
        {
            if (_activeSheet == null || !IsCurrentDesignerEditable()) return;
            int count = _activeSheet.CriteriaColumns.Count;
            if (sourceIndex < 0 || sourceIndex >= count) return;

            int finalIndex;
            if (isInsertSlot)
            {
                if (targetIndex < 0 || targetIndex > count) return;
                finalIndex = sourceIndex < targetIndex ? targetIndex - 1 : targetIndex;
            }
            else
            {
                if (targetIndex < 0 || targetIndex >= count) return;
                finalIndex = targetIndex;
            }

            var oldTopX = new Dictionary<CriteriaColumnModel, double>();
            foreach (var kv in _criteriaCardButtons)
            {
                if (kv.Key >= 0 && kv.Key < count && PresentationSource.FromVisual(kv.Value) != null)
                {
                    try
                    {
                        oldTopX[_activeSheet.CriteriaColumns[kv.Key]] = kv.Value.TranslatePoint(new Point(0, 0), StackInteractiveColumns).X;
                    }
                    catch { }
                }
            }

            var oldGridX = new Dictionary<CriteriaColumnModel, double>();
            foreach (var kv in _criteriaGridHeaderButtons)
            {
                if (kv.Key >= 0 && kv.Key < count && PresentationSource.FromVisual(kv.Value) != null)
                {
                    try
                    {
                        oldGridX[_activeSheet.CriteriaColumns[kv.Key]] = kv.Value.TranslatePoint(new Point(0, 0), GridSheetPreview).X;
                    }
                    catch { }
                }
            }

            var movedModel = _activeSheet.CriteriaColumns[sourceIndex];

            _movingCriteriaSourceIndex = null;
            _pendingDoubleClickMoveIndex = null;
            _criteriaDragCandidateIndex = null;
            _hoverReorderSourceIndex = null;
            _hoverReorderTargetIndex = null;
            _hoverReorderIsSlot = false;
            _criteriaSingleClickTimer.Stop();
            _pendingSingleClickEditIndex = null;

            bool didMove = finalIndex != sourceIndex && finalIndex >= 0 && finalIndex < count;
            if (didMove)
            {
                _activeSheet.CriteriaColumns.Move(sourceIndex, finalIndex);
                _activeSheet.ReindexOrders();
                MarkTemplateDirty();
            }

            bool canEditNow = IsCurrentDesignerEditable();
            RebuildInteractiveCriteriaStrip(canEditNow);
            RebuildPreviewDataGrid(canEditNow);
            RebuildBottomSheetTabsBar();

            if (didMove)
            {
                Dispatcher.BeginInvoke(
                    new Action(() => PlayFlipReorderAnimation(oldTopX, oldGridX, movedModel)),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void AutoScrollCriteriaStripDuringDrag(DragEventArgs e)
        {
            if (ScrollCriteriaColumns == null || ScrollCriteriaColumns.ScrollableWidth <= 0) return;

            Point pos = e.GetPosition(ScrollCriteriaColumns);
            const double edgeZone = 55.0;
            const double step = 22.0;

            if (pos.X < edgeZone && ScrollCriteriaColumns.HorizontalOffset > 0)
            {
                ScrollCriteriaColumns.ScrollToHorizontalOffset(Math.Max(0, ScrollCriteriaColumns.HorizontalOffset - step));
            }
            else if (pos.X > ScrollCriteriaColumns.ActualWidth - edgeZone && ScrollCriteriaColumns.HorizontalOffset < ScrollCriteriaColumns.ScrollableWidth)
            {
                ScrollCriteriaColumns.ScrollToHorizontalOffset(Math.Min(ScrollCriteriaColumns.ScrollableWidth, ScrollCriteriaColumns.HorizontalOffset + step));
            }
        }

        private void ScrollCriteriaColumns_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(CriteriaDragDataFormat))
            {
                AutoScrollCriteriaStripDuringDrag(e);
            }
        }

        private void AttachCriteriaReorderHandlers(Button btn, int colIndex, bool isTopCard, UIElement? dropHost = null)
        {
            if (_activeSheet == null || _currentTemplate?.IsSystemTemplate == true) return;

            UIElement targetHost = dropHost ?? btn;
            bool suppressThisButtonClick = false;

            targetHost.AllowDrop = true;

            btn.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (_isSelectingCriteriaForCopy) return;

                if (_movingCriteriaSourceIndex.HasValue)
                {
                    suppressThisButtonClick = false;
                    return;
                }

                if (_activeSheet == null || _activeSheet.CriteriaColumns.Count <= 1)
                {
                    suppressThisButtonClick = false;
                    return;
                }

                if (e.ClickCount >= 2)
                {
                    _criteriaSingleClickTimer.Stop();
                    _pendingSingleClickEditIndex = null;
                    suppressThisButtonClick = true;
                    _criteriaDragStartPoint = e.GetPosition(this);
                    _criteriaDragCandidateIndex = colIndex;
                    _pendingDoubleClickMoveIndex = colIndex;
                    _isCriteriaDraggingNow = false;
                }
                else
                {
                    suppressThisButtonClick = false;
                    _criteriaDragStartPoint = e.GetPosition(this);
                    _criteriaDragCandidateIndex = colIndex;
                    _pendingDoubleClickMoveIndex = null;
                    _isCriteriaDraggingNow = false;
                }
            };

            btn.PreviewMouseMove += (_, e) =>
            {
                if (_isSelectingCriteriaForCopy || _movingCriteriaSourceIndex.HasValue) return;
                if (e.LeftButton != MouseButtonState.Pressed) return;
                if (_criteriaDragCandidateIndex != colIndex || _isCriteriaDraggingNow) return;
                if (_activeSheet == null || _activeSheet.CriteriaColumns.Count <= 1) return;

                Point currentPos = e.GetPosition(this);
                if (Math.Abs(currentPos.X - _criteriaDragStartPoint.X) >= 7.0 ||
                    Math.Abs(currentPos.Y - _criteriaDragStartPoint.Y) >= 7.0)
                {
                    _criteriaSingleClickTimer.Stop();
                    _pendingSingleClickEditIndex = null;
                    _pendingDoubleClickMoveIndex = null;
                    _isCriteriaDraggingNow = true;
                    suppressThisButtonClick = true;

                    btn.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.50, TimeSpan.FromMilliseconds(120)));
                    btn.Effect = new DropShadowEffect
                    {
                        Color = Color.FromRgb(245, 158, 11),
                        BlurRadius = 14,
                        ShadowDepth = 2,
                        Opacity = 0.45
                    };

                    var dragData = new DataObject(CriteriaDragDataFormat, colIndex);
                    try
                    {
                        DragDrop.DoDragDrop(btn, dragData, DragDropEffects.Move);
                    }
                    finally
                    {
                        _isCriteriaDraggingNow = false;
                        _criteriaDragCandidateIndex = null;
                        btn.BeginAnimation(UIElement.OpacityProperty, null);
                        btn.Opacity = 1.0;
                        btn.Effect = null;
                        ClearHoverReorderPreview();
                    }
                }
            };

            btn.PreviewMouseLeftButtonUp += (_, _) =>
            {
                _criteriaDragCandidateIndex = null;
                if (_pendingDoubleClickMoveIndex == colIndex && !_isCriteriaDraggingNow)
                {
                    _pendingDoubleClickMoveIndex = null;
                    Dispatcher.BeginInvoke(new Action(() => StartCriteriaMoveMode(colIndex)));
                }
            };

            targetHost.MouseEnter += (_, _) =>
            {
                if (_movingCriteriaSourceIndex.HasValue && !_isSelectingCriteriaForCopy)
                {
                    ApplyHoverReorderPreview(_movingCriteriaSourceIndex.Value, colIndex, isInsertSlot: false);
                }
            };

            targetHost.MouseLeave += (_, _) =>
            {
                if (_movingCriteriaSourceIndex.HasValue && !_isSelectingCriteriaForCopy)
                {
                    ClearHoverReorderPreview(colIndex, isInsertSlot: false);
                }
            };

            targetHost.DragEnter += (_, e) =>
            {
                if (!e.Data.GetDataPresent(CriteriaDragDataFormat)) return;
                int srcIdx = (int)e.Data.GetData(CriteriaDragDataFormat);
                e.Effects = srcIdx != colIndex ? DragDropEffects.Move : DragDropEffects.None;
                ApplyHoverReorderPreview(srcIdx, colIndex, isInsertSlot: false);
                e.Handled = true;
            };

            targetHost.DragOver += (_, e) =>
            {
                if (!e.Data.GetDataPresent(CriteriaDragDataFormat)) return;
                int srcIdx = (int)e.Data.GetData(CriteriaDragDataFormat);
                e.Effects = srcIdx != colIndex ? DragDropEffects.Move : DragDropEffects.None;
                ApplyHoverReorderPreview(srcIdx, colIndex, isInsertSlot: false);
                if (isTopCard) AutoScrollCriteriaStripDuringDrag(e);
                e.Handled = true;
            };

            targetHost.DragLeave += (_, _) =>
            {
                ClearHoverReorderPreview(colIndex, isInsertSlot: false);
            };

            targetHost.Drop += (_, e) =>
            {
                if (!e.Data.GetDataPresent(CriteriaDragDataFormat)) return;
                int srcIdx = (int)e.Data.GetData(CriteriaDragDataFormat);
                e.Handled = true;

                if (srcIdx != colIndex)
                {
                    Dispatcher.BeginInvoke(new Action(() => MoveCriteriaColumn(srcIdx, colIndex, isInsertSlot: false)));
                }
                else
                {
                    ClearHoverReorderPreview();
                }
            };

            btn.Click += (_, _) =>
            {
                if (suppressThisButtonClick)
                {
                    suppressThisButtonClick = false;
                    return;
                }

                if (_movingCriteriaSourceIndex.HasValue)
                {
                    int srcIdx = _movingCriteriaSourceIndex.Value;
                    if (srcIdx == colIndex)
                    {
                        CancelCriteriaMoveMode();
                    }
                    else
                    {
                        MoveCriteriaColumn(srcIdx, colIndex, isInsertSlot: false);
                    }
                    return;
                }

                if (_activeSheet != null && _activeSheet.CriteriaColumns.Count > 1)
                {
                    _pendingSingleClickEditIndex = colIndex;
                    _criteriaSingleClickTimer.Stop();
                    _criteriaSingleClickTimer.Start();
                }
                else
                {
                    OpenEditCriteriaPopup(colIndex);
                }
            };
        }

        private Button CreateInsertColumnPlusButton(int insertAtIndex)
        {
            bool isMoveMode = _movingCriteriaSourceIndex.HasValue;
            Brush normalBg = isMoveMode
                ? new SolidColorBrush(Color.FromRgb(254, 243, 199))
                : new SolidColorBrush(Color.FromRgb(226, 232, 240));
            Brush normalFg = isMoveMode
                ? new SolidColorBrush(Color.FromRgb(146, 64, 14))
                : new SolidColorBrush(Color.FromRgb(15, 23, 42));
            Brush normalBorder = isMoveMode
                ? new SolidColorBrush(Color.FromRgb(245, 158, 11))
                : new SolidColorBrush(Color.FromRgb(148, 163, 184));

            var plusBtn = new Button
            {
                Content = isMoveMode ? "⬇" : "+",
                Width = isMoveMode ? 32 : 28,
                Height = 38,
                FontWeight = FontWeights.Bold,
                FontSize = isMoveMode ? 14 : 16,
                Cursor = Cursors.Hand,
                Background = normalBg,
                Foreground = normalFg,
                BorderBrush = normalBorder,
                BorderThickness = new Thickness(isMoveMode ? 1.5 : 1),
                Margin = new Thickness(2, 0, 2, 0),
                AllowDrop = true,
                ToolTip = isMoveMode
                    ? "اضغط هنا لنقل العمود المحدد إلى هذا الموضع"
                    : "اضغط (+) لإدراج عمود مبدأ تقييم جديد في هذا المكان — أو اسحب عموداً وأفلته هنا لتغيير ترتيبه"
            };

            EnsureAnimTransforms(plusBtn);
            _criteriaSlotButtons[insertAtIndex] = plusBtn;

            plusBtn.MouseEnter += (_, _) =>
            {
                if (_movingCriteriaSourceIndex.HasValue && !_isSelectingCriteriaForCopy)
                {
                    ApplyHoverReorderPreview(_movingCriteriaSourceIndex.Value, insertAtIndex, isInsertSlot: true);
                }
            };

            plusBtn.MouseLeave += (_, _) =>
            {
                if (_movingCriteriaSourceIndex.HasValue && !_isSelectingCriteriaForCopy)
                {
                    ClearHoverReorderPreview(insertAtIndex, isInsertSlot: true);
                }
            };

            plusBtn.DragEnter += (_, e) =>
            {
                if (!e.Data.GetDataPresent(CriteriaDragDataFormat)) return;
                int srcIdx = (int)e.Data.GetData(CriteriaDragDataFormat);
                e.Effects = DragDropEffects.Move;
                plusBtn.Background = new SolidColorBrush(Color.FromRgb(253, 224, 71));
                plusBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(217, 119, 6));
                plusBtn.BorderThickness = new Thickness(2);
                ApplyHoverReorderPreview(srcIdx, insertAtIndex, isInsertSlot: true);
                e.Handled = true;
            };

            plusBtn.DragOver += (_, e) =>
            {
                if (!e.Data.GetDataPresent(CriteriaDragDataFormat)) return;
                int srcIdx = (int)e.Data.GetData(CriteriaDragDataFormat);
                e.Effects = DragDropEffects.Move;
                ApplyHoverReorderPreview(srcIdx, insertAtIndex, isInsertSlot: true);
                AutoScrollCriteriaStripDuringDrag(e);
                e.Handled = true;
            };

            plusBtn.DragLeave += (_, _) =>
            {
                plusBtn.Background = normalBg;
                plusBtn.BorderBrush = normalBorder;
                plusBtn.BorderThickness = new Thickness(isMoveMode ? 1.5 : 1);
                ClearHoverReorderPreview(insertAtIndex, isInsertSlot: true);
            };

            plusBtn.Drop += (_, e) =>
            {
                plusBtn.Background = normalBg;
                plusBtn.BorderBrush = normalBorder;
                plusBtn.BorderThickness = new Thickness(isMoveMode ? 1.5 : 1);

                if (!e.Data.GetDataPresent(CriteriaDragDataFormat)) return;
                int srcIdx = (int)e.Data.GetData(CriteriaDragDataFormat);
                e.Handled = true;

                Dispatcher.BeginInvoke(new Action(() => MoveCriteriaColumn(srcIdx, insertAtIndex, isInsertSlot: true)));
            };

            plusBtn.Click += (_, _) =>
            {
                if (_activeSheet == null || _isReadOnlyViewMode) return;

                if (_movingCriteriaSourceIndex.HasValue)
                {
                    MoveCriteriaColumn(_movingCriteriaSourceIndex.Value, insertAtIndex, isInsertSlot: true);
                    return;
                }

                var newCol = new CriteriaColumnModel
                {
                    CriteriaTitle = "قواعد التقييم",
                    MaxScore = 0m,
                    ColumnOrder = insertAtIndex + 1
                };

                _activeSheet.CriteriaColumns.Insert(insertAtIndex, newCol);
                MarkTemplateDirty();
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

                MarkTemplateDirty();
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

            MarkTemplateDirty();
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

            MarkTemplateDirty();
            RebuildBottomSheetTabsBar();
            MessageBox.Show(
                $"✔ تم تعميم أعمدة التقييم بنجاح على جميع أوراق القالب ({_currentTemplate.Sheets.Count} ورقة).\nاضغط (💾 حفظ واعتماد القالب بالكامل) لاعتماد التغيير.",
                "تم التعميم بنجاح",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void ClearTemplateEmployeeSearchSilently()
        {
            if (TxtSearchTemplateEmployees != null && !string.IsNullOrEmpty(TxtSearchTemplateEmployees.Text))
            {
                _isClearingTemplateSearch = true;
                TxtSearchTemplateEmployees.Text = string.Empty;
                _isClearingTemplateSearch = false;
            }
            if (BtnClearTemplateEmployeeSearch != null)
                BtnClearTemplateEmployeeSearch.Visibility = Visibility.Collapsed;
            if (TxtTemplateSearchMatchCount != null)
                TxtTemplateSearchMatchCount.Visibility = Visibility.Collapsed;
        }

        private void RebuildPreviewDataGrid(bool canEditNow)
        {
            ClearTemplateEmployeeSearchSilently();
            GridSheetPreview.Columns.Clear();
            _criteriaGridHeaderButtons.Clear();
            if (_activeSheet == null) return;

            _activeSheet.ReindexOrders();
            var wrapStyle = TryFindResource("WrappingCellTextStyle") as Style;

            GridSheetPreview.Columns.Add(new DataGridTextColumn
            {
                Header = "م",
                Binding = new System.Windows.Data.Binding("RowOrder"),
                ElementStyle = wrapStyle,
                Width = 48,
                MinWidth = 48
            });

            GridSheetPreview.Columns.Add(new DataGridTextColumn
            {
                Header = "الاسم التقييمي للموظف",
                Binding = new System.Windows.Data.Binding("EvaluationName"),
                ElementStyle = wrapStyle,
                Width = 200,
                MinWidth = 175
            });

            GridSheetPreview.Columns.Add(new DataGridTextColumn
            {
                Header = "الفرع",
                Binding = new System.Windows.Data.Binding("BranchName"),
                ElementStyle = wrapStyle,
                Width = 85,
                MinWidth = 75
            });

            GridSheetPreview.Columns.Add(new DataGridTextColumn
            {
                Header = "الوظيفة ونوعها",
                Binding = new System.Windows.Data.Binding("FullJobDisplay"),
                ElementStyle = wrapStyle,
                Width = 140,
                MinWidth = 120
            });

            GridSheetPreview.Columns.Add(new DataGridTextColumn
            {
                Header = "الحافز",
                Binding = new System.Windows.Data.Binding("IncentiveBadgeDisplay"),
                ElementStyle = wrapStyle,
                Width = 85,
                MinWidth = 80
            });

            for (int i = 0; i < _activeSheet.CriteriaColumns.Count; i++)
            {
                int capturedIndex = i;
                var crit = _activeSheet.CriteriaColumns[i];

                bool isSelCopy = _isSelectingCriteriaForCopy && _selectedCriteriaIndicesForCopy.Contains(capturedIndex);
                bool isMovingHdr = !_isSelectingCriteriaForCopy && _movingCriteriaSourceIndex == capturedIndex;
                string hdrPrefix = _isSelectingCriteriaForCopy
                    ? (isSelCopy ? "☑ " : "☐ ")
                    : (isMovingHdr ? "✋ " : string.Empty);

                var headerStack = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                headerStack.Children.Add(new TextBlock
                {
                    Text = $"{hdrPrefix}{crit.CriteriaTitle}",
                    FontSize = 12,
                    LineHeight = 17,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    MaxHeight = 54,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Center,
                    MaxWidth = _activeSheet.CriteriaColumns.Count <= 3 ? 320 : 205,
                    FontWeight = FontWeights.Bold,
                    Foreground = isSelCopy
                        ? new SolidColorBrush(Color.FromRgb(4, 120, 87))
                        : new SolidColorBrush(Color.FromRgb(30, 64, 175))
                });
                headerStack.Children.Add(new TextBlock
                {
                    Text = $"({crit.MaxScore:0} درجة)",
                    FontSize = 11,
                    TextAlignment = TextAlignment.Center,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 1, 0, 0),
                    Foreground = isSelCopy
                        ? new SolidColorBrush(Color.FromRgb(6, 95, 70))
                        : new SolidColorBrush(Color.FromRgb(51, 65, 85))
                });

                string hdrHint = _isSelectingCriteriaForCopy
                    ? (isSelCopy ? "محدد للنسخ — اضغط لإلغاء التحديد" : "اضغط لتحديد هذا العمود للنسخ")
                    : (isMovingHdr
                        ? "جاري نقل هذا العمود — اضغط على عمود آخر لنقله أو اضغط عليه لإلغاء النقل"
                        : (_movingCriteriaSourceIndex.HasValue
                            ? "اضغط هنا لنقل العمود المحدد إلى هذا المكان"
                            : (canEditNow ? "نقرة واحدة للتعديل — أو اضغط مرتين / اسحب بالماوس لتحريك العمود" : "وضع القراءة فقط")));

                var headerBtn = new Button
                {
                    Content = headerStack,
                    Background = isSelCopy
                        ? new SolidColorBrush(Color.FromRgb(220, 252, 231))
                        : (isMovingHdr ? new SolidColorBrush(Color.FromRgb(254, 243, 199)) : Brushes.Transparent),
                    BorderBrush = isSelCopy
                        ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
                        : (isMovingHdr ? new SolidColorBrush(Color.FromRgb(217, 119, 6)) : Brushes.Transparent),
                    BorderThickness = (isSelCopy || isMovingHdr) ? new Thickness(1.5) : new Thickness(0),
                    Padding = new Thickness(6, 2, 6, 2),
                    Cursor = (_isSelectingCriteriaForCopy || canEditNow) ? Cursors.Hand : Cursors.Arrow,
                    ToolTip = $"{crit.CriteriaTitle} ({crit.MaxScore:0} درجة)\n{hdrHint}"
                };

                EnsureAnimTransforms(headerBtn);
                _criteriaGridHeaderButtons[capturedIndex] = headerBtn;

                if (isMovingHdr)
                {
                    AnimateElementTransform(headerBtn, 0.0, -1.5, 1.04, 200);
                }

                if (_isSelectingCriteriaForCopy)
                {
                    headerBtn.Click += (_, _) => ToggleCriteriaSelectionForCopy(capturedIndex);
                }
                else if (canEditNow)
                {
                    if (_currentTemplate == null || !_currentTemplate.IsSystemTemplate)
                    {
                        AttachCriteriaReorderHandlers(headerBtn, capturedIndex, isTopCard: false, dropHost: headerBtn);
                    }
                    else
                    {
                        headerBtn.Click += (_, _) => OpenEditCriteriaPopup(capturedIndex);
                    }
                }

                var col = new DataGridTextColumn
                {
                    Header = headerBtn,
                    Binding = new System.Windows.Data.Binding
                    {
                        Source = string.Empty
                    },
                    ElementStyle = wrapStyle,
                    Width = _activeSheet.CriteriaColumns.Count <= 3
                        ? new DataGridLength(1, DataGridLengthUnitType.Star)
                        : new DataGridLength(215),
                    MinWidth = 150
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
            MarkTemplateDirty();

            await SaveSystemTemplateChangesImmediatelyAsync();

            RefreshAvailableEmployeesCombo();
            if (!string.IsNullOrWhiteSpace(TxtSearchTemplateEmployees?.Text))
            {
                ApplyDesignerEmployeeSearchFilter();
            }
            else
            {
                RebuildPreviewDataGrid(true);
                RebuildBottomSheetTabsBar();
            }
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
            MarkTemplateDirty();

            await SaveSystemTemplateChangesImmediatelyAsync();

            RefreshAvailableEmployeesCombo();
            if (!string.IsNullOrWhiteSpace(TxtSearchTemplateEmployees?.Text))
            {
                ApplyDesignerEmployeeSearchFilter();
            }
            else
            {
                RebuildPreviewDataGrid(true);
                RebuildBottomSheetTabsBar();
            }
        }

        private class CrossSheetDesignerEmployeeSearchRow
        {
            public int RowOrder { get; set; }
            public TemplateSheetModel SourceSheet { get; set; } = null!;
            public SheetEmployeeRowModel SourceEmployee { get; set; } = null!;
            public string SheetDisplay => $"{SourceSheet.SheetName} ({SourceSheet.CategoryWithBranchDisplay})";
            public string EvaluationName => SourceEmployee.EvaluationName;
            public string BranchName => SourceEmployee.BranchName;
            public string FullJobDisplay => SourceEmployee.FullJobDisplay;
            public string IncentiveBadgeDisplay => SourceEmployee.IncentiveBadgeDisplay;
        }

        private static string NormalizeArabicSearchForDesigner(string text)
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

        private static bool MatchesTemplateEmployeeSearch(TemplateSheetModel sheet, SheetEmployeeRowModel emp, string normFilter)
        {
            return NormalizeArabicSearchForDesigner(emp.EvaluationName).Contains(normFilter, StringComparison.OrdinalIgnoreCase)
                || NormalizeArabicSearchForDesigner(emp.SystemName).Contains(normFilter, StringComparison.OrdinalIgnoreCase)
                || NormalizeArabicSearchForDesigner(emp.BranchName).Contains(normFilter, StringComparison.OrdinalIgnoreCase)
                || NormalizeArabicSearchForDesigner(emp.FullJobDisplay).Contains(normFilter, StringComparison.OrdinalIgnoreCase)
                || NormalizeArabicSearchForDesigner(emp.CategoryName).Contains(normFilter, StringComparison.OrdinalIgnoreCase)
                || NormalizeArabicSearchForDesigner(sheet.SheetName).Contains(normFilter, StringComparison.OrdinalIgnoreCase)
                || NormalizeArabicSearchForDesigner(sheet.CategoryWithBranchDisplay).Contains(normFilter, StringComparison.OrdinalIgnoreCase);
        }

        private void RebuildCrossSheetDesignerSearchGrid()
        {
            GridSheetPreview.Columns.Clear();
            _criteriaGridHeaderButtons.Clear();

            var wrapStyle = TryFindResource("WrappingCellTextStyle") as Style;

            GridSheetPreview.Columns.Add(new DataGridTextColumn
            {
                Header = "م",
                Binding = new System.Windows.Data.Binding("RowOrder"),
                ElementStyle = wrapStyle,
                Width = 48,
                MinWidth = 48
            });

            GridSheetPreview.Columns.Add(new DataGridTextColumn
            {
                Header = "ورقة التقييم",
                Binding = new System.Windows.Data.Binding("SheetDisplay"),
                ElementStyle = wrapStyle,
                Width = 200,
                MinWidth = 165
            });

            GridSheetPreview.Columns.Add(new DataGridTextColumn
            {
                Header = "الاسم التقييمي للموظف",
                Binding = new System.Windows.Data.Binding("EvaluationName"),
                ElementStyle = wrapStyle,
                Width = 220,
                MinWidth = 185
            });

            GridSheetPreview.Columns.Add(new DataGridTextColumn
            {
                Header = "الفرع",
                Binding = new System.Windows.Data.Binding("BranchName"),
                ElementStyle = wrapStyle,
                Width = 90,
                MinWidth = 80
            });

            GridSheetPreview.Columns.Add(new DataGridTextColumn
            {
                Header = "الوظيفة ونوعها",
                Binding = new System.Windows.Data.Binding("FullJobDisplay"),
                ElementStyle = wrapStyle,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 140
            });

            GridSheetPreview.Columns.Add(new DataGridTextColumn
            {
                Header = "الحافز",
                Binding = new System.Windows.Data.Binding("IncentiveBadgeDisplay"),
                ElementStyle = wrapStyle,
                Width = 90,
                MinWidth = 80
            });
        }

        private void ApplyDesignerEmployeeSearchFilter()
        {
            if (_currentTemplate == null || _activeSheet == null) return;

            string filterText = TxtSearchTemplateEmployees?.Text.Trim() ?? string.Empty;
            bool hasFilter = !string.IsNullOrEmpty(filterText);

            if (BtnClearTemplateEmployeeSearch != null)
                BtnClearTemplateEmployeeSearch.Visibility = hasFilter ? Visibility.Visible : Visibility.Collapsed;

            if (!hasFilter)
            {
                if (TxtTemplateSearchMatchCount != null)
                    TxtTemplateSearchMatchCount.Visibility = Visibility.Collapsed;

                RebuildPreviewDataGrid(IsCurrentDesignerEditable());
                RebuildBottomSheetTabsBar();
                return;
            }

            string normFilter = NormalizeArabicSearchForDesigner(filterText);
            var matchedRows = new List<CrossSheetDesignerEmployeeSearchRow>();
            var matchCountsBySheet = new Dictionary<TemplateSheetModel, int>();
            int totalTemplateEmps = 0;

            foreach (var sheet in _currentTemplate.Sheets)
            {
                totalTemplateEmps += sheet.EmployeeRows.Count;
                int sheetMatches = 0;
                foreach (var emp in sheet.EmployeeRows)
                {
                    if (MatchesTemplateEmployeeSearch(sheet, emp, normFilter))
                    {
                        sheetMatches++;
                        matchedRows.Add(new CrossSheetDesignerEmployeeSearchRow
                        {
                            RowOrder = matchedRows.Count + 1,
                            SourceSheet = sheet,
                            SourceEmployee = emp
                        });
                    }
                }
                matchCountsBySheet[sheet] = sheetMatches;
            }

            RebuildCrossSheetDesignerSearchGrid();
            GridSheetPreview.ItemsSource = null;
            GridSheetPreview.ItemsSource = matchedRows;

            int matchedSheetsCount = matchCountsBySheet.Values.Count(c => c > 0);
            if (TxtTemplateSearchMatchCount != null)
            {
                TxtTemplateSearchMatchCount.Text = $"النتائج: {matchedRows.Count} من {totalTemplateEmps} (في {matchedSheetsCount} أوراق)";
                TxtTemplateSearchMatchCount.Visibility = Visibility.Visible;
            }

            RebuildBottomSheetTabsBar(matchCountsBySheet);
        }

        private void TxtSearchTemplateEmployees_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isClearingTemplateSearch) return;
            ApplyDesignerEmployeeSearchFilter();
        }

        private void BtnClearTemplateEmployeeSearch_Click(object sender, RoutedEventArgs e)
        {
            TxtSearchTemplateEmployees.Text = string.Empty;
        }

        private async void GridSheetPreview_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (GridSheetPreview.SelectedItem is CrossSheetDesignerEmployeeSearchRow searchRow)
            {
                var targetSheet = searchRow.SourceSheet;
                var targetEmp = searchRow.SourceEmployee;

                TxtSearchTemplateEmployees.Text = string.Empty;
                if (!ReferenceEquals(_activeSheet, targetSheet))
                {
                    await ActivateSheetInDesignerAsync(targetSheet);
                }

                GridSheetPreview.SelectedItem = targetEmp;
                GridSheetPreview.ScrollIntoView(targetEmp);
            }
        }

        private async void GridSheetPreview_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && IsCurrentDesignerEditable() && GridSheetPreview.SelectedItems.Count > 0)
            {
                e.Handled = true;
                await RemoveSelectedEmployeesFromTemplateAsync(showEmptySelectionHint: false);
            }
        }

        private async void BtnRemoveEmployeeFromSheet_Click(object sender, RoutedEventArgs e)
        {
            await RemoveSelectedEmployeesFromTemplateAsync(showEmptySelectionHint: true);
        }

        private async Task RemoveSelectedEmployeesFromTemplateAsync(bool showEmptySelectionHint)
        {
            if (_currentTemplate == null || _activeSheet == null || !IsCurrentDesignerEditable())
                return;

            var targets = new List<(TemplateSheetModel Sheet, SheetEmployeeRowModel Emp)>();
            foreach (var item in GridSheetPreview.SelectedItems)
            {
                if (item is SheetEmployeeRowModel directEmp)
                {
                    targets.Add((_activeSheet, directEmp));
                }
                else if (item is CrossSheetDesignerEmployeeSearchRow searchRow)
                {
                    targets.Add((searchRow.SourceSheet, searchRow.SourceEmployee));
                }
            }

            if (targets.Count == 0)
            {
                if (showEmptySelectionHint)
                {
                    MessageBox.Show("يرجى تحديد موظف واحد أو أكثر من جدول الورقة لإزالته.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                return;
            }

            string confirmMessage;
            if (targets.Count == 1)
            {
                confirmMessage =
                    $"هل أنت متأكد من حذف الموظف ({targets[0].Emp.EvaluationName}) من ورقة ({targets[0].Sheet.SheetName}) في هذا القالب؟\n\n" +
                    "ملاحظة: بعد الضغط على (💾 حفظ واعتماد القالب بالكامل) سيظل الموظف محذوفاً من القالب حتى تعيد إضافته يدوياً.";
            }
            else
            {
                var previewNames = targets
                    .Take(8)
                    .Select(t => $"• {t.Emp.EvaluationName} ({t.Sheet.SheetName})")
                    .ToList();
                if (targets.Count > 8)
                {
                    previewNames.Add($"• ... و ({targets.Count - 8}) موظفين آخرين");
                }

                confirmMessage =
                    $"هل أنت متأكد من حذف ({targets.Count}) موظفين محددين من هذا القالب؟\n\n" +
                    string.Join("\n", previewNames) + "\n\n" +
                    "ملاحظة: بعد الضغط على (💾 حفظ واعتماد القالب بالكامل) سيظل الموظفون المحذوفون مستثنين من القالب حتى تعيد إضافتهم يدوياً.";
            }

            var confirm = MessageBox.Show(
                confirmMessage,
                "تأكيد حذف موظفين من القالب",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (confirm != MessageBoxResult.Yes)
                return;

            var affectedSheets = new HashSet<TemplateSheetModel>();
            foreach (var (sheet, emp) in targets)
            {
                if (sheet.EmployeeRows.Remove(emp))
                {
                    affectedSheets.Add(sheet);
                }
            }

            foreach (var sheet in affectedSheets)
            {
                sheet.ReindexOrders();
            }
            if (affectedSheets.Count > 0)
            {
                MarkTemplateDirty();
            }

            await SaveSystemTemplateChangesImmediatelyAsync();

            RefreshAvailableEmployeesCombo();
            if (!string.IsNullOrWhiteSpace(TxtSearchTemplateEmployees?.Text))
            {
                ApplyDesignerEmployeeSearchFilter();
            }
            else
            {
                RebuildPreviewDataGrid(true);
                RebuildBottomSheetTabsBar();
            }
        }

        private void RebuildBottomSheetTabsBar(Dictionary<TemplateSheetModel, int>? searchMatchCounts = null)
        {
            StackSheetTabs.Children.Clear();
            if (_currentTemplate == null) return;

            bool isSearchMode = searchMatchCounts != null;

            foreach (var sheet in _currentTemplate.Sheets)
            {
                bool isCurrent = !isSearchMode && ReferenceEquals(sheet, _activeSheet);
                bool isValid100 = sheet.IsCriteriaTotalValid && sheet.EmployeeRows.Count > 0;
                int matchCnt = isSearchMode && searchMatchCounts!.TryGetValue(sheet, out int c) ? c : 0;

                string statusIcon = isValid100 ? "✔" : "⚠";
                string tabContent = isSearchMode
                    ? $"{statusIcon}  {sheet.SheetName} ({sheet.CategoryWithBranchDisplay}) [🔍 {matchCnt} نتيجة]"
                    : $"{statusIcon}  {sheet.SheetName} ({sheet.CategoryWithBranchDisplay}) [{sheet.EmployeeRows.Count} موظف]";

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
                tabBtn.Click += async (_, _) =>
                {
                    if (!string.IsNullOrEmpty(TxtSearchTemplateEmployees?.Text))
                        TxtSearchTemplateEmployees.Text = string.Empty;
                    await ActivateSheetInDesignerAsync(capturedSheet);
                };

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
                MarkTemplateDirty();
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
            MarkTemplateDirty();
            await ActivateSheetInDesignerAsync(_currentTemplate.Sheets[0]);
        }

        private async void BtnDesignerSaveTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTemplate == null || _isReadOnlyViewMode) return;

            bool isSuccess;
            string message;
            int savedId;
            BtnDesignerSaveTemplate.IsEnabled = false;
            try
            {
                (isSuccess, message, savedId) = await _service.SaveTemplateAsync(_currentTemplate, SelectedTemplateYear, SelectedTemplateMonth);
            }
            finally
            {
                BtnDesignerSaveTemplate.IsEnabled = IsCurrentDesignerEditable();
            }

            MessageBox.Show(message, isSuccess ? "تم حفظ القالب" : "تنبيه التحقق من شروط القالب", MessageBoxButton.OK, isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);

            if (isSuccess && savedId > 0)
            {
                _hasUnsavedChanges = false;
                UpdateSaveTemplateDirtyLabel();
                int? prevActiveCatId = _activeSheet?.TargetCategoryID;
                string prevActiveSheetName = _activeSheet?.SheetName ?? string.Empty;

                var reloaded = await _service.GetTemplateFullDetailsAsync(savedId, SelectedTemplateYear, SelectedTemplateMonth);
                if (reloaded != null)
                {
                    _currentTemplate = reloaded;
                    if (_currentTemplate.Sheets.Count > 0)
                    {
                        var sheetToFocus = _currentTemplate.Sheets.FirstOrDefault(s =>
                                (prevActiveCatId.HasValue && s.TargetCategoryID == prevActiveCatId) ||
                                (!string.IsNullOrEmpty(prevActiveSheetName) && string.Equals(s.SheetName, prevActiveSheetName, StringComparison.OrdinalIgnoreCase)))
                            ?? _currentTemplate.Sheets[0];

                        await ActivateSheetInDesignerAsync(sheetToFocus);
                    }
                }
                await RefreshSavedTemplatesGridAsync(syncSystemTemplates: false);
            }
        }


        private async void BtnDesignerExportExcel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTemplate == null) return;

            await PromptAndExportTemplateExcelAsync(
                evaluatorId: _currentTemplate.EvaluatorID,
                evaluatorName: _currentTemplate.EvaluatorName,
                fallbackTemplateId: _currentTemplate.TemplateID,
                preloadedSelectedMonthModel: _currentTemplate);
        }

        private async void BtnSwitchToEditMode_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTemplate == null) return;

            if (_currentTemplate.IsMonthLocked)
            {
                MessageBox.Show("لا يمكن تعديل قالب هذا الشهر لأنه معتمد ومغلق نهائياً.", "شهر مغلق", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool hasRolePermission = (_currentTemplate.TemplateID == 0) ? CanInsert : CanUpdate;
            if (!hasRolePermission)
            {
                MessageBox.Show("عذراً، لا تملك صلاحية (تعديل) قوالب التقييم.", "تنبيه صلاحيات", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _isReadOnlyViewMode = false;
            var targetSheet = _activeSheet ?? _currentTemplate.Sheets.FirstOrDefault();
            if (targetSheet != null)
            {
                await ActivateSheetInDesignerAsync(targetSheet);
            }
        }

        private static string SanitizeFileNamePart(string rawName)
        {
            string clean = (rawName ?? string.Empty).Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
            {
                clean = clean.Replace(invalid, '-');
            }
            return string.IsNullOrWhiteSpace(clean) ? "مقيم" : clean;
        }

        private async Task<string> ResolveExcelProtectionPasswordForExportAsync()
        {
            if (IsAdmin)
            {
                string typed = GetTypedExcelPassword();
                if (!string.IsNullOrWhiteSpace(typed))
                    return typed;
            }
            return await _service.GetEffectiveExcelProtectionPasswordAsync();
        }

        private async Task PromptAndExportTemplateExcelAsync(
            int evaluatorId,
            string evaluatorName,
            int fallbackTemplateId,
            TemplateFullModel? preloadedSelectedMonthModel)
        {
            int targetYear = SelectedTemplateYear;
            int targetMonth = SelectedTemplateMonth;

            var optionsDlg = new ExportTemplateExcelOptionsDialog(evaluatorName, targetYear, targetMonth)
            {
                Owner = Window.GetWindow(this)
            };

            if (optionsDlg.ShowDialog() != true || optionsDlg.SelectedScope == TemplateExcelExportScope.Cancel)
                return;

            string protectionPassword = await ResolveExcelProtectionPasswordForExportAsync();
            string safeEvalName = SanitizeFileNamePart(evaluatorName);

            if (optionsDlg.SelectedScope == TemplateExcelExportScope.SelectedMonthOnly)
            {
                TemplateFullModel? model = preloadedSelectedMonthModel
                    ?? await _service.GetTemplateFullDetailsAsync(fallbackTemplateId, targetYear, targetMonth);

                if (model == null || model.Sheets.Count == 0)
                {
                    MessageBox.Show("لا توجد أوراق في القالب لاستخراجها.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string monthName = IncentiveCalculatorService.GetArabicMonthName(targetMonth);
                var sfd = new SaveFileDialog
                {
                    Title = $"استخراج قالب التقييم لشهر ({monthName} {targetYear} م) بصيغة Excel",
                    Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                    FileName = $"{targetMonth:00}_قالب_تقييم_{monthName}_{safeEvalName}.xlsx"
                };

                if (sfd.ShowDialog() == true)
                {
                    try
                    {
                        TemplateExcelExportService.GenerateTemplateExcel(
                            model,
                            sfd.FileName,
                            targetYear,
                            targetMonth,
                            protectionPassword);

                        var openNow = MessageBox.Show(
                            $"✔ تم استخراج قالب التقييم المحمي لشهر ({monthName} {targetYear} م) بنجاح في المسار:\n{sfd.FileName}\n\n" +
                            "🔒 الأسماء والأكواد والمجموع مقفلة ومحمية، وخلايا الدرجات فقط مفتوحة للإدخال.\n" +
                            "هل تريد فتح ملف الإكسل الآن؟",
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
                return;
            }

            // استخراج جميع الشهور (12 شهراً) داخل مجلد واحد باسم (قوالب تقييم - اسم المقيم (السنة))
            var folderDialog = new OpenFolderDialog
            {
                Title = $"اختر المكان الذي سيتم فيه إنشاء مجلد (قوالب تقييم - {safeEvalName} ({targetYear}))"
            };

            if (folderDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(folderDialog.FolderName))
                return;

            string folderName = $"قوالب تقييم - {safeEvalName} ({targetYear})";
            string targetFolder = Path.Combine(folderDialog.FolderName, folderName);

            BtnExportSelectedTemplateExcel.IsEnabled = false;
            BtnDesignerExportExcel.IsEnabled = false;
            try
            {
                Directory.CreateDirectory(targetFolder);
                int exportedCount = 0;

                for (int m = 1; m <= 12; m++)
                {
                    TemplateFullModel? monthModel;
                    if (preloadedSelectedMonthModel != null && m == targetMonth)
                    {
                        monthModel = preloadedSelectedMonthModel;
                    }
                    else
                    {
                        int resolvedTid = await _service.ResolveTemplateIdForEvaluatorInMonthAsync(evaluatorId, targetYear, m);
                        if (resolvedTid <= 0) resolvedTid = fallbackTemplateId;
                        if (resolvedTid <= 0) continue;

                        monthModel = await _service.GetTemplateFullDetailsAsync(resolvedTid, targetYear, m);
                    }

                    if (monthModel == null || monthModel.Sheets.Count == 0)
                        continue;

                    string mName = IncentiveCalculatorService.GetArabicMonthName(m);
                    string fileName = $"{m:00}_قالب_تقييم_{mName}_{safeEvalName}.xlsx";
                    string filePath = Path.Combine(targetFolder, fileName);

                    TemplateExcelExportService.GenerateTemplateExcel(
                        monthModel,
                        filePath,
                        targetYear,
                        m,
                        protectionPassword);

                    exportedCount++;
                }

                if (exportedCount == 0)
                {
                    MessageBox.Show("لم يتم توليد أي ملفات لأن القالب لا يحتوي على أوراق تقييم.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var openFolderChoice = MessageBox.Show(
                    $"✔ تم استخراج ({exportedCount}) قالب تقييم محمي لجميع أشهر سنة ({targetYear} م) بنجاح داخل المجلد:\n{targetFolder}\n\n" +
                    "• كل شهر تم توليده بنسخة القالب وموظفي ذلك الشهر تحديداً.\n" +
                    "• جميع الملفات محمية بكلمة سر قوالب الإكسل المعتمدة.\n\n" +
                    "هل تريد فتح المجلد الآن؟",
                    "تم استخراج قوالب جميع الشهور",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (openFolderChoice == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = targetFolder,
                        UseShellExecute = true
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"حدث خطأ أثناء استخراج قوالب جميع الشهور:\n{ex.Message}", "خطأ تصدير", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnExportSelectedTemplateExcel.IsEnabled = true;
                BtnDesignerExportExcel.IsEnabled = _currentTemplate != null;
            }
        }

    }
}













































































































