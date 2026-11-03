using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class EmployeeDialog : Window
    {
        private readonly MasterDataService _service;
        private readonly EmployeeDto _editingEmployee;
        private List<CategoryDto> _allCategories = new();

        public EmployeeDialog(EmployeeDto? employeeToEdit = null)
        {
            InitializeComponent();
            _service = new MasterDataService();
            _editingEmployee = employeeToEdit ?? new EmployeeDto();

            if (_editingEmployee.EmployeeID > 0)
            {
                TxtDialogTitle.Text = $"تعديل بيانات الموظف: {_editingEmployee.EvaluationName}";
            }

            Loaded += async (_, _) =>
            {
                var branches = await _service.GetBranchesAsync();
                var jobs = await _service.GetJobsAsync();
                _allCategories = await _service.GetCategoriesAsync();

                CmbBranch.ItemsSource = branches;
                CmbJob.ItemsSource = jobs;

                if (_editingEmployee.EmployeeID > 0)
                {
                    TxtSystemName.Text = _editingEmployee.SystemName;
                    TxtEvaluationName.Text = _editingEmployee.EvaluationName;
                    CmbBranch.SelectedValue = _editingEmployee.BranchID;
                    CmbJob.SelectedValue = _editingEmployee.JobID;
                    FilterCategoriesByBranch(_editingEmployee.BranchID, _editingEmployee.CategoryID);
                }
                else
                {
                    if (branches.Count > 0) CmbBranch.SelectedIndex = 0;
                    if (jobs.Count > 0) CmbJob.SelectedIndex = 0;
                }

                TxtSystemName.Focus();
            };
        }

        private void FilterCategoriesByBranch(int branchId, int? selectCategoryId = null)
        {
            var filtered = _allCategories.Where(c => c.BranchID == branchId).ToList();
            CmbCategory.ItemsSource = filtered;

            if (selectCategoryId.HasValue && filtered.Any(c => c.CategoryID == selectCategoryId.Value))
            {
                CmbCategory.SelectedValue = selectCategoryId.Value;
            }
            else if (filtered.Count > 0)
            {
                CmbCategory.SelectedIndex = 0;
            }
            else
            {
                TxtAutoEvaluatorsPreview.Text = "لا توجد فئات مضافة لهذا الفرع بعد. يرجى إنشاء فئة لهذا الفرع أولاً.";
            }
        }

        private void CmbBranch_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbBranch.SelectedValue is int branchId)
            {
                FilterCategoriesByBranch(branchId);
            }
        }

        private void CmbCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbCategory.SelectedItem is CategoryDto selectedCat)
            {
                TxtAutoEvaluatorsPreview.Text = string.IsNullOrWhiteSpace(selectedCat.EvaluatorsSummary)
                    ? "لم يتم تحديد مقيمين لهذه الفئة."
                    : $"{selectedCat.EvaluatorsSummary}   (المجموع: {selectedCat.TotalWeights:0.##} درجة)";
            }
            else
            {
                TxtAutoEvaluatorsPreview.Text = "يرجى اختيار الفئة لعرض أسماء المقيمين وأوزانهم تلقائياً...";
            }
        }

        private void BtnCopyFromSystemName_Click(object sender, RoutedEventArgs e)
        {
            string sys = TxtSystemName.Text.Trim();
            if (string.IsNullOrEmpty(sys)) return;

            // استخراج الاسم بدون اللقب الوظيفي بعد الشرطة إن وجد (مثلاً: "ابتسام الزيادي - استاذة/" -> "ابتسام الزيادي")
            int dashIndex = sys.IndexOf('-');
            TxtEvaluationName.Text = dashIndex > 0 ? sys[..dashIndex].Trim() : sys;
        }

        private async void BtnSaveEmployee_Click(object sender, RoutedEventArgs e)
        {
            var dto = new EmployeeDto
            {
                EmployeeID = _editingEmployee.EmployeeID,
                SystemName = TxtSystemName.Text.Trim(),
                EvaluationName = TxtEvaluationName.Text.Trim(),
                BranchID = CmbBranch.SelectedValue is int bId ? bId : 0,
                JobID = CmbJob.SelectedValue is int jId ? jId : 0,
                CategoryID = CmbCategory.SelectedValue is int cId ? cId : 0
            };

            BtnSaveEmployee.IsEnabled = false;
            var (isSuccess, message) = await _service.SaveEmployeeAsync(dto);
            BtnSaveEmployee.IsEnabled = true;

            if (!isSuccess)
            {
                MessageBox.Show(message, "تنبيه التحقق من البيانات", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(message, "نجاح العملية", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
