using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SchoolEvaluationERP.Models;

namespace SchoolEvaluationERP.Views
{
    /// <summary>
    /// محرك التنقل التلقائي السريع (أفقي / رأسي) وإدخال الدرجات الصحيحة فقط داخل جداول التقييم الشهري.
    /// </summary>
    public static class ScoreGridNavigationHelper
    {
        public static void AttachEditorHandlers(
            DataGrid grid,
            DataGridPreparingCellForEditEventArgs e,
            int firstCriteriaColumnIndex,
            int criteriaCount,
            Func<bool> isHorizontalModeGetter,
            Action? onScoreUpdated = null)
        {
            if (e.EditingElement is not TextBox tb) return;
            if (e.Row.Item is not WeeklyScoreRowViewModel empRow) return;

            int gridColIndex = grid.Columns.IndexOf(e.Column);
            int critIndex = gridColIndex - firstCriteriaColumnIndex;
            if (critIndex < 0 || critIndex >= criteriaCount) return;

            bool isAdvancing = false;

            void TriggerAdvance()
            {
                if (isAdvancing) return;
                isAdvancing = true;

                grid.Dispatcher.BeginInvoke(new Action(() =>
                {
                    CommitAndMoveNext(grid, empRow, critIndex, firstCriteriaColumnIndex, criteriaCount, isHorizontalModeGetter());
                    onScoreUpdated?.Invoke();
                }), DispatcherPriority.Input);
            }

            tb.PreviewTextInput += (_, textArgs) =>
            {
                string norm = WeeklyScoreRowViewModel.NormalizeAsciiDigits(textArgs.Text);
                foreach (char ch in norm)
                {
                    if (!char.IsDigit(ch))
                    {
                        textArgs.Handled = true;
                        return;
                    }
                }
            };

            DataObject.AddPastingHandler(tb, (_, pasteArgs) =>
            {
                if (pasteArgs.DataObject.GetDataPresent(typeof(string)))
                {
                    string pasted = WeeklyScoreRowViewModel.NormalizeAsciiDigits((string)pasteArgs.DataObject.GetData(typeof(string)));
                    foreach (char ch in pasted.Trim())
                    {
                        if (!char.IsDigit(ch))
                        {
                            pasteArgs.CancelCommand();
                            return;
                        }
                    }
                }
                else
                {
                    pasteArgs.CancelCommand();
                }
            });

            tb.PreviewKeyDown += (_, keyArgs) =>
            {
                if (keyArgs.Key == Key.Space)
                {
                    keyArgs.Handled = true;
                    return;
                }

                if (keyArgs.Key == Key.Enter)
                {
                    keyArgs.Handled = true;
                    string clean = WeeklyScoreRowViewModel.NormalizeAsciiDigits(tb.Text.Trim());
                    int maxScore = empRow.GetMaxScoreAt(critIndex);
                    int parsed = 0;
                    if (!string.IsNullOrEmpty(clean) && int.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    {
                        parsed = v;
                    }

                    if (parsed > maxScore)
                    {
                        MessageBox.Show(
                            $"الدرجة المدخلة ({parsed}) تتجاوز الحد الأقصى المسموح لهذا العمود ({maxScore}).",
                            "تنبيه تجاوز درجة العمود",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        parsed = maxScore;
                        tb.Text = maxScore.ToString(CultureInfo.InvariantCulture);
                        tb.SelectAll();
                        return;
                    }

                    empRow.SetScoreAt(critIndex, Math.Max(0, parsed));
                    tb.Text = empRow.GetScoreAt(critIndex).ToString(CultureInfo.InvariantCulture);
                    TriggerAdvance();
                }
            };

            void EvaluateCurrentText()
            {
                if (isAdvancing) return;

                string rawText = tb.Text.Trim();
                if (string.IsNullOrEmpty(rawText)) return;

                string clean = WeeklyScoreRowViewModel.NormalizeAsciiDigits(rawText);
                if (!int.TryParse(clean, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
                    return;

                int maxScore = empRow.GetMaxScoreAt(critIndex);
                int maxDigits = Math.Max(1, maxScore.ToString(CultureInfo.InvariantCulture).Length);

                if (val > maxScore)
                {
                    MessageBox.Show(
                        $"الدرجة المدخلة ({val}) للموظف ({empRow.EvaluationName}) تتجاوز الدرجة القصوى لهذا العمود وهي ({maxScore}) درجة.",
                        "تنبيه تجاوز الدرجة القصوى",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    isAdvancing = true;
                    empRow.SetScoreAt(critIndex, maxScore);
                    tb.Text = maxScore.ToString(CultureInfo.InvariantCulture);
                    tb.SelectAll();
                    isAdvancing = false;
                    onScoreUpdated?.Invoke();
                    return;
                }

                empRow.SetScoreAt(critIndex, val);
                onScoreUpdated?.Invoke();

                // الانتقال التلقائي الفوري في حالتين:
                // 1) استيفاء عدد خانات الدرجة القصوى للعمود (مثلاً رقم واحد في عمود من 5، أو رقمين مثل 20 أو 05 في عمود من 25)
                // 2) الإدخال الذكي لرقم واحد مستحيل إكماله برقم ثانٍ ضمن سقف العمود (مثل كتابة 4 في عمود درجته 25 لأن 40 > 25)
                bool reachedMaxDigits = clean.Length >= maxDigits;
                bool smartSingleDigitComplete = val > 0 && (val * 10) > maxScore;

                if (reachedMaxDigits || smartSingleDigitComplete)
                {
                    tb.Text = val.ToString(CultureInfo.InvariantCulture);
                    TriggerAdvance();
                }
            }

            tb.TextChanged += (_, _) => EvaluateCurrentText();

            if (e.EditingEventArgs is TextCompositionEventArgs)
            {
                // بدأ المستخدم الكتابة مباشرة على الخلية قبل فتح التحرير؛ نضع المؤشر في نهاية الرقم المكتوب ونفحص الانتقال الفوري
                tb.Focus();
                tb.CaretIndex = tb.Text.Length;
                EvaluateCurrentText();
            }
            else
            {
                // فتح التحرير بالماوس أو الانتقال التلقائي: نظلل القيمة الحالية ليستبدلها أول رقم يُكتب فوراً
                tb.Focus();
                tb.SelectAll();
            }
        }

        private static void CommitAndMoveNext(
            DataGrid grid,
            WeeklyScoreRowViewModel currentRow,
            int currentCritIndex,
            int firstCriteriaColumnIndex,
            int criteriaCount,
            bool isHorizontal)
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);

            int totalRows = grid.Items.Count;
            if (totalRows == 0 || criteriaCount == 0) return;

            int currentRowIndex = grid.Items.IndexOf(currentRow);
            if (currentRowIndex < 0) return;

            int nextRowIndex = -1;
            int nextCritIndex = -1;

            if (isHorizontal)
            {
                if (currentCritIndex + 1 < criteriaCount)
                {
                    nextRowIndex = currentRowIndex;
                    nextCritIndex = currentCritIndex + 1;
                }
                else if (currentRowIndex + 1 < totalRows)
                {
                    nextRowIndex = currentRowIndex + 1;
                    nextCritIndex = 0;
                }
            }
            else
            {
                if (currentRowIndex + 1 < totalRows)
                {
                    nextRowIndex = currentRowIndex + 1;
                    nextCritIndex = currentCritIndex;
                }
                else if (currentCritIndex + 1 < criteriaCount)
                {
                    nextRowIndex = 0;
                    nextCritIndex = currentCritIndex + 1;
                }
            }

            if (nextRowIndex < 0 || nextCritIndex < 0)
                return;

            int targetGridColIndex = firstCriteriaColumnIndex + nextCritIndex;
            if (targetGridColIndex < 0 || targetGridColIndex >= grid.Columns.Count)
                return;

            object targetItem = grid.Items[nextRowIndex];
            DataGridColumn targetColumn = grid.Columns[targetGridColIndex];

            grid.SelectedItem = targetItem;
            grid.ScrollIntoView(targetItem, targetColumn);
            grid.UpdateLayout();

            var cell = GetCell(grid, nextRowIndex, targetGridColIndex);
            if (cell != null)
            {
                cell.Focus();
            }

            grid.CurrentCell = new DataGridCellInfo(targetItem, targetColumn);
            grid.BeginEdit();
        }

        private static DataGridCell? GetCell(DataGrid grid, int rowIndex, int columnIndex)
        {
            var rowContainer = grid.ItemContainerGenerator.ContainerFromIndex(rowIndex) as DataGridRow;
            if (rowContainer == null)
            {
                grid.UpdateLayout();
                grid.ScrollIntoView(grid.Items[rowIndex]);
                rowContainer = grid.ItemContainerGenerator.ContainerFromIndex(rowIndex) as DataGridRow;
            }

            if (rowContainer == null) return null;

            var presenter = FindVisualChild<DataGridCellsPresenter>(rowContainer);
            if (presenter == null)
            {
                rowContainer.ApplyTemplate();
                presenter = FindVisualChild<DataGridCellsPresenter>(rowContainer);
            }

            if (presenter == null) return null;

            var cell = presenter.ItemContainerGenerator.ContainerFromIndex(columnIndex) as DataGridCell;
            if (cell == null)
            {
                grid.ScrollIntoView(rowContainer, grid.Columns[columnIndex]);
                cell = presenter.ItemContainerGenerator.ContainerFromIndex(columnIndex) as DataGridCell;
            }

            return cell;
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                    return typedChild;

                var found = FindVisualChild<T>(child);
                if (found != null)
                    return found;
            }
            return null;
        }
    }
}
