using System.Windows;

namespace SchoolEvaluationERP.Views
{
    public partial class SmallTextPromptDialog : Window
    {
        public string EnteredText { get; private set; } = string.Empty;

        public SmallTextPromptDialog(string dialogTitle, string fieldLabel, string confirmButtonText = "إضافة", string initialValue = "")
        {
            InitializeComponent();

            Title = dialogTitle;
            TxtPromptTitle.Text = dialogTitle;
            TxtFieldLabel.Text = fieldLabel;
            BtnConfirm.Content = confirmButtonText;
            TxtInputValue.Text = initialValue ?? string.Empty;

            Loaded += (_, _) =>
            {
                TxtInputValue.Focus();
                TxtInputValue.SelectAll();
            };
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            string clean = TxtInputValue.Text.Trim();
            if (string.IsNullOrWhiteSpace(clean))
            {
                MessageBox.Show("يرجى إدخال الاسم المطلوب أولاً.", "تنبيه", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtInputValue.Focus();
                return;
            }

            EnteredText = clean;
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
