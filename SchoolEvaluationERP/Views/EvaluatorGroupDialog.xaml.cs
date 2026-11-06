using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SchoolEvaluationERP.Models;
using SchoolEvaluationERP.Services;

namespace SchoolEvaluationERP.Views
{
    public class EditableGroupMemberRow : INotifyPropertyChanged
    {
        private string _evaluatorName = string.Empty;

        public int EvaluatorID { get; set; }

        public string EvaluatorName
        {
            get => _evaluatorName;
            set
            {
                if (_evaluatorName != value)
                {
                    _evaluatorName = value ?? string.Empty;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class EvaluatorGroupDialog : Window
    {
        private readonly MasterDataService _service = new();
        private readonly int _editingGroupId;
        private readonly ObservableCollection<EditableGroupMemberRow> _members = new();

        public EvaluatorGroupDialog(EvaluatorGroupDto? groupToEdit = null)
        {
            _editingGroupId = groupToEdit?.GroupID ?? 0;
            InitializeComponent();

            GridMembers.ItemsSource = _members;
            _members.CollectionChanged += (_, _) => UpdateGroupRuleBanner();

            if (groupToEdit != null && groupToEdit.GroupID > 0)
            {
                TxtDialogTitle.Text = $"تعديل مجموعة المقيمين: {groupToEdit.GroupName}";
                TxtGroupName.Text = groupToEdit.GroupName;
                foreach (var mem in groupToEdit.Members)
                {
                    _members.Add(new EditableGroupMemberRow
                    {
                        EvaluatorID = mem.EvaluatorID,
                        EvaluatorName = mem.EvaluatorName
                    });
                }
            }

            UpdateGroupRuleBanner();

            Loaded += (_, _) =>
            {
                TxtGroupName.Focus();
                TxtGroupName.SelectAll();
            };
        }

        private void UpdateGroupRuleBanner()
        {
            int count = _members.Count;
            if (count == 0)
            {
                BannerGroupRule.Background = new SolidColorBrush(Color.FromRgb(255, 251, 235));
                BannerGroupRule.BorderBrush = new SolidColorBrush(Color.FromRgb(253, 230, 138));
                TxtGroupRuleHint.Foreground = new SolidColorBrush(Color.FromRgb(180, 83, 9));
                TxtGroupRuleHint.Text = "⚠ لم يتم إدراج أسماء بعد — أضف اسماً واحداً (مسؤول عام) أو عدة أسماء (مجموعة مقيمين).";
            }
            else if (count == 1)
            {
                BannerGroupRule.Background = new SolidColorBrush(Color.FromRgb(236, 253, 245));
                BannerGroupRule.BorderBrush = new SolidColorBrush(Color.FromRgb(167, 243, 208));
                TxtGroupRuleHint.Foreground = new SolidColorBrush(Color.FromRgb(4, 120, 87));
                TxtGroupRuleHint.Text = "👤 مقيم واحد في المجموعة: يعتبر مسؤولاً تلقائياً عن الفئة بالكامل في جميع الفروع بدون الحاجة لتحديده يدوياً لكل موظف.";
            }
            else
            {
                BannerGroupRule.Background = new SolidColorBrush(Color.FromRgb(239, 246, 255));
                BannerGroupRule.BorderBrush = new SolidColorBrush(Color.FromRgb(191, 219, 254));
                TxtGroupRuleHint.Foreground = new SolidColorBrush(Color.FromRgb(30, 64, 175));
                TxtGroupRuleHint.Text = $"👥 مجموعة مقيمين ({count} مقيمين): عند إضافة موظف تحدد من يقيمه من هذه المجموعة، وتقسم درجة المجموعة بالتساوي على المختارين ويؤخذ متوسطهم.";
            }
        }

        private void TxtNewMemberName_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                AddPendingMemberFromTextBox();
            }
        }

        private void BtnAddMember_Click(object sender, RoutedEventArgs e)
        {
            AddPendingMemberFromTextBox();
        }

        private void AddPendingMemberFromTextBox()
        {
            string name = TxtNewMemberName.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
                return;

            if (_members.Any(m => string.Equals(m.EvaluatorName.Trim(), name, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show($"الاسم ({name}) مضاف بالفعل في هذه المجموعة.", "مكرر", MessageBoxButton.OK, MessageBoxImage.Information);
                TxtNewMemberName.SelectAll();
                TxtNewMemberName.Focus();
                return;
            }

            _members.Add(new EditableGroupMemberRow
            {
                EvaluatorID = 0,
                EvaluatorName = name
            });

            // إذا كان اسم المجموعة فارغاً، نقترح اسم أول مقيم لتسهيل الإدخال السريع للمقيم الفردي
            if (string.IsNullOrWhiteSpace(TxtGroupName.Text) && _members.Count == 1)
            {
                TxtGroupName.Text = name;
            }

            TxtNewMemberName.Clear();
            TxtNewMemberName.Focus();
        }

        private void BtnRemoveMemberRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is EditableGroupMemberRow row)
            {
                _members.Remove(row);
            }
        }

        private async void BtnSaveGroup_Click(object sender, RoutedEventArgs e)
        {
            GridMembers.CommitEdit(DataGridEditingUnit.Cell, true);
            GridMembers.CommitEdit(DataGridEditingUnit.Row, true);

            // إذا كتب المستخدم اسماً في صندوق الإضافة ولم يضغط زر (+) ندرجه تلقائياً
            if (!string.IsNullOrWhiteSpace(TxtNewMemberName.Text))
            {
                string pending = TxtNewMemberName.Text.Trim();
                if (!_members.Any(m => string.Equals(m.EvaluatorName.Trim(), pending, StringComparison.OrdinalIgnoreCase)))
                {
                    _members.Add(new EditableGroupMemberRow { EvaluatorID = 0, EvaluatorName = pending });
                    TxtNewMemberName.Clear();
                }
            }

            string groupName = TxtGroupName.Text.Trim();
            if (string.IsNullOrWhiteSpace(groupName) && _members.Count == 1)
            {
                groupName = _members[0].EvaluatorName.Trim();
                TxtGroupName.Text = groupName;
            }

            var tupleList = _members
                .Select(m => (m.EvaluatorID, m.EvaluatorName.Trim()))
                .Where(x => !string.IsNullOrWhiteSpace(x.Item2))
                .ToList();

            BtnSaveGroup.IsEnabled = false;
            var (isSuccess, message) = await _service.SaveEvaluatorGroupAsync(_editingGroupId, groupName, tupleList);
            BtnSaveGroup.IsEnabled = true;

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
