# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A FactoryTalk Optix HMI project (Studio 1.8.1.27, `temp1.optix`) for managing the recipes of a mixer ("batidora"). It is an OPC UA information model serialized as YAML under `Nodes/`, plus C# NetLogic (.NET 8) under `ProjectFiles/NetSolution/`. Node names, UI text and custom code comments are in Spanish. There is no git repository and there are no automated tests or linter.

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
- Do not edit generated files: `NetSolution/Private/*`, `temp1.csproj`, `temp1.references` and `ProjectFiles/UserDefinedModule.xml`. The type GUIDs in them mirror the `Id: g=...` of UI types in the YAML (`MainWindow`, `ListaRecetas`, `LegacyRecipesEditor`).
- Keep existing `Id: g=...` values. The `ns=N` indexes in YAML (e.g. `ns=15` = Recipe, `ns=11` = Store, `ns=18` = SQLiteStore) refer to the dependency list in `temp1.optix`.
- A `Type: NetLogic` node in YAML binds to the C# class **by node name**: class name, file name and node name must match. A public `[ExportMethod]` also needs a matching `Class: Method` node with its `InputArguments`; event handlers call it through `MethodsToCall` using the method name as a string. Studio regenerates these method nodes when the C# is edited and built there.
- Files use CRLF line endings.

## Architecture

### Recipe data flow

```
DataStores/DB (SQLite table "SchemaBatidora")
      ^  SaveToDB   |  SelectFromDB / ComboBox selection
      |             v
Recipes/SchemaBatidora/EditModel  <-- editable controls, via alias {SchemaBatidora}/...
      ^  LoadFromPLC |  ApplyFromEditModel
      |              v
Model/Receta (schema TargetNode)  <-- "current value" labels, direct absolute paths
```

`ApplyFromDB` copies from the DB straight to `Model/Receta`, bypassing `EditModel`. `Model/Receta` is the stand-in for the PLC: "LoadFromPLC" reads those plain model variables, as no communication driver is configured (`CommDrivers` is empty).

- The project-level `SchemaBatidora` alias (end of `Nodes/temp1.yaml`) points to `Recipes/SchemaBatidora/EditModel`. The generated editor controls rely on it, and its name must equal the schema name.
- `DB` has `InMemory: true`, so **recipes are lost when the runtime stops**. Retentivity covers only `Security` and `Alarms`, so `Model/Receta` is not persisted either.
- Table columns are `Name` (the recipe key) plus one `/`-prefixed column per parameter, e.g. `"/NombreReceta"`. They must be quoted in SQL.

### Adding or renaming a recipe parameter

The twelve parameters are duplicated across several places that must stay in sync: `Model/Receta/Receta.yaml`; `Root` **and** `EditModel` in `Recipes/Recipes.yaml`; the table columns in `DataStores/DataStores.yaml`; the generated controls in `LegacyRecipesEditor` (`UI/Screens/Screens.yaml`); the translation keys in `Translations/Translations.yaml` (the `Dimensions` row count must match); and, if shown in the list, the query and columns of the `DataGrid1` in `ListaRecetas`. Parameters are matched by browse name between schema and target. Prefix conventions mark the process phase: `Carga_`, `Mezcla_`, `NoTam_`, `Chopper_`, `Descarga_`; `Seg`/`Min` suffixes give the time unit.

Preferred route: change the model and schema in Studio, then run `RecipesEditorLegacyUISetup.Setup()` (design time), which **deletes and rebuilds every control** in `ScrollView/ColumnLayout`, resets the ComboBox query and adds translation keys. Hand-made edits inside that container are lost.

### UI

`MainWindow` (`UI/UI.yaml`) holds a `NavigationPanel` with two panels defined in `UI/Screens/Screens.yaml`:

- `LegacyRecipesEditor`: edit/save/delete/load/apply. An instance of Rockwell's template library widget (see `ns=4;LibraryReference`) customized with the logo `ProjectFiles/LOGO.png`.
- `ListaRecetas`: read-only `DataGrid` over the DB. Columns bind to the **SQL aliases** (`{Item}/Codigo`, `{Item}/MezclaMin`, ...), so changing an alias in the query breaks its column.

It is served both natively and by `WebPresentationEngine` on port 8443, using the same `MainWindow`.

### NetLogic

- `RecipeControllerLegacy`, `RecipesEditorLegacyComboBoxLogic`, `RecipesEditorLegacyUISetup`: from the Rockwell template library; prefer not to modify them. `RecipeControllerLegacy` also exposes its methods through boolean `*Trigger` variables (`VariableTransitionEventDispatcher` nodes) that can be driven from a PLC; results go to its `Result` (1 = ok, 2 = error) and `Message` variables and reset after 5 s. `Import` deletes existing recipes with matching names before inserting the CSV rows, without a transaction.
- `RuntimeNetLogic1`: the project's own code; centers `MainWindow` using `user32.dll` P/Invoke, so it only works on Windows runtimes and must be a child of a `Window`.
