using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using D365Tools.Shared;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;
using Label = System.Windows.Forms.Label;

namespace D365Tools.AuditManager
{
    /// <summary>
    /// Shows auditing of tables and columns for several environments (e.g. DEV/TEST/PROD) side by side.
    /// Changes are staged and saved in the background – environments in parallel.
    /// </summary>
    public class AuditManagerControl : MultipleConnectionsPluginControlBase, IGitHubPlugin, IHelpPlugin
    {
        private const int EagerAttributeLoadLimit = 30;
        private const string EnvColumnPrefix = "env_";
        private const string LabelColumnPrefix = "lbl_";

        private static readonly string[] EntityProperties =
            { "LogicalName", "DisplayName", "IsAuditEnabled", "IsCustomEntity" };

        private static readonly string[] AttributeProperties =
            { "LogicalName", "DisplayName", "IsAuditEnabled", "AttributeType", "AttributeTypeName", "AttributeOf", "IsCustomAttribute" };

        private readonly List<DataverseEnvironment> _envs = new List<DataverseEnvironment>();
        private readonly Dictionary<string, AuditEntityRow> _entities = new Dictionary<string, AuditEntityRow>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<DataverseEnvironment, OrgAuditInfo> _orgAudit = new Dictionary<DataverseEnvironment, OrgAuditInfo>();
        private readonly Timer _searchTimer = new Timer { Interval = 250 };
        private readonly ToolTip _toolTip = new ToolTip();
        private readonly ToolSettings _settings;

        private List<int> _languages = new List<int>();
        private EnvLanguages _languageInfo;
        private AuditEntityRow _selectedEntity;
        private bool _busy;
        private bool _saving;
        private bool _filling;
        private int _operations;

        private ToolStrip _toolbar;
        private ToolStripTextBox _txtFilter;
        private ToolStripButton _btnLoad, _btnSave, _btnDiscard, _btnOnlyDiff, _btnHideLocked, _btnPublish;
        private ToolStripDropDownButton _ddAlign, _ddLanguages, _ddTheme;
        private FlowLayoutPanel _envBar;
        private DataGridView _gridEntities, _gridFields;
        private TextBox _txtEntitySearch, _txtFieldSearch;
        private Label _lblEntitiesTitle, _lblFieldsTitle;
        private SplitContainer _outerSplit, _innerSplit;
        private StatusStrip _statusStrip;
        private ToolStripStatusLabel _lblStatus, _lblPending;
        private ToolStripProgressBar _progress;
        private LogPanel _log;

