using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        private List<TemplateAssignedUserDto> _allActiveUsers = new();
        private readonly ObservableCollection<TemplateAssignedUserDto> _selectedUsers = new();
        private bool _isLoadingAssignedForSelection;

        public EvaluatorDto? SelectedEvaluator { get; private set; }
        public List<int> SelectedAssignedUserIds => _selectedUsers.Select(u => u.UserID).ToList();
        public bool AssignedUsersModified { get; private set; }

        public SelectEvaluatorDialog()
        {
            InitializeComponent();
            ListSelectedUsers.ItemsSource = _selectedUsers;

            bool isAdmin = AuthService.CurrentSession?.IsAdmin == true;
            PanelAssignUsers.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;

            Loaded += async (_, _) =>
            {
                _allEvaluators = await _service.SearchEvaluatorsAsync(excludeSystemGroups: true);
                if (isAdmin)
                {
                    _allActiveUsers = await _service.GetActiveAssignableUsersAsync();
                    RefreshAssignableUsersCombo();
                }

                LstEvaluators.ItemsSource = _allEvaluators;
                if (_allEvaluators.Count > 0)
                    LstEvaluators.SelectedIndex = 0;

                TxtSearchEvaluator.Focus();
            };
        }

        private void RefreshAssignableUsersCombo()
        {
            var chosenIds = _selectedUsers.Select(u => u.UserID).ToHashSet();
            var remaining = _allActiveUsers.Where(u => !chosenIds.Contains(u.UserID)).ToList();

            CmbAssignUser.ItemsSource = remaining;
            if (remaining.Count > 0)
            {
                CmbAssignUser.SelectedIndex = 0;
                CmbAssignUser.IsEnabled = true;
                BtnAddAssignedUser.IsEnabled = true;
                TxtAssignUsersNote.Visibility = Visibility.Collapsed;
            }
            else
            {
                CmbAssignUser.SelectedItem = null;
                CmbAssignUser.IsEnabled = false;
                BtnAddAssignedUser.IsEnabled = false;
                TxtAssignUsersNote.Text = _allActiveUsers.Count == 0
                    ? "ℹ لا توجد حسابات مستخدمين عاديين نشطين حالياً (يمكنك إنشاؤها من شاشة 8. المستخدمون والصلاحيات)."
                    : "✔ تم إضافة جميع المستخدمين النشطين المتاحين.";
                TxtAssignUsersNote.Visibility = Visibility.Visible;
            }

            TxtNoSelectedUsersHint.Visibility = _selectedUsers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void LstEvaluators_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AuthService.CurrentSession?.IsAdmin != true) return;
            if (LstEvaluators.SelectedItem is not EvaluatorDto ev) return;
            if (_isLoadingAssignedForSelection) return;

            _isLoadingAssignedForSelection = true;
            try
            {
                var existing = await _service.GetAssignedUsersForEvaluatorAsync(ev.EvaluatorID);
                _selectedUsers.Clear();
                foreach (var u in existing)
                {
                    _selectedUsers.Add(u);
                }
                AssignedUsersModified = false;
                RefreshAssignableUsersCombo();
            }
            finally
            {
                _isLoadingAssignedForSelection = false;
            }
        }

        private void BtnAddAssignedUser_Click(object sender, RoutedEventArgs e)
        {
            if (CmbAssignUser.SelectedItem is not TemplateAssignedUserDto selected)
                return;

            if (_selectedUsers.Any(u => u.UserID == selected.UserID))
                return;

            _selectedUsers.Add(selected);
            AssignedUsersModified = true;
            RefreshAssignableUsersCombo();
        }

        private void BtnRemoveSelectedUser_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int userId)
            {
                var target = _selectedUsers.FirstOrDefault(u => u.UserID == userId);
                if (target != null)
                {
                    _selectedUsers.Remove(target);
                    AssignedUsersModified = true;
                    RefreshAssignableUsersCombo();
                }
            }
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
