using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace D365Tools.Shared
{
    /// <summary>Coloured log at the bottom (thread-safe, re-colours itself when the theme changes).</summary>
    internal sealed class LogPanel : RichTextBox
    {
        private enum Level { Info, Success, Warn, Error }

        private readonly List<(DateTime time, string message, Level level)> _entries = new List<(DateTime, string, Level)>();

        public LogPanel()
        {
            ReadOnly = true;
            BorderStyle = BorderStyle.None;
            Font = UiTheme.Mono;
            WordWrap = false;
            DetectUrls = false;
            UiTheme.Role(this, UiRole.Alt);
        }

        public void Info(string message) => Append(message, Level.Info);
        public void Success(string message) => Append(message, Level.Success);
        public void Warn(string message) => Append(message, Level.Warn);
        public void Error(string message) => Append(message, Level.Error);

        private static Color ColorOf(Level level)
        {
            switch (level)
            {
                case Level.Success: return UiTheme.OnText;
                case Level.Warn: return UiTheme.WarnText;
                case Level.Error: return UiTheme.ErrorText;
                default: return UiTheme.TextPrimary;
            }
        }

        private void Append(string message, Level level)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => Append(message, level)));
                return;
            }
            var entry = (DateTime.Now, message, level);
            _entries.Add(entry);
            Write(entry);
            ScrollToCaret();
        }

        private void Write((DateTime time, string message, Level level) entry)
        {
            SelectionStart = TextLength;
            SelectionLength = 0;
            SelectionColor = ColorOf(entry.level);
            AppendText($"{entry.time:HH:mm:ss}  {entry.message}{Environment.NewLine}");
        }

        /// <summary>Re-writes all lines with the colours of the current theme.</summary>
        public void Recolor()
        {
            BackColor = UiTheme.SurfaceAlt;
            ForeColor = UiTheme.TextPrimary;
            Clear();
            foreach (var entry in _entries) Write(entry);
            ScrollToCaret();
        }
    }
}
