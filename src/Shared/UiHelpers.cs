using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace D365Tools.Shared
{
    internal sealed class ActionDisposable : IDisposable
    {
        private Action _onDispose;
        public ActionDisposable(Action onDispose) { _onDispose = onDispose; }
        public void Dispose()
        {
            _onDispose?.Invoke();
            _onDispose = null;
        }
    }

    internal sealed class SaveProgress
    {
        public SaveProgress(bool success, string message)
        {
            Success = success;
            Message = message;
        }

        public bool Success { get; }
        public string Message { get; }
    }

    internal static class UiHelpers
    {
        private const int EM_SETCUEBANNER = 0x1501;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        /// <summary>Placeholder text for a TextBox (.NET Framework has no PlaceholderText).</summary>
        public static void SetPlaceholder(TextBox box, string text)
        {
            if (box.IsHandleCreated) SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text);
            else box.HandleCreated += (s, e) => SendMessage(box.Handle, EM_SETCUEBANNER, (IntPtr)1, text);
        }

        /// <summary>Header with a title on the left and a search box on the right.</summary>
        public static TableLayoutPanel Header(string title, string searchPlaceholder, out Label titleLabel, out TextBox searchBox)
        {
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 40,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(8, 6, 8, 4)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));

            titleLabel = UiTheme.Role(new Label
            {
                Text = title,
                Font = UiTheme.Title,
                Dock = DockStyle.Fill,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                AutoEllipsis = true
            }, UiRole.Title);
            searchBox = new TextBox { Dock = DockStyle.Fill, Font = UiTheme.Regular, BorderStyle = BorderStyle.FixedSingle };
            SetPlaceholder(searchBox, searchPlaceholder);

            header.Controls.Add(titleLabel, 0, 0);
            header.Controls.Add(searchBox, 1, 0);
            return header;
        }

        /// <summary>Panel = header (title + search) above a grid.</summary>
        public static Panel GridPanel(DataGridView grid, string title, string searchPlaceholder, out Label titleLabel, out TextBox searchBox)
        {
            var panel = new Panel { Dock = DockStyle.Fill };
            var header = Header(title, searchPlaceholder, out titleLabel, out searchBox);
            grid.Dock = DockStyle.Fill;
            panel.Controls.Add(grid);
            panel.Controls.Add(header);
            grid.BringToFront();
            return panel;
        }

        /// <summary>Log area with a small caption.</summary>
        public static Panel LogArea(LogPanel log)
        {
            var panel = UiTheme.Role(new Panel { Dock = DockStyle.Fill }, UiRole.Alt);
            var caption = UiTheme.Role(new Label { Text = "Log", Dock = DockStyle.Top, Height = 24, Font = UiTheme.Bold, Padding = new Padding(8, 4, 0, 0) }, UiRole.Alt);
            log.Dock = DockStyle.Fill;
            panel.Controls.Add(log);
            panel.Controls.Add(caption);
            log.BringToFront();
            return panel;
        }

        /// <summary>
        /// Sets distance and minimum sizes once the control has its real size
        /// (WinForms throws InvalidOperationException when done in the constructor).
        /// </summary>
        public static void ConfigureSplit(SplitContainer split, int panel1Min, int panel2Min, int distance)
        {
            try
            {
                var total = split.Orientation == Orientation.Vertical ? split.Width : split.Height;
                if (total <= panel1Min + panel2Min + split.SplitterWidth) return;
                split.SplitterDistance = Math.Max(panel1Min, Math.Min(distance, total - panel2Min - split.SplitterWidth));
                split.Panel1MinSize = panel1Min;
                split.Panel2MinSize = panel2Min;
            }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
        }

        /// <summary>Refills a grid and keeps scroll position, sorting and the current row.</summary>
        public static void Refill(DataGridView grid, DataGridViewRow[] rows)
        {
            var firstRow = grid.FirstDisplayedScrollingRowIndex;
            var currentTag = grid.CurrentRow?.Tag;
            var currentCol = grid.CurrentCell?.ColumnIndex ?? -1;
            var sortColumn = grid.SortedColumn;
            var sortOrder = grid.SortOrder;

            grid.SuspendLayout();
            grid.Rows.Clear();
            grid.Rows.AddRange(rows);

            if (sortColumn != null && sortOrder != SortOrder.None && grid.Columns.Contains(sortColumn))
            {
                grid.Sort(sortColumn, sortOrder == SortOrder.Ascending
                    ? System.ComponentModel.ListSortDirection.Ascending
                    : System.ComponentModel.ListSortDirection.Descending);
            }

            if (currentTag != null)
            {
                var match = grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => ReferenceEquals(r.Tag, currentTag));
                if (match != null)
                {
                    var col = currentCol >= 0 && currentCol < grid.ColumnCount && grid.Columns[currentCol].Visible ? currentCol : FirstVisibleColumn(grid);
                    if (col >= 0) grid.CurrentCell = match.Cells[col];
                }
            }

            if (firstRow >= 0 && firstRow < grid.RowCount)
            {
                try { grid.FirstDisplayedScrollingRowIndex = firstRow; } catch (InvalidOperationException) { }
            }
            grid.ResumeLayout();
        }

        private static int FirstVisibleColumn(DataGridView grid)
        {
            var c = grid.Columns.GetFirstColumn(DataGridViewElementStates.Visible);
            return c?.Index ?? -1;
        }
    }
}
