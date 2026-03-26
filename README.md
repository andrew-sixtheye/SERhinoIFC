# SERhinoIFC

A Rhino plugin for importing and exporting IFC (Industry Foundation Classes) files, built for light-gauge steel framing workflows.

SERhinoIFC bridges Rhino and BIM — bringing IFC geometry into Rhino as meshes or Breps with full metadata, and exporting Rhino objects back to IFC for use in viewers, coordination tools, and custom software (if you need a custom exporter for your software reach out).

## Commands

| Command | Description |
|---------|-------------|
| `SEIfcImport` | Import an IFC file into Rhino |
| `SEIfcExport` | Export selected Rhino objects to IFC |

## Features

### Import
- **Mesh or Brep geometry** — choose tessellated meshes (fast, reliable) or polysurface Breps (editable, precise)
- **Automatic unit detection** — reads IFC length units (metric, imperial, all SI prefixes) and scales to match Rhino document units
- **Full metadata** — IFC properties, quantities, materials, and type info stored as Rhino user text
- **Layer organization** — objects sorted by storey and IFC class (`Level 1::IfcBeam`, etc.)
- **Steel profile support** — C, I, U, L, Rectangle, Circle, and arbitrary closed profiles

### Export
- **General IFC** — exports geometry as `IfcFacetedBrep` for standard IFC viewers (BIMvision, Solibri, etc.)
- **SE-Cbot (custom)** — exports as `IfcExtrudedAreaSolid` with cold-formed steel material properties and profile property sets
- **Metadata roundtrip** — Rhino user text is written back as IFC property sets, quantities, and materials
- **Configurable** — unit selection, tolerance, IFC schema, author/organization fields

## Installation

### From a Release

1. Download the latest `.zip` from the [Releases](../../releases) page.
2. **Unblock the zip** — right-click in Windows Explorer, Properties, check "Unblock", OK.
3. Extract to a folder (e.g. `%AppData%\McNeel\Rhinoceros\8.0\RhinoPlugins\SERhinoIFC\`).
4. In Rhino: Tools > Options > Plug-ins > Install > browse to `SERhinoIFC.rhp`.
5. Restart Rhino.

### From Source

Requires .NET 8 SDK and .NET Framework 4.8 targeting pack.

```
git clone https://github.com/andrew-sixtheye/SERhinoIFC.git
cd SERhinoIFC
dotnet build
```

Load `bin\x64\Debug\net48\SERhinoIFC.rhp` into Rhino via the Plugin Manager.

## Requirements

- **Rhino 7 or 8** (Windows)
- Windows only — the xBIM Geometry Engine is a native C++ dependency that does not run on macOS

## Known Limitations

- **IFC2x3 export only** — IFC4 option exists in the UI but is not yet implemented
- **No boolean geometry** — openings/voids are not subtracted on Brep import
- **SE-Cbot export requires prismatic geometry** — freeform meshes and NURBS surfaces are skipped
- **Limited profile types** — T-shape, hollow sections, Z-shape not yet supported

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| [xBIM Essentials](https://github.com/xBimTeam/XbimEssentials) | 5.1.341 | IFC data model, file I/O |
| [xBIM Geometry](https://github.com/xBimTeam/XbimGeometry) | 5.1.820 | IFC geometry tessellation |
| [RhinoCommon](https://www.nuget.org/packages/RhinoCommon) | 7.38+ | Rhino API (provided at runtime) |

## License

Proprietary. Copyright Sixth Eye.
