using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class EmployeeDialog : Window
    {
        private readonly MasterDataService _service = new();
        private readonly EmployeeDto _editingEmployee;

        private List<CategoryDto> _allCategories = new();
        private List<JobDto> _allJobs = new();
        private List<JobTypeDto> _allJobTypes = new();
        private List<EmployeeEvaluatorGroupSection> _groupSections = new();
        private bool _isInitializing;

        public EmployeeDialog(EmployeeDto? employeeToEdit = null)
        {
            _editingEmployee = employeeToEdit ?? new EmployeeDto();
            _isInitializing = true;
            InitializeComponent();
            _isInitializing = false;

            if (_editingEmployee.EmployeeID > 0)
            {
                TxtDialogTitle.Text = $"تعديل بيانات الموظف: {_editingEmployee.EvaluationName}";
            }

            Loaded += async (_, _) =>
            {
                await InitializeDialogAsync();
            };
        }

        private async Task InitializeDialogAsync()
        {
            _isInitializing = true;

            var branches = await _service.GetBranchesAsync();
            _allCategories = await _service.GetCategoriesAsync();
            _allJobs = await _service.GetJobsAsync();
            _allJobTypes = await _service.GetJobTypesAsync();

            CmbBranch.ItemsSource = branches;
            CmbCategory.ItemsSource = _allCategories;

            if (_editingEmployee.EmployeeID > 0)
            {
                TxtSystemName.Text = _editingEmployee.SystemName;
                TxtEvaluationName.Text = _editingEmployee.EvaluationName;
                CmbBranch.SelectedValue = _editingEmployee.BranchID;

                CmbHasIncentive.SelectedIndex = _editingEmployee.HasIncentive ? 0 : 1;
                CmbIsContinuing.SelectedIndex = _editingEmployee.IsContinuing ? 0 : 1;

                CmbCategory.SelectedValue = _editingEmployee.CategoryID;
                PopulateJobsForCategory(_editingEmployee.CategoryID, _editingEmployee.JobID);
                PopulateJobTypesForJob(_editingEmployee.JobID, _editingEmployee.JobTypeID);
            }
            else
            {
                if (branches.Count > 0) CmbBranch.SelectedIndex = 0;
                CmbHasIncentive.SelectedIndex = 0;
                CmbIsContinuing.SelectedIndex = 0;

                if (_allCategories.Count > 0)
                {
                    CmbCategory.SelectedIndex = 0;
                    int catId = _allCategories[0].CategoryID;
                    PopulateJobsForCategory(catId, null);
                }
            }

            _isInitializing = false;

            if (CmbCategory.SelectedValue is int activeCatId && activeCatId > 0)
            {
                await LoadEvaluatorGroupsForCategoryAsync(activeCatId);
            }
            else
            {
                UpdateOverallCoverageBadge();
            }

            TxtSystemName.Focus();
        }

        private void PopulateJobsForCategory(int categoryId, int? selectJobId)
        {
            var filtered = _allJobs.Where(j => j.CategoryID == categoryId).ToList();
            CmbJob.ItemsSource = filtered;

            if (selectJobId.HasValue && filtered.Any(j => j.JobID == selectJobId.Value))
            {
                CmbJob.SelectedValue = selectJobId.Value;
            }
            else if (filtered.Count > 0)
            {
                CmbJob.SelectedIndex = 0;
                PopulateJobTypesForJob(filtered[0].JobID, null);
            }
            else
            {
                CmbJobType.ItemsSource = null;
            }
        }

        private void PopulateJobTypesForJob(int jobId, int? selectJobTypeId)
        {
            var filtered = _allJobTypes.Where(jt => jt.JobID == jobId).ToList();
            CmbJobType.ItemsSource = filtered;

            if (selectJobTypeId.HasValue && filtered.Any(jt => jt.JobTypeID == selectJobTypeId.Value))
            {
                CmbJobType.SelectedValue = selectJobTypeId.Value;
            }
            else if (filtered.Count > 0)
            {
                CmbJobType.SelectedIndex = 0;
            }
        }

        private async void CmbCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (CmbCategory.SelectedValue is int catId && catId > 0)
            {
                PopulateJobsForCategory(catId, null);
                await LoadEvaluatorGroupsForCategoryAsync(catId);
            }
            else
            {
                CmbJob.ItemsSource = null;
                CmbJobType.ItemsSource = null;
                _groupSections.Clear();
                ItemsEvaluatorGroups.ItemsSource = null;
                UpdateOverallCoverageBadge();
            }
        }

        private void CmbJob_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isInitializing) return;
            if (CmbJob.SelectedValue is int jobId && jobId > 0)
            {
                PopulateJobTypesForJob(jobId, null);
            }
            else
            {
                CmbJobType.ItemsSource = null;
            }
        }

        private async Task LoadEvaluatorGroupsForCategoryAsync(int categoryId)
        {
            _groupSections = await _service.GetCategoryGroupsForEmployeeDialogAsync(categoryId, _editingEmployee.EmployeeID);
            foreach (var sec in _groupSections)
            {
                sec.WireMemberNotifications(UpdateOverallCoverageBadge);
            }

            ItemsEvaluatorGroups.ItemsSource = _groupSections;
            UpdateOverallCoverageBadge();
        }

        private void UpdateOverallCoverageBadge()
        {
            decimal totalCatWeight = _groupSections.Sum(s => s.GroupWeight);
            if (_groupSections.Count == 0 || totalCatWeight != 100m)
            {
                TxtNoCategoryWeightsWarning.Visibility = Visibility.Visible;
                BadgeCoverageStatus.Background = new SolidColorBrush(Color.FromRgb(254, 242, 242));
                BadgeCoverageStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(254, 202, 202));
                TxtOverallCoverageStatus.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                TxtOverallCoverageStatus.Text = $"تخصيص الفئة غير مكتمل ({totalCatWeight:0.##} / 100)";
                return;
            }

            TxtNoCategoryWeightsWarning.Visibility = Visibility.Collapsed;
            decimal coveredWeight = _groupSections.Where(s => s.IsGroupCovered).Sum(s => s.GroupWeight);

            if (coveredWeight == 100m)
            {
                BadgeCoverageStatus.Background = new SolidColorBrush(Color.FromRgb(236, 253, 245));
                BadgeCoverageStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(167, 243, 208));
                TxtOverallCoverageStatus.Foreground = new SolidColorBrush(Color.FromRgb(4, 120, 87));
                TxtOverallCoverageStatus.Text = "✔ التغطية مكتملة: 100 من 100 درجة";
            }
            else
            {
                BadgeCoverageStatus.Background = new SolidColorBrush(Color.FromRgb(255, 251, 235));
                BadgeCoverageStatus.BorderBrush = new SolidColorBrush(Color.FromRgb(253, 230, 138));
                TxtOverallCoverageStatus.Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9));
                TxtOverallCoverageStatus.Text = $"المغطى حالياً: {coveredWeight:0.##} من 100 درجة";
            }
        }

        private void BtnCopyFromSystemName_Click(object sender, RoutedEventArgs e)
        {
            string sys = TxtSystemName.Text.Trim();
            if (string.IsNullOrEmpty(sys)) return;

            int dashIndex = sys.IndexOf('-');
            TxtEvaluationName.Text = dashIndex > 0 ? sys[..dashIndex].Trim() : sys;
        }

        private async void BtnSaveEmployee_Click(object sender, RoutedEventArgs e)
        {
            bool hasIncentive = (CmbHasIncentive.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "False";
            bool isContinuing = (CmbIsContinuing.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "False";

            var selectedEvalIds = _groupSections
                .SelectMany(s => s.Members)
                .Where(m => m.IsSelected)
                .Select(m => m.EvaluatorID)
                .Distinct()
                .ToList();

            var dto = new EmployeeDto
            {
                EmployeeID = _editingEmployee.EmployeeID,
                SystemName = TxtSystemName.Text.Trim(),
                EvaluationName = TxtEvaluationName.Text.Trim(),
                BranchID = CmbBranch.SelectedValue is int bId ? bId : 0,
                CategoryID = CmbCategory.SelectedValue is int cId ? cId : 0,
                JobID = CmbJob.SelectedValue is int jId ? jId : 0,
                JobTypeID = CmbJobType.SelectedValue is int jtId ? jtId : 0,
                HasIncentive = hasIncentive,
                IsContinuing = isContinuing,
                SelectedEvaluatorIds = selectedEvalIds
            };

            BtnSaveEmployee.IsEnabled = false;
            var (isSuccess, message) = await _service.SaveEmployeeAsync(dto);
            BtnSaveEmployee.IsEnabled = true;

            if (!isSuccess)
            {
                MessageBox.Show(message, "تنبيه التحقق من البيانات", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(message, "تم الحفظ بنجاح", MessageBoxButton.OK, MessageBoxImage.Information);
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
