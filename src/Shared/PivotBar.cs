using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace D365Tools.Shared
{
    /// <summary>Flat tab header (fully themeable, unlike the WinForms TabControl).</summary>
    internal sealed class PivotBar : FlowLayoutPanel
    {
        private readonly List<Button> _buttons = new List<Button>();

        public event EventHandler SelectedIndexChanged;

        public int SelectedIndex { get; private set; }

        public PivotBar()
        {
            Dock = DockStyle.Top;
            Height = 38;
            Padding = new Padding(6, 4, 6, 0);
            WrapContents = false;
            DoubleBuffered = true;
        }

        public void AddItem(string text)
        {
            var index = _buttons.Count;
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                Height = 30,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0, 0, 4, 4),
                Padding = new Padding(6, 0, 6, 0),
                Cursor = Cursors.Hand,
                TabStop = false
            };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (s, e) => Select(index);
            _buttons.Add(button);
            Controls.Add(button);
            ApplyColors();
        }

        public void SetText(int index, string text)
        {
            if (_buttons[index].Text != text) _buttons[index].Text = text;
        }

        public void Select(int index)
        {
            var changed = index != SelectedIndex;
            SelectedIndex = index;
            ApplyColors();
            if (changed) SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ApplyColors()
        {
            BackColor = UiTheme.Surface;
            for (var i = 0; i < _buttons.Count; i++)
            {
                var b = _buttons[i];
                var selected = i == SelectedIndex;
                b.BackColor = UiTheme.Surface;
                b.ForeColor = selected ? UiTheme.Accent : UiTheme.TextSecondary;
                b.Font = selected ? UiTheme.Bold : UiTheme.Regular;
                b.FlatAppearance.MouseOverBackColor = UiTheme.HeaderBack;
                b.FlatAppearance.MouseDownBackColor = UiTheme.SelectionBack;
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var line = new SolidBrush(UiTheme.Border))
                e.Graphics.FillRectangle(line, 0, Height - 1, Width, 1);
            if (SelectedIndex < 0 || SelectedIndex >= _buttons.Count) return;
            var b = _buttons[SelectedIndex];
            using (var accent = new SolidBrush(UiTheme.Accent))
                e.Graphics.FillRectangle(accent, b.Left + 4, b.Bottom + 1, b.Width - 8, 3);
        }
    }
}
