using System.Collections.Generic;
using System.Linq;
using System.Windows;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public enum HierarchyItemMode
    {
        Branch,
        Category,
        JobEdit,
        JobTypeEdit
    }

    public partial class HierarchyItemDialog : Window
    {
        private readonly MasterDataService _service = new();
        private readonly HierarchyItemMode _mode;
        private readonly int _editingId;
        private readonly int? _initialParentId;

        private HierarchyItemDialog(HierarchyItemMode mode, int editingId, string initialName, int? initialParentId)
        {
            _mode = mode;
            _editingId = editingId;
            _initialParentId = initialParentId;

            InitializeComponent();

            TxtItemName.Text = initialName ?? string.Empty;
            ConfigureLayout();

            Loaded += async (_, _) =>
            {
                if (_mode == HierarchyItemMode.JobEdit)
                {
                    var cats = await _service.GetCategoriesAsync();
                    CmbParent.DisplayMemberPath = "DisplayFullName";
                    CmbParent.SelectedValuePath = "CategoryID";
                    CmbParent.ItemsSource = cats;
                    if (_initialParentId.HasValue && cats.Any(c => c.CategoryID == _initialParentId.Value))
                        CmbParent.SelectedValue = _initialParentId.Value;
                    else if (cats.Count > 0)
                        CmbParent.SelectedIndex = 0;
                }
                else if (_mode == HierarchyItemMode.JobTypeEdit)
                {
                    var jobs = await _service.GetJobsAsync();
                    CmbParent.DisplayMemberPath = "DisplayWithCategory";
                    CmbParent.SelectedValuePath = "JobID";
                    CmbParent.ItemsSource = jobs;
                    if (_initialParentId.HasValue && jobs.Any(j => j.JobID == _initialParentId.Value))
                        CmbParent.SelectedValue = _initialParentId.Value;
                    else if (jobs.Count > 0)
                        CmbParent.SelectedIndex = 0;
                }

                TxtItemName.Focus();
                TxtItemName.SelectAll();
            };
        }

        public static HierarchyItemDialog ForBranch(BranchDto? branch = null) =>
            new(HierarchyItemMode.Branch, branch?.BranchID ?? 0, branch?.BranchName ?? "", null);

        public static HierarchyItemDialog ForCategory(CategoryDto? category = null) =>
            new(HierarchyItemMode.Category, category?.CategoryID ?? 0, category?.CategoryName ?? "", null);

        public static HierarchyItemDialog ForJobEdit(JobDto job) =>
            new(HierarchyItemMode.JobEdit, job.JobID, job.JobTitle, job.CategoryID);

        public static HierarchyItemDialog ForJobTypeEdit(JobTypeDto jobType) =>
            new(HierarchyItemMode.JobTypeEdit, jobType.JobTypeID, jobType.JobTypeName, jobType.JobID);

        private void ConfigureLayout()
        {
            bool isEdit = _editingId > 0;
            BtnSave.Content = isEdit ? "💾 حفظ التعديلات" : "➕ إنشاء";

            switch (_mode)
            {
                case HierarchyItemMode.Branch:
                    Title = isEdit ? "تعديل اسم الفرع" : "إنشاء فرع جديد";
                    TxtHeaderTitle.Text = Title;
                    TxtNameLabel.Text = "اسم الفرع:";
                    break;

                case HierarchyItemMode.Category:
                    Title = isEdit ? "تعديل اسم الفئة" : "إنشاء فئة جديدة";
                    TxtHeaderTitle.Text = Title;
                    PanelParentSelector.Visibility = Visibility.Collapsed;
                    TxtNameLabel.Text = "اسم الفئة:";
                    break;

                case HierarchyItemMode.JobEdit:
                    Title = "تعديل بيانات الوظيفة";
                    TxtHeaderTitle.Text = Title;
                    PanelParentSelector.Visibility = Visibility.Visible;
                    TxtParentLabel.Text = "تحديد الفئة:";
                    TxtNameLabel.Text = "اسم الوظيفة:";
                    break;

                case HierarchyItemMode.JobTypeEdit:
                    Title = "تعديل نوع الوظيفة";
                    TxtHeaderTitle.Text = Title;
                    PanelParentSelector.Visibility = Visibility.Visible;
                    TxtParentLabel.Text = "تحديد الوظيفة:";
                    TxtNameLabel.Text = "اسم نوع الوظيفة:";
                    break;
            }
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            BtnSave.IsEnabled = false;
            (bool isSuccess, string msg) result;

            switch (_mode)
            {
                case HierarchyItemMode.Branch:
                    result = await _service.SaveBranchAsync(_editingId, TxtItemName.Text);
                    break;

                case HierarchyItemMode.Category:
                    result = await _service.SaveCategoryBasicAsync(_editingId, TxtItemName.Text);
                    break;

                case HierarchyItemMode.JobEdit:
                    int catId = CmbParent.SelectedValue is int cId ? cId : 0;
                    result = await _service.SaveJobAsync(_editingId, catId, TxtItemName.Text);
                    break;

                default:
                    int jobId = CmbParent.SelectedValue is int jId ? jId : 0;
                    result = await _service.SaveJobTypeAsync(_editingId, jobId, TxtItemName.Text);
                    break;
            }

            BtnSave.IsEnabled = true;

            if (!result.isSuccess)
            {
                MessageBox.Show(result.msg, "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

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
