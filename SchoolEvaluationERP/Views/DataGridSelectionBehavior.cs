using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SchoolEvaluationERP.Views
{
    /// <summary>
    /// يعمم سلوك التحديد المتعدد وإلغاء التحديد بالضغط العادي (غير السريع) مع دعم الدبل كليك السريع
    /// على جميع جداول (DataGrid) في كافة شاشات ونوافذ النظام تلقائياً.
    /// </summary>
    public static class DataGridSelectionBehavior
    {
        private static bool _isRegistered;

        private static DataGridRow? _lastNewlySelectedRow;
        private static DateTime _lastNewlySelectedTime = DateTime.MinValue;
        private static DataGridRow? _rowEligibleForSlowDeselect;
        private static Point _mouseDownPoint;
        private static bool _isDoubleClickActive;
        private static DispatcherTimer? _pendingDeselectTimer;
        private static DataGrid? _pendingDeselectGrid;
        private static DataGridRow? _pendingDeselectRow;

        public static void RegisterGlobalHandlers()
        {
            if (_isRegistered) return;
            _isRegistered = true;

            EventManager.RegisterClassHandler(
                typeof(DataGrid),
                UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(OnGridPreviewMouseLeftButtonDown));

            EventManager.RegisterClassHandler(
                typeof(DataGrid),
                UIElement.PreviewMouseLeftButtonUpEvent,
                new MouseButtonEventHandler(OnGridPreviewMouseLeftButtonUp));

            EventManager.RegisterClassHandler(
                typeof(DataGrid),
                UIElement.PreviewKeyDownEvent,
                new KeyEventHandler(OnGridPreviewKeyDown));

            EventManager.RegisterClassHandler(
                typeof(DataGrid),
                Control.MouseDoubleClickEvent,
                new MouseButtonEventHandler(OnGridMouseDoubleClick));
        }

        public static void CancelPendingDeselect()
        {
            if (_pendingDeselectTimer != null)
            {
                _pendingDeselectTimer.Stop();
                _pendingDeselectTimer = null;
            }
            _pendingDeselectGrid = null;
            _pendingDeselectRow = null;
        }

        private static void OnGridMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            CancelPendingDeselect();
            _isDoubleClickActive = true;
            _rowEligibleForSlowDeselect = null;
        }

        private static void OnGridPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGrid grid || e.OriginalSource is not DependencyObject depObj)
                return;

            // تجاهل النقرات على رؤوس الأعمدة أو أشرطة التمرير أو الأزرار أو مربعات النص أو القوائم المنسدلة
            if (FindVisualParent<DataGridColumnHeader>(depObj) != null ||
                FindVisualParent<ScrollBar>(depObj) != null ||
                FindVisualParent<ButtonBase>(depObj) != null ||
                FindVisualParent<TextBox>(depObj) != null ||
                FindVisualParent<ComboBox>(depObj) != null)
            {
                CancelPendingDeselect();
                _rowEligibleForSlowDeselect = null;
                return;
            }

            var clickedRow = FindVisualParent<DataGridRow>(depObj);
            if (clickedRow == null)
            {
                CancelPendingDeselect();
                if (!grid.IsReadOnly)
                {
                    grid.CommitEdit(DataGridEditingUnit.Cell, true);
                    grid.CommitEdit(DataGridEditingUnit.Row, true);
                }
                grid.UnselectAll();
                grid.SelectedItem = null;
                _lastNewlySelectedRow = null;
                return;
            }

            // إذا نقر المستخدم داخل خلية قابلة للكتابة والتحرير المباشر (مثل خلايا الدرجات أو نسبة الموازنة)،
            // نسمح للخلية بفتح التحرير الطبيعي دون إلغاء تحديد السطر
            var clickedCell = FindVisualParent<DataGridCell>(depObj);
            bool isEditableCell = !grid.IsReadOnly &&
                                  clickedCell != null &&
                                  clickedCell.Column != null &&
                                  !clickedCell.Column.IsReadOnly;

            if (isEditableCell)
            {
                CancelPendingDeselect();
                _rowEligibleForSlowDeselect = null;
                return;
            }

            // في حالة الدبل كليك السريع: نلغي مؤقت إلغاء التحديد ونضمن بقاء السطر محدداً باللون الأزرق ليفتح فوراً
            if (e.ClickCount >= 2)
            {
                CancelPendingDeselect();
                _isDoubleClickActive = true;
                _rowEligibleForSlowDeselect = null;
                clickedRow.IsSelected = true;
                grid.SelectedItem = clickedRow.Item;
                return;
            }

            _isDoubleClickActive = false;
            CancelPendingDeselect();

            // عند الضغط مع Ctrl أو Shift للتحديد المتعدد، نترك الجدول يعمل بطبيعته القياسية
            if (Keyboard.Modifiers != ModifierKeys.None)
            {
                _rowEligibleForSlowDeselect = null;
                return;
            }

            bool wasAlreadySelected = clickedRow.IsSelected && grid.SelectedItems.Count == 1;
            double msSinceFirstSelected = (DateTime.UtcNow - _lastNewlySelectedTime).TotalMilliseconds;

            if (wasAlreadySelected && ReferenceEquals(clickedRow, _lastNewlySelectedRow) && msSinceFirstSelected > 380)
            {
                // السطر محدد مسبقاً باللون الأزرق وضغط عليه المستخدم مرة ثانية (وليس بسرعة) -> مؤهل لإلغاء التحديد
                _rowEligibleForSlowDeselect = clickedRow;
                _mouseDownPoint = e.GetPosition(grid);
            }
            else if (wasAlreadySelected && !ReferenceEquals(clickedRow, _lastNewlySelectedRow))
            {
                _rowEligibleForSlowDeselect = clickedRow;
                _mouseDownPoint = e.GetPosition(grid);
            }
            else
            {
                // سطر جديد يتم تحديده الآن باللون الأزرق
                _rowEligibleForSlowDeselect = null;
                _lastNewlySelectedRow = clickedRow;
                _lastNewlySelectedTime = DateTime.UtcNow;
            }
        }

        private static void OnGridPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not DataGrid grid)
                return;

            if (_isDoubleClickActive || _rowEligibleForSlowDeselect == null)
            {
                _rowEligibleForSlowDeselect = null;
                return;
            }

            // إذا سحب المستخدم الماوس لتحديد عدة صفوف (Drag Selection)، لا نلغي التحديد
            Point upPoint = e.GetPosition(grid);
            if (Math.Abs(upPoint.X - _mouseDownPoint.X) > 6 ||
                Math.Abs(upPoint.Y - _mouseDownPoint.Y) > 6 ||
                grid.SelectedItems.Count > 1)
            {
                _rowEligibleForSlowDeselect = null;
                return;
            }

            var targetRow = _rowEligibleForSlowDeselect;
            _rowEligibleForSlowDeselect = null;

            // انتظار 220 ملي ثانية للتأكد أن الضغطة ليست بداية دبل كليك سريع، ثم إلغاء التحديد
            CancelPendingDeselect();
            _pendingDeselectGrid = grid;
            _pendingDeselectRow = targetRow;
            _pendingDeselectTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(220)
            };
            _pendingDeselectTimer.Tick += (_, _) =>
            {
                var g = _pendingDeselectGrid;
                var r = _pendingDeselectRow;
                CancelPendingDeselect();

                if (g != null && r != null && r.IsSelected && !r.IsEditing && g.SelectedItems.Count == 1)
                {
                    g.UnselectAll();
                    r.IsSelected = false;
                    g.SelectedItem = null;
                    _lastNewlySelectedRow = null;
                }
            };
            _pendingDeselectTimer.Start();
        }

        private static void OnGridPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is DataGrid grid && e.Key == Key.Escape)
            {
                CancelPendingDeselect();
                if (!grid.IsReadOnly)
                {
                    grid.CommitEdit(DataGridEditingUnit.Cell, true);
                    grid.CommitEdit(DataGridEditingUnit.Row, true);
                }
                grid.UnselectAll();
                grid.SelectedItem = null;
                _lastNewlySelectedRow = null;
                e.Handled = true;
            }
        }

        private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T typedParent)
                    return typedParent;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
        }
    }
}
