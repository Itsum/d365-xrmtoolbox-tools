using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using D365Tools.Shared;
using McTools.Xrm.Connection;
using Microsoft.Xrm.Sdk;
using XrmToolBox.Extensibility;
using XrmToolBox.Extensibility.Interfaces;
using Label = System.Windows.Forms.Label;

namespace D365Tools.TranslationManager
{
    /// <summary>
    /// Shows the labels of an environment (tables, columns, choices, forms) in any installed language,
    /// lets you edit them inline and saves everything in the background.
    /// </summary>
    public class TranslationManagerControl : PluginControlBase, IGitHubPlugin, IHelpPlugin
    {
        private const string LangColumnPrefix = "lang_";
        private static readonly ItemArea[] Areas = { ItemArea.Entity, ItemArea.Fields, ItemArea.Options, ItemArea.Forms };

        private readonly Dictionary<string, EntityTranslations> _details = new Dictionary<string, EntityTranslations>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _loading = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<ItemArea, DataGridView> _grids = new Dictionary<ItemArea, DataGridView>();
        private readonly Timer _searchTimer = new Timer { Interval = 250 };
        private readonly ToolSettings _settings;

        private List<EntityListItem> _entityList = new List<EntityListItem>();
        private EntityListItem _selected;
        private DataverseEnvironment _env;
        private EnvLanguages _envLanguages;
        private List<int> _languages = new List<int>();
        private bool _busy, _saving, _filling;
        private int _operations;

        private ToolStrip _toolbar;
        private ToolStripTextBox _txtFilter;
        private ToolStripButton _btnLoad, _btnReloadEntity, _btnSave, _btnDiscard, _btnOnlyMissing, _btnShowDescriptions, _btnPublish;
        private ToolStripDropDownButton _ddLanguages, _ddTheme;
        private Label _lblEnvInfo;
        private DataGridView _gridEntities;
        private TextBox _txtEntitySearch, _txtDetailSearch;
        private Label _lblEntitiesTitle, _lblDetailTitle, _lblExplainTitle;
        private RichTextBox _txtExplain;
        private PivotBar _pivot;
        private SplitContainer _outerSplit, _innerSplit, _rightSplit;
        private StatusStrip _statusStrip;
        private ToolStripStatusLabel _lblStatus, _lblPending;
        private ToolStripProgressBar _progress;
        private LogPanel _log;

        public TranslationManagerControl()
        {
            _settings = ToolSettings.Load(typeof(TranslationManagerPlugin));
            UiTheme.SetMode(_settings.Theme);
            BuildUi();
            _searchTimer.Tick += (s, e) =>
            {
                _searchTimer.Stop();
                FillEntityGrid();
                FillDetailGrids();
            };
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
            }
            base.Dispose(disposing);
        }

        private EntityTranslations CurrentDetails =>
            _selected != null && _details.TryGetValue(_selected.LogicalName, out var d) ? d : null;

        private ItemArea ActiveArea => (ItemArea)_pivot.SelectedIndex;
        private DataGridView ActiveGrid => _grids[ActiveArea];

        #region UI

        private void BuildUi()
        {
            SuspendLayout();
            Font = UiTheme.Regular;

            // --- Toolbar ---
            _toolbar = new ToolStrip();
            _btnLoad = UiTheme.Button("Load tables", "Reload the table list (F5)", (s, e) => ExecuteMethod(LoadEntities));
            _txtFilter = new ToolStripTextBox
            {
                AutoSize = false,
                Width = 220,
                ToolTipText = "Optional: logical names, comma separated (e.g. account, contact, salesorder).\nEmpty = all tables with a display name. Enter = Load."
            };
            _txtFilter.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                ExecuteMethod(LoadEntities);
            };
            _btnReloadEntity = UiTheme.Button("Reload table", "Read the labels of the selected table again from Dataverse (discards its unsaved changes)", (s, e) => ReloadCurrentEntity());
            _btnOnlyMissing = UiTheme.Toggle("Only missing", "Only show texts that are empty in one of the selected languages", false, (s, e) => FillDetailGrids());
            _btnShowDescriptions = UiTheme.Toggle("Descriptions", "Also show column descriptions (tooltips) in the Columns tab", false, (s, e) => FillDetailGrids());
            _ddLanguages = new ToolStripDropDownButton("Languages") { ToolTipText = "Which languages to show and edit (saved per tool)" };
            Menus.KeepOpenOnCheck(_ddLanguages);
            _ddTheme = Menus.ThemeMenu(ChangeTheme);

