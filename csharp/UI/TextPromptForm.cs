using System;
using System.Drawing;
using System.Windows.Forms;

namespace OpenPixel.UI
{
    // Minimal single-line prompt so settings can collect a list of tickers
    // without adding a second window class per field.
    public sealed class TextPromptForm : Form
    {
        private readonly TextBox _input;
        private string _result;

        private TextPromptForm(string title, string description, string initial)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(18, 18, 22);
            ForeColor = Color.FromArgb(239, 237, 232);
            ClientSize = new Size(400, 132);

            var label = new Label
            {
                Text = description,
                Left = 16,
                Top = 14,
                Width = 368,
                Height = 32,
                ForeColor = Color.FromArgb(185, 182, 174)
            };
            Controls.Add(label);

            _input = new TextBox
            {
                Left = 16,
                Top = 50,
                Width = 368,
                Text = initial ?? "",
                BackColor = Color.FromArgb(30, 30, 36),
                ForeColor = Color.FromArgb(239, 237, 232),
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_input);

            var ok = new Button
            {
                Text = "Save",
                Left = 216,
                Top = 88,
                Width = 80,
                Height = 28,
                DialogResult = DialogResult.OK,
                BackColor = Color.FromArgb(60, 120, 220),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            ok.FlatAppearance.BorderSize = 0;
            Controls.Add(ok);

            var cancel = new Button
            {
                Text = "Cancel",
                Left = 304,
                Top = 88,
                Width = 80,
                Height = 28,
                DialogResult = DialogResult.Cancel,
                BackColor = Color.FromArgb(40, 40, 46),
                ForeColor = Color.FromArgb(220, 220, 226),
                FlatStyle = FlatStyle.Flat
            };
            cancel.FlatAppearance.BorderSize = 0;
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
            Shown += (_, _) => { _input.Focus(); _input.SelectAll(); };
            FormClosing += (_, e) =>
            {
                if (DialogResult == DialogResult.OK) _result = _input.Text;
            };
        }

        public static string Ask(string title, string description, string initial)
        {
            using var form = new TextPromptForm(title, description, initial);
            return form.ShowDialog() == DialogResult.OK ? form._result : null;
        }
    }
}
