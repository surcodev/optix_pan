# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A FactoryTalk Optix HMI project (Studio 1.8.1.27, `temp1.optix`) for managing the recipes of a mixer ("batidora"). It is an OPC UA information model serialized as YAML under `Nodes/`, plus C# NetLogic (.NET 8) under `ProjectFiles/NetSolution/`. Node names, UI text and custom code comments are in Spanish. There are no automated tests or linter. Process requirements come from `DOC/PROYECTO.xlsx`; the target look is in the `DOC/*.png` images.

## Build, run, logs

There is no system-wide .NET SDK; use the one bundled with Studio. Compile-check NetLogic from PowerShell at the project root:

```powershell
& "C:\Program Files\Rockwell Automation\FactoryTalk Optix\Studio 1.8.1.27\Tools\DotNetSdk\dotnet.exe" build ProjectFiles\NetSolution\temp1.csproj
```

This only checks and builds the C# (output in `NetSolution/bin`). It does not validate the YAML. Running the application means launching it from Studio's emulator, which copies the project to its own folder:

- Deployed copy: `%LOCALAPPDATA%\Rockwell Automation\FactoryTalk Optix\Emulator\Projects\temp1` (never edit; runtime data lives in its `ApplicationFiles/`)
- Runtime log (includes `Log.Info/Warning/Error` from NetLogic): `%LOCALAPPDATA%\Rockwell Automation\FactoryTalk Optix\Emulator\Log\temp1\FTOptixRuntime.0.log`
- Studio log: `%LOCALAPPDATA%\Rockwell Automation\FactoryTalk Optix\FTOptixStudio\Log\FTOptixStudio.0.log`

## Editing rules

- **Studio owns `Nodes/*.yaml`.** It keeps the model in memory and rewrites the files on save. Have the user save and close the project in Studio before editing YAML, then reopen it. Check with `tasklist | grep -i FTOptixStudio`. C# files can be edited while Studio is open.
- Do not edit generated files: `NetSolution/Private/*`, `temp1.csproj`, `temp1.references` and `ProjectFiles/UserDefinedModule.xml`. The type GUIDs in them mirror the `Id: g=...` of UI types in the YAML (`MainWindow` and the screens).
- Keep existing `Id: g=...` values. The `ns=N` indexes in YAML (e.g. `ns=15` = Recipe, `ns=11` = Store, `ns=18` = SQLiteStore) refer to the dependency list in `temp1.optix`.
- A `Type: NetLogic` node in YAML binds to the C# class **by node name**: class name, file name and node name must match. A public `[ExportMethod]` also needs a matching `Class: Method` node with its `InputArguments`; event handlers call it through `MethodsToCall` using the method name as a string. Studio regenerates these method nodes when the C# is edited and built there.
- Files use CRLF line endings.

## Architecture

### Recipe data flow

```
DataStores/DB_SQLSERVER (SQL Server table "SchemaBatidora")
      ^  SaveToDB   |  SelectFromDB / ComboBox selection
      |             v
Recipes/SchemaBatidora/EditModel  <-- editable controls, via alias {SchemaBatidora}/...
      ^  LoadFromPLC |  ApplyFromEditModel
      |              v
Model/Receta (schema TargetNode)  <-- "current value" labels, direct absolute paths
```

`ApplyFromDB` copies from the DB straight to `Model/Receta`, bypassing `EditModel`. `Model/Receta` is the stand-in for the PLC: "LoadFromPLC" reads those plain model variables, as no communication driver is configured (`CommDrivers` is empty).

- The project-level `SchemaBatidora` alias (end of `Nodes/temp1.yaml`) points to `Recipes/SchemaBatidora/EditModel`. The generated editor controls rely on it, and its name must equal the schema name.
- All recipes, trends, alarms and recipe history live in `DataStores/DB_SQLSERVER` (ODBC, DSN `FlexDSN`, SQL Server database `optix`; DDL in `DOC/SQLServer_MixerMarion.sql`). The password is stored as an encrypted `SecretValue`; a copy of the project outside Studio's emulator cannot decrypt it. `DataStores/DB` (SQLite) is the previous store, kept but unused. Optix translates `LIMIT` for SQL Server. Retentivity covers only `Security` and `Alarms`, so `Model/Receta` and `Model/Proceso` are not persisted; `RecetasDemo` reapplies `BPE-M-18-C-4` at start.
- Table columns are `Name` (the recipe key) plus one `/`-prefixed column per parameter, e.g. `"/NombreReceta"`. They must be quoted in SQL.

### Adding or renaming a recipe parameter

