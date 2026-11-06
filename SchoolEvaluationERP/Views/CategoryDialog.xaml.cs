using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class CategoryDialog : Window
    {
        private readonly MasterDataService _service = new();
        private readonly int _initialCategoryId;
        private List<CategoryEvaluatorGroupSelectRow> _groupRows = new();
        private bool _isLoadingCategories;

        public bool HasSavedChanges { get; private set; }

        public CategoryDialog(int initialCategoryId = 0)
        {
            _initialCategoryId = initialCategoryId;
            _isLoadingCategories = true;
            InitializeComponent();
            _isLoadingCategories = false;

            Loaded += async (_, _) =>
            {
                await LoadCategoriesAsync(_initialCategoryId);
            };
        }

        private async Task LoadCategoriesAsync(int selectCategoryId)
        {
            _isLoadingCategories = true;
            var categories = await _service.GetCategoriesAsync();
            GridCategoriesList.ItemsSource = categories;
            _isLoadingCategories = false;

            if (categories.Count == 0)
            {
                TxtSelectedCategoryTitle.Text = "لا توجد فئات مسجلة بعد — أنشئ الفئات أولاً.";
                BtnSaveWeights.IsEnabled = false;
                return;
            }

            var match = selectCategoryId > 0
                ? categories.FirstOrDefault(c => c.CategoryID == selectCategoryId)
                : null;

            GridCategoriesList.SelectedItem = match ?? categories[0];
        }

        private async void GridCategoriesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingCategories) return;

            if (GridCategoriesList.SelectedItem is not CategoryDto selectedCat)
            {
                GridEvaluatorGroups.ItemsSource = null;
                return;
            }

            TxtSelectedCategoryTitle.Text = $"الفئة المحددة: {selectedCat.CategoryName}";

            _groupRows = await _service.GetCategoryEvaluatorGroupMatrixAsync(selectedCat.CategoryID);
            foreach (var row in _groupRows)
            {
                row.PropertyChanged += GroupRow_PropertyChanged;
            }

            GridEvaluatorGroups.ItemsSource = _groupRows;
            UpdateLiveTotalCounter();
        }

        private void GroupRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            UpdateLiveTotalCounter();
        }

        private void UpdateLiveTotalCounter()
        {
            decimal total = _groupRows.Where(x => x.IsSelected).Sum(x => x.WeightScore);
            decimal remaining = 100m - total;

            if (total == 100m)
            {
                CounterBanner.Background = new SolidColorBrush(Color.FromRgb(236, 253, 245));
                CounterBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(167, 243, 208));
                TxtTotalScoreStatus.Foreground = new SolidColorBrush(Color.FromRgb(4, 120, 87));
                TxtTotalScoreStatus.Text = "✔ المجموع مكتمل: 100 من 100 درجة (جاهز للحفظ)";
            }
            else if (total < 100m)
            {
                CounterBanner.Background = new SolidColorBrush(Color.FromRgb(255, 251, 235));
                CounterBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(253, 230, 138));
                TxtTotalScoreStatus.Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9));
                TxtTotalScoreStatus.Text = $"مجموع الدرجات الحالي: {total:0.##} من 100  |  المتبقي: {remaining:0.##} درجة";
            }
            else
            {
                CounterBanner.Background = new SolidColorBrush(Color.FromRgb(254, 242, 242));
                CounterBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(254, 202, 202));
                TxtTotalScoreStatus.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
                TxtTotalScoreStatus.Text = $"⚠ تجاوز الحد المسموح! المجموع الحالي: {total:0.##} من 100 (الزيادة: {Math.Abs(remaining):0.##})";
            }
        }

        private void BtnDistributeEqually_Click(object sender, RoutedEventArgs e)
        {
            var selected = _groupRows.Where(x => x.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("يرجى وضع علامة صح (اختيار) أمام مجموعات المقيمين المراد توزيع الـ 100 درجة عليها أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            decimal baseShare = Math.Floor((100m / selected.Count) * 100m) / 100m;
            decimal distributed = 0m;

            for (int i = 0; i < selected.Count; i++)
            {
                if (i == selected.Count - 1)
                {
                    selected[i].WeightScore = 100m - distributed;
                }
                else
                {
                    selected[i].WeightScore = baseShare;
                    distributed += baseShare;
                }
            }

            GridEvaluatorGroups.Items.Refresh();
            UpdateLiveTotalCounter();
        }

        private async void BtnSaveWeights_Click(object sender, RoutedEventArgs e)
        {
            if (GridCategoriesList.SelectedItem is not CategoryDto selectedCat)
            {
                MessageBox.Show("يرجى اختيار الفئة من القائمة اليمنى أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            GridEvaluatorGroups.CommitEdit(DataGridEditingUnit.Cell, true);
            GridEvaluatorGroups.CommitEdit(DataGridEditingUnit.Row, true);

            BtnSaveWeights.IsEnabled = false;
            var (isSuccess, message) = await _service.SaveCategoryEvaluatorWeightsAsync(selectedCat.CategoryID, _groupRows);
            BtnSaveWeights.IsEnabled = true;

            if (!isSuccess)
            {
                MessageBox.Show(message, "تنبيه التحقق من الدرجات", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            HasSavedChanges = true;
            MessageBox.Show(message, "تم الحفظ بنجاح", MessageBoxButton.OK, MessageBoxImage.Information);
            await LoadCategoriesAsync(selectedCat.CategoryID);
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = HasSavedChanges;
            Close();
        }
    }
}
