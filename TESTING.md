# Testing before publishing

Three layers: an automated smoke test without Dataverse, a package check, and a manual run in a real XrmToolBox.

## 1. Automated smoke test (no Dataverse needed)

```powershell
dotnet build tools\UiPreview\UiPreview.csproj -c Release
tools\UiPreview\bin\Release\net48\UiPreview.exe "$PWD\tools\UiPreview\out"
```

Writes `smoke-test.txt` (PASS/FAIL per step, exit code 1 on any FAIL) and light/dark screenshots of both tools.
It covers these Tool Library checklist items for both tools:

| Checklist item | What the smoke test does |
|---|---|
| Tool can be opened without an organization connected | creates the tool in a form without connection |
| Without a connection, controls can be used without error | clicks every toolbar button, opens every menu, toggles every option, types into filters/search, switches tabs, F5, Ctrl+S |
| Without a connection, controls that need one open the connection dialog | *Load*, *Load tables*, F5, *+ Add environment* must raise XrmToolBox's connection request |
| Tool adapts to resizing | resizes to 640×420 and 1700×1000 |
| (XrmToolBox calls the method again by name after connecting) | `UpdateConnection(..., "LoadEntities")` / `"AddEnvironment"` must find a public method |
| Light/dark theme | switches both themes at runtime |

## 2. Package check

```powershell
powershell -ExecutionPolicy Bypass -File pack.ps1 -CheckUrls
```

Checks both `.nupkg` files in `artifacts\` against the “Nuget package checklist”: icon URL, project URL, DLL under `Plugins`,
package version = DLL version, only own assemblies, `XrmToolBox` tag and dependency, authors, description, release notes.
`-CheckUrls` also checks that the icon/project URLs are reachable (push the repository first).

## 3. Manual test in XrmToolBox

Install into a **separate sandbox** so your normal XrmToolBox stays untouched
(XrmToolBox's `/overridepath` gives the sandbox its own Plugins folder, settings and connections):

```powershell
powershell -ExecutionPolicy Bypass -File deploy.ps1 -Sandbox "C:\XtbSandbox" -XrmToolBoxExe "C:\Tools\XrmToolBox\XrmToolBox.exe"
```

Or into your normal XrmToolBox: `powershell -ExecutionPolicy Bypass -File deploy.ps1` (close XrmToolBox first).

Use a **DEV/test environment** for anything that saves.

| # | Checklist item | Steps | Expected |
|---|---|---|---|
| 1 | Dedicated large + small image | XrmToolBox → Settings → switch the tools list between large and small display | both tools show their own logo (shield / “A Ä”) |
| 2 | Opens without connection | disconnect, open both tools | no error |
| 3 | Controls need a connection → dialog | click *Load* / *Load tables* / *+ Add environment* | connection dialog; after connecting the action continues |
| 4 | Resize | resize the XrmToolBox window, drag the splitters | grids, toolbar (overflow »), panels adapt |
| 5 | With connection, no errors | Audit: connect DEV, add TEST (and PROD), load `account, contact, salesorder`, open menus, filters, right-click a column header. Translation: pick `account`, go through all tabs, change Languages, toggle Only missing/Descriptions | no error dialogs |
| 6 | Long operations are async | while loading all tables (empty filter) and while saving: scroll, switch tables, open menus | XrmToolBox stays responsive; progress in the status bar |
| 7 | Audit save | untick one column in DEV only → yellow → Save → log shows “disabled … Published” | cell turns white; reload shows the new value; maker portal → column → Advanced → auditing matches |
| 8 | Translation save | change a column display name in the second language → explanation box → Save | log shows the request + publish; the new label appears in the app in that language |
| 9 | Form label | Forms tab → change a field label → Save | only that form shows the new label |
| 10 | Global choice warning | Choices tab → a global choice value | warning in the explanation box and in the save dialog |
| 11 | Languages | Languages → tick a third language | new column appears; restart XrmToolBox → selection kept |
| 12 | Theme | Theme → Dark / Light / Windows default | whole tool switches; restart → kept |
| 13 | Close with unsaved changes | change something, close the tab | one “Discard unsaved changes?” question |

Undo the test changes afterwards (tick back / restore the label → Save).