The twelve parameters are duplicated across several places that must stay in sync: `Model/Receta/Receta.yaml`; `Root` **and** `EditModel` in `Recipes/Recipes.yaml`; the table columns in `DataStores/DataStores.yaml`; the generated controls in `LegacyRecipesEditor` (`UI/Screens/Screens.yaml`); the translation keys in `Translations/Translations.yaml` (the `Dimensions` row count must match); the rows of the `Recetas` page editor; and, if shown in the list, the query and columns of its `GridRecetas`. Parameters are matched by browse name between schema and target. Prefix conventions mark the process phase: `Carga_`, `Mezcla_`, `NoTam_`, `Chopper_`, `Descarga_`; `Seg`/`Min` suffixes give the time unit.

Preferred route: change the model and schema in Studio, then run `RecipesEditorLegacyUISetup.Setup()` (design time), which **deletes and rebuilds every control** in `ScrollView/ColumnLayout`, resets the ComboBox query and adds translation keys. Hand-made edits inside that container are lost.

### UI

`MainWindow` (`UI/UI.yaml`, 1280x800) has a header, a left menu (`MenuLateral`) and `MainPanelLoader`. Each menu entry calls `ChangePanel` and compares `MainPanelLoader/CurrentPanel@BrowseName` with its page name to draw the active state, so renaming a page breaks its highlight. Pages in `UI/Screens/Screens.yaml`: `Principal` (mimic), `Recetas`, `Tendencias` (live), `Historial`, `Alarmas`. `LegacyRecipesEditor` and `ListaRecetas` are the earlier screens, kept but not referenced. Graphics are SVGs in `ProjectFiles/HMI`.

The same `MainWindow` is served natively and by `WebPresentationEngine` on port 8443 (HTTP).

In YAML, a property that carries a `DynamicLink` or converter child must **not** have `ModellingRule: Optional`; with it the runtime loads without errors but the binding is ignored. In `DataGrid` image columns, use `ValueMapConverter` and set `OrderBy`; an earlier version with `ConditionalConverter` and no `OrderBy` crashed the runtime when opening `Alarmas` (cause not isolated).

### Demo simulation

`NetLogic/SimuladorProceso` runs the cycle (Reposo, Tamizado, CargaCompleta, Mezclado, MezclaFin, Descarga) every 200 ms. It reads the active recipe from `Model/Receta` and writes `Model/Proceso` and `Model/Produccion`; screens only read those. `VelocidadSim` scales time except the VS1 cleaning, which is real time. `Loggers/RegistroProceso`, `Loggers/RegistroAlarmas` and the `Alarms` nodes read `Model/Proceso`. `T_*` variables are lane-offset copies of the digital signals for the trend. To connect a PLC, replace the simulator with tag links to `Model/Proceso`.

`NetLogic/RecetasDemo` implements the recipe page: `Crear`, `Editar`, `Eliminar` (second press confirms), `Usar` (store to `TargetNode`), `Guardar` (validates, then clears and closes the form), `Cancelar`, `Categoria`. Form state lives in `Model/Proceso` (`EditCodigo`, `EditNueva`, `EditActivo`, `EditTitulo`) and is shared by all sessions. `RecetasLista`, inside the page, reads the selected grid row (rows exist only in the UI session) and calls `RecetasDemo`. It writes `HistorialRecetas` and seeds the five recipes when none of them exists. `7.55` and `13.05` in the spreadsheet are min:sec and are stored as decimal minutes. Text boxes need `ValueChangeBehaviour: 1`, or typed text is lost unless Enter is pressed; SpinBoxes always need Enter.

`Tendencias` is live only (follow mode, `AutoRefreshTime` on the grid). `Historial` shows a chosen interval of up to 24 h: `HistorialProceso`, inside the page and per session, drives the calendar popup, both trends, the grid `Query` and the Excel export (a hand-built .xlsx, no library). On the web it writes to `ProjectFiles/Descargas` and calls `UISession.DownloadFile`, which needs `Historial_*.xlsx` in `AllowedLocalSources` of `WebPresentationEngine`; natively it saves to the Windows Downloads folder. Axis times and text are local time; queries use `LocalTimestamp`. `XAxis.Time` is set first and `XAxis.Window` 400 ms later: changing both at once leaves unloaded gaps in the web client.

### NetLogic

- `RecipeControllerLegacy`, `RecipesEditorLegacyComboBoxLogic`, `RecipesEditorLegacyUISetup`: from the Rockwell template library; prefer not to modify them. `RecipeControllerLegacy` also exposes its methods through boolean `*Trigger` variables (`VariableTransitionEventDispatcher` nodes) that can be driven from a PLC; results go to its `Result` (1 = ok, 2 = error) and `Message` variables and reset after 5 s. `Import` deletes existing recipes with matching names before inserting the CSV rows, without a transaction.
- `RuntimeNetLogic1`: the project's own code; centers `MainWindow` using `user32.dll` P/Invoke, so it only works on Windows runtimes and must be a child of a `Window`.
