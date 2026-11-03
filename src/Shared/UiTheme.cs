using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace D365Tools.Shared
{
    public enum ThemeMode
    {
        System,
        Light,
        Dark
    }

    /// <summary>How a control is coloured when the theme is applied.</summary>
    internal enum UiRole
    {
        Default,
        Alt,
        Secondary,
        Title,
        Explain,
        Primary,
        Splitter
    }

    internal sealed class Palette
    {
        public Color Accent, Surface, SurfaceAlt, HeaderBack, Border, Input, TextPrimary, TextSecondary, TextDisabled;
        public Color OnBack, OnText, PendingBack, MissingBack, MissingTranslationBack, ErrorBack, ErrorText, WarnText, WarnBack;
        public Color SelectionBack, ExplainBack, PrimaryText;
    }

    /// <summary>Light/dark colours, fonts and styling helpers shared by both tools.</summary>
    internal static class UiTheme
    {
        private static readonly Palette LightPalette = new Palette
        {
            Accent = Color.FromArgb(0, 120, 212),
            Surface = Color.White,
            SurfaceAlt = Color.FromArgb(250, 249, 248),
            HeaderBack = Color.FromArgb(243, 242, 241),
            Border = Color.FromArgb(225, 223, 221),
            Input = Color.White,
            TextPrimary = Color.FromArgb(32, 31, 30),
            TextSecondary = Color.FromArgb(96, 94, 92),
            TextDisabled = Color.FromArgb(161, 159, 157),
            OnBack = Color.FromArgb(223, 246, 221),
            OnText = Color.FromArgb(16, 124, 16),
            PendingBack = Color.FromArgb(255, 241, 196),
            MissingBack = Color.FromArgb(237, 235, 233),
            MissingTranslationBack = Color.FromArgb(253, 238, 239),
            ErrorBack = Color.FromArgb(253, 231, 233),
            ErrorText = Color.FromArgb(164, 38, 44),
            WarnText = Color.FromArgb(202, 80, 16),
            WarnBack = Color.FromArgb(253, 236, 222),
            SelectionBack = Color.FromArgb(222, 236, 249),
            ExplainBack = Color.FromArgb(239, 246, 252),
            PrimaryText = Color.White
        };

        private static readonly Palette DarkPalette = new Palette
        {
            Accent = Color.FromArgb(76, 160, 236),
            Surface = Color.FromArgb(32, 32, 32),
            SurfaceAlt = Color.FromArgb(40, 40, 40),
            HeaderBack = Color.FromArgb(45, 45, 48),
            Border = Color.FromArgb(62, 62, 66),
            Input = Color.FromArgb(45, 45, 48),
            TextPrimary = Color.FromArgb(243, 242, 241),
            TextSecondary = Color.FromArgb(190, 188, 186),
            TextDisabled = Color.FromArgb(121, 119, 117),
            OnBack = Color.FromArgb(28, 60, 32),
            OnText = Color.FromArgb(108, 203, 95),
            PendingBack = Color.FromArgb(84, 70, 18),
            MissingBack = Color.FromArgb(52, 52, 52),
            MissingTranslationBack = Color.FromArgb(74, 36, 42),
            ErrorBack = Color.FromArgb(96, 30, 34),
            ErrorText = Color.FromArgb(241, 112, 123),
            WarnText = Color.FromArgb(247, 163, 92),
            WarnBack = Color.FromArgb(70, 45, 20),
            SelectionBack = Color.FromArgb(38, 79, 120),
            ExplainBack = Color.FromArgb(27, 42, 56),
            PrimaryText = Color.White
        };

        private static Palette _p = LightPalette;
        private static readonly ConditionalWeakTable<Control, object> Roles = new ConditionalWeakTable<Control, object>();

        /// <summary>Raised after the theme changed – open tool instances re-apply their colours.</summary>
        public static event EventHandler Changed;

        public static ThemeMode Mode { get; private set; } = ThemeMode.System;
        public static bool IsDark { get; private set; }

        public static Color Accent => _p.Accent;
        public static Color Surface => _p.Surface;
        public static Color SurfaceAlt => _p.SurfaceAlt;
        public static Color HeaderBack => _p.HeaderBack;
        public static Color Border => _p.Border;
        public static Color Input => _p.Input;
        public static Color TextPrimary => _p.TextPrimary;
        public static Color TextSecondary => _p.TextSecondary;
        public static Color TextDisabled => _p.TextDisabled;
        public static Color OnBack => _p.OnBack;
        public static Color OnText => _p.OnText;
        public static Color PendingBack => _p.PendingBack;
        public static Color MissingBack => _p.MissingBack;
        public static Color MissingTranslationBack => _p.MissingTranslationBack;
        public static Color ErrorBack => _p.ErrorBack;
        public static Color ErrorText => _p.ErrorText;
        public static Color WarnText => _p.WarnText;
        public static Color WarnBack => _p.WarnBack;
        public static Color SelectionBack => _p.SelectionBack;
        public static Color ExplainBack => _p.ExplainBack;

        public static readonly Font Regular = new Font("Segoe UI", 9F);
        public static readonly Font Bold = new Font("Segoe UI Semibold", 9F);
        public static readonly Font Title = new Font("Segoe UI Semibold", 10.5F);
        public static readonly Font Mono = new Font("Consolas", 9F);

        public static void SetMode(ThemeMode mode)
        {
            Mode = mode;
            IsDark = mode == ThemeMode.Dark || (mode == ThemeMode.System && WindowsAppsUseDarkTheme());
            _p = IsDark ? DarkPalette : LightPalette;
            Changed?.Invoke(null, EventArgs.Empty);
        }

        private static bool WindowsAppsUseDarkTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch
            {
                return false;
            }
        }

        public static T Role<T>(T control, UiRole role) where T : Control
        {
            Roles.Remove(control);
            Roles.Add(control, role);
            return control;
        }

        private static UiRole RoleOf(Control control) => Roles.TryGetValue(control, out var r) ? (UiRole)r : UiRole.Default;

        /// <summary>Selection colour that keeps the hue (green/yellow/red), just slightly darker/lighter.</summary>
        public static Color SelectedOf(Color back) =>
            back.ToArgb() == Surface.ToArgb() ? SelectionBack : Blend(back, IsDark ? Color.White : Color.Black, IsDark ? 0.10 : 0.09);

        public static Color Blend(Color a, Color b, double amount)
        {
            int Mix(int x, int y) => (int)Math.Round(x + (y - x) * amount);
            return Color.FromArgb(Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
        }

        #region Apply

        /// <summary>Recursively applies the current palette to a control tree.</summary>
        public static void Apply(Control control)
        {
            switch (control)
            {
                case PivotBar pivot:
                    pivot.ApplyColors();
                    return;
                case DataGridView grid:
                    ApplyGrid(grid);
                    DarkScrollbars(grid);
                    foreach (Control child in grid.Controls) DarkScrollbars(child);
                    return;
                case ToolStrip strip:
                    ApplyToolStrip(strip);
                    return;
                case RichTextBox rtb:
                    rtb.BackColor = BackFor(RoleOf(rtb), Input);
                    rtb.ForeColor = TextPrimary;
                    DarkScrollbars(rtb);
                    return;
                case TextBox box:
                    box.BackColor = Input;
                    box.ForeColor = TextPrimary;
                    return;
                case LinkLabel link:
                    link.LinkColor = Accent;
                    link.ActiveLinkColor = Accent;
                    link.BackColor = Color.Transparent;
                    return;
                case Label label:
                    ApplyLabel(label);
                    return;
                case Button button:
                    button.FlatStyle = FlatStyle.Flat;
                    button.BackColor = Surface;
                    button.ForeColor = Accent;
                    button.FlatAppearance.BorderColor = Accent;
                    button.FlatAppearance.MouseOverBackColor = SelectionBack;
                    return;
                case SplitContainer split:
                    split.BackColor = Border;
                    break;
                default:
                    control.BackColor = BackFor(RoleOf(control), Surface);
                    control.ForeColor = TextPrimary;
                    DarkScrollbars(control);
                    break;
            }

            foreach (Control child in control.Controls) Apply(child);
        }

        private static Color BackFor(UiRole role, Color fallback)
        {
            switch (role)
            {
                case UiRole.Alt: return SurfaceAlt;
                case UiRole.Explain: return ExplainBack;
                case UiRole.Splitter: return Border;
                default: return fallback;
            }
        }

        private static void ApplyLabel(Label label)
        {
            var role = RoleOf(label);
            label.BackColor = role == UiRole.Alt ? SurfaceAlt : role == UiRole.Explain ? ExplainBack : Color.Transparent;
            switch (role)
            {
                case UiRole.Secondary:
                case UiRole.Alt:
                    label.ForeColor = TextSecondary;
                    break;
                case UiRole.Title:
                    label.ForeColor = TextPrimary;
                    break;
                case UiRole.Explain:
                    label.ForeColor = Accent;
                    break;
                default:
                    label.ForeColor = TextPrimary;
                    break;
            }
        }

        public static void ApplyToolStrip(ToolStrip strip)
        {
            strip.BackColor = strip is StatusStrip ? HeaderBack : Surface;
            strip.ForeColor = TextPrimary;
            strip.Renderer = new ThemedRenderer();
            foreach (ToolStripItem item in strip.Items) ApplyItem(item);
        }

        /// <summary>Re-applies colours to a (re)built menu.</summary>
        public static void ApplyMenu(ToolStripItem item) => ApplyItem(item);

        private static void ApplyItem(ToolStripItem item)
        {
            if (Equals(item.Tag, UiRole.Primary))
            {
                item.BackColor = Accent;
                item.ForeColor = _p.PrimaryText;
            }
            else if (!(item is ToolStripStatusLabel) || item.ForeColor == SystemColors.ControlText)
            {
                item.ForeColor = TextPrimary;
            }

            if (item is ToolStripTextBox tb)
            {
                tb.BackColor = Input;
                tb.ForeColor = TextPrimary;
                tb.BorderStyle = BorderStyle.FixedSingle;
            }
            if (item is ToolStripDropDownItem dd)
            {
                dd.DropDown.BackColor = SurfaceAlt;
                dd.DropDown.ForeColor = TextPrimary;
                dd.DropDown.Renderer = new ThemedRenderer();
                foreach (ToolStripItem child in dd.DropDownItems) ApplyItem(child);
            }
        }

        private static void ApplyGrid(DataGridView grid)
        {
            grid.BackgroundColor = Surface;
            grid.GridColor = Border;
            grid.DefaultCellStyle.BackColor = Surface;
            grid.DefaultCellStyle.ForeColor = TextPrimary;
            grid.DefaultCellStyle.SelectionBackColor = SelectionBack;
            grid.DefaultCellStyle.SelectionForeColor = TextPrimary;
            grid.ColumnHeadersDefaultCellStyle.BackColor = HeaderBack;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = TextPrimary;
            grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = HeaderBack;
            grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextPrimary;
            grid.Invalidate();
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string appName, string idList);

        /// <summary>Dark scrollbars on Windows 10/11 (no-op elsewhere).</summary>
        private static void DarkScrollbars(Control control)
        {
            void Set()
            {
                try { SetWindowTheme(control.Handle, IsDark ? "DarkMode_Explorer" : "Explorer", null); }
                catch { /* older Windows: keep default */ }
            }
            if (control.IsHandleCreated) Set();
            else control.HandleCreated += (s, e) => Set();
        }

        #endregion

        #region Builders

        public static void StyleGrid(DataGridView grid)
        {
            grid.BorderStyle = BorderStyle.None;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.RowHeadersVisible = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AutoGenerateColumns = false;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.ColumnHeadersDefaultCellStyle.Font = Bold;
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersHeight = 34;
            grid.DefaultCellStyle.Font = Regular;
            grid.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
            grid.RowTemplate.Height = 26;
            grid.Font = Regular;
            grid.ShowCellToolTips = true;
            typeof(DataGridView).InvokeMember("DoubleBuffered",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.SetProperty, null, grid, new object[] { true });
            ApplyGrid(grid);
        }

        public static void StyleToolStrip(ToolStrip strip)
        {
            strip.GripStyle = ToolStripGripStyle.Hidden;
            strip.Font = Regular;
            strip.Padding = new Padding(8, 4, 8, 4);
            ApplyToolStrip(strip);
        }

        public static DataGridViewTextBoxColumn TextColumn(string name, string header, int width, bool readOnly = true, bool fill = false)
        {
            var col = new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = header,
                Width = width,
                ReadOnly = readOnly,
                SortMode = DataGridViewColumnSortMode.Automatic
            };
            if (fill)
            {
                col.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
                col.MinimumWidth = width;
            }
            return col;
        }

        public static DataGridViewCheckBoxColumn CheckColumn(string name, string header, int width = 90)
        {
            return new DataGridViewCheckBoxColumn
            {
                Name = name,
                HeaderText = header,
                Width = width,
                SortMode = DataGridViewColumnSortMode.NotSortable,
                FlatStyle = FlatStyle.Flat,
                DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter }
            };
        }

        public static ToolStripButton Button(string text, string tooltip, EventHandler onClick)
        {
            var b = new ToolStripButton(text) { ToolTipText = tooltip, DisplayStyle = ToolStripItemDisplayStyle.Text };
            b.Click += onClick;
            return b;
        }

        public static ToolStripButton Toggle(string text, string tooltip, bool initial, EventHandler onChanged)
        {
            var b = new ToolStripButton((initial ? "☑ " : "☐ ") + text) { ToolTipText = tooltip, CheckOnClick = true, Checked = initial };
            b.CheckedChanged += (s, e) =>
            {
                b.Text = (b.Checked ? "☑ " : "☐ ") + text;
                onChanged?.Invoke(s, e);
            };
            return b;
        }

        public static ToolStripButton PrimaryButton(string text, string tooltip, EventHandler onClick)
        {
            var b = Button(text, tooltip, onClick);
            b.Tag = UiRole.Primary;
            b.Font = Bold;
            b.Margin = new Padding(6, 1, 0, 2);
            b.Padding = new Padding(8, 0, 8, 0);
            return b;
        }

        #endregion

        /// <summary>Renderer whose colours follow the current palette (menus, separators, arrows, checked items).</summary>
        private sealed class ThemedRenderer : ToolStripProfessionalRenderer
        {
            public ThemedRenderer() : base(new ThemedColors()) { RoundedEdges = false; }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                e.ArrowColor = e.Item?.Enabled == false ? TextDisabled : TextPrimary;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (!e.Item.Enabled) e.TextColor = TextDisabled;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                // Simple check mark that is visible in both themes
                var r = e.ImageRectangle;
                using (var pen = new Pen(Accent, 2f))
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    e.Graphics.DrawLines(pen, new[]
                    {
                        new PointF(r.Left + r.Width * 0.2f, r.Top + r.Height * 0.55f),
                        new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.75f),
                        new PointF(r.Left + r.Width * 0.8f, r.Top + r.Height * 0.3f)
                    });
                }
            }
        }

        private sealed class ThemedColors : ProfessionalColorTable
        {
            public override Color ToolStripGradientBegin => Surface;
            public override Color ToolStripGradientMiddle => Surface;
            public override Color ToolStripGradientEnd => Surface;
            public override Color ToolStripBorder => Border;
            public override Color ToolStripDropDownBackground => SurfaceAlt;
            public override Color ImageMarginGradientBegin => SurfaceAlt;
            public override Color ImageMarginGradientMiddle => SurfaceAlt;
            public override Color ImageMarginGradientEnd => SurfaceAlt;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Border;
            public override Color MenuItemSelected => SelectionBack;
            public override Color MenuItemSelectedGradientBegin => SelectionBack;
            public override Color MenuItemSelectedGradientEnd => SelectionBack;
            public override Color MenuItemPressedGradientBegin => HeaderBack;
            public override Color MenuItemPressedGradientMiddle => HeaderBack;
            public override Color MenuItemPressedGradientEnd => HeaderBack;
            public override Color ButtonSelectedHighlight => SelectionBack;
            public override Color ButtonSelectedGradientBegin => SelectionBack;
            public override Color ButtonSelectedGradientMiddle => SelectionBack;
            public override Color ButtonSelectedGradientEnd => SelectionBack;
            public override Color ButtonSelectedBorder => Border;
            public override Color ButtonCheckedGradientBegin => Surface;
            public override Color ButtonCheckedGradientMiddle => Surface;
            public override Color ButtonCheckedGradientEnd => Surface;
            public override Color ButtonCheckedHighlight => Surface;
            public override Color ButtonPressedGradientBegin => Border;
            public override Color ButtonPressedGradientMiddle => Border;
            public override Color ButtonPressedGradientEnd => Border;
            public override Color CheckBackground => SurfaceAlt;
            public override Color CheckSelectedBackground => SelectionBack;
            public override Color CheckPressedBackground => SelectionBack;
            public override Color StatusStripGradientBegin => HeaderBack;
            public override Color StatusStripGradientEnd => HeaderBack;
            public override Color SeparatorDark => Border;
            public override Color SeparatorLight => Border;
        }
    }
}
