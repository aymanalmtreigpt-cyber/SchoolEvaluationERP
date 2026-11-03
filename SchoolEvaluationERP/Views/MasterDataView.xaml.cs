using System;
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
        private readonly MasterDataService _service;
        private int _selectedBranchId = 0;
        private int _selectedJobId = 0;
        private int _selectedEvaluatorId = 0;

        private bool CanInsert => AuthService.CurrentSession?.HasPermission("MasterData", "insert") == true;
        private bool CanUpdate => AuthService.CurrentSession?.HasPermission("MasterData", "update") == true;
        private bool CanDelete => AuthService.CurrentSession?.HasPermission("MasterData", "delete") == true;

        public MasterDataView()
        {
            InitializeComponent();
            _service = new MasterDataService();
            Loaded += async (_, _) =>
            {
                ApplyPermissionsToControls();
                await RefreshAllMasterDataAsync();
            };
        }

        private void ApplyPermissionsToControls()
        {
            bool canSaveSimple = CanInsert || CanUpdate;

            BtnSaveBranch.IsEnabled = canSaveSimple;
            TxtBranchName.IsEnabled = canSaveSimple;
            BtnDeleteBranch.IsEnabled = CanDelete;

            BtnSaveJob.IsEnabled = canSaveSimple;
            TxtJobTitle.IsEnabled = canSaveSimple;
            BtnDeleteJob.IsEnabled = CanDelete;

            BtnSaveEvaluator.IsEnabled = canSaveSimple;
            TxtEvaluatorName.IsEnabled = canSaveSimple;
            BtnDeleteEvaluator.IsEnabled = CanDelete;

            BtnAddCategoryPopup.Visibility = CanInsert ? Visibility.Visible : Visibility.Collapsed;
            BtnEditCategoryPopup.Visibility = CanUpdate ? Visibility.Visible : Visibility.Collapsed;
            BtnDeleteCategory.Visibility = CanDelete ? Visibility.Visible : Visibility.Collapsed;

            BtnAddEmployeePopup.Visibility = CanInsert ? Visibility.Visible : Visibility.Collapsed;
            BtnImportExcel.Visibility = CanInsert ? Visibility.Visible : Visibility.Collapsed;
            BtnEditEmployeePopup.Visibility = CanUpdate ? Visibility.Visible : Visibility.Collapsed;
            BtnDeleteEmployee.Visibility = CanDelete ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task RefreshAllMasterDataAsync()
        {
            var branches = await _service.GetBranchesAsync();
            var jobs = await _service.GetJobsAsync();
            var evaluators = await _service.GetEvaluatorsAsync();
            var categories = await _service.GetCategoriesAsync();

            GridBranches.ItemsSource = branches;
            GridJobs.ItemsSource = jobs;
            GridEvaluators.ItemsSource = evaluators;
            GridCategories.ItemsSource = categories;

            CmbFilterBranch.ItemsSource = branches;

            await RefreshEmployeesGridAsync();
        }

        private async Task RefreshEmployeesGridAsync()
        {
            string search = TxtSearchEmployee.Text.Trim();
            int? filterBranch = CmbFilterBranch.SelectedValue is int bId && bId > 0 ? bId : null;
            var employees = await _service.GetEmployeesAsync(search, filterBranch);
            GridEmployees.ItemsSource = employees;
            TxtEmployeesCountBadge.Text = $"إجمالي الموظفين: {employees.Count}";
        }

        // =========================================================================
        // 1. أحداث الفروع (Branches)
        // =========================================================================
        private void GridBranches_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GridBranches.SelectedItem is BranchDto b)
            {
                _selectedBranchId = b.BranchID;
                TxtBranchName.Text = b.BranchName;
                BtnSaveBranch.Content = "تحديث";
            }
            else
            {
                _selectedBranchId = 0;
                TxtBranchName.Text = string.Empty;
                BtnSaveBranch.Content = "حفظ";
            }
        }

        private async void BtnSaveBranch_Click(object sender, RoutedEventArgs e)
        {
            var (isSuccess, msg) = await _service.SaveBranchAsync(_selectedBranchId, TxtBranchName.Text);
            if (!isSuccess)
            {
                MessageBox.Show(msg, "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _selectedBranchId = 0;
            TxtBranchName.Text = string.Empty;
            BtnSaveBranch.Content = "حفظ";
            await RefreshAllMasterDataAsync();
        }

        private async void BtnDeleteBranch_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedBranchId <= 0)
            {
                MessageBox.Show("يرجى تحديد فرع من الجدول لحذفه.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var (isSuccess, msg) = await _service.DeleteBranchAsync(_selectedBranchId);
            MessageBox.Show(msg, isSuccess ? "تم الحذف" : "تنبيه", MessageBoxButton.OK, isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (isSuccess)
            {
                _selectedBranchId = 0;
                TxtBranchName.Text = string.Empty;
                BtnSaveBranch.Content = "حفظ";
                await RefreshAllMasterDataAsync();
            }
        }

        // =========================================================================
        // 2. أحداث الوظائف (Jobs)
        // =========================================================================
        private void GridJobs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GridJobs.SelectedItem is JobDto j)
            {
                _selectedJobId = j.JobID;
                TxtJobTitle.Text = j.JobTitle;
                BtnSaveJob.Content = "تحديث";
            }
            else
            {
                _selectedJobId = 0;
                TxtJobTitle.Text = string.Empty;
                BtnSaveJob.Content = "حفظ";
            }
        }

        private async void BtnSaveJob_Click(object sender, RoutedEventArgs e)
        {
            var (isSuccess, msg) = await _service.SaveJobAsync(_selectedJobId, TxtJobTitle.Text);
            if (!isSuccess)
            {
                MessageBox.Show(msg, "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _selectedJobId = 0;
            TxtJobTitle.Text = string.Empty;
            BtnSaveJob.Content = "حفظ";
            await RefreshAllMasterDataAsync();
        }

        private async void BtnDeleteJob_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedJobId <= 0)
            {
                MessageBox.Show("يرجى تحديد وظيفة من الجدول لحذفها.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var (isSuccess, msg) = await _service.DeleteJobAsync(_selectedJobId);
            MessageBox.Show(msg, isSuccess ? "تم الحذف" : "تنبيه", MessageBoxButton.OK, isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (isSuccess)
            {
                _selectedJobId = 0;
                TxtJobTitle.Text = string.Empty;
                BtnSaveJob.Content = "حفظ";
                await RefreshAllMasterDataAsync();
            }
        }

        // =========================================================================
        // 3. أحداث المقيمين (Evaluators)
        // =========================================================================
        private void GridEvaluators_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GridEvaluators.SelectedItem is EvaluatorDto ev)
            {
                _selectedEvaluatorId = ev.EvaluatorID;
                TxtEvaluatorName.Text = ev.EvaluatorName;
                BtnSaveEvaluator.Content = "تحديث";
            }
            else
            {
                _selectedEvaluatorId = 0;
                TxtEvaluatorName.Text = string.Empty;
                BtnSaveEvaluator.Content = "حفظ";
            }
        }

        private async void BtnSaveEvaluator_Click(object sender, RoutedEventArgs e)
        {
            var (isSuccess, msg) = await _service.SaveEvaluatorAsync(_selectedEvaluatorId, TxtEvaluatorName.Text);
            if (!isSuccess)
            {
                MessageBox.Show(msg, "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _selectedEvaluatorId = 0;
            TxtEvaluatorName.Text = string.Empty;
            BtnSaveEvaluator.Content = "حفظ";
            await RefreshAllMasterDataAsync();
        }

        private async void BtnDeleteEvaluator_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedEvaluatorId <= 0)
            {
                MessageBox.Show("يرجى تحديد مقيم من الجدول لحذفه.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var (isSuccess, msg) = await _service.DeleteEvaluatorAsync(_selectedEvaluatorId);
            MessageBox.Show(msg, isSuccess ? "تم الحذف" : "تنبيه", MessageBoxButton.OK, isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (isSuccess)
            {
                _selectedEvaluatorId = 0;
                TxtEvaluatorName.Text = string.Empty;
                BtnSaveEvaluator.Content = "حفظ";
                await RefreshAllMasterDataAsync();
            }
        }

        // =========================================================================
        // 4. أحداث نافذة الفئات المنبثقة (Category Popup - 100 Points Rule)
        // =========================================================================
        private async void BtnAddCategoryPopup_Click(object sender, RoutedEventArgs e)
        {
            var evaluators = await _service.GetEvaluatorsAsync();
            if (evaluators.Count == 0)
            {
                MessageBox.Show("يرجى إضافة أسماء المقيمين في بطاقة (أسماء المقيمين والمسؤولين) أولاً حتى تتمكن من اختيارهم وتوزيع الـ 100 درجة عليهم.", "تنبيه خطوة سابقة", MessageBoxButton.OK, MessageBoxImage.Information);
                TxtEvaluatorName.Focus();
                return;
            }

            var dlg = new CategoryDialog(0)
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() == true)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        private async void BtnEditCategoryPopup_Click(object sender, RoutedEventArgs e)
        {
            if (GridCategories.SelectedItem is not CategoryDto selected)
            {
                MessageBox.Show("يرجى تحديد فئة من جدول الفئات لتعديلها.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new CategoryDialog(selected.CategoryID, selected.CategoryName, selected.BranchID)
            {
                Owner = Window.GetWindow(this)
            };
            if (dlg.ShowDialog() == true)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        private void GridCategories_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (CanUpdate && GridCategories.SelectedItem is CategoryDto)
            {
                BtnEditCategoryPopup_Click(sender, e);
            }
        }

        private async void BtnDeleteCategory_Click(object sender, RoutedEventArgs e)
        {
            if (GridCategories.SelectedItem is not CategoryDto selected)
            {
                MessageBox.Show("يرجى تحديد فئة من الجدول لحذفها.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show($"هل أنت متأكد من حذف الفئة ({selected.DisplayFullName})؟", "تأكيد الحذف", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            var (isSuccess, msg) = await _service.DeleteCategoryAsync(selected.CategoryID);
            MessageBox.Show(msg, isSuccess ? "تم الحذف" : "تنبيه", MessageBoxButton.OK, isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (isSuccess)
            {
                await RefreshAllMasterDataAsync();
            }
        }

        // =========================================================================
        // 5. أحداث الموظفين وملفات الإكسل (Employees Popup & Excel Import/Export)
        // =========================================================================
        private async void BtnAddEmployeePopup_Click(object sender, RoutedEventArgs e)
        {
            var categories = await _service.GetCategoriesAsync();
            if (categories.Count == 0)
            {
                MessageBox.Show("يرجى إنشاء فئة واحدة على الأقل (في التبويب الأول) وتحديد مقيميها الـ 100 درجة قبل إضافة الموظفين.", "تنبيه خطوة سابقة", MessageBoxButton.OK, MessageBoxImage.Information);
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
            var selectedList = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Cast<EmployeeDto>(GridEmployees.SelectedItems));
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

        private async void CmbFilterBranch_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            await RefreshEmployeesGridAsync();
        }

        private async void BtnResetEmployeeFilters_Click(object sender, RoutedEventArgs e)
        {
            TxtSearchEmployee.Text = string.Empty;
            CmbFilterBranch.SelectedItem = null;
            await RefreshEmployeesGridAsync();
        }

        private void BtnDownloadExcelTemplate_Click(object sender, RoutedEventArgs e)
        {
            var sfd = new SaveFileDialog
            {
                Title = "حفظ قالب إكسل لبيانات الموظفين",
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                FileName = "قالب_استيراد_الموظفين.xlsx"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    _service.GenerateEmployeesExcelTemplate(sfd.FileName);
                    MessageBox.Show($"تم حفظ قالب الإكسل بنجاح في المسار:\n{sfd.FileName}\n\nيمكنك تعبئة أسماء الموظفين فيه ثم رفعه عبر زر (📥 استيراد من Excel).", "تم حفظ القالب", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"تعذر حفظ الملف: {ex.Message}", "خطأ", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnImportExcel_Click(object sender, RoutedEventArgs e)
        {
            var ofd = new OpenFileDialog
            {
                Title = "اختر ملف إكسل لبيانات الموظفين",
                Filter = "Excel Files (*.xlsx)|*.xlsx"
            };

            if (ofd.ShowDialog() == true)
            {
                BtnImportExcel.IsEnabled = false;
                var (isSuccess, summary) = await _service.ImportEmployeesFromExcelAsync(ofd.FileName);
                BtnImportExcel.IsEnabled = true;

                MessageBox.Show(summary, isSuccess ? "تقرير استيراد ملف الإكسل" : "تنبيه الاستيراد", MessageBoxButton.OK, isSuccess ? MessageBoxImage.Information : MessageBoxImage.Warning);

                if (isSuccess)
                {
                    await RefreshAllMasterDataAsync();
                }
            }
        }
    }
}