            _btnSave = UiTheme.PrimaryButton("Save (0)", "Save all changes in the background (Ctrl+S)", (s, e) => SaveChanges());
            _btnDiscard = UiTheme.Button("Discard", "Undo all unsaved changes", (s, e) => DiscardChanges());
            _btnPublish = UiTheme.Toggle("Publish after save", "After saving, publish only the affected tables/choices once", true, null);
            _btnSave.Alignment = ToolStripItemAlignment.Right;
            _btnDiscard.Alignment = ToolStripItemAlignment.Right;
            _btnPublish.Alignment = ToolStripItemAlignment.Right;

            _toolbar.Items.AddRange(new ToolStripItem[]
            {
                _btnLoad,
                new ToolStripLabel("Tables:"),
                _txtFilter,
                new ToolStripSeparator(),
                _btnReloadEntity,
                new ToolStripSeparator(),
                _btnOnlyMissing,
                _btnShowDescriptions,
                new ToolStripSeparator(),
                _ddLanguages,
                _ddTheme,
                _btnSave,
                _btnDiscard,
                _btnPublish
            });
            UiTheme.StyleToolStrip(_toolbar);

            // --- Environment info ---
            _lblEnvInfo = UiTheme.Role(new Label
            {
                Dock = DockStyle.Top,
                Height = 30,
                Padding = new Padding(10, 0, 10, 0),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Not connected – pick an environment (DEV, TEST or PROD) in XrmToolBox.",
                AutoEllipsis = true
            }, UiRole.Alt);

            // --- Left: table list ---
            _gridEntities = new DataGridView { SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, ReadOnly = true };
            UiTheme.StyleGrid(_gridEntities);
            var stateCol = UiTheme.TextColumn("state", "", 26);
            stateCol.ToolTipText = "● = unsaved changes,  ! = display name missing in a selected language";
            _gridEntities.Columns.Add(stateCol);
            _gridEntities.Columns.Add(UiTheme.TextColumn("logical", "Logical name", 150));
            _gridEntities.CellFormatting += GridEntities_CellFormatting;
            _gridEntities.SelectionChanged += GridEntities_SelectionChanged;
            var entityPanel = UiHelpers.GridPanel(_gridEntities, "Tables", "Search …", out _lblEntitiesTitle, out _txtEntitySearch);
            _txtEntitySearch.TextChanged += (s, e) => { _searchTimer.Stop(); _searchTimer.Start(); };

            // --- Right: tabs + explanation ---
            _pivot = new PivotBar();
            var gridHost = new Panel { Dock = DockStyle.Fill };
            foreach (var area in Areas)
            {
                _pivot.AddItem(AreaTitle(area));
                var grid = CreateDetailGrid(area);
                grid.Dock = DockStyle.Fill;
                grid.Visible = area == ItemArea.Entity;
                _grids[area] = grid;
                gridHost.Controls.Add(grid);
            }
            _pivot.SelectedIndexChanged += (s, e) =>
            {
                foreach (var area in Areas) _grids[area].Visible = area == ActiveArea;
                UpdateExplain();
            };

            var detailHeader = UiHelpers.Header("Select a table on the left", "Search all tabs (name, text) …", out _lblDetailTitle, out _txtDetailSearch);
            _txtDetailSearch.TextChanged += (s, e) => { _searchTimer.Stop(); _searchTimer.Start(); };

