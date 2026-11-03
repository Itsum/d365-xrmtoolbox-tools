using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace D365Tools.Shared
{
    internal static class Menus
    {
        /// <summary>"Theme" dropdown with Windows default / Light / Dark.</summary>
        public static ToolStripDropDownButton ThemeMenu(Action<ThemeMode> onChanged)
        {
            var menu = new ToolStripDropDownButton("Theme") { ToolTipText = "Light or dark appearance (saved per tool)" };
            void Add(string text, ThemeMode mode)
            {
                var item = new ToolStripMenuItem(text) { Tag = mode };
                item.Click += (s, e) => onChanged(mode);
                menu.DropDownItems.Add(item);
            }
            Add("Windows default", ThemeMode.System);
            Add("Light", ThemeMode.Light);
            Add("Dark", ThemeMode.Dark);
            menu.DropDownOpening += (s, e) =>
            {
                foreach (ToolStripMenuItem item in menu.DropDownItems)
                    item.Checked = (ThemeMode)item.Tag == UiTheme.Mode;
            };
            return menu;
        }

        /// <summary>
        /// Fills a "Languages" dropdown with the installed languages. The dropdown stays open while ticking,
        /// <paramref name="onChanged"/> receives the new selection.
        /// </summary>
        public static void FillLanguageMenu(ToolStripDropDownButton menu, EnvLanguages env, IList<int> selected, Action<List<int>> onChanged)
        {
            menu.DropDownItems.Clear();
            if (env == null || !env.All.Any())
            {
                menu.DropDownItems.Add(new ToolStripMenuItem("Connect to load the installed languages") { Enabled = false });
                menu.Text = "Languages";
                return;
            }

            menu.Text = "Languages: " + string.Join(", ", selected.Select(Languages.Name));
            foreach (var lcid in env.All)
            {
                var item = new ToolStripMenuItem(Languages.Label(lcid) + (lcid == env.Base ? "  – base language" : ""))
                {
                    Tag = lcid,
                    Checked = selected.Contains(lcid),
                    CheckOnClick = true
                };
                item.CheckedChanged += (s, e) =>
                {
                    var selection = menu.DropDownItems.OfType<ToolStripMenuItem>()
                        .Where(i => i.Tag is int && i.Checked).Select(i => (int)i.Tag).ToList();
                    if (selection.Count == 0)
                    {
                        item.Checked = true; // keep at least one language
                        return;
                    }
                    onChanged(Languages.Order(selection, env));
                    menu.Text = "Languages: " + string.Join(", ", Languages.Order(selection, env).Select(Languages.Name));
                };
                menu.DropDownItems.Add(item);
            }
            menu.DropDownItems.Add(new ToolStripSeparator());
            var reset = new ToolStripMenuItem("Reset to default (base + first additional language)");
            reset.Click += (s, e) => onChanged(env.Defaults);
            menu.DropDownItems.Add(reset);

            UiTheme.ApplyMenu(menu);
        }

        /// <summary>Keeps a dropdown open while check items are clicked.</summary>
        public static void KeepOpenOnCheck(ToolStripDropDownButton menu)
        {
            menu.DropDown.Closing += (s, e) =>
            {
                if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked && menu.DropDown.Items.OfType<ToolStripMenuItem>().Any(i => i.Selected && i.CheckOnClick))
                    e.Cancel = true;
            };
        }
    }
}