        public AuditManagerControl()
        {
            _settings = ToolSettings.Load(typeof(AuditManagerPlugin));
            UiTheme.SetMode(_settings.Theme);
            BuildUi();
            _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); RefreshGrids(); };
            UiTheme.Changed += OnThemeChanged;
            ApplyTheme();
        }

        public string RepositoryName => ToolInfo.GitHubRepository;
        public string UserName => ToolInfo.GitHubUser;
        public string HelpUrl => ToolInfo.HelpUrl;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                UiTheme.Changed -= OnThemeChanged;
                _searchTimer.Dispose();
                _toolTip.Dispose();
            }
            base.Dispose(disposing);
        }

        #region UI

        private void BuildUi()
        {
            SuspendLayout();
            Font = UiTheme.Regular;

            // --- Toolbar ---
            _toolbar = new ToolStrip();
            _btnLoad = UiTheme.Button("Load", "Load tables and audit settings from all connected environments (F5)", (s, e) => ExecuteMethod(LoadEntities));
            _txtFilter = new ToolStripTextBox
            {
                AutoSize = false,
                Width = 260,
                Text = "account, contact, salesorder",
                ToolTipText = "Logical names, comma separated (e.g. account, contact, salesorder).\nLeave empty for all tables. Enter = Load."
            };
            _txtFilter.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                ExecuteMethod(LoadEntities);
            };

            _btnOnlyDiff = UiTheme.Toggle("Only differences", "Only show tables/columns whose auditing differs between environments", false, (s, e) => RefreshGrids());
            _btnHideLocked = UiTheme.Toggle("Hide locked", "Hide tables/columns whose audit setting cannot be changed (IsAuditEnabled.CanBeChanged = false)", true, (s, e) => RefreshGrids());
            _ddAlign = new ToolStripDropDownButton("Align")
            {
                ToolTipText = "Copy the audit settings of the selected tables (including all columns) from one environment to others.\nOnly staged – nothing is written until you click Save."
            };
            _ddLanguages = new ToolStripDropDownButton("Languages") { ToolTipText = "Which languages to show display names in (saved per tool)" };
            Menus.KeepOpenOnCheck(_ddLanguages);
            _ddTheme = Menus.ThemeMenu(ChangeTheme);

            _btnSave = UiTheme.PrimaryButton("Save (0)", "Save all staged changes in the background (Ctrl+S)", (s, e) => SaveChanges());
            _btnDiscard = UiTheme.Button("Discard", "Undo all unsaved changes", (s, e) => DiscardChanges());
            _btnPublish = UiTheme.Toggle("Publish after save", "After saving, publish only the changed tables (recommended – auditing takes effect after publishing)", true, null);
            _btnSave.Alignment = ToolStripItemAlignment.Right;
            _btnDiscard.Alignment = ToolStripItemAlignment.Right;
            _btnPublish.Alignment = ToolStripItemAlignment.Right;

            _toolbar.Items.AddRange(new ToolStripItem[]
            {
                _btnLoad,
                new ToolStripSeparator(),
                new ToolStripLabel("Tables:"),
                _txtFilter,
                new ToolStripSeparator(),
                _btnOnlyDiff,
                _btnHideLocked,
                new ToolStripSeparator(),
                _ddAlign,
                _ddLanguages,
                _ddTheme,
                _btnSave,
                _btnDiscard,
                _btnPublish
            });
            UiTheme.StyleToolStrip(_toolbar);

            // --- Environment bar ---
            _envBar = UiTheme.Role(new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 42,
                Padding = new Padding(8, 7, 8, 5),
                WrapContents = false,
                AutoScroll = true
            }, UiRole.Alt);

            // --- Grids ---
            _gridEntities = CreateGrid();
            _gridFields = CreateGrid();
            _gridEntities.SelectionChanged += GridEntities_SelectionChanged;

            var entityPanel = UiHelpers.GridPanel(_gridEntities, "Tables", "Search (name or display name) …", out _lblEntitiesTitle, out _txtEntitySearch);
            var fieldPanel = UiHelpers.GridPanel(_gridFields, "Columns", "Search columns …", out _lblFieldsTitle, out _txtFieldSearch);
            _txtEntitySearch.TextChanged += (s, e) => { _searchTimer.Stop(); _searchTimer.Start(); };
            _txtFieldSearch.TextChanged += (s, e) => { _searchTimer.Stop(); _searchTimer.Start(); };

            _innerSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 5 };
            _innerSplit.Panel1.Controls.Add(entityPanel);
            _innerSplit.Panel2.Controls.Add(fieldPanel);

            // --- Log ---
            _log = new LogPanel();
            _outerSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5, FixedPanel = FixedPanel.Panel2 };
            _outerSplit.Panel1.Controls.Add(_innerSplit);
            _outerSplit.Panel2.Controls.Add(UiHelpers.LogArea(_log));

            // --- Status bar ---
            _statusStrip = new StatusStrip { SizingGrip = false, Font = UiTheme.Regular };
            _lblStatus = new ToolStripStatusLabel("Connect to an environment to start.") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _lblPending = new ToolStripStatusLabel { Font = UiTheme.Bold };
            _progress = new ToolStripProgressBar { Width = 220, Visible = false };
            _statusStrip.Items.AddRange(new ToolStripItem[] { _lblStatus, _lblPending, _progress });

            // Docking order: Fill first, then Top/Bottom
            Controls.Add(_outerSplit);
            Controls.Add(_envBar);
            Controls.Add(_toolbar);
            Controls.Add(_statusStrip);

            RebuildColumns(_gridEntities, false);
            RebuildColumns(_gridFields, true);
            RebuildAlignMenu();
            RebuildLanguageMenu();
            RenderEnvBar();
            UpdateCommandState();
            ResumeLayout();
        }

        private DataGridView CreateGrid()
        {
            var grid = new DataGridView
            {
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2
            };
            UiTheme.StyleGrid(grid);
            grid.CellFormatting += Grid_CellFormatting;
            grid.CellBeginEdit += Grid_CellBeginEdit;
            grid.CellValueChanged += Grid_CellValueChanged;
            grid.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (grid.IsCurrentCellDirty && grid.CurrentCell is DataGridViewCheckBoxCell)
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            grid.ColumnHeaderMouseClick += Grid_ColumnHeaderMouseClick;
            grid.KeyDown += (s, e) =>
            {
                // Space toggles all selected rows in the current environment column
                if (e.KeyCode == Keys.Space && grid.CurrentCell != null && grid.Columns[grid.CurrentCell.ColumnIndex].Tag is DataverseEnvironment env && grid.SelectedRows.Count > 1)
                {
                    e.Handled = true;
                    ToggleSelected(grid, env);
                }
            };
            return grid;
        }

        private void RebuildColumns(DataGridView grid, bool fields)
        {
            grid.Rows.Clear();
            grid.Columns.Clear();

            var diff = UiTheme.TextColumn("diff", "≠", 30);
            diff.ToolTipText = "≠ = auditing differs between environments (or the item is missing in one of them).";
            diff.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.Columns.Add(diff);
            grid.Columns.Add(UiTheme.TextColumn("logical", "Logical name", 165));

            // Environment columns right after the name so they never scroll out of view
            for (var i = 0; i < _envs.Count; i++)
            {
                var env = _envs[i];
                var col = UiTheme.CheckColumn(EnvColumnPrefix + i, env.Name, 82);
                col.Tag = env;
                col.ToolTipText = $"{env.Name}\n{env.Url}\n\nTicked = auditing enabled.\nRight-click this header: enable/disable all visible rows.";
                col.HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleCenter;
                if (env.Color.HasValue)
                {
                    col.HeaderCell.Style.BackColor = env.Color.Value;
                    col.HeaderCell.Style.SelectionBackColor = env.Color.Value;
                    col.HeaderCell.Style.ForeColor = env.Detail.EnvironmentTextColor ?? Color.White;
                }
                grid.Columns.Add(col);
            }

            foreach (var lcid in _languages)
            {
                var col = UiTheme.TextColumn(LabelColumnPrefix + lcid, Languages.Name(lcid), 120, fill: true);
                col.ToolTipText = $"Display name – {Languages.Label(lcid)}" + (lcid == _languageInfo?.Base ? " (base language)" : "");
                col.Tag = lcid;
                grid.Columns.Add(col);
            }
            if (fields) grid.Columns.Add(UiTheme.TextColumn("type", "Type", 100));
            grid.Columns.Add(UiTheme.TextColumn("origin", "Origin", 70));
        }

        private void RebuildAlignMenu()
        {
            _ddAlign.DropDownItems.Clear();
            if (_envs.Count < 2)
            {
                _ddAlign.DropDownItems.Add(new ToolStripMenuItem("Connect at least 2 environments first") { Enabled = false });
            }
            else
            {
                foreach (var source in _envs)
                {
                    var sourceItem = new ToolStripMenuItem($"Copy from {source.Name}");
                    foreach (var target in _envs.Where(t => t != source))
                    {
                        sourceItem.DropDownItems.Add($"→ to {target.Name}", null, (s, e) => AlignSelected(source, new[] { target }));
                    }
                    if (_envs.Count > 2)
                    {
                        var others = _envs.Where(t => t != source).ToList();
                        sourceItem.DropDownItems.Add($"→ to all others ({string.Join(", ", others)})", null, (s, e) => AlignSelected(source, others));
                    }
                    _ddAlign.DropDownItems.Add(sourceItem);
                }
            }
            UiTheme.ApplyMenu(_ddAlign);
        }

        private void RebuildLanguageMenu()
        {
            Menus.FillLanguageMenu(_ddLanguages, _languageInfo, _languages, ChangeLanguages);
        }

        private void ChangeLanguages(List<int> selection)
        {
            var isDefault = _languageInfo != null && selection.SequenceEqual(_languageInfo.Defaults);
            _languages = selection;
            _settings.Languages = isDefault ? new int[0] : selection.ToArray();
            _settings.Save(typeof(AuditManagerPlugin));
            RebuildColumns(_gridEntities, false);
            RebuildColumns(_gridFields, true);
            RebuildLanguageMenu();
            ApplyTheme();
            RefreshGrids();
        }

        private void ChangeTheme(ThemeMode mode)
        {
            _settings.Theme = mode;
            _settings.Save(typeof(AuditManagerPlugin));
            UiTheme.SetMode(mode); // raises Changed → OnThemeChanged
        }

        private void OnThemeChanged(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action(ApplyTheme)); return; }
            ApplyTheme();
        }

        private void ApplyTheme()
        {
            UiTheme.Apply(this);
            _lblPending.ForeColor = UiTheme.WarnText;
            _log.Recolor();
            RenderEnvBar();
        }

        private void RenderEnvBar()
        {
            _envBar.SuspendLayout();
            foreach (Control c in _envBar.Controls.Cast<Control>().ToList()) c.Dispose();
            _envBar.Controls.Clear();
            _envBar.BackColor = UiTheme.SurfaceAlt;

            foreach (var env in _envs) _envBar.Controls.Add(CreateEnvChip(env));

            var add = new Button
            {
                Text = "+ Add environment",
                AutoSize = true,
                FlatStyle = FlatStyle.Flat,
                BackColor = UiTheme.Surface,
                ForeColor = UiTheme.Accent,
                Font = UiTheme.Bold,
                Margin = new Padding(0, 0, 8, 0),
                Height = 26,
                Cursor = Cursors.Hand
            };
            add.FlatAppearance.BorderColor = UiTheme.Accent;
            add.Click += (s, e) => ExecuteMethod(AddEnvironment);
            _toolTip.SetToolTip(add, "Add another connection (e.g. TEST or PROD) from the XrmToolBox connection manager.");
            _envBar.Controls.Add(add);

            if (_envs.Count < 3)
            {
                _envBar.Controls.Add(new Label
                {
                    AutoSize = true,
                    ForeColor = UiTheme.TextSecondary,
                    Margin = new Padding(0, 5, 0, 0),
                    Text = _envs.Count == 0
                        ? "Connect to your first environment (e.g. DEV), then add TEST and PROD."
                        : "Tip: add TEST and PROD with “+ Add environment” – each becomes its own column."
                });
            }
            _envBar.ResumeLayout();
        }

        private Control CreateEnvChip(DataverseEnvironment env)
        {
            var chip = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                BackColor = UiTheme.Surface,
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(4, 2, 4, 0),
                Margin = new Padding(0, 0, 8, 0)
            };
            Label ChipLabel(string text, Color color, Font font = null) =>
                new Label { Text = text, AutoSize = true, ForeColor = color, Font = font ?? UiTheme.Regular, Margin = new Padding(0, 3, 4, 0) };

            chip.Controls.Add(ChipLabel("●", env.Color ?? UiTheme.Accent));
            var name = ChipLabel(env.Name, UiTheme.TextPrimary, UiTheme.Bold);
            _toolTip.SetToolTip(name, env.Url + (env.IsPrimary ? "\n(main XrmToolBox connection)" : ""));
            chip.Controls.Add(name);

            if (_orgAudit.TryGetValue(env, out var org))
            {
                if (org.Error != null)
                {
                    var l = ChipLabel("Org audit: ?", UiTheme.TextSecondary);
                    _toolTip.SetToolTip(l, org.Error);
                    chip.Controls.Add(l);
                }
                else if (org.IsAuditEnabled)
                {
                    var l = ChipLabel("Org audit: on", UiTheme.OnText);
                    _toolTip.SetToolTip(l, "Organization-wide auditing is enabled." + (org.IsUserAccessAuditEnabled ? "\nUser access is logged as well." : ""));
                    chip.Controls.Add(l);
                }
                else
                {
                    var l = ChipLabel("Org audit: OFF", UiTheme.ErrorText, UiTheme.Bold);
                    _toolTip.SetToolTip(l, "Organization-wide auditing is off. Table/column settings are saved but only take effect once it is enabled.");
                    chip.Controls.Add(l);
                    var link = new LinkLabel { Text = "enable", AutoSize = true, Margin = new Padding(0, 3, 4, 0), LinkColor = UiTheme.Accent, ActiveLinkColor = UiTheme.Accent };
                    link.LinkClicked += (s, e) => EnableOrgAudit(env);
                    chip.Controls.Add(link);
                }
            }

            if (!env.IsPrimary)
            {
                var remove = new LinkLabel { Text = "✕", AutoSize = true, Margin = new Padding(2, 3, 0, 0), LinkColor = UiTheme.TextSecondary, ActiveLinkColor = UiTheme.TextSecondary, LinkBehavior = LinkBehavior.NeverUnderline };
                _toolTip.SetToolTip(remove, $"Remove {env.Name} from the comparison");
                remove.LinkClicked += (s, e) =>
                {
                    if (_saving) return;
                    RemoveAdditionalOrganization(env.Detail);
                };
                chip.Controls.Add(remove);
            }
            return chip;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            UiHelpers.ConfigureSplit(_outerSplit, 200, 60, _outerSplit.Height - 150);
            UiHelpers.ConfigureSplit(_innerSplit, 250, 250, (int)(_innerSplit.Width * 0.45));
            if (Service != null) RebuildEnvironments();
            _log.Info("How to: 1) connect to DEV  2) “+ Add environment” for TEST/PROD  3) enter tables (empty = all) → Load  4) tick → Save.");
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.S)) { SaveChanges(); return true; }
            if (keyData == Keys.F5) { ExecuteMethod(LoadEntities); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        #endregion

        #region Connections

        public override void UpdateConnection(IOrganizationService newService, ConnectionDetail detail, string actionName, object parameter)
        {
            base.UpdateConnection(newService, detail, actionName, parameter);
            if (actionName != "AdditionalOrganization") RebuildEnvironments();
        }

        protected override void ConnectionDetailsUpdated(NotifyCollectionChangedEventArgs e)
        {
            RebuildEnvironments();
        }

        /// <summary>Called by "+ Add environment" (public: XrmToolBox re-invokes it by name after connecting).</summary>
        public void AddEnvironment()
        {
            AddAdditionalOrganization();
        }

        private void RebuildEnvironments()
        {
            if (InvokeRequired) { BeginInvoke(new Action(RebuildEnvironments)); return; }

            var envs = new List<DataverseEnvironment>();
            if (Service != null && ConnectionDetail != null)
                envs.Add(new DataverseEnvironment(ConnectionDetail, Service, true));
            foreach (var detail in AdditionalConnectionDetails.ToList())
            {
                if (ConnectionDetail != null && detail.ConnectionId == ConnectionDetail.ConnectionId) continue;
                IOrganizationService svc = detail.ServiceClient ?? detail.GetCrmServiceClient(false);
                if (svc != null) envs.Add(new DataverseEnvironment(detail, svc, false));
            }

            var unchanged = envs.Count == _envs.Count
                && envs.Zip(_envs, (a, b) => ReferenceEquals(a.Detail, b.Detail) && ReferenceEquals(a.Service, b.Service)).All(x => x);
            if (unchanged) return;

            var hadData = _entities.Count > 0;
            if (CollectChanges().Count > 0) _log.Warn("Environments changed – unsaved changes were discarded.");

            _envs.Clear();
            _envs.AddRange(envs);
            _entities.Clear();
            _orgAudit.Clear();
            _selectedEntity = null;

            RebuildColumns(_gridEntities, false);
            RebuildColumns(_gridFields, true);
            RebuildAlignMenu();
            ApplyTheme();
            UpdateFieldsTitle();
            _lblEntitiesTitle.Text = "Tables";
            UpdatePendingCount();

            if (_envs.Count > 0) _log.Info($"Connected: {string.Join(", ", _envs.Select(x => x.Name))}");
            SetStatus(_envs.Count == 0 ? "Not connected." : $"{_envs.Count} environment(s) connected – click Load.");
            if (hadData && _envs.Count > 0) LoadEntities();
        }

        private async void EnableOrgAudit(DataverseEnvironment env)
        {
            if (!_orgAudit.TryGetValue(env, out var org) || org.OrganizationId == Guid.Empty) return;
            if (MessageBox.Show(this,
                    $"Enable organization-wide auditing in “{env.Name}”?\n\n(Same as Settings → Auditing → “Start Auditing”.)",
                    "Organization auditing", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                using (BeginOperation($"Enabling organization auditing in {env.Name} …"))
                {
                    await Task.Run(() => env.Service.Update(new Entity("organization", org.OrganizationId) { ["isauditenabled"] = true }));
                }
                org.IsAuditEnabled = true;
                _log.Success($"[{env.Name}] Organization-wide auditing enabled.");
                RenderEnvBar();
            }
            catch (Exception ex)
            {
                _log.Error($"[{env.Name}] Could not enable organization auditing: {MetadataHelper.ErrorText(ex)}");
            }
        }

        #endregion

        #region Loading

        private sealed class EnvLoadResult
        {
            public DataverseEnvironment Env;
            public EntityMetadata[] Entities = new EntityMetadata[0];
            public OrgAuditInfo Org;
            public Exception Error;
        }

        /// <summary>Loads tables from all environments (public: XrmToolBox re-invokes it by name after connecting).</summary>
        public async void LoadEntities()
        {
            if (_busy || _saving) return;
            if (_envs.Count == 0) RebuildEnvironments();
            if (_envs.Count == 0) return;
            if (CollectChanges().Count > 0 && MessageBox.Show(this, "There are unsaved changes. Reload and discard them?",
                    "Reload", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            var names = MetadataHelper.ParseNames(_txtFilter.Text);
            var envs = _envs.ToList();
            _busy = true;
            UpdateCommandState();
            try
            {
                EnvLoadResult[] results;
                using (BeginOperation(names.Count == 0 ? "Loading all tables …" : $"Loading {names.Count} table(s) from {envs.Count} environment(s) …"))
                {
                    results = await Task.WhenAll(envs.Select(env => Task.Run(() => LoadEnvironment(env, names))));
                }

                _entities.Clear();
                _orgAudit.Clear();
                _selectedEntity = null;

                foreach (var r in results)
                {
                    if (r.Org != null) _orgAudit[r.Env] = r.Org;
                    if (r.Error != null)
                    {
                        _log.Error($"[{r.Env.Name}] Loading failed: {MetadataHelper.ErrorText(r.Error)}");
                        continue;
                    }
                    foreach (var em in r.Entities)
                    {
                        if (!_entities.TryGetValue(em.LogicalName, out var row))
                        {
                            row = new AuditEntityRow { LogicalName = em.LogicalName, IsCustom = em.IsCustomEntity == true };
                            _entities[em.LogicalName] = row;
                        }
                        MetadataHelper.MergeLabels(row.Labels, em.DisplayName);
                        var value = em.IsAuditEnabled?.Value ?? false;
                        row.Flags[r.Env] = new AuditFlag
                        {
                            Exists = true,
                            CanChange = em.IsAuditEnabled?.CanBeChanged ?? false,
                            Original = value,
                            Current = value
                        };
                    }
                    var org = r.Org;
                    _log.Info($"[{r.Env.Name}] {r.Entities.Length} table(s) loaded. Organization auditing: {(org == null || org.Error != null ? "unknown" : org.IsAuditEnabled ? "on" : "OFF")}");
                }

                var notFound = names.Where(n => !_entities.ContainsKey(n)).ToList();
                if (notFound.Count > 0) _log.Warn($"Not found in any environment: {string.Join(", ", notFound)}");

                UpdateLanguages();
                RenderEnvBar();
                FillEntityGrid();
                SetStatus($"{_entities.Count} table(s) loaded.");

                if (_entities.Count <= EagerAttributeLoadLimit)
                    await LoadAttributesAsync(_entities.Values.ToList());
                else
                    ShowFields();
            }
            catch (Exception ex)
            {
                _log.Error("Loading failed: " + MetadataHelper.ErrorText(ex));
                MessageBox.Show(this, MetadataHelper.ErrorText(ex), "Loading failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _busy = false;
                UpdateCommandState();
            }
        }

        /// <summary>Languages of the main environment first, then the ones only installed in the others.</summary>
        private void UpdateLanguages()
        {
            var withLanguages = _envs.Where(e => e.Languages != null && e.Languages.Base > 0).ToList();
            if (withLanguages.Count == 0) return;
            var primary = withLanguages[0].Languages;
            var info = new EnvLanguages
            {
                Base = primary.Base,
                Provisioned = withLanguages.SelectMany(e => e.Languages.All).Distinct().ToArray()
            };
            info.Provisioned = primary.All.Concat(info.Provisioned.Where(l => !primary.All.Contains(l))).ToArray();

            var selection = Languages.Order(Languages.Resolve(_settings.Languages, info), info);
            var changed = _languageInfo == null || !selection.SequenceEqual(_languages);
            _languageInfo = info;
            _languages = selection;
            if (changed)
            {
                RebuildColumns(_gridEntities, false);
                RebuildColumns(_gridFields, true);
                ApplyTheme();
            }
            RebuildLanguageMenu();
        }

        private static EnvLoadResult LoadEnvironment(DataverseEnvironment env, List<string> names)
        {
            var result = new EnvLoadResult { Env = env };
            try
            {
                result.Entities = MetadataHelper.RetrieveEntities(env.Service, names, EntityProperties);
            }
            catch (Exception ex)
            {
                result.Error = ex;
            }

            try
            {
                env.Languages = MetadataHelper.RetrieveLanguages(env.Service);
            }
            catch
            {
                // languages are optional – display names just stay empty
            }

            result.Org = new OrgAuditInfo();
            try
            {
                var query = new QueryExpression("organization")
                {
                    ColumnSet = new ColumnSet("organizationid", "isauditenabled", "isuseraccessauditenabled"),
                    TopCount = 1
                };
                var org = env.Service.RetrieveMultiple(query).Entities.FirstOrDefault();
                if (org != null)
                {
                    result.Org.OrganizationId = org.Id;
                    result.Org.IsAuditEnabled = org.GetAttributeValue<bool>("isauditenabled");
                    result.Org.IsUserAccessAuditEnabled = org.GetAttributeValue<bool>("isuseraccessauditenabled");
                }
            }
            catch (Exception ex)
            {
                result.Org.Error = MetadataHelper.ErrorText(ex);
            }
            return result;
        }

        private async Task LoadAttributesAsync(IList<AuditEntityRow> rows)
        {
            rows = rows.Where(r => !r.AttributesLoaded && !r.AttributesLoading).ToList();
            if (rows.Count == 0) return;
            foreach (var r in rows) r.AttributesLoading = true;
            UpdateFieldsTitle();

            var envs = _envs.ToList();
            try
            {
                using (BeginOperation($"Loading columns of {rows.Count} table(s) …"))
                {
                    var results = await Task.WhenAll(envs.Select(env => Task.Run(() =>
                    {
                        var names = rows.Where(r => r.Flag(env).Exists).Select(r => r.LogicalName).ToList();
                        if (names.Count == 0) return (env, metas: new EntityMetadata[0], error: (Exception)null);
                        try
                        {
                            return (env, metas: MetadataHelper.RetrieveEntities(env.Service, names, new[] { "LogicalName" }, AttributeProperties), error: (Exception)null);
                        }
                        catch (Exception ex)
                        {
                            return (env, metas: (EntityMetadata[])null, error: ex);
                        }
                    })));

                    foreach (var (env, metas, error) in results)
                    {
                        if (error != null)
                        {
                            _log.Error($"[{env.Name}] Loading columns failed: {MetadataHelper.ErrorText(error)}");
                            continue;
                        }
                        foreach (var em in metas)
                        {
                            if (!_entities.TryGetValue(em.LogicalName, out var entityRow)) continue;
                            foreach (var am in em.Attributes.Where(MetadataHelper.IsRelevantAttribute))
                            {
                                if (!entityRow.Attributes.TryGetValue(am.LogicalName, out var ar))
                                {
                                    ar = new AuditAttributeRow
                                    {
                                        Entity = entityRow,
                                        LogicalName = am.LogicalName,
                                        IsCustom = am.IsCustomAttribute == true,
                                        TypeName = MetadataHelper.FriendlyType(am)
                                    };
                                    entityRow.Attributes[am.LogicalName] = ar;
                                }
                                MetadataHelper.MergeLabels(ar.Labels, am.DisplayName);
                                var value = am.IsAuditEnabled?.Value ?? false;
                                ar.Flags[env] = new AuditFlag
                                {
                                    Exists = true,
                                    CanChange = am.IsAuditEnabled?.CanBeChanged ?? false,
                                    Original = value,
                                    Current = value
                                };
                            }
                        }
                    }
                }
            }
            finally
            {
                foreach (var r in rows)
                {
                    r.AttributesLoaded = true;
                    r.AttributesLoading = false;
                }
            }

            FillEntityGrid();
            if (_selectedEntity != null && rows.Contains(_selectedEntity)) FillFieldGrid();
        }

        #endregion

        #region Grid filling

        private void RefreshGrids()
        {
            FillEntityGrid();
            FillFieldGrid();
        }

        private IEnumerable<AuditEntityRow> FilteredEntities()
        {
            var search = _txtEntitySearch.Text.Trim();
            return _entities.Values
                .Where(e => e.Matches(search))
                .Where(e => !_btnOnlyDiff.Checked
                            || e.HasDifference(_envs)
                            || e.Attributes.Values.Any(a => a.HasDifference(_envs)))
                .Where(e => !_btnHideLocked.Checked || !e.IsLockedEverywhere(_envs))
                .OrderBy(e => e.LogicalName);
        }

        private IEnumerable<AuditAttributeRow> FilteredAttributes(AuditEntityRow entity)
        {
            var search = _txtFieldSearch.Text.Trim();
            return entity.Attributes.Values
                .Where(a => a.Matches(search))
                .Where(a => !_btnOnlyDiff.Checked || a.HasDifference(_envs))
                .Where(a => !_btnHideLocked.Checked || !a.IsLockedEverywhere(_envs))
                .OrderBy(a => a.LogicalName);
        }

        private void FillEntityGrid()
        {
            _filling = true;
            try
            {
                var rows = FilteredEntities().Select(e => CreateRow(_gridEntities, e)).ToArray();
                UiHelpers.Refill(_gridEntities, rows);
                _lblEntitiesTitle.Text = _entities.Count == 0 ? "Tables" : $"Tables ({rows.Length} of {_entities.Count})";
            }
            finally
            {
                _filling = false;
            }

            var current = _gridEntities.CurrentRow?.Tag as AuditEntityRow;
            if (current != _selectedEntity)
            {
                _selectedEntity = current;
                ShowFields();
            }
        }

        private void ShowFields()
        {
            if (_selectedEntity != null && !_selectedEntity.AttributesLoaded)
            {
                _gridFields.Rows.Clear();
                UpdateFieldsTitle();
                if (!_selectedEntity.AttributesLoading) _ = LoadAttributesAsync(new[] { _selectedEntity });
                return;
            }
            FillFieldGrid();
        }

        private void FillFieldGrid()
        {
            _filling = true;
            try
            {
                var rows = _selectedEntity == null || !_selectedEntity.AttributesLoaded
                    ? new DataGridViewRow[0]
                    : FilteredAttributes(_selectedEntity).Select(a => CreateRow(_gridFields, a)).ToArray();
                UiHelpers.Refill(_gridFields, rows);
            }
            finally
            {
                _filling = false;
            }
            UpdateFieldsTitle();
        }

        private void UpdateFieldsTitle()
        {
            var e = _selectedEntity;
            if (e == null) { _lblFieldsTitle.Text = "Columns – select a table on the left"; return; }
            var label = e.AnyLabel(_languages);
            var name = string.IsNullOrEmpty(label) ? e.LogicalName : $"{label} ({e.LogicalName})";
            _lblFieldsTitle.Text = e.AttributesLoaded
                ? $"Columns of {name} – {_gridFields.RowCount} of {e.Attributes.Count}"
                : $"Columns of {name} – loading …";
        }

        private DataGridViewRow CreateRow(DataGridView grid, AuditRowBase item)
        {
            var row = new DataGridViewRow { Tag = item };
            row.CreateCells(grid);
            row.Cells[grid.Columns["diff"].Index].Value = item.HasDifference(_envs) ? "≠" : "";
            row.Cells[grid.Columns["logical"].Index].Value = item.LogicalName;
            row.Cells[grid.Columns["origin"].Index].Value = item.IsCustom ? "Custom" : "System";
            if (item is AuditAttributeRow attr) row.Cells[grid.Columns["type"].Index].Value = attr.TypeName;
            foreach (var lcid in _languages)
                row.Cells[grid.Columns[LabelColumnPrefix + lcid].Index].Value = item.Label(lcid);

            for (var i = 0; i < _envs.Count; i++)
            {
                var env = _envs[i];
                var index = grid.Columns[EnvColumnPrefix + i].Index;
                var flag = item.Flag(env);
                if (!flag.Exists)
                    row.Cells[index] = new DataGridViewTextBoxCell { Value = "–" };
                else
                    row.Cells[index].Value = flag.Current;
                row.Cells[index].ToolTipText = FlagTooltip(item, env);
            }
            return row;
        }

        private void UpdateRowVisuals(DataGridView grid, DataGridViewRow row)
        {
            if (!(row.Tag is AuditRowBase item)) return;
            row.Cells[grid.Columns["diff"].Index].Value = item.HasDifference(_envs) ? "≠" : "";
            for (var i = 0; i < _envs.Count; i++)
            {
                var cell = row.Cells[grid.Columns[EnvColumnPrefix + i].Index];
                var flag = item.Flag(_envs[i]);
                if (flag.Exists && cell is DataGridViewCheckBoxCell && !Equals(cell.Value, flag.Current)) cell.Value = flag.Current;
                cell.ToolTipText = FlagTooltip(item, _envs[i]);
            }
            grid.InvalidateRow(row.Index);
        }

        private string FlagTooltip(AuditRowBase item, DataverseEnvironment env)
        {
            var flag = item.Flag(env);
            var what = item is AuditEntityRow ? "Table" : "Column";
            if (!flag.Exists) return $"{what} does not exist in {env.Name}.";

            var sb = new StringBuilder();
            sb.Append($"{env.Name}: auditing {(flag.Current ? "ON" : "off")}");
            if (flag.IsDirty) sb.Append($"  (staged – saved value is “{(flag.Original ? "on" : "off")}”)");
            if (!flag.CanChange) sb.Append("\nLocked: managed property IsAuditEnabled.CanBeChanged = false.");
            if (item is AuditEntityRow entity && entity.AttributesLoaded)
            {
                var attrs = entity.Attributes.Values.Select(a => a.Flag(env)).Where(f => f.Exists).ToList();
                sb.Append($"\nAudited columns: {attrs.Count(f => f.Current)} of {attrs.Count}");
            }
            if (item is AuditAttributeRow attr && !attr.Entity.Flag(env).Current)
                sb.Append("\nNote: the table itself is not audited here – column auditing only takes effect when the table is audited too.");
            if (_orgAudit.TryGetValue(env, out var org) && org.Error == null && !org.IsAuditEnabled)
                sb.Append("\n⚠ Organization-wide auditing is OFF.");
            if (flag.Error != null) sb.Append("\n\nError on last save: ").Append(flag.Error);
            return sb.ToString();
        }

        #endregion

        #region Grid events

        private void Grid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var grid = (DataGridView)sender;
            var column = grid.Columns[e.ColumnIndex];
            if (!(grid.Rows[e.RowIndex].Tag is AuditRowBase item)) return;

            switch (column.Name)
            {
                case "diff":
                    e.CellStyle.ForeColor = UiTheme.WarnText;
                    e.CellStyle.Font = UiTheme.Bold;
                    e.CellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
                    return;
                case "logical":
                    e.CellStyle.Font = UiTheme.Mono;
                    return;
                case "origin":
                case "type":
                    e.CellStyle.ForeColor = UiTheme.TextSecondary;
                    return;
            }

            if (!(column.Tag is DataverseEnvironment env)) return;
            var flag = item.Flag(env);
            Color back;
            if (!flag.Exists) back = UiTheme.MissingBack;
            else if (flag.Error != null) back = UiTheme.ErrorBack;
            else if (flag.IsDirty) back = UiTheme.PendingBack;
            else if (flag.Current) back = UiTheme.OnBack;
            else back = UiTheme.Surface;

            e.CellStyle.BackColor = back;
            e.CellStyle.SelectionBackColor = UiTheme.SelectedOf(back);
            e.CellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            e.CellStyle.ForeColor = !flag.Exists || !flag.CanChange ? UiTheme.TextDisabled : UiTheme.TextPrimary;
        }

        private void Grid_CellBeginEdit(object sender, DataGridViewCellCancelEventArgs e)
        {
            var grid = (DataGridView)sender;
            if (_saving || _busy)
            {
                e.Cancel = true;
                SetStatus("Please wait until loading/saving has finished.");
                return;
            }
            if (!(grid.Columns[e.ColumnIndex].Tag is DataverseEnvironment env) || !(grid.Rows[e.RowIndex].Tag is AuditRowBase item))
            {
                e.Cancel = true;
                return;
            }
            var flag = item.Flag(env);
            if (!flag.Exists || !flag.CanChange)
            {
                e.Cancel = true;
                SetStatus(!flag.Exists ? $"{item.LogicalName} does not exist in {env.Name}." : $"Auditing of {item.LogicalName} cannot be changed in {env.Name}.");
            }
        }

        private void Grid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_filling || e.RowIndex < 0) return;
            var grid = (DataGridView)sender;
            if (!(grid.Columns[e.ColumnIndex].Tag is DataverseEnvironment env)) return;
            var row = grid.Rows[e.RowIndex];
            if (!(row.Tag is AuditRowBase item)) return;

            var flag = item.Flag(env);
            var value = row.Cells[e.ColumnIndex].Value is bool b && b;
            if (!flag.Exists || flag.Current == value) return;

            flag.Current = value;
            flag.Error = null;
            UpdateRowVisuals(grid, row);
            if (item is AuditAttributeRow) RefreshEntityRow(_selectedEntity);
            UpdatePendingCount();
        }

        private void RefreshEntityRow(AuditEntityRow entity)
        {
            if (entity == null) return;
            var row = _gridEntities.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => ReferenceEquals(r.Tag, entity));
            if (row == null) return;
            _filling = true;
            try { UpdateRowVisuals(_gridEntities, row); }
            finally { _filling = false; }
        }

        private void GridEntities_SelectionChanged(object sender, EventArgs e)
        {
            if (_filling) return;
            var current = _gridEntities.CurrentRow?.Tag as AuditEntityRow;
            if (current == _selectedEntity) return;
            _selectedEntity = current;
            ShowFields();
        }

        private void Grid_ColumnHeaderMouseClick(object sender, DataGridViewCellMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            var grid = (DataGridView)sender;
            if (!(grid.Columns[e.ColumnIndex].Tag is DataverseEnvironment env)) return;
            if (_saving || _busy) return;

            var what = grid == _gridEntities ? "tables" : "columns";
            var menu = new ContextMenuStrip { Font = UiTheme.Regular };
            menu.Items.Add($"Enable auditing for all visible {what} in {env.Name}", null, (s, a) => SetAllVisible(grid, env, true));
            menu.Items.Add($"Disable auditing for all visible {what} in {env.Name}", null, (s, a) => SetAllVisible(grid, env, false));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add($"Undo staged changes of visible {what} in {env.Name}", null, (s, a) => RevertVisible(grid, env));
            UiTheme.ApplyToolStrip(menu);
            menu.Closed += (s, a) => BeginInvoke(new Action(menu.Dispose));
            menu.Show(Cursor.Position);
        }

        #endregion

        #region Bulk actions

        private static IEnumerable<AuditRowBase> VisibleItems(DataGridView grid) =>
            grid.Rows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<AuditRowBase>();

        private void SetAllVisible(DataGridView grid, DataverseEnvironment env, bool value)
        {
            var count = 0;
            foreach (var item in VisibleItems(grid))
            {
                var flag = item.Flag(env);
                if (!flag.Exists || !flag.CanChange || flag.Current == value) continue;
                flag.Current = value;
                flag.Error = null;
                count++;
            }
            RefreshGrids();
            UpdatePendingCount();
            SetStatus($"{count} item(s) {(value ? "enabled" : "disabled")} in {env.Name} (staged).");
        }

        private void RevertVisible(DataGridView grid, DataverseEnvironment env)
        {
            foreach (var item in VisibleItems(grid))
            {
                var flag = item.Flag(env);
                flag.Current = flag.Original;
                flag.Error = null;
            }
            RefreshGrids();
            UpdatePendingCount();
        }

        private void ToggleSelected(DataGridView grid, DataverseEnvironment env)
        {
            if (_saving || _busy) return;
            var items = grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<AuditRowBase>().ToList();
            var flags = items.Select(i => i.Flag(env)).Where(f => f.Exists && f.CanChange).ToList();
            if (flags.Count == 0) return;
            var newValue = !flags.All(f => f.Current);
            foreach (var f in flags)
            {
                f.Current = newValue;
                f.Error = null;
            }
            RefreshGrids();
            UpdatePendingCount();
        }

        private async void AlignSelected(DataverseEnvironment source, IList<DataverseEnvironment> targets)
        {
            if (_saving || _busy) return;
            var rows = _gridEntities.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<AuditEntityRow>().ToList();
            if (rows.Count == 0)
            {
                MessageBox.Show(this, "Select one or more tables on the left first (Ctrl/Shift + click).", "Align", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            await LoadAttributesAsync(rows);

            int changed = 0, skipped = 0;
            foreach (var entity in rows)
            {
                var result = Align(entity, source, targets);
                changed += result.changed;
                skipped += result.skipped;
                foreach (var attr in entity.Attributes.Values)
                {
                    result = Align(attr, source, targets);
                    changed += result.changed;
                    skipped += result.skipped;
                }
            }

            RefreshGrids();
            UpdatePendingCount();
            _log.Info($"Align {source.Name} → {string.Join(", ", targets)} ({rows.Count} table(s)): {changed} change(s) staged"
                      + (skipped > 0 ? $", {skipped} skipped (missing in target or locked)" : "")
                      + ". Not saved yet.");
        }

        private static (int changed, int skipped) Align(AuditRowBase item, DataverseEnvironment source, IEnumerable<DataverseEnvironment> targets)
        {
            var src = item.Flag(source);
            if (!src.Exists) return (0, 0);
            int changed = 0, skipped = 0;
            foreach (var target in targets)
            {
                var dst = item.Flag(target);
                if (dst.Exists && dst.Current == src.Current) continue;
                if (!dst.Exists || !dst.CanChange)
                {
                    skipped++;
                    continue;
                }
                dst.Current = src.Current;
                dst.Error = null;
                changed++;
            }
            return (changed, skipped);
        }

        private void DiscardChanges()
        {
            if (_saving) return;
            foreach (var entity in _entities.Values)
            {
                foreach (var f in entity.Flags.Values) { f.Current = f.Original; f.Error = null; }
                foreach (var a in entity.Attributes.Values)
                    foreach (var f in a.Flags.Values) { f.Current = f.Original; f.Error = null; }
            }
            RefreshGrids();
            UpdatePendingCount();
            SetStatus("Changes discarded.");
        }

        #endregion

        #region Saving

        private List<AuditChange> CollectChanges()
        {
            var list = new List<AuditChange>();
            foreach (var entity in _entities.Values)
            {
                foreach (var env in _envs)
                {
                    var f = entity.Flag(env);
                    if (f.IsDirty) list.Add(new AuditChange { Env = env, Entity = entity, Flag = f, NewValue = f.Current });
                }
                foreach (var attr in entity.Attributes.Values)
                {
                    foreach (var env in _envs)
                    {
                        var f = attr.Flag(env);
                        if (f.IsDirty) list.Add(new AuditChange { Env = env, Entity = entity, Attribute = attr, Flag = f, NewValue = f.Current });
                    }
                }
            }
            return list;
        }

        private async void SaveChanges()
        {
            if (_saving || _busy) return;
            if (_gridEntities.IsCurrentCellInEditMode) _gridEntities.EndEdit();
            if (_gridFields.IsCurrentCellInEditMode) _gridFields.EndEdit();

            var changes = CollectChanges();
            if (changes.Count == 0)
            {
                SetStatus("Nothing to save.");
                return;
            }

            var publish = _btnPublish.Checked;
            var groups = changes.GroupBy(c => c.Env).ToList();
            var summary = string.Join(Environment.NewLine, groups.Select(g =>
                $"• {g.Key.Name}: {g.Count(c => c.Attribute == null)} table(s), {g.Count(c => c.Attribute != null)} column(s)  →  {g.Count(c => c.NewValue)}× on, {g.Count(c => !c.NewValue)}× off"));
            var prod = groups.Where(g => g.Key.LooksLikeProduction).Select(g => g.Key.Name).ToList();
            var text = $"Save these changes?\n\n{summary}"
                       + (prod.Count > 0 ? $"\n\n⚠ Production: {string.Join(", ", prod)}" : "")
                       + (publish ? "\n\nAfterwards only the affected tables are published." : "\n\nNothing will be published.")
                       + "\n\nSaving runs in the background (environments in parallel) – you can keep working.";
            if (MessageBox.Show(this, text, "Save auditing", MessageBoxButtons.YesNo,
                    prod.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes) return;

            _saving = true;
            UpdateCommandState();
            var total = changes.Count + (publish ? groups.Count : 0);
            int done = 0, failed = 0;
            _progress.Style = ProgressBarStyle.Blocks;
            _progress.Maximum = Math.Max(1, total);
            _progress.Value = 0;
            _progress.Visible = true;

            var progress = new Progress<SaveProgress>(p =>
            {
                done++;
                if (!p.Success) failed++;
                _progress.Value = Math.Min(done, _progress.Maximum);
                _lblStatus.Text = $"Saving {done}/{total} – {p.Message}";
                if (p.Success) _log.Success(p.Message); else _log.Error(p.Message);
                _gridEntities.Invalidate();
                _gridFields.Invalidate();
            });

            var sw = Stopwatch.StartNew();
            _log.Info($"Saving {changes.Count} change(s) in {groups.Count} environment(s) …");
            try
            {
                await Task.WhenAll(groups.Select(g => Task.Run(() => SaveEnvironment(g.Key, g.ToList(), publish, progress))));
            }
            catch (Exception ex)
            {
                _log.Error("Unexpected error: " + MetadataHelper.ErrorText(ex));
            }
            finally
            {
                // Progress callbacks are posted to the UI thread – let them run first
                await Task.Yield();
                _saving = false;
                _progress.Visible = false;
                RefreshGrids();
                UpdatePendingCount();
                UpdateCommandState();
                var msg = failed == 0
                    ? $"Done: {changes.Count} change(s) saved in {sw.Elapsed.TotalSeconds:0} s."
                    : $"Done with {failed} error(s) – see the log; affected cells are red.";
                SetStatus(msg);
                if (failed == 0) _log.Success(msg); else _log.Error(msg);
            }
        }

        /// <summary>Runs on a background thread. Sequential per environment (metadata lock), environments in parallel.</summary>
        private static void SaveEnvironment(DataverseEnvironment env, List<AuditChange> changes, bool publish, IProgress<SaveProgress> progress)
        {
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var byEntity in changes.GroupBy(c => c.Entity.LogicalName))
            {
                // table before its columns
                foreach (var change in byEntity.OrderBy(c => c.Attribute == null ? 0 : 1))
                {
                    try
                    {
                        if (change.Attribute == null)
                            SetEntityAudit(env.Service, change.Entity.LogicalName, change.NewValue);
                        else
                            SetAttributeAudit(env.Service, change.Entity.LogicalName, change.Attribute.LogicalName, change.NewValue);

                        change.Flag.Original = change.NewValue;
                        change.Flag.Error = null;
                        touched.Add(change.Entity.LogicalName);
                        progress.Report(new SaveProgress(true, $"[{env.Name}] {change.Display}: auditing {(change.NewValue ? "enabled" : "disabled")}"));
                    }
                    catch (Exception ex)
                    {
                        change.Flag.Error = MetadataHelper.ErrorText(ex);
                        progress.Report(new SaveProgress(false, $"[{env.Name}] {change.Display}: {change.Flag.Error}"));
                    }
                }
            }

            if (!publish) return;
            if (touched.Count == 0)
            {
                progress.Report(new SaveProgress(true, $"[{env.Name}] Nothing to publish."));
                return;
            }
            try
            {
                MetadataHelper.Publish(env.Service, touched);
                progress.Report(new SaveProgress(true, $"[{env.Name}] Published: {string.Join(", ", touched)}"));
            }
            catch (Exception ex)
            {
                progress.Report(new SaveProgress(false, $"[{env.Name}] Publishing failed: {MetadataHelper.ErrorText(ex)}"));
            }
        }

        private static void SetEntityAudit(IOrganizationService service, string entity, bool value)
        {
            var metadata = ((RetrieveEntityResponse)service.Execute(new RetrieveEntityRequest
            {
                LogicalName = entity,
                EntityFilters = EntityFilters.Entity,
                RetrieveAsIfPublished = true
            })).EntityMetadata;

            if (metadata.IsAuditEnabled == null) metadata.IsAuditEnabled = new BooleanManagedProperty(value);
            else metadata.IsAuditEnabled.Value = value;

            service.Execute(new UpdateEntityRequest { Entity = metadata, MergeLabels = true });
        }

        private static void SetAttributeAudit(IOrganizationService service, string entity, string attribute, bool value)
        {
            var metadata = ((RetrieveAttributeResponse)service.Execute(new RetrieveAttributeRequest
            {
                EntityLogicalName = entity,
                LogicalName = attribute,
                RetrieveAsIfPublished = true
            })).AttributeMetadata;

            if (metadata.IsAuditEnabled == null) metadata.IsAuditEnabled = new BooleanManagedProperty(value);
            else metadata.IsAuditEnabled.Value = value;

            service.Execute(new UpdateAttributeRequest { EntityName = entity, Attribute = metadata, MergeLabels = true });
        }

        #endregion

        #region Status

        private IDisposable BeginOperation(string message)
        {
            _operations++;
            SetStatus(message);
            if (!_saving)
            {
                _progress.Style = ProgressBarStyle.Marquee;
                _progress.Visible = true;
            }
            UpdateCommandState();
            return new ActionDisposable(() =>
            {
                _operations--;
                if (_operations == 0 && !_saving)
                {
                    _progress.Visible = false;
                    _progress.Style = ProgressBarStyle.Blocks;
                    SetStatus("Ready.");
                }
                UpdateCommandState();
            });
        }

        private void SetStatus(string text)
        {
            _lblStatus.Text = text;
        }

        private void UpdatePendingCount()
        {
            var count = CollectChanges().Count;
            _btnSave.Text = $"Save ({count})";
            _lblPending.Text = count > 0 ? $"{count} unsaved change(s)" : string.Empty;
            UpdateCommandState();
        }

        private void UpdateCommandState()
        {
            if (_btnSave == null) return;
            var pending = _lblPending.Text.Length > 0;
            _btnLoad.Enabled = !_busy && !_saving;
            _txtFilter.Enabled = !_saving;
            _btnSave.Enabled = !_busy && !_saving && pending;
            _btnDiscard.Enabled = !_saving && pending;
            _ddAlign.Enabled = !_saving && !_busy && _envs.Count > 1;
        }

        public override void ClosingPlugin(PluginCloseInfo info)
        {
            if (_saving)
            {
                // never close while writing metadata in the background
                if (!info.Silent) MessageBox.Show(this, "Still saving. Please wait until it has finished.", "Audit Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
                info.Cancel = true;
                return;
            }
            if (CollectChanges().Count > 0 && !info.Silent)
            {
                // one question instead of ours + XrmToolBox's "Are you sure…"
                info.Cancel = MessageBox.Show(this, "Discard unsaved changes and close?", "Audit Manager",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes;
                return;
            }
            base.ClosingPlugin(info);
        }

        #endregion
    }
}