            var explainPanel = UiTheme.Role(new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 6) }, UiRole.Explain);
            _lblExplainTitle = UiTheme.Role(new Label { Dock = DockStyle.Top, Height = 24, Font = UiTheme.Title, AutoEllipsis = true }, UiRole.Explain);
            _txtExplain = UiTheme.Role(new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                Font = UiTheme.Regular,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                DetectUrls = false
            }, UiRole.Explain);
            explainPanel.Controls.Add(_txtExplain);
            explainPanel.Controls.Add(_lblExplainTitle);

            _rightSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5, FixedPanel = FixedPanel.Panel2 };
            _rightSplit.Panel1.Controls.Add(gridHost);
            _rightSplit.Panel2.Controls.Add(explainPanel);

            var rightPanel = new Panel { Dock = DockStyle.Fill };
            rightPanel.Controls.Add(_rightSplit);
            rightPanel.Controls.Add(_pivot);
            rightPanel.Controls.Add(detailHeader);

            _innerSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 5 };
            _innerSplit.Panel1.Controls.Add(entityPanel);
            _innerSplit.Panel2.Controls.Add(rightPanel);

            // --- Log ---
            _log = new LogPanel();
            _outerSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 5, FixedPanel = FixedPanel.Panel2 };
            _outerSplit.Panel1.Controls.Add(_innerSplit);
            _outerSplit.Panel2.Controls.Add(UiHelpers.LogArea(_log));

            // --- Status bar ---
            _statusStrip = new StatusStrip { SizingGrip = false, Font = UiTheme.Regular };
            _lblStatus = new ToolStripStatusLabel("Ready.") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            _lblPending = new ToolStripStatusLabel { Font = UiTheme.Bold };
            _progress = new ToolStripProgressBar { Width = 220, Visible = false };
            _statusStrip.Items.AddRange(new ToolStripItem[] { _lblStatus, _lblPending, _progress });

            Controls.Add(_outerSplit);
            Controls.Add(_lblEnvInfo);
            Controls.Add(_toolbar);
            Controls.Add(_statusStrip);

            RebuildLanguageColumns();
            RebuildLanguageMenu();
            UpdateExplain();
            UpdateCommandState();
            ResumeLayout();
        }

        private DataGridView CreateDetailGrid(ItemArea area)
        {
            var grid = new DataGridView
            {
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                MultiSelect = false,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2
            };
            UiTheme.StyleGrid(grid);

            string[] headers;
            int[] widths;
            switch (area)
            {
                case ItemArea.Entity:
                    headers = new[] { "Property", "Metadata", "Logical name" };
                    widths = new[] { 110, 150, 130 };
                    break;
                case ItemArea.Fields:
                    headers = new[] { "Logical name", "Type", "Property" };
                    widths = new[] { 180, 105, 95 };
                    break;
                case ItemArea.Options:
                    headers = new[] { "Column / choice", "Value", "Kind" };
                    widths = new[] { 210, 75, 95 };
                    break;
                default:
                    headers = new[] { "Form", "Element", "Name" };
                    widths = new[] { 200, 100, 150 };
                    break;
            }

            grid.Columns.Add(UiTheme.TextColumn("info1", headers[0], widths[0]));
            grid.Columns.Add(UiTheme.TextColumn("info2", headers[1], widths[1]));
            grid.Columns.Add(UiTheme.TextColumn("info3", headers[2], widths[2]));

            grid.CellFormatting += DetailGrid_CellFormatting;
            grid.CellBeginEdit += DetailGrid_CellBeginEdit;
            grid.CellValueChanged += DetailGrid_CellValueChanged;
            grid.CurrentCellChanged += (s, e) => { if (grid.Visible) UpdateExplain(); };
            grid.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex >= 0 && grid.Columns[e.ColumnIndex].Tag is int) grid.BeginEdit(true);
            };
            return grid;
        }

        /// <summary>One column per selected language in the table list and in every tab.</summary>
        private void RebuildLanguageColumns()
        {
            foreach (var grid in _grids.Values.Concat(new[] { _gridEntities }))
            {
                if (grid.IsCurrentCellInEditMode) grid.EndEdit();
                grid.Rows.Clear();
                foreach (var col in grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Name.StartsWith(LangColumnPrefix)).ToList())
                    grid.Columns.Remove(col);

                var isList = grid == _gridEntities;
                foreach (var lcid in _languages)
                {
                    var isBase = lcid == _envLanguages?.Base;
                    var col = UiTheme.TextColumn(LangColumnPrefix + lcid, Languages.Name(lcid) + (isBase && !isList ? " (base)" : ""), isList ? 110 : 170, readOnly: isList, fill: true);
                    col.Tag = lcid;
                    col.ToolTipText = Languages.Label(lcid) + (isBase ? " – base language" : "") + (isList ? "" : "\nDouble-click or F2 to edit.");
                    grid.Columns.Add(col);
                }
            }
        }

        private void RebuildLanguageMenu()
        {
            Menus.FillLanguageMenu(_ddLanguages, _envLanguages, _languages, ChangeLanguages);
        }

        private void ChangeLanguages(List<int> selection)
        {
            var isDefault = _envLanguages != null && selection.SequenceEqual(_envLanguages.Defaults);
            _languages = selection;
            _settings.Languages = isDefault ? new int[0] : selection.ToArray();
            _settings.Save(typeof(TranslationManagerPlugin));
            RebuildLanguageColumns();
            RebuildLanguageMenu();
            ApplyTheme();
            UpdateEnvInfo();
            FillEntityGrid();
            FillDetailGrids();
        }

        private void ChangeTheme(ThemeMode mode)
        {
            _settings.Theme = mode;
            _settings.Save(typeof(TranslationManagerPlugin));
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
            UpdateEnvInfo();
        }

        private static string AreaTitle(ItemArea area)
        {
            switch (area)
            {
                case ItemArea.Entity: return "Table";
                case ItemArea.Fields: return "Columns";
                case ItemArea.Options: return "Choices";
                default: return "Forms";
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            UiHelpers.ConfigureSplit(_outerSplit, 250, 50, _outerSplit.Height - 130);
            UiHelpers.ConfigureSplit(_innerSplit, 220, 300, Math.Max(300, (int)(_innerSplit.Width * 0.30)));
            UiHelpers.ConfigureSplit(_rightSplit, 150, 80, _rightSplit.Height - 170);
            _log.Info("How to: connect → pick a table on the left → type in a language column (double-click/F2) → Save. The box below always explains WHAT and WHERE you are changing.");
            if (Service != null && _env == null) OnConnected(ConnectionDetail);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.S)) { SaveChanges(); return true; }
            if (keyData == Keys.F5) { ExecuteMethod(LoadEntities); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        #endregion

        #region Connection + languages

        public override void UpdateConnection(IOrganizationService newService, ConnectionDetail detail, string actionName, object parameter)
        {
            base.UpdateConnection(newService, detail, actionName, parameter);
            OnConnected(detail);
        }

        private async void OnConnected(ConnectionDetail detail)
        {
            if (AllDirty().Any()) _log.Warn("Environment changed – unsaved changes of the previous environment were discarded.");
            _env = detail != null && Service != null ? new DataverseEnvironment(detail, Service, true) : null;
            _details.Clear();
            _loading.Clear();
            _entityList = new List<EntityListItem>();
            _selected = null;
            _envLanguages = null;
            _languages = new List<int>();
            RebuildLanguageColumns();
            RebuildLanguageMenu();
            FillEntityGrid();
            FillDetailGrids();
            UpdatePendingCount();
            UpdateEnvInfo();
            if (_env == null) return;

            _log.Info($"Connected to {_env.Name} ({_env.Url}).");
            var svc = Service;
            EnvLanguages languages;
            try
            {
                languages = await Task.Run(() => MetadataHelper.RetrieveLanguages(svc));
            }
            catch (Exception ex)
            {
                _log.Warn("Could not read the installed languages: " + MetadataHelper.ErrorText(ex));
                languages = new EnvLanguages { Base = 1033, Provisioned = new[] { 1033 } };
            }
            if (svc != Service) return;

            _envLanguages = languages;
            _env.Languages = languages;
            _languages = Languages.Order(Languages.Resolve(_settings.Languages, languages), languages);
            RebuildLanguageColumns();
            RebuildLanguageMenu();
            ApplyTheme();
            FillEntityGrid();
            FillDetailGrids();
            ExecuteMethod(LoadEntities);
        }

        private void UpdateEnvInfo()
        {
            if (_env == null)
            {
                _lblEnvInfo.Text = "Not connected – pick an environment (DEV, TEST or PROD) in XrmToolBox.";
                _lblEnvInfo.BackColor = UiTheme.SurfaceAlt;
                _lblEnvInfo.ForeColor = UiTheme.TextSecondary;
                return;
            }
            var langs = _envLanguages != null
                ? $"Base language: {Languages.Name(_envLanguages.Base)}  ·  Installed: {string.Join(", ", _envLanguages.All.Select(Languages.Name))}  ·  Editing: {string.Join(", ", _languages.Select(Languages.Name))}"
                : "Reading installed languages …";
            _lblEnvInfo.Text = $"Environment: {_env.Name}   ·   {_env.Url}   ·   {langs}";
            _lblEnvInfo.BackColor = _env.LooksLikeProduction ? UiTheme.WarnBack : UiTheme.SurfaceAlt;
            _lblEnvInfo.ForeColor = _env.LooksLikeProduction ? UiTheme.WarnText : UiTheme.TextSecondary;
        }

        #endregion

        #region Loading

        /// <summary>Loads the table list (public: XrmToolBox re-invokes it by name after connecting).</summary>
        public async void LoadEntities()
        {
            if (_busy || _saving || Service == null) return;
            var names = MetadataHelper.ParseNames(_txtFilter.Text);
            var svc = Service;
            _busy = true;
            UpdateCommandState();
            try
            {
                using (BeginOperation(names.Count == 0 ? "Loading table list …" : $"Loading {names.Count} table(s) …"))
                {
                    _entityList = await Task.Run(() => TranslationService.LoadEntityList(svc, names));
                }
                var notFound = names.Where(n => _entityList.All(e => e.LogicalName != n)).ToList();
                if (notFound.Count > 0) _log.Warn("Not found: " + string.Join(", ", notFound));
                _log.Info($"{_entityList.Count} table(s) loaded.");
                FillEntityGrid();
                SetStatus($"{_entityList.Count} table(s) – pick one on the left.");
            }
            catch (Exception ex)
            {
                _log.Error("Loading tables failed: " + MetadataHelper.ErrorText(ex));
                MessageBox.Show(this, MetadataHelper.ErrorText(ex), "Loading failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _busy = false;
                UpdateCommandState();
            }
        }

        private async Task LoadDetailsAsync(string entity)
        {
            if (_loading.Contains(entity) || Service == null) return;
            _loading.Add(entity);
            UpdateDetailTitle();
            var svc = Service;
            var preferred = _languages.Count > 0 ? _languages[0] : _envLanguages?.Base ?? 1033;
            try
            {
                using (BeginOperation($"Loading labels of {entity} …"))
                {
                    var sw = Stopwatch.StartNew();
                    var details = await Task.Run(() => TranslationService.LoadEntity(svc, entity, preferred));
                    if (svc != Service) return;
                    _details[entity] = details;
                    _log.Info($"{entity}: {details.FieldItems.Count(i => i.Kind == ItemKind.AttributeDisplayName)} columns, {details.OptionItems.Count} choice values, " +
                              $"{details.FormItems.Select(f => f.FormId).Distinct().Count()} forms with {details.FormItems.Count} labels ({sw.Elapsed.TotalSeconds:0.0} s).");
                }
            }
            catch (Exception ex)
            {
                _log.Error($"{entity}: loading failed – {MetadataHelper.ErrorText(ex)}");
            }
            finally
            {
                _loading.Remove(entity);
            }
            if (_selected?.LogicalName == entity) ShowDetails();
            RefreshEntityListRow(entity);
        }

        private void ReloadCurrentEntity()
        {
            if (_selected == null || _saving) return;
            var details = CurrentDetails;
            if (details != null && details.All.Any(i => i.Dirty) &&
                MessageBox.Show(this, $"Discard unsaved changes in {_selected.LogicalName} and reload?", "Reload",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            _details.Remove(_selected.LogicalName);
            UpdatePendingCount();
            ShowDetails();
        }

        #endregion

        #region Table list

        private void FillEntityGrid()
        {
            _filling = true;
            try
            {
                var search = _txtEntitySearch.Text.Trim();
                var rows = _entityList.Where(e => e.Matches(search)).Select(CreateEntityRow).ToArray();
                UiHelpers.Refill(_gridEntities, rows);
                _lblEntitiesTitle.Text = _entityList.Count == 0 ? "Tables" : $"Tables ({rows.Length} of {_entityList.Count})";
            }
            finally
            {
                _filling = false;
            }
            var current = _gridEntities.CurrentRow?.Tag as EntityListItem;
            if (current != _selected)
            {
                _selected = current;
                ShowDetails();
            }
            UpdateCommandState();
        }

        private DataGridViewRow CreateEntityRow(EntityListItem item)
        {
            var row = new DataGridViewRow { Tag = item };
            var values = new List<object> { EntityState(item, out var tooltip), item.LogicalName };
            values.AddRange(_languages.Select(l => (object)item.Label(l)));
            row.CreateCells(_gridEntities, values.ToArray());
            row.Cells[0].ToolTipText = tooltip;
            return row;
        }

        private string EntityState(EntityListItem item, out string tooltip)
        {
            var pending = _details.TryGetValue(item.LogicalName, out var d) ? d.All.Count(i => i.Dirty) : 0;
            if (pending > 0)
            {
                tooltip = $"{pending} unsaved change(s)";
                return "●";
            }
            var missing = _languages.Where(l => string.IsNullOrWhiteSpace(item.Label(l))).ToList();
            if (missing.Count > 0)
            {
                tooltip = "Display name missing in: " + string.Join(", ", missing.Select(Languages.Name));
                return "!";
            }
            tooltip = null;
            return string.Empty;
        }

        private void RefreshEntityListRow(string entity)
        {
            var row = _gridEntities.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => (r.Tag as EntityListItem)?.LogicalName == entity);
            if (row == null) return;
            var item = (EntityListItem)row.Tag;
            row.Cells["state"].Value = EntityState(item, out var tooltip);
            row.Cells["state"].ToolTipText = tooltip;
            foreach (var lcid in _languages) row.Cells[LangColumnPrefix + lcid].Value = item.Label(lcid);
        }

        private void GridEntities_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var col = _gridEntities.Columns[e.ColumnIndex].Name;
            if (col == "state")
            {
                e.CellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
                e.CellStyle.Font = UiTheme.Bold;
                e.CellStyle.ForeColor = Equals(e.Value, "●") ? UiTheme.Accent : UiTheme.WarnText;
            }
            else if (col == "logical")
            {
                e.CellStyle.Font = UiTheme.Mono;
            }
        }

        private void GridEntities_SelectionChanged(object sender, EventArgs e)
        {
            if (_filling) return;
            var current = _gridEntities.CurrentRow?.Tag as EntityListItem;
            if (current == _selected) return;
            _selected = current;
            ShowDetails();
            UpdateCommandState();
        }

        #endregion

        #region Detail tabs

        private void ShowDetails()
        {
            FillDetailGrids();
            if (_selected != null && CurrentDetails == null) _ = LoadDetailsAsync(_selected.LogicalName);
        }

        private void FillDetailGrids()
        {
            var details = CurrentDetails;
            var search = _txtDetailSearch.Text.Trim();
            _filling = true;
            try
            {
                foreach (var area in Areas)
                {
                    var grid = _grids[area];
                    if (grid.IsCurrentCellInEditMode) grid.EndEdit();
                    var all = details?.ItemsFor(area) ?? new List<TranslationItem>();
                    var rows = all
                        .Where(i => i.Matches(search))
                        .Where(i => !_btnOnlyMissing.Checked || i.IsMissingAny(_languages) || i.Dirty)
                        .Where(i => area != ItemArea.Fields || _btnShowDescriptions.Checked || i.Kind != ItemKind.AttributeDescription || i.Dirty)
                        .Select(i => CreateDetailRow(grid, i))
                        .ToArray();
                    UiHelpers.Refill(grid, rows);
                    UpdatePivotTitle(area, rows.Length);
                }
            }
            finally
            {
                _filling = false;
            }
            UpdateDetailTitle();
            UpdateExplain();
        }

        private void UpdatePivotTitle(ItemArea area, int visibleRows)
        {
            var details = CurrentDetails;
            var dirty = details != null && details.ItemsFor(area).Any(i => i.Dirty);
            _pivot.SetText((int)area, details == null ? AreaTitle(area) : $"{AreaTitle(area)} ({visibleRows}){(dirty ? " ●" : "")}");
        }

        private DataGridViewRow CreateDetailRow(DataGridView grid, TranslationItem item)
        {
            var row = new DataGridViewRow { Tag = item };
            var values = new List<object> { item.Info1, item.Info2, item.Info3 };
            values.AddRange(_languages.Select(l => (object)item.Get(l)));
            row.CreateCells(grid, values.ToArray());
            for (var i = 0; i < _languages.Count; i++) row.Cells[3 + i].ToolTipText = CellTooltip(item, _languages[i]);
            return row;
        }

        private string CellTooltip(TranslationItem item, int lcid)
        {
            if (!item.CanEdit) return "Locked: " + item.ReadOnlyReason;
            if (item.Error != null && item.IsDirty(lcid)) return "Error on save: " + item.Error;
            if (item.IsDirty(lcid)) return $"Staged. Saved value: “{item.GetOriginal(lcid)}”";
            if (item.IsMissing(lcid, _languages)) return $"{Languages.Name(lcid)} text is missing – double-click or F2 to add it.";
            return "Double-click or F2 to edit.";
        }

        private void UpdateDetailTitle()
        {
            if (_selected == null) { _lblDetailTitle.Text = "Select a table on the left"; return; }
            var label = _languages.Select(_selected.Label).FirstOrDefault(l => !string.IsNullOrEmpty(l));
            var name = string.IsNullOrEmpty(label) ? _selected.LogicalName : $"{label} ({_selected.LogicalName})";
            _lblDetailTitle.Text = CurrentDetails != null ? $"Labels: {name}" : $"Labels: {name} – loading …";
        }

        private void UpdateExplain()
        {
            var grid = ActiveGrid;
            var cell = grid.CurrentCell;
            var item = cell != null && cell.RowIndex >= 0 ? grid.Rows[cell.RowIndex].Tag as TranslationItem : null;
            if (item == null)
            {
                _lblExplainTitle.Text = "What am I changing – and where?";
                _txtExplain.Text = "Click a cell in a language column. This box then tells you which text it is, where Dataverse stores it, where users see it and what happens when you save.\n\n" +
                                   "Colours: yellow = changed (not saved yet) · pink = translation missing · grey = locked · red = error on save.\n" +
                                   "Use “Languages” in the toolbar to show and edit more languages.";
                return;
            }
            var lcid = grid.Columns[cell.ColumnIndex].Tag is int l ? l : 0;
            var (title, body) = TranslationExplainer.Explain(item, lcid, CurrentDetails, _env?.Name ?? "?", _languages);
            _lblExplainTitle.Text = title;
            _txtExplain.Text = body;
        }

        private void DetailGrid_CellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0) return;
            var grid = (DataGridView)sender;
            if (!(grid.Rows[e.RowIndex].Tag is TranslationItem item)) return;
            var column = grid.Columns[e.ColumnIndex];
            if (!(column.Tag is int lcid))
            {
                if (column.Name == "info1" && item.Area == ItemArea.Fields) e.CellStyle.Font = UiTheme.Mono;
                else if (column.Name != "info1") e.CellStyle.ForeColor = UiTheme.TextSecondary;
                if (item.Area == ItemArea.Options && item.GlobalOptionSet != null && column.Name == "info3") e.CellStyle.ForeColor = UiTheme.WarnText;
                return;
            }

            Color back;
            var fore = UiTheme.TextPrimary;
            if (!item.CanEdit)
            {
                back = UiTheme.SurfaceAlt;
                fore = UiTheme.TextDisabled;
            }
            else if (item.Error != null && item.IsDirty(lcid)) back = UiTheme.ErrorBack;
            else if (item.IsDirty(lcid)) back = UiTheme.PendingBack;
            else if (item.IsMissing(lcid, _languages)) back = UiTheme.MissingTranslationBack;
            else back = UiTheme.Surface;

            e.CellStyle.BackColor = back;
            e.CellStyle.ForeColor = fore;
            e.CellStyle.SelectionBackColor = UiTheme.SelectedOf(back);
            e.CellStyle.SelectionForeColor = fore;
        }

        private void DetailGrid_CellBeginEdit(object sender, DataGridViewCellCancelEventArgs e)
        {
            var grid = (DataGridView)sender;
            if (!(grid.Columns[e.ColumnIndex].Tag is int) || !(grid.Rows[e.RowIndex].Tag is TranslationItem item))
            {
                e.Cancel = true;
                return;
            }
            if (_saving)
            {
                e.Cancel = true;
                SetStatus("Please wait – saving is in progress.");
            }
            else if (!item.CanEdit)
            {
                e.Cancel = true;
                SetStatus("Locked: " + item.ReadOnlyReason);
            }
        }

        private void DetailGrid_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_filling || e.RowIndex < 0) return;
            var grid = (DataGridView)sender;
            if (!(grid.Columns[e.ColumnIndex].Tag is int lcid)) return;
            var row = grid.Rows[e.RowIndex];
            if (!(row.Tag is TranslationItem item)) return;
            var cell = row.Cells[e.ColumnIndex];
            var text = (cell.Value as string ?? string.Empty).Trim();

            if (text.Length == 0 && !item.AllowEmpty && item.GetOriginal(lcid).Length > 0)
            {
                var previous = item.Get(lcid);
                BeginInvoke(new Action(() =>
                {
                    _filling = true;
                    try { cell.Value = previous; }
                    finally { _filling = false; }
                }));
                SetStatus("A name cannot be empty – input reverted.");
                return;
            }

            if (text == item.Get(lcid)) return;
            item.Set(lcid, text);
            item.Error = null;
            cell.ToolTipText = CellTooltip(item, lcid);
            grid.InvalidateRow(e.RowIndex);

            UpdatePivotTitle(ActiveArea, grid.RowCount);
            RefreshEntityListRow(item.Entity);
            UpdatePendingCount();
            UpdateExplain();
        }

        #endregion

        #region Saving

        private IEnumerable<TranslationItem> AllDirty() => _details.Values.SelectMany(d => d.All).Where(i => i.Dirty);

        private async void SaveChanges()
        {
            if (_saving || _busy || Service == null) return;
            foreach (var g in _grids.Values.Where(g => g.IsCurrentCellInEditMode)) g.EndEdit();

            var dirty = AllDirty().ToList();
            if (dirty.Count == 0)
            {
                SetStatus("Nothing to save.");
                return;
            }

            var plan = TranslationService.BuildPlan(dirty);
            var globals = dirty.Where(i => i.GlobalOptionSet != null).Select(i => i.GlobalOptionSet).Distinct().ToList();
            var managed = dirty.Count(i => i.IsManaged || (i.EntityIsManaged && i.Area != ItemArea.Forms));
            var forms = dirty.Where(i => i.Area == ItemArea.Forms).Select(i => i.FormId).Distinct().Count();
            var languages = dirty.SelectMany(i => i.DirtyLanguages).Distinct().Select(Languages.Name);
            var publish = _btnPublish.Checked;

            var text = $"Save {dirty.Count} changed text(s) in “{_env.Name}”?\n\n" +
                       $"• Table names: {dirty.Count(i => i.Area == ItemArea.Entity)}\n" +
                       $"• Column names/descriptions: {dirty.Count(i => i.Area == ItemArea.Fields)}\n" +
                       $"• Choice values: {dirty.Count(i => i.Area == ItemArea.Options)}\n" +
                       $"• Form labels: {dirty.Count(i => i.Area == ItemArea.Forms)} on {forms} form(s)\n" +
                       $"• Languages: {string.Join(", ", languages)}\n" +
                       $"→ {plan.Count} request(s), tables: {string.Join(", ", dirty.Select(i => i.Entity).Distinct())}" +
                       (globals.Count > 0 ? $"\n\n⚠ Global choices (apply to ALL tables using them): {string.Join(", ", globals)}" : "") +
                       (managed > 0 ? $"\n\nNote: {managed} text(s) come from managed solutions → saved as an unmanaged customization (active layer)." : "") +
                       (_env.LooksLikeProduction ? "\n\n⚠ This looks like a PRODUCTION environment." : "") +
                       (publish ? "\n\nAfterwards only the affected components are published once." : "\n\nNothing will be published – changes show up only after publishing.") +
                       "\n\nSaving runs in the background – you can keep working.";
            if (MessageBox.Show(this, text, "Save labels", MessageBoxButtons.YesNo,
                    _env.LooksLikeProduction || globals.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes)
            {
                foreach (var item in dirty) item.Snapshot = null;
                return;
            }

            _saving = true;
            UpdateCommandState();
            var total = plan.Count + (publish ? 1 : 0);
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
                foreach (var g in _grids.Values) g.Invalidate();
            });

            var svc = Service;
            var sw = Stopwatch.StartNew();
            _log.Info($"Saving {dirty.Count} text(s) with {plan.Count} request(s) …");
            try
            {
                await Task.Run(() => TranslationService.Execute(svc, plan, publish, progress));
            }
            catch (Exception ex)
            {
                _log.Error("Unexpected error: " + MetadataHelper.ErrorText(ex));
            }
            finally
            {
                await Task.Yield();
                _saving = false;
                _progress.Visible = false;
                SyncEntityListLabels();
                FillEntityGrid();
                FillDetailGrids();
                UpdatePendingCount();
                var msg = failed == 0
                    ? $"Done: {dirty.Count} text(s) saved in {sw.Elapsed.TotalSeconds:0} s."
                    : $"Done with {failed} error(s) – see the log; affected cells are red.";
                SetStatus(msg);
                if (failed == 0) _log.Success(msg); else _log.Error(msg);
            }
        }

        private void SyncEntityListLabels()
        {
            foreach (var details in _details.Values)
            {
                var name = details.EntityItems.FirstOrDefault(i => i.Kind == ItemKind.EntityDisplayName);
                var listItem = _entityList.FirstOrDefault(e => e.LogicalName == details.LogicalName);
                if (name == null || listItem == null) continue;
                foreach (var pair in name.Original) listItem.Labels[pair.Key] = pair.Value;
            }
        }

        private void DiscardChanges()
        {
            if (_saving) return;
            foreach (var item in AllDirty().ToList()) item.Revert();
            FillEntityGrid();
            FillDetailGrids();
            UpdatePendingCount();
            SetStatus("Changes discarded.");
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
            return new ActionDisposable(() =>
            {
                _operations--;
                if (_operations == 0 && !_saving)
                {
                    _progress.Visible = false;
                    _progress.Style = ProgressBarStyle.Blocks;
                    SetStatus("Ready.");
                }
            });
        }

        private void SetStatus(string text) => _lblStatus.Text = text;

        private void UpdatePendingCount()
        {
            var count = AllDirty().Count();
            _btnSave.Text = $"Save ({count})";
            _lblPending.Text = count > 0 ? $"{count} unsaved change(s)" : string.Empty;
            UpdateCommandState();
        }

        private void UpdateCommandState()
        {
            if (_btnSave == null) return;
            var pending = _lblPending.Text.Length > 0;
            _btnLoad.Enabled = !_busy && !_saving;
            _btnReloadEntity.Enabled = !_saving && _selected != null;
            _btnSave.Enabled = !_busy && !_saving && pending;
            _btnDiscard.Enabled = !_saving && pending;
        }

        public override void ClosingPlugin(PluginCloseInfo info)
        {
            if (_saving)
            {
                // never close while writing metadata in the background
                if (!info.Silent) MessageBox.Show(this, "Still saving. Please wait until it has finished.", "Translation Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
                info.Cancel = true;
                return;
            }
            if (AllDirty().Any() && !info.Silent)
            {
                // one question instead of ours + XrmToolBox's "Are you sure…"
                info.Cancel = MessageBox.Show(this, "Discard unsaved changes and close?", "Translation Manager",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes;
                return;
            }
            base.ClosingPlugin(info);
        }

        #endregion
    }
}
