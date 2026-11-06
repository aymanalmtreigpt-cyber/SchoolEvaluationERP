using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class MasterDataView : UserControl
    {
        private readonly MasterDataService _service = new();
        private bool _isRefreshingData;

        private bool CanInsert => AuthService.CurrentSession?.HasPermission("MasterData", "insert") == true;
        private bool CanUpdate => AuthService.CurrentSession?.HasPermission("MasterData", "update") == true;
        private bool CanDelete => AuthService.CurrentSession?.HasPermission("MasterData", "delete") == true;

        public MasterDataView()
        {
            _isRefreshingData = true;
            InitializeComponent();
            _isRefreshingData = false;

            Loaded += async (_, _) =>
            {
                ApplyPermissionsToControls();
                await RefreshAllMasterDataAsync();
            };
        }

        private void ApplyPermissionsToControls()
        {
            bool canSave = CanInsert || CanUpdate;

            BtnCreateBranch.Visibility = CanInsert ? Visibility.Visible : Visibility.Collapsed;
            BtnEditBranch.Visibility = CanUpdate ? Visibility.Visible : Visibility.Collapsed;
            BtnDeleteBranch.Visibility = CanDelete ? Visibility.Visible : Visibility.Collapsed;

            BtnCreateCategory.Visibility = CanInsert ? Visibility.Visible : Visibility.Collapsed;
            BtnEditCategory.Visibility = CanUpdate ? Visibility.Visible : Visibility.Collapsed;
            BtnDeleteCategoryBasic.Visibility = CanDelete ? Visibility.Visible : Visibility.Collapsed;

            BtnCreateJob.Visibility = CanInsert ? Visibility.Visible : Visibility.Collapsed;
            BtnEditJob.Visibility = CanUpdate ? Visibility.Visible : Visibility.Collapsed;
            BtnDeleteJob.Visibility = CanDelete ? Visibility.Visible : Visibility.Collapsed;

            BtnCreateJobType.Visibility = CanInsert ? Visibility.Visible : Visibility.Collapsed;
            BtnEditJobType.Visibility = CanUpdate ? Visibility.Visible : Visibility.Collapsed;
            BtnDeleteJobType.Visibility = CanDelete ? Visibility.Visible : Visibility.Collapsed;

            BtnCreateEvaluatorGroup.Visibility = CanInsert ? Visibility.Visible : Visibility.Collapsed;
            BtnEditEvaluatorGroup.Visibility = CanUpdate ? Visibility.Visible : Visibility.Collapsed;
            BtnDeleteEvaluatorGroup.Visibility = CanDelete ? Visibility.Visible : Visibility.Collapsed;
            BtnImportEvaluatorsExcel.Visibility = canSave ? Visibility.Visible : Visibility.Collapsed;
            BtnAllocateCategoryWeights.Visibility = canSave ? Visibility.Visible : Visibility.Collapsed;

            BtnAddEmployeePopup.Visibility = CanInsert ? Visibility.Visible : Visibility.Collapsed;
            BtnImportExcel.Visibility = CanInsert ? Visibility.Visible : Visibility.Collapsed;
            BtnEditEmployeePopup.Visibility = CanUpdate ? Visibility.Visible : Visibility.Collapsed;
            BtnDeleteEmployee.Visibility = CanDelete ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task RefreshAllMasterDataAsync()
        {
            _isRefreshingData = true;

            int? prevFilterBranch = CmbFilterBranch.SelectedValue as int?;
            int? prevFilterCat = CmbFilterCategory.SelectedValue as int?;

            var branches = await _service.GetBranchesAsync();
            var categories = await _service.GetCategoriesAsync();
            var jobs = await _service.GetJobsAsync();
            var jobTypes = await _service.GetJobTypesAsync();
            var evalGroups = await _service.GetEvaluatorGroupsAsync();

            GridBranches.ItemsSource = branches;
            GridCategoriesHierarchy.ItemsSource = categories;
            GridJobs.ItemsSource = jobs;
            GridJobTypes.ItemsSource = jobTypes;

            GridEvaluatorGroups.ItemsSource = evalGroups;
            GridCategoriesWeights.ItemsSource = categories;

            CmbFilterBranch.ItemsSource = branches;
            if (prevFilterBranch.HasValue && branches.Any(b => b.BranchID == prevFilterBranch.Value))
                CmbFilterBranch.SelectedValue = prevFilterBranch.Value;

            CmbFilterCategory.ItemsSource = categories;
            if (prevFilterCat.HasValue && categories.Any(c => c.CategoryID == prevFilterCat.Value))
                CmbFilterCategory.SelectedValue = prevFilterCat.Value;

            _isRefreshingData = false;

            await RefreshEmployeesGridAsync();
        }

        private async Task RefreshEmployeesGridAsync()
        {
            if (_isRefreshingData || !IsLoaded || GridEmployees == null || TxtEmployeesCountBadge == null) return;

            string search = TxtSearchEmployee?.Text?.Trim() ?? string.Empty;
            int? filterBranch = CmbFilterBranch?.SelectedValue is int bId && bId > 0 ? bId : null;
            int? filterCategory = CmbFilterCategory?.SelectedValue is int cId && cId > 0 ? cId : null;
            string statusFilter = (CmbFilterStatus?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ALL";

            var employees = await _service.GetEmployeesAsync(search, filterBranch, filterCategory, statusFilter);
            GridEmployees.ItemsSource = employees;
            TxtEmployeesCountBadge.Text = $"إجمالي الموظفين: {employees.Count}";
        }

        // =========================================================================
        // 1. الفروع (Branches) — نوافذ منبثقة (إنشاء فرع / تعديل / حذف)
        // =========================================================================
        private async void BtnCreateBranch_Click(object sender, RoutedEventArgs e)
        {
            var dlg = HierarchyItemDialog.ForBranch(null);
            dlg.Owner = Window.GetWindow(this);
            if (dlg.ShowDialog() == true)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        private async void BtnEditBranch_Click(object sender, RoutedEventArgs e)
        {
            if (GridBranches.SelectedItem is not BranchDto selected)
            {
                MessageBox.Show("يرجى تحديد فرع من الجدول لتعديله.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = HierarchyItemDialog.ForBranch(selected);
            dlg.Owner = Window.GetWindow(this);
            if (dlg.ShowDialog() == true)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        private void GridBranches_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (CanUpdate && GridBranches.SelectedItem is BranchDto)
            {
                BtnEditBranch_Click(sender, e);
            }
        }

        private async void BtnDeleteBranch_Click(object sender, RoutedEventArgs e)
        {
            if (GridBranches.SelectedItem is not BranchDto selected)
            {
                MessageBox.Show("يرجى تحديد فرع من الجدول لحذفه.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show($"هل أنت متأكد من حذف الفرع ({selected.BranchName})؟", "تأكيد الحذف", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            var (isSuccess, msg) = await _service.DeleteBranchAsync(selected.BranchID);
            MessageBox.Show(msg, isSuccess ? "تم الحذف" : "تنبيه", MessageBoxButton.OK, isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (isSuccess)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        // =========================================================================
        // 2. الفئات (Categories) — نوافذ منبثقة (إنشاء فئة / تعديل / حذف)
        // =========================================================================
        private async void BtnCreateCategory_Click(object sender, RoutedEventArgs e)
        {
            var dlg = HierarchyItemDialog.ForCategory(null);
            dlg.Owner = Window.GetWindow(this);
            if (dlg.ShowDialog() == true)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        private async void BtnEditCategory_Click(object sender, RoutedEventArgs e)
        {
            if (GridCategoriesHierarchy.SelectedItem is not CategoryDto selected)
            {
                MessageBox.Show("يرجى تحديد فئة من الجدول لتعديلها.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = HierarchyItemDialog.ForCategory(selected);
            dlg.Owner = Window.GetWindow(this);
            if (dlg.ShowDialog() == true)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        private void GridCategoriesHierarchy_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (CanUpdate && GridCategoriesHierarchy.SelectedItem is CategoryDto)
            {
                BtnEditCategory_Click(sender, e);
            }
        }

        private async void BtnDeleteCategoryBasic_Click(object sender, RoutedEventArgs e)
        {
            if (GridCategoriesHierarchy.SelectedItem is not CategoryDto selected)
            {
                MessageBox.Show("يرجى تحديد فئة من الجدول لحذفها.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show($"هل أنت متأكد من حذف الفئة ({selected.CategoryName})؟", "تأكيد الحذف", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            var (isSuccess, msg) = await _service.DeleteCategoryAsync(selected.CategoryID);
            MessageBox.Show(msg, isSuccess ? "تم الحذف" : "تنبيه", MessageBoxButton.OK, isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (isSuccess)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        // =========================================================================
        // 4. الوظائف التابعة للفئات (Jobs) — نافذة قائمة متعددة + تعديل + حذف
        // =========================================================================
        private async void BtnCreateJob_Click(object sender, RoutedEventArgs e)
        {
            var cats = await _service.GetCategoriesAsync();
            if (cats.Count == 0)
            {
                MessageBox.Show("يرجى إنشاء فئة واحدة على الأقل أولاً قبل إنشاء الوظائف.", "تنبيه خطوة سابقة", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int? preselectedCat = (GridCategoriesHierarchy.SelectedItem as CategoryDto)?.CategoryID;
            var dlg = new HierarchyBatchListDialog(BatchHierarchyMode.JobsForCategory, preselectedCat)
            {
                Owner = Window.GetWindow(this)
            };

            if (dlg.ShowDialog() == true)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        private async void BtnEditJob_Click(object sender, RoutedEventArgs e)
        {
            if (GridJobs.SelectedItem is not JobDto selected)
            {
                MessageBox.Show("يرجى تحديد وظيفة من الجدول لتعديلها.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = HierarchyItemDialog.ForJobEdit(selected);
            dlg.Owner = Window.GetWindow(this);
            if (dlg.ShowDialog() == true)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        private void GridJobs_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (CanUpdate && GridJobs.SelectedItem is JobDto)
            {
                BtnEditJob_Click(sender, e);
            }
        }

        private async void BtnDeleteJob_Click(object sender, RoutedEventArgs e)
        {
            if (GridJobs.SelectedItem is not JobDto selected)
            {
                MessageBox.Show("يرجى تحديد وظيفة من الجدول لحذفها.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show($"هل أنت متأكد من حذف الوظيفة ({selected.JobTitle})؟", "تأكيد الحذف", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            var (isSuccess, msg) = await _service.DeleteJobAsync(selected.JobID);
            MessageBox.Show(msg, isSuccess ? "تم الحذف" : "تنبيه", MessageBoxButton.OK, isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (isSuccess)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        // =========================================================================
        // 5. أنواع الوظائف التابعة للوظيفة (JobTypes) — نافذة قائمة متعددة + تعديل + حذف
        // =========================================================================
        private async void BtnCreateJobType_Click(object sender, RoutedEventArgs e)
        {
            var jobs = await _service.GetJobsAsync();
            if (jobs.Count == 0)
            {
                MessageBox.Show("يرجى إنشاء وظيفة واحدة على الأقل أولاً قبل إنشاء أنواع الوظائف.", "تنبيه خطوة سابقة", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int? preselectedJob = (GridJobs.SelectedItem as JobDto)?.JobID;
            var dlg = new HierarchyBatchListDialog(BatchHierarchyMode.JobTypesForJob, preselectedJob)
            {
                Owner = Window.GetWindow(this)
            };

            if (dlg.ShowDialog() == true)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        private async void BtnEditJobType_Click(object sender, RoutedEventArgs e)
        {
            if (GridJobTypes.SelectedItem is not JobTypeDto selected)
            {
                MessageBox.Show("يرجى تحديد نوع وظيفة من الجدول لتعديله.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = HierarchyItemDialog.ForJobTypeEdit(selected);
            dlg.Owner = Window.GetWindow(this);
            if (dlg.ShowDialog() == true)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        private void GridJobTypes_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (CanUpdate && GridJobTypes.SelectedItem is JobTypeDto)
            {
                BtnEditJobType_Click(sender, e);
            }
        }

        private async void BtnDeleteJobType_Click(object sender, RoutedEventArgs e)
        {
            if (GridJobTypes.SelectedItem is not JobTypeDto selected)
            {
                MessageBox.Show("يرجى تحديد نوع وظيفة من الجدول لحذفه.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show($"هل أنت متأكد من حذف نوع الوظيفة ({selected.JobTypeName})؟", "تأكيد الحذف", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            var (isSuccess, msg) = await _service.DeleteJobTypeAsync(selected.JobTypeID);
            MessageBox.Show(msg, isSuccess ? "تم الحذف" : "تنبيه", MessageBoxButton.OK, isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (isSuccess)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        // =========================================================================
        // 6. مجموعات المقيمين + استيراد المقيمين من Excel + تخصيص درجات الـ 100 للفئات
        // =========================================================================
        private async void BtnCreateEvaluatorGroup_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new EvaluatorGroupDialog(null)
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() == true)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        private async void BtnEditEvaluatorGroup_Click(object sender, RoutedEventArgs e)
        {
            if (GridEvaluatorGroups.SelectedItem is not EvaluatorGroupDto selected)
            {
                MessageBox.Show("يرجى تحديد مجموعة مقيمين من الجدول لتعديلها.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new EvaluatorGroupDialog(selected)
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() == true)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        private void GridEvaluatorGroups_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (CanUpdate && GridEvaluatorGroups.SelectedItem is EvaluatorGroupDto)
            {
                BtnEditEvaluatorGroup_Click(sender, e);
            }
        }

        private async void BtnDeleteEvaluatorGroup_Click(object sender, RoutedEventArgs e)
        {
            if (GridEvaluatorGroups.SelectedItem is not EvaluatorGroupDto selected)
            {
                MessageBox.Show("يرجى تحديد مجموعة مقيمين من الجدول لحذفها.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show($"هل أنت متأكد من حذف المجموعة ({selected.GroupName})؟", "تأكيد الحذف", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            var (isSuccess, msg) = await _service.DeleteEvaluatorGroupAsync(selected.GroupID);
            MessageBox.Show(msg, isSuccess ? "تم الحذف" : "تنبيه", MessageBoxButton.OK, isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (isSuccess)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        private async void BtnDownloadEvaluatorsTemplate_Click(object sender, RoutedEventArgs e)
        {
            var sfd = new SaveFileDialog
            {
                Title = "حفظ قالب إكسل لبيانات المقيمين وأوزان الفئات",
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                FileName = "قالب_استيراد_المقيمين_وأوزان_الفئات.xlsx"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    BtnDownloadEvaluatorsTemplate.IsEnabled = false;
                    await _service.GenerateEvaluatorsExcelTemplateAsync(sfd.FileName);
                    BtnDownloadEvaluatorsTemplate.IsEnabled = true;

                    MessageBox.Show(
                        $"تم حفظ قالب المقيمين وأوزان الفئات بنجاح في المسار:\n{sfd.FileName}\n\n" +
                        "الترتيب: العمود 1 لاسم الفئة، الصف 1 لأسماء المجموعات، الصف 2 لأسماء المقيمين، ومن الصف 3 درجات المجموعات لكل فئة (مجموع الفئة = 100).",
                        "تم حفظ قالب المقيمين",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    BtnDownloadEvaluatorsTemplate.IsEnabled = true;
                    MessageBox.Show($"تعذر حفظ الملف: {ex.Message}", "خطأ", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnImportEvaluatorsExcel_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Title = "اختر ملف بيانات المقيمين وأوزان الفئات (Excel أو CSV)",
                Filter = "Excel / CSV Files (*.xlsx;*.csv)|*.xlsx;*.csv"
            };

            if (ofd.ShowDialog() == true)
            {
                BtnImportEvaluatorsExcel.IsEnabled = false;
                var (isSuccess, summary) = await _service.ImportEvaluatorsAndWeightsFromExcelAsync(ofd.FileName);
                BtnImportEvaluatorsExcel.IsEnabled = true;

                MessageBox.Show(
                    summary,
                    isSuccess ? "تقرير استيراد بيانات المقيمين وأوزان الفئات" : "تنبيه الاستيراد",
                    MessageBoxButton.OK,
                    isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);

                if (isSuccess)
                {
                    await RefreshAllMasterDataAsync();
                }
            }
        }

        private async void BtnAllocateCategoryWeights_Click(object sender, RoutedEventArgs e)
        {
            var groups = await _service.GetEvaluatorGroupsAsync();
            if (groups.Count == 0)
            {
                MessageBox.Show("يرجى أولاً الضغط على (+ إنشاء مقيمين) أو (📥 استيراد المقيمين من Excel) لإضافة مجموعة مقيمين واحدة على الأقل قبل تخصيص الدرجات للفئات.", "تنبيه خطوة سابقة", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int initialCatId = (GridCategoriesWeights.SelectedItem as CategoryDto)?.CategoryID ?? 0;
            var dlg = new CategoryDialog(initialCatId)
            {
                Owner = Window.GetWindow(this)
            };
            dlg.ShowDialog();
            if (dlg.HasSavedChanges)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        private void GridCategoriesWeights_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if ((CanInsert || CanUpdate) && GridCategoriesWeights.SelectedItem is CategoryDto)
            {
                BtnAllocateCategoryWeights_Click(sender, e);
            }
        }

        // =========================================================================
        // 7. سجل الموظفين واستيراد وتصدير Excel بنظام المصفوفة (1 / 0)
        // =========================================================================
        private async void BtnAddEmployeePopup_Click(object sender, RoutedEventArgs e)
        {
            var categories = await _service.GetCategoriesAsync();
            if (categories.Count == 0)
            {
                MessageBox.Show("يرجى إنشاء الفئات وتخصيص درجات مقيميها (100 درجة) أولاً قبل إضافة الموظفين.", "تنبيه خطوة سابقة", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new EmployeeDialog(null)
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() == true)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        private async void BtnEditEmployeePopup_Click(object sender, RoutedEventArgs e)
        {
            if (GridEmployees.SelectedItem is not EmployeeDto selected)
            {
                MessageBox.Show("يرجى تحديد موظف من الجدول لتعديل بياناته.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new EmployeeDialog(selected)
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() == true)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        private void GridEmployees_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (CanUpdate && GridEmployees.SelectedItem is EmployeeDto)
            {
                BtnEditEmployeePopup_Click(sender, e);
            }
        }

        private async void BtnDeleteEmployee_Click(object sender, RoutedEventArgs e)
        {
            var selectedList = GridEmployees.SelectedItems.Cast<EmployeeDto>().ToList();
            if (selectedList.Count == 0)
            {
                MessageBox.Show("يرجى تحديد موظف واحد أو أكثر من الجدول لحذفه.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string confirmMsg = selectedList.Count == 1
                ? $"هل أنت متأكد من حذف الموظف ({selectedList[0].EvaluationName})؟"
                : $"هل أنت متأكد من حذف ({selectedList.Count}) موظفين محددين دفعة واحدة؟";

            var confirm = MessageBox.Show(confirmMsg, "تأكيد الحذف", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            int deletedCount = 0;
            string lastMsg = string.Empty;
            foreach (var emp in selectedList)
            {
                var (isSuccess, msg) = await _service.DeleteEmployeeAsync(emp.EmployeeID);
                lastMsg = msg;
                if (isSuccess) deletedCount++;
            }

            await RefreshAllMasterDataAsync();
            MessageBox.Show(
                selectedList.Count == 1 ? lastMsg : $"تم حذف ({deletedCount}) من أصل ({selectedList.Count}) موظفين بنجاح.",
                "تمت العملية",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private async void TxtSearchEmployee_TextChanged(object sender, TextChangedEventArgs e)
        {
            await RefreshEmployeesGridAsync();
        }

        private async void EmployeeFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await RefreshEmployeesGridAsync();
        }

        private async void BtnResetEmployeeFilters_Click(object sender, RoutedEventArgs e)
        {
            _isRefreshingData = true;
            TxtSearchEmployee.Text = string.Empty;
            CmbFilterBranch.SelectedItem = null;
            CmbFilterCategory.SelectedItem = null;
            CmbFilterStatus.SelectedIndex = 0;
            _isRefreshingData = false;

            await RefreshEmployeesGridAsync();
        }

        private async void BtnDownloadExcelTemplate_Click(object sender, RoutedEventArgs e)
        {
            var sfd = new SaveFileDialog
            {
                Title = "حفظ قالب إكسل لبيانات الموظفين والمقيمين (1 / 0)",
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                FileName = "قالب_استيراد_الموظفين_الشامل.xlsx"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    BtnDownloadExcelTemplate.IsEnabled = false;
                    await _service.GenerateEmployeesExcelTemplateAsync(sfd.FileName);
                    BtnDownloadExcelTemplate.IsEnabled = true;

                    MessageBox.Show(
                        $"تم حفظ قالب الموظفين بنظام المصفوفة بنجاح في المسار:\n{sfd.FileName}\n\n" +
                        "• الصف 1: بيانات الموظف (الأعمدة 1–8) ثم أسماء مجموعات التقييم.\n" +
                        "• الصف 2: أسماء المقيمين تحت كل مجموعة.\n" +
                        "• الصف 3 فصاعداً: بيانات الموظفين وأرقام (1 = ينتمي، 0 = لا ينتمي).",
                        "تم حفظ القالب",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    BtnDownloadExcelTemplate.IsEnabled = true;
                    MessageBox.Show($"تعذر حفظ الملف: {ex.Message}", "خطأ", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnImportExcel_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Title = "اختر ملف إكسل لبيانات الموظفين (Excel أو CSV)",
                Filter = "Excel / CSV Files (*.xlsx;*.csv)|*.xlsx;*.csv"
            };

            if (ofd.ShowDialog() == true)
            {
                BtnImportExcel.IsEnabled = false;
                var (isSuccess, summary) = await _service.ImportEmployeesFromExcelAsync(ofd.FileName);
                BtnImportExcel.IsEnabled = true;

                MessageBox.Show(summary, isSuccess ? "تقرير استيراد الموظفين" : "تنبيه الاستيراد", MessageBoxButton.OK, isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);

                if (isSuccess)
                {
                    await RefreshAllMasterDataAsync();
                }
            }
        }
    }
}
