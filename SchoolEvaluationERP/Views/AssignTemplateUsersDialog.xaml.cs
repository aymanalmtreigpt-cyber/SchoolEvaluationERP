using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public partial class AssignTemplateUsersDialog : Window
    {
        private readonly TemplateService _service = new();
        private readonly int _evaluatorId;
        private readonly string _evaluatorDisplay;
        private List<TemplateAssignedUserDto> _allActiveUsers = new();
        private readonly ObservableCollection<TemplateAssignedUserDto> _assignedUsers = new();

        public AssignTemplateUsersDialog(int evaluatorId, string evaluatorDisplay)
        {
            _evaluatorId = evaluatorId;
            _evaluatorDisplay = evaluatorDisplay;
            InitializeComponent();

            TxtDialogTitle.Text = $"👤 المسؤولون عن قالب: {_evaluatorDisplay}";
            ListAssignedUsers.ItemsSource = _assignedUsers;

            Loaded += async (_, _) => await LoadUsersAsync();
        }

        private async Task LoadUsersAsync()
        {
            _allActiveUsers = await _service.GetActiveAssignableUsersAsync();
            var currentAssigned = await _service.GetAssignedUsersForEvaluatorAsync(_evaluatorId);

            _assignedUsers.Clear();
            foreach (var u in currentAssigned)
            {
                _assignedUsers.Add(u);
            }

            RefreshAvailableComboAndSummary();
        }

        private void RefreshAvailableComboAndSummary()
        {
            var assignedIds = _assignedUsers.Select(u => u.UserID).ToHashSet();
            var remaining = _allActiveUsers.Where(u => !assignedIds.Contains(u.UserID)).ToList();

            CmbAvailableUsers.ItemsSource = remaining;
            if (remaining.Count > 0)
            {
                CmbAvailableUsers.SelectedIndex = 0;
                CmbAvailableUsers.IsEnabled = true;
                BtnAddUser.IsEnabled = true;
                TxtAvailableUsersNote.Visibility = Visibility.Collapsed;
            }
            else
            {
                CmbAvailableUsers.SelectedItem = null;
                CmbAvailableUsers.IsEnabled = false;
                BtnAddUser.IsEnabled = false;
                TxtAvailableUsersNote.Text = _allActiveUsers.Count == 0
                    ? "ℹ لا توجد حسابات مستخدمين عاديين نشطين حالياً. يمكنك إنشاء حسابات للمقيمين من شاشة (8. المستخدمون والصلاحيات)."
                    : "✔ تم إضافة جميع المستخدمين النشطين المتاحين إلى القائمة.";
                TxtAvailableUsersNote.Visibility = Visibility.Visible;
            }

            TxtAssignedListHeader.Text = $"المستخدمون المسؤولون المضافون ({_assignedUsers.Count}):";
            TxtEmptyAssignedHint.Visibility = _assignedUsers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnAddUser_Click(object sender, RoutedEventArgs e)
        {
            if (CmbAvailableUsers.SelectedItem is not TemplateAssignedUserDto selected)
                return;

            if (_assignedUsers.Any(u => u.UserID == selected.UserID))
                return;

            _assignedUsers.Add(selected);
            RefreshAvailableComboAndSummary();
        }

        private void BtnRemoveAssignedUser_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is int userId)
            {
                var target = _assignedUsers.FirstOrDefault(u => u.UserID == userId);
                if (target != null)
                {
                    _assignedUsers.Remove(target);
                    RefreshAvailableComboAndSummary();
                }
            }
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            bool isSuccess;
            string message;
            BtnSave.IsEnabled = false;
            try
            {
                var userIds = _assignedUsers.Select(u => u.UserID).ToList();
                (isSuccess, message) = await _service.SaveTemplateAssignedUsersAsync(_evaluatorId, userIds);
            }
            finally
            {
                BtnSave.IsEnabled = true;
            }

            if (!isSuccess)
            {
                MessageBox.Show(message, "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
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
