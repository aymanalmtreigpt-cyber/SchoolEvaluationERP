using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public enum BatchHierarchyMode
    {
        JobsForCategory,
        JobTypesForJob
    }

    public class BatchTextItem : INotifyPropertyChanged
    {
        private string _itemName = string.Empty;

        public string ItemName
        {
            get => _itemName;
            set
            {
                if (_itemName != value)
                {
                    _itemName = value ?? string.Empty;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? prop = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    public partial class HierarchyBatchListDialog : Window
    {
        private readonly MasterDataService _service = new();
        private readonly BatchHierarchyMode _mode;
        private readonly int? _preselectedParentId;
        private readonly ObservableCollection<BatchTextItem> _items = new();

        public HierarchyBatchListDialog(BatchHierarchyMode mode, int? preselectedParentId = null)
        {
            _mode = mode;
            _preselectedParentId = preselectedParentId;

            InitializeComponent();

            ItemsContainer.ItemsSource = _items;
            _items.CollectionChanged += (_, _) => UpdateContainerHeaderAndEmptyState();

            ConfigureLabels();
            UpdateContainerHeaderAndEmptyState();

            Loaded += async (_, _) =>
            {
                if (_mode == BatchHierarchyMode.JobsForCategory)
                {
                    var categories = await _service.GetCategoriesAsync();
                    CmbParent.DisplayMemberPath = "DisplayFullName";
                    CmbParent.SelectedValuePath = "CategoryID";
                    CmbParent.ItemsSource = categories;

                    if (_preselectedParentId.HasValue && categories.Any(c => c.CategoryID == _preselectedParentId.Value))
                        CmbParent.SelectedValue = _preselectedParentId.Value;
                    else if (categories.Count > 0)
                        CmbParent.SelectedIndex = 0;
                }
                else
                {
                    var jobs = await _service.GetJobsAsync();
                    CmbParent.DisplayMemberPath = "DisplayWithCategory";
                    CmbParent.SelectedValuePath = "JobID";
                    CmbParent.ItemsSource = jobs;

                    if (_preselectedParentId.HasValue && jobs.Any(j => j.JobID == _preselectedParentId.Value))
                        CmbParent.SelectedValue = _preselectedParentId.Value;
                    else if (jobs.Count > 0)
                        CmbParent.SelectedIndex = 0;
                }
            };
        }

        private void ConfigureLabels()
        {
            if (_mode == BatchHierarchyMode.JobsForCategory)
            {
                Title = "إنشاء وظائف تابعة للفئة";
                TxtDialogTitle.Text = "إنشاء وظائف تابعة للفئة";
                TxtDialogSubtitle.Text = "حدد الفئة ثم اضغط (+ إضافة وظيفة) لإضافة وظيفة أو أكثر إلى الحاوية، ويمكنك تعديلها بالقلم أو حذفها بالسلة قبل الحفظ.";
                TxtParentSelectorLabel.Text = "تحديد الفئة:";
                BtnOpenAddSubPopup.Content = "+ إضافة وظيفة";
                BtnSaveAll.Content = "💾 حفظ الوظائف";
            }
            else
            {
                Title = "إنشاء أنواع وظائف تابعة للوظيفة";
                TxtDialogTitle.Text = "إنشاء أنواع وظائف تابعة للوظيفة";
                TxtDialogSubtitle.Text = "حدد الوظيفة ثم اضغط (+ إضافة نوع وظيفة) لإضافة التخصصات/الأنواع إلى الحاوية، ويمكنك تعديلها بالقلم أو حذفها بالسلة قبل الحفظ.";
                TxtParentSelectorLabel.Text = "تحديد الوظيفة:";
                BtnOpenAddSubPopup.Content = "+ إضافة نوع وظيفة";
                BtnSaveAll.Content = "💾 حفظ أنواع الوظائف";
            }
        }

        private void UpdateContainerHeaderAndEmptyState()
        {
            TxtEmptyContainerHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TxtContainerHeader.Text = _mode == BatchHierarchyMode.JobsForCategory
                ? $"حاوية الوظائف المضافة ({_items.Count}):"
                : $"حاوية أنواع الوظائف المضافة ({_items.Count}):";
        }

        private void BtnOpenAddSubPopup_Click(object sender, RoutedEventArgs e)
        {
            string title = _mode == BatchHierarchyMode.JobsForCategory ? "إضافة وظيفة جديدة" : "إضافة نوع وظيفة جديد";
            string label = _mode == BatchHierarchyMode.JobsForCategory ? "اسم الوظيفة (مثل: معلم، مربية):" : "اسم نوع الوظيفة (مثل: معلم لغة عربية):";

            var prompt = new SmallTextPromptDialog(title, label, "إضافة")
            {
                Owner = this
            };

            if (prompt.ShowDialog() == true)
            {
                if (_items.Any(x => string.Equals(x.ItemName.Trim(), prompt.EnteredText, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show($"الاسم ({prompt.EnteredText}) مضاف بالفعل في القائمة الحالية.", "مكرر", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                _items.Add(new BatchTextItem { ItemName = prompt.EnteredText });
            }
        }

        private void BtnEditItemInContainer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not BatchTextItem targetItem)
                return;

            string title = _mode == BatchHierarchyMode.JobsForCategory ? "تعديل اسم الوظيفة" : "تعديل اسم نوع الوظيفة";
            string label = _mode == BatchHierarchyMode.JobsForCategory ? "اسم الوظيفة:" : "اسم نوع الوظيفة:";

            var prompt = new SmallTextPromptDialog(title, label, "تعديل", targetItem.ItemName)
            {
                Owner = this
            };

            if (prompt.ShowDialog() == true)
            {
                if (_items.Any(x => !ReferenceEquals(x, targetItem) && string.Equals(x.ItemName.Trim(), prompt.EnteredText, StringComparison.OrdinalIgnoreCase)))
                {
                    MessageBox.Show($"الاسم ({prompt.EnteredText}) موجود بالفعل في القائمة.", "مكرر", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                targetItem.ItemName = prompt.EnteredText;
            }
        }

        private void BtnRemoveItemFromContainer_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is BatchTextItem targetItem)
            {
                _items.Remove(targetItem);
            }
        }

        private async void BtnSaveAll_Click(object sender, RoutedEventArgs e)
        {
            int parentId = CmbParent.SelectedValue is int pId ? pId : 0;
            var names = _items.Select(x => x.ItemName.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

            BtnSaveAll.IsEnabled = false;
            (bool isSuccess, string msg) result = _mode == BatchHierarchyMode.JobsForCategory
                ? await _service.SaveJobsBatchAsync(parentId, names)
                : await _service.SaveJobTypesBatchAsync(parentId, names);
            BtnSaveAll.IsEnabled = true;

            if (!result.isSuccess)
            {
                MessageBox.Show(result.msg, "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            MessageBox.Show(result.msg, "تم الحفظ بنجاح", MessageBoxButton.OK, MessageBoxImage.Information);
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
