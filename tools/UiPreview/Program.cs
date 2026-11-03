using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using D365Tools.AuditManager;
using D365Tools.Shared;
using D365Tools.TranslationManager;
using McTools.Xrm.Connection;
using XrmToolBox.Extensibility;

namespace D365Tools.UiPreview
{
    /// <summary>Screenshots with sample data (light + dark) and a no-connection smoke test – no Dataverse needed.</summary>
    internal static class Program
    {
        private static readonly List<string> UiErrors = new List<string>();

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => UiErrors.Add(e.Exception.GetType().Name + ": " + e.Exception.Message);

            var outDir = args.Length > 0 ? args[0] : AppDomain.CurrentDomain.BaseDirectory;
            var mode = args.Length > 1 ? args[1] : "all";
            Directory.CreateDirectory(outDir);

            // Run inside a real message loop (like XrmToolBox) so WinForms cleans up its windows on exit
            var exitCode = 0;
            var start = new Timer { Interval = 1 };
            start.Tick += (s, e) =>
            {
                start.Stop();
                exitCode = Run(outDir, mode);
                Application.ExitThread();
            };
            start.Start();
            Application.Run();
            return exitCode;
        }

        private static int Run(string outDir, string mode)
        {
            var report = new List<string>();
            try
            {
                if (mode != "smoke")
                {
                    foreach (var theme in new[] { ThemeMode.Light, ThemeMode.Dark })
                    {
                        var suffix = theme == ThemeMode.Dark ? "dark" : "light";
                        Render(BuildAudit, theme, Path.Combine(outDir, $"audit-manager-{suffix}.png"));
                        Render(BuildTranslation, theme, Path.Combine(outDir, $"translation-manager-{suffix}.png"));
                    }
                }
                if (mode != "render")
                {
                    report.AddRange(Smoke("Audit Manager", () => new AuditManagerControl()));
                    report.AddRange(Smoke("Translation Manager", () => new TranslationManagerControl()));
                }
            }
            catch (Exception ex)
            {
                report.Add("FAIL  harness: " + ex);
            }

            // let pending timers/callbacks run so errors after closing are caught too
            for (var i = 0; i < 20; i++)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(25);
            }
            report.AddRange(UiErrors.Select(e => "FAIL  UI thread: " + e));
            File.WriteAllLines(Path.Combine(outDir, "smoke-test.txt"), report);
            return report.Any(l => l.StartsWith("FAIL")) ? 1 : 0;
        }

        #region Helpers

        private static T Field<T>(object o, string name) =>
            (T)o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(o);

        private static void SetField(object o, string name, object value) =>
            o.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(o, value);

        private static object Call(object o, string name, params object[] args) =>
            o.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(o, args);

        private static DataverseEnvironment Env(string name, Color color, bool primary) =>
            new DataverseEnvironment(new ConnectionDetail(true)
            {
                ConnectionName = name,
                WebApplicationUrl = $"https://contoso-{name.ToLowerInvariant()}.crm4.dynamics.com/",
                EnvironmentColor = color,
                EnvironmentTextColor = Color.White
            }, null, primary)
            {
                Languages = new EnvLanguages { Base = 1033, Provisioned = new[] { 1033, 1031, 1036 } }
            };

        private static Dictionary<int, string> L(string en, string de) => new Dictionary<int, string> { [1033] = en, [1031] = de };

        private static void Render(Func<Control> build, ThemeMode theme, string file)
        {
            var control = build();
            using (var form = new Form { Width = 1500, Height = 880, StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000), ShowInTaskbar = false })
            {
                control.Dock = DockStyle.Fill;
                form.Controls.Add(control);
                form.Show();
                UiTheme.SetMode(theme);
                Application.DoEvents();
                if (control.Tag is Action afterShow) afterShow();
                Application.DoEvents();
                using (var bmp = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                {
                    control.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.ClientSize));
                    bmp.Save(file, ImageFormat.Png);
                }
            }
        }

        #endregion

        #region Sample data

        private static Control BuildAudit()
        {
            var c = new AuditManagerControl();
            var dev = Env("DEV", Color.FromArgb(16, 124, 16), true);
            var test = Env("TEST", Color.FromArgb(202, 80, 16), false);
            var prod = Env("PROD", Color.FromArgb(164, 38, 44), false);
            Field<List<DataverseEnvironment>>(c, "_envs").AddRange(new[] { dev, test, prod });
            SetField(c, "_languageInfo", dev.Languages);
            SetField(c, "_languages", new List<int> { 1033, 1031 });

            var org = Field<Dictionary<DataverseEnvironment, OrgAuditInfo>>(c, "_orgAudit");
            org[dev] = new OrgAuditInfo { IsAuditEnabled = true, OrganizationId = Guid.NewGuid() };
            org[test] = new OrgAuditInfo { IsAuditEnabled = true, OrganizationId = Guid.NewGuid() };
            org[prod] = new OrgAuditInfo { IsAuditEnabled = false, OrganizationId = Guid.NewGuid() };

            AuditFlag F(bool v) => new AuditFlag { Exists = true, CanChange = true, Original = v, Current = v };
            T Row<T>(T row, Dictionary<int, string> labels) where T : AuditRowBase
            {
                foreach (var p in labels) row.Labels[p.Key] = p.Value;
                return row;
            }

            var entities = Field<Dictionary<string, AuditEntityRow>>(c, "_entities");
            var account = Row(new AuditEntityRow { LogicalName = "account", AttributesLoaded = true }, L("Account", "Firma"));
            account.Flags[dev] = F(true); account.Flags[test] = F(true); account.Flags[prod] = F(false);
            var contact = Row(new AuditEntityRow { LogicalName = "contact", AttributesLoaded = true }, L("Contact", "Kontakt"));
            contact.Flags[dev] = F(true); contact.Flags[test] = F(true); contact.Flags[prod] = F(true);
            var order = Row(new AuditEntityRow { LogicalName = "salesorder", AttributesLoaded = true }, L("Order", "Auftrag"));
            order.Flags[dev] = F(false); order.Flags[test] = F(false); order.Flags[prod] = F(false);
            order.Flags[dev].Current = true;
            var custom = Row(new AuditEntityRow { LogicalName = "contoso_project", IsCustom = true, AttributesLoaded = true }, L("Project", "Projekt"));
            custom.Flags[dev] = F(true); custom.Flags[test] = F(true); custom.Flags[prod] = AuditFlag.Missing();
            foreach (var e in new[] { account, contact, order, custom }) entities[e.LogicalName] = e;

            void Attr(AuditEntityRow e, string name, string en, string de, string type, bool d, bool t, bool p, bool isCustom = false)
            {
                var a = Row(new AuditAttributeRow { Entity = e, LogicalName = name, TypeName = type, IsCustom = isCustom }, L(en, de));
                a.Flags[dev] = F(d); a.Flags[test] = F(t); a.Flags[prod] = F(p);
                e.Attributes[name] = a;
            }
            Attr(account, "accountnumber", "Account Number", "Firmennummer", "Text", true, true, true);
            Attr(account, "creditlimit", "Credit Limit", "Kreditlimit", "Currency", true, true, false);
            Attr(account, "emailaddress1", "Email", "E-Mail", "Text", true, false, false);
            Attr(account, "industrycode", "Industry", "Branche", "Choice", false, false, false);
            Attr(account, "name", "Account Name", "Firmenname", "Text", true, true, true);
            Attr(account, "ownerid", "Owner", "Besitzer", "Owner", true, true, true);
            Attr(account, "parentaccountid", "Parent Account", "Übergeordnete Firma", "Lookup", false, false, false);
            Attr(account, "primarycontactid", "Primary Contact", "Primärer Kontakt", "Lookup", true, true, true);
            Attr(account, "revenue", "Annual Revenue", "Jahresumsatz", "Currency", true, true, true);
            Attr(account, "statecode", "Status", "Status", "Status", true, true, true);
            Attr(account, "contoso_segment", "Segment", "", "Choice", true, true, false, true);
            account.Attributes["emailaddress1"].Flags[prod].Current = true;
            account.Attributes["creditlimit"].Flags[prod].Error = "Principal user is missing prvWriteEntity privilege.";
            account.Attributes["creditlimit"].Flags[prod].Current = true;

            Call(c, "RebuildColumns", Field<DataGridView>(c, "_gridEntities"), false);
            Call(c, "RebuildColumns", Field<DataGridView>(c, "_gridFields"), true);
            Call(c, "RebuildAlignMenu");
            Call(c, "RebuildLanguageMenu");
            c.Tag = new Action(() =>
            {
                Call(c, "ApplyTheme");
                Call(c, "FillEntityGrid");
                Call(c, "UpdatePendingCount");
                var log = Field<LogPanel>(c, "_log");
                log.Info("[DEV] 3 table(s) loaded. Organization auditing: on");
                log.Info("[PROD] 3 table(s) loaded. Organization auditing: OFF");
                log.Warn("Not found in any environment: contoso_foo");
            });
            return c;
        }

        private static Control BuildTranslation()
        {
            var c = new TranslationManagerControl();
            var env = Env("TEST", Color.FromArgb(202, 80, 16), true);
            SetField(c, "_env", env);
            SetField(c, "_envLanguages", env.Languages);
            SetField(c, "_languages", new List<int> { 1033, 1031 });

            SetField(c, "_entityList", new List<EntityListItem>
            {
                new EntityListItem { LogicalName = "account", Labels = L("Account", "Firma") },
                new EntityListItem { LogicalName = "contact", Labels = L("Contact", "Kontakt") },
                new EntityListItem { LogicalName = "contoso_project", Labels = new Dictionary<int, string> { [1033] = "Project" }, IsCustom = true },
                new EntityListItem { LogicalName = "salesorder", Labels = L("Order", "Auftrag") }
            });

            var d = new EntityTranslations { LogicalName = "account", Label = "Account" };
            TranslationItem I(ItemKind kind, Dictionary<int, string> labels, string i1, string i2, string i3, Action<TranslationItem> setup = null)
            {
                var item = new TranslationItem { Kind = kind, Entity = "account", EntityLabel = "Account", Info1 = i1, Info2 = i2, Info3 = i3 };
                item.Load(labels);
                setup?.Invoke(item);
                return item;
            }
            d.EntityItems.Add(I(ItemKind.EntityDisplayName, L("Account", "Firma"), "Display name", "DisplayName", "account"));
            d.EntityItems.Add(I(ItemKind.EntityPluralName, L("Accounts", "Firmen"), "Plural name", "DisplayCollectionName", "account"));
            d.EntityItems.Add(I(ItemKind.EntityDescription, L("Business that represents a customer.", "Unternehmen, das einen Kunden darstellt."), "Description", "Description", "account"));

            void AddField(string name, string type, string en, string de, Action<TranslationItem> setup = null) =>
                d.FieldItems.Add(I(ItemKind.AttributeDisplayName, L(en, de), name, type, "Display name", x => { x.Attribute = name; x.AttributeLabel = en; x.AttributeType = type; setup?.Invoke(x); }));
            AddField("accountnumber", "Text", "Account Number", "Firmennummer");
            AddField("creditlimit", "Currency", "Credit Limit", "Kreditlimit");
            AddField("contoso_segment", "Choice", "Segment", "");
            AddField("emailaddress1", "Text", "Email", "E-Mail");
            AddField("industrycode", "Choice", "Industry", "Branche");
            AddField("name", "Text", "Account Name", "Firmenname", x => x.Set(1033, "Company Name"));
            AddField("ownerid", "Owner", "Owner", "Besitzer", x => { x.CanEdit = false; x.ReadOnlyReason = "This column must not be renamed (managed property IsRenameable = false)."; });
            AddField("primarycontactid", "Lookup", "Primary Contact", "Primärer Kontakt");
            AddField("revenue", "Currency", "Annual Revenue", "Jahresumsatz");

            d.OptionItems.Add(I(ItemKind.OptionLabel, L("Accounting", "Buchhaltung"), "industrycode", "1", "Choice", x => { x.Attribute = "industrycode"; x.OptionValue = 1; x.UsedByAttributes.Add("industrycode"); }));
            d.OptionItems.Add(I(ItemKind.OptionLabel, new Dictionary<int, string> { [1033] = "Large" }, "contoso_size  (global – contoso_size)", "2", "Global", x => { x.Attribute = "contoso_size"; x.OptionValue = 2; x.GlobalOptionSet = "contoso_size"; x.UsedByAttributes.Add("contoso_size"); }));

            var formId = Guid.NewGuid();
            d.FormItems.Add(I(ItemKind.FormTab, L("Summary", "Zusammenfassung"), "Account (Main)", "Tab", "SUMMARY_TAB", x => { x.FormId = formId; x.FormName = "Account"; x.FormType = "Main"; x.ElementKind = "Tab"; x.ElementName = "SUMMARY_TAB"; }));
            d.FormItems.Add(I(ItemKind.FormCell, L("Account Name", "Firmenname"), "Account (Main)", "Field", "name", x => { x.FormId = formId; x.FormName = "Account"; x.FormType = "Main"; x.ElementKind = "Field"; x.ElementName = "name"; x.Attribute = "name"; }));

            Field<Dictionary<string, EntityTranslations>>(c, "_details")["account"] = d;

            c.Tag = new Action(() =>
            {
                Call(c, "RebuildLanguageColumns");
                Call(c, "RebuildLanguageMenu");
                Call(c, "ApplyTheme");
                Call(c, "FillEntityGrid");
                Field<PivotBar>(c, "_pivot").Select(1);
                Call(c, "FillDetailGrids");
                var fields = Field<Dictionary<ItemArea, DataGridView>>(c, "_grids")[ItemArea.Fields];
                var nameRow = fields.Rows.Cast<DataGridViewRow>().First(r => ((TranslationItem)r.Tag).Attribute == "name");
                fields.CurrentCell = nameRow.Cells["lang_1033"];
                Call(c, "UpdatePendingCount");
                Call(c, "UpdateExplain");
                Field<LogPanel>(c, "_log").Info("account: 9 columns, 2 choice values, 1 forms with 2 labels (1.2 s).");
            });
            return c;
        }

        #endregion

        #region Smoke test (Tool Library checklist items that need no Dataverse)

        private static IEnumerable<string> Smoke(string name, Func<PluginControlBase> factory)
        {
            var results = new List<string>();
            PluginControlBase control = null;
            var requests = 0;

            void Step(string what, Action action, bool expectConnectionRequest = false)
            {
                var before = requests;
                var errorsBefore = UiErrors.Count;
                try
                {
                    action();
                    Application.DoEvents();
                    if (UiErrors.Count > errorsBefore)
                        results.Add($"FAIL  {name}: {what} – {string.Join("; ", UiErrors.Skip(errorsBefore))}");
                    else if (expectConnectionRequest && requests == before)
                        results.Add($"FAIL  {name}: {what} – no connection dialog was requested");
                    else
                        results.Add($"PASS  {name}: {what}{(expectConnectionRequest ? " → asks for a connection" : "")}");
                }
                catch (Exception ex)
                {
                    var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                    results.Add($"FAIL  {name}: {what} – {inner.GetType().Name}: {inner.Message}");
                }
            }

            using (var form = new Form { Width = 1300, Height = 800, StartPosition = FormStartPosition.Manual, Location = new Point(-3000, -3000), ShowInTaskbar = false })
            {
                Step("opens without an organization connected", () =>
                {
                    control = factory();
                    control.OnRequestConnection += (s, e) => requests++;
                    control.Dock = DockStyle.Fill;
                    form.Controls.Add(control);
                    form.Show();
                });
                if (control == null) return results;

                var toolbar = Field<ToolStrip>(control, "_toolbar");
                foreach (var item in toolbar.Items.Cast<ToolStripItem>().ToList())
                {
                    switch (item)
                    {
                        case ToolStripDropDownButton dd:
                            Step($"menu “{dd.Text}” opens", () => { dd.ShowDropDown(); Application.DoEvents(); dd.HideDropDown(); });
                            break;
                        case ToolStripButton button when button.CheckOnClick:
                            Step($"toggle “{button.Text}”", () => { button.PerformClick(); button.PerformClick(); });
                            break;
                        case ToolStripButton button:
                            var needsConnection = button.Text.StartsWith("Load");
                            Step($"button “{button.Text}”{(button.Enabled ? "" : " (disabled)")}", () => button.PerformClick(), needsConnection);
                            break;
                        case ToolStripTextBox box:
                            Step("type in the table filter", () => box.Text = "account, contact");
                            break;
                    }
                }

                if (control is AuditManagerControl)
                {
                    var add = Field<FlowLayoutPanel>(control, "_envBar").Controls.OfType<Button>().First();
                    Step("button “+ Add environment”", () => add.PerformClick(), true);
                    foreach (var grid in new[] { "_gridEntities", "_gridFields" })
                        Step($"search box above {grid}", () => Field<TextBox>(control, grid == "_gridEntities" ? "_txtEntitySearch" : "_txtFieldSearch").Text = "acc");
                }
                else
                {
                    var pivot = Field<PivotBar>(control, "_pivot");
                    Step("switch tabs (Table/Columns/Choices/Forms)", () => { for (var i = 3; i >= 0; i--) pivot.Select(i); });
                    Step("search box", () => Field<TextBox>(control, "_txtDetailSearch").Text = "name");
                }

                Step("F5 (load)", () => Call(control, "ProcessCmdKey", new Message(), Keys.F5), true);
                Step("Ctrl+S with nothing to save", () => Call(control, "ProcessCmdKey", new Message(), Keys.Control | Keys.S));
                Step("resize XrmToolBox small", () => form.Size = new Size(640, 420));
                Step("resize XrmToolBox large", () => form.Size = new Size(1700, 1000));
                Step("switch to dark theme", () => UiTheme.SetMode(ThemeMode.Dark));
                Step("switch to light theme", () => UiTheme.SetMode(ThemeMode.Light));
                Step("XrmToolBox re-invokes “LoadEntities” after connecting (method must be public)",
                    () => control.UpdateConnection(null, new ConnectionDetail(true) { ConnectionName = "Smoke" }, "LoadEntities", null));
                if (control is AuditManagerControl)
                    Step("XrmToolBox re-invokes “AddEnvironment” after connecting (method must be public)",
                        () => control.UpdateConnection(null, new ConnectionDetail(true) { ConnectionName = "Smoke" }, "AddEnvironment", null), true);

                // Silent = XrmToolBox closes without its own "Are you sure…" prompt
                var close = new PluginCloseInfo { Silent = true };
                Step("close tool without changes", () => control.ClosingPlugin(close));
                if (close.Cancel) results.Add($"FAIL  {name}: closing was cancelled without unsaved changes");
            }
            return results;
        }

        #endregion
    }
}
