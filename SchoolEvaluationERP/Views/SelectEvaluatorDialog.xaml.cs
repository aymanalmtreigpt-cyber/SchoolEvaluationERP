using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class SelectEvaluatorDialog : Window
    {
        private readonly TemplateService _service = new();
        private List<EvaluatorDto> _allEvaluators = new();

        public EvaluatorDto? SelectedEvaluator { get; private set; }

        public SelectEvaluatorDialog()
        {
            InitializeComponent();

            Loaded += async (_, _) =>
            {
                _allEvaluators = await _service.SearchEvaluatorsAsync();
                LstEvaluators.ItemsSource = _allEvaluators;
                if (_allEvaluators.Count > 0)
                    LstEvaluators.SelectedIndex = 0;

                TxtSearchEvaluator.Focus();
            };
        }

        private void TxtSearchEvaluator_TextChanged(object sender, TextChangedEventArgs e)
        {
            string q = TxtSearchEvaluator.Text.Trim();
            var filtered = string.IsNullOrEmpty(q)
                ? _allEvaluators
                : _allEvaluators.Where(x =>
                    x.EvaluatorName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    x.GroupName.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

            LstEvaluators.ItemsSource = filtered;
            if (filtered.Count > 0)
                LstEvaluators.SelectedIndex = 0;
        }

        private void LstEvaluators_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LstEvaluators.SelectedItem is EvaluatorDto)
            {
                BtnCreate_Click(sender, e);
            }
        }

        private void BtnCreate_Click(object sender, RoutedEventArgs e)
        {
            if (LstEvaluators.SelectedItem is not EvaluatorDto selected)
            {
                MessageBox.Show("يرجى اختيار اسم المقيم من القائمة أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SelectedEvaluator = selected;
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
