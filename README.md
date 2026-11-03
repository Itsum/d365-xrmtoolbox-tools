# D365 Audit Manager & D365 Translation Manager

Two [XrmToolBox](https://www.xrmtoolbox.com) tools for Dynamics 365 / Dataverse.

| Tool | What it does |
|---|---|
| **D365 Audit Manager** | Shows organization, table and column auditing for several environments (e.g. DEV / TEST / PROD) side by side, highlights differences, lets you tick/untick or copy settings between environments, and saves in the background. |
| **D365 Translation Manager** | Shows the labels of tables, columns, choices and forms in any installed language, lets you edit them inline, explains for every cell *what* you change and *where* it is stored, and saves in the background. |

Both tools: display names in the **base language + first additional language** by default, more languages via the **Languages** menu (remembered per tool) · **light and dark theme** (Theme menu: Windows default / Light / Dark) · saving never freezes XrmToolBox · only what changed is published.

## D365 Audit Manager

| Light | Dark |
|---|---|
| ![Audit Manager – light](docs/screenshots/audit-manager-light.png) | ![Audit Manager – dark](docs/screenshots/audit-manager-dark.png) |

1. Connect to your first environment (e.g. DEV).
2. **+ Add environment** → add TEST and PROD. Each environment becomes a column (in its XrmToolBox environment colour).
3. Enter tables, e.g. `account, contact, salesorder` (empty = all) → **Load** (F5).
4. Tick/untick → **Save** (Ctrl+S).

| Colour | Meaning |
|---|---|
| green | auditing enabled |
| white | disabled |
| yellow | changed, not saved yet |
| red | error on last save (tooltip shows the message) |
| grey “–” | table/column does not exist in this environment |
| `≠` | auditing differs between environments |

- **Right-click an environment column header**: enable/disable all visible rows there, or undo.
- **Align**: copy the settings of the selected tables (incl. all columns) from one environment to others, e.g. DEV → TEST + PROD.
- **Only differences**, **Hide locked** (audit setting locked by a managed property), search boxes.
- Select several rows + Space toggles them together in the current environment column.
- The environment bar shows **organization-wide auditing**; if it is off you can enable it there. A column is only audited when organization **and** table **and** column auditing are on – tooltips point this out.

**Saving:** environments in parallel, sequential within one environment (Dataverse locks metadata changes).
Per table/column: `RetrieveEntity`/`RetrieveAttribute` → set `IsAuditEnabled` → `UpdateEntity`/`UpdateAttribute`;
then one `PublishXml` per environment for the affected tables only.

## D365 Translation Manager

| Light | Dark |
|---|---|
| ![Translation Manager – light](docs/screenshots/translation-manager-light.png) | ![Translation Manager – dark](docs/screenshots/translation-manager-dark.png) |

1. Connect to an environment – the table list loads automatically.
2. Pick a table → tabs **Table**, **Columns**, **Choices**, **Forms**.
3. Type in a language column (double-click / F2) → yellow → **Save** (Ctrl+S).

The box **“What am I changing – and where?”** explains the selected cell: which metadata property it is,
where users see it, where it is stored – and warns about **global choices** (used in other tables too),
**form labels that override** the column display name, and items from **managed solutions** (the change becomes an active-layer customization).

| Tab | Texts | Saved with |
|---|---|---|
| Table | display name, plural name, description | `UpdateEntityRequest` |
| Columns | display name, description (toggle “Descriptions”) | `UpdateAttributeRequest` |
| Choices | choice / multi-select values, status reason, status, yes/no – local and global | `UpdateOptionValueRequest`, `UpdateStateValueRequest`, `UpdateAttributeRequest`, `UpdateOptionSetRequest` |
| Forms | labels of tabs, sections and fields on main, quick create, quick view and card forms | `systemform.formxml` |

Why it is fast: changes are batched (one request per table / column / choice value / form, `MergeLabels` so other languages stay untouched)
and published **once** at the end for the affected components – in the background.

Filters: **Only missing** (pink = missing text in a selected language), search across all tabs.
Grey cells are locked (`IsRenameable = false` or a non-customizable choice).

Not covered: view names, sitemap titles, dashboards, business process flows, web resources.

## Install

- **From the Tool Library** (after publishing): search for “D365 Audit Manager” / “D365 Translation Manager”.
- **Manually / for testing:** see [TESTING.md](TESTING.md) – `deploy.ps1` builds and copies the DLLs into XrmToolBox's `Plugins` folder.

## Build

Requirements: .NET SDK 6 or later (builds .NET Framework 4.8 via reference assemblies – no Visual Studio needed; VS 2022 opens `D365Tools.sln` as well).

```powershell
dotnet build D365Tools.sln -c Release
```

```
Directory.Build.props        repository URL, author, version – used by DLLs and NuGet packages
src/Shared/                  shared code (metadata helpers, theme, languages, settings), compiled into both DLLs
src/D365AuditManager/        tool 1 (MultipleConnectionsPluginControlBase) + .nuspec
src/D365TranslationManager/  tool 2 (PluginControlBase) + .nuspec
tools/UiPreview/             screenshots + smoke test without Dataverse (see TESTING.md)
tools/generate-icons.ps1     tool logos (PluginIcons.cs + docs/icons/*.png)
deploy.ps1                   build + copy into an XrmToolBox Plugins folder
pack.ps1                     NuGet packages for the Tool Library + checklist validation
```

Both DLLs are self-contained (no extra assemblies in the Plugins folder).

## Publish to the XrmToolBox Tool Library

1. Edit `Directory.Build.props`: `RepositoryUrl`, `Authors`, `Version` (4 parts, must not end with `.0`; raise it for every release).
2. Push the repository to GitHub (the icon URLs point to `docs/icons/*.png` on the `main` branch).
3. `powershell -ExecutionPolicy Bypass -File pack.ps1 -CheckUrls` → all checks must pass.
4. `dotnet nuget push artifacts\*.nupkg --api-key <your nuget.org key> --source https://api.nuget.org/v3/index.json`
5. After nuget.org has indexed the packages, submit both package ids on the XrmToolBox portal and wait for validation.

Consider adding a `LICENSE` file (e.g. MIT) before publishing an open-source tool.
