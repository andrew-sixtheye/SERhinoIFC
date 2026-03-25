# SERhinoIFC

A compiled C# Rhino plugin (.rhp) that adds IFC import and export commands to Rhino 7 and Rhino 8 on Windows.

## What It Does

**`IfcImport`** — Import an IFC file as Rhino geometry with automatic unit detection.

- Reads the declared length unit from the IFC file (meters, millimeters, feet, inches, etc.) and scales geometry to match the active Rhino document units. Never hardcodes a scale factor.
- Organizes imported objects into layers by storey and IFC class: `Level 1::IfcWall`, `Level 1::IfcColumn`, etc.
- Stores the IFC element name and GlobalId as Rhino object attributes.

**`IfcExport`** — Export selected Rhino objects to IFC with a configuration dialog.

The export dialog lets you choose between two modes:

- **General IFC** — Exports geometry as `IfcFacetedBrep` (triangulated mesh). Produces valid IFC files that open in standard viewers (BIMvision, Solibri, etc.). Maps Rhino layer names to IFC element types (wall, slab, column, beam, roof) via keyword matching.

- **FrameCAD / Constructobot** — Exports geometry as `IfcExtrudedAreaSolid` for compatibility with the FrameCAD IFCtoFramecad importer. Includes member naming conventions (`<Frame>-<Token>` format), TRUSS/PANEL layer classification, cold-formed steel material properties (550 MPa yield/ultimate), and profile property sets. Objects that cannot be represented as extrusions are skipped with a warning.

## Installation

### From a Release Zip

1. Download the latest release `.zip` from the [Releases](../../releases) page.
2. Unblock the zip: right-click the zip in Windows Explorer → Properties → check "Unblock" → OK.
3. Extract the zip to a folder (e.g. `C:\Users\YourName\AppData\Roaming\McNeel\Rhinoceros\8.0\RhinoPlugins\SERhinoIFC\`).
4. Open Rhino → Tools → Options → Plug-ins → Install → browse to `SERhinoIFC.rhp`.
5. Restart Rhino. The `IfcImport` and `IfcExport` commands should now appear in the command autocomplete.

### From Source

Prerequisites:
- .NET 8 SDK (or later) — needed to build `net48` targets
- .NET Framework 4.8 targeting pack (included via NuGet, no separate install required)

```
git clone https://github.com/andrew-sixtheye/RhinoIFC.git
cd RhinoIFC
dotnet build
```

The output `.rhp` is at `bin\x64\Debug\net48\SERhinoIFC.rhp`. Load it into Rhino via the Plugin Manager.

## Known Limitations

- **Windows only.** The xBIM Geometry Engine (`Xbim.Geometry.Engine64.dll`) is a native C++ dependency (Open Cascade) that only runs on Windows. The plugin will not work on macOS even though Rhino 8 supports it.
- **IFC2x3 only for export.** The export dialog shows IFC4 as an option, but the current implementation writes IFC2x3. IFC4 export is a future enhancement.
- **FrameCAD export requires extrusion or prismatic brep geometry.** Freeform meshes, NURBS surfaces, and curves without profiles are skipped.
- **No round-trip fidelity.** Importing an IFC file and re-exporting it will lose non-geometric data (property sets, type definitions, material assignments from the original file).

## Packaging a Release

To create a distributable zip for end users:

```
dotnet build -c Release
```

Then package the contents of `bin\x64\Release\net48\` into a zip:

```powershell
$outDir = "bin\x64\Release\net48"
$files = Get-ChildItem $outDir -Include *.rhp,*.dll -Recurse
Compress-Archive -Path $files.FullName -DestinationPath "SERhinoIFC-v1.0.0.zip"
```

The zip must include:
- `SERhinoIFC.rhp` — the plugin (renamed .dll)
- All `*.dll` files in the output directory (xBIM, Esent, Microsoft.Extensions, etc.)
- `Xbim.Geometry.Engine64.dll` — the native geometry engine

Do **not** include `RhinoCommon.dll` or `Eto.dll` — Rhino provides these at runtime. The build is already configured to exclude them.

## Project Structure

```
SERhinoIFC/
├── SERhinoIFC.sln
├── SERhinoIFC.csproj
├── SERhinoIFCPlugin.cs          # Plugin registration (GUID, OnLoad)
├── Commands/
│   ├── IfcImportCommand.cs      # IfcImport command
│   └── IfcExportCommand.cs      # IfcExport command
├── Import/
│   ├── IfcImporter.cs           # Tessellation + mesh creation pipeline
│   └── UnitResolver.cs          # Dynamic IFC unit detection
├── Export/
│   ├── ExportOptions.cs         # Options data class + ExportMode enum
│   ├── GeneralExporter.cs       # IfcFacetedBrep export
│   └── FrameCADExporter.cs      # IfcExtrudedAreaSolid export
├── Dialogs/
│   └── ExportOptionsDialog.cs   # Eto.Forms export config dialog
└── Helpers/
    ├── MemberClassifier.cs      # Layer name → IFC type mapping
    └── GeometryHelper.cs        # Mesh generation from Brep/Extrusion
```

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| RhinoCommon | 7.38.24338.17001 | Rhino API (excluded from output) |
| Xbim.Essentials | 5.1.341 | IFC data model, file read/write |
| Xbim.Geometry | 5.1.820 | IFC geometry tessellation (native) |
