# SERhinoIFC — Rhino Plugin for IFC Import/Export

## What This Plugin Does

SERhinoIFC is a Rhino plugin that imports and exports IFC (Industry Foundation Classes) files for BIM interoperability. It targets light gauge steel framing workflows — cold-formed steel members like C-shapes, tracks, and studs — and supports roundtrip between Rhino and fabrication software like FrameCAD/Constructobot.

**Commands:**
- `SEIfcImport` — Import IFC files as mesh or Brep geometry with full metadata
- `SEIfcExport` — Export selected Rhino objects to IFC with metadata roundtrip

## File Structure

```
SERhinoIFC/
├── SERhinoIFCPlugin.cs          # Plugin registration, GUID, startup load
├── SERhinoIFC.csproj            # Build config, NuGet refs, assembly GUID
│
├── Commands/
│   ├── IfcImportCommand.cs      # Import command (file dialog → unit dialog → import)
│   └── IfcExportCommand.cs      # Export command (selection → options dialog → export)
│
├── Dialogs/
│   ├── ImportUnitsDialog.cs     # Import options: detected units, override, geometry type (Mesh/Brep)
│   └── ExportOptionsDialog.cs   # Export options: mode, units, tolerance, schema, author
│
├── Import/
│   ├── IfcImporter.cs           # Core import logic (tessellation, Brep, metadata, placement)
│   └── UnitResolver.cs          # Reads IFC unit declarations (SI, conversion-based, all prefixes)
│
├── Export/
│   ├── GeneralExporter.cs       # General IFC export (geometry as IfcFacetedBrep)
│   ├── FrameCADExporter.cs      # SE-Cbot export (geometry as IfcExtrudedAreaSolid)
│   ├── IfcMetadataWriter.cs     # Shared: reads Rhino user text → writes IFC metadata
│   └── ExportOptions.cs         # Export enums and unit helper
│
├── Helpers/
│   ├── GeometryHelper.cs        # Brep/Extrusion → triangulated mesh (SimplePlanes=true)
│   └── MemberClassifier.cs      # Layer name → IFC type fallback mapping
│
└── test files/                  # Test IFC files and screenshots (not tracked in git)
```

## Key Architecture Decisions

### xBIM Essentials + Geometry Engine for Import
- **xBIM Essentials** (v5.1.341) handles IFC file I/O and data model access via `Xbim.Ifc4.Interfaces` (works for both IFC2x3 and IFC4)
- **xBIM Geometry Engine** (v5.1.820, based on Open Cascade) provides tessellation for the primary mesh import path
- The Brep import path does NOT use the geometry engine — it reads IFC geometry entities directly and builds Rhino Breps from planar faces

### InMemoryModel for Export (not Esent)
- Export uses `IfcStore.Create(credentials, schema, XbimStoreType.InMemoryModel)`
- The Esent database backend has documented entity label collision bugs (xBIM Issue #18) that cause `InvalidCastException` and "Failed to add entity" errors when creating many entities
- InMemoryModel avoids all database-related issues and is faster for typical export sizes

### SimplePlanes Meshing
- `GeometryHelper.MeshBrep()` uses `MeshingParameters` with `SimplePlanes = true`
- Without this, Rhino's default meshing creates 50+ vertices per planar rectangle face, producing hundreds of thousands of IFC entities on export
- With `SimplePlanes = true`, planar faces get minimal triangulation (~2 triangles per rectangle), matching xBIM's tessellation output

### Eto.Forms for UI
- Cross-platform UI framework built into RhinoCommon
- Used for both import and export option dialogs
- Avoids WinForms dependency, works on both Windows and Mac Rhino

### Manual Brep Construction (not Extrusion.Create)
- Brep import builds polysurfaces from individual planar quad faces + caps using `Brep.CreatePlanarBreps` + `Brep.JoinBreps`
- `Rhino.Geometry.Extrusion.Create()` and `Surface.CreateExtrusion()` both have orientation issues (centering, height sign reversal) that caused profile flips
- Manual planar face construction mirrors the mesh path exactly, guaranteeing identical geometry placement

### IFC Profile Centering (IFC Spec)
- All parametric profiles (C, I, U, L, Rectangle) are centered at the bounding box origin per IFC specification
- C-shape opens in +X direction (web at -Width/2, flanges at +Width/2)
- This matches xBIM's geometry engine interpretation

## Import Pipeline (step by step)

1. **File Selection** — User picks .ifc file via `OpenFileDialog`
2. **Unit Detection** — `IfcImporter.ReadUnitInfo()` opens the file, `UnitResolver.GetIfcUnitInfo()` reads `IfcProject.UnitsInContext` to detect length units (handles SI with all prefixes, conversion-based units like inches/feet)
3. **Import Dialog** — Shows detected IFC units, current Rhino units, unit override dropdown, geometry type selector (Mesh/Brep)
4. **Scale Factor** — `UnitResolver.ComputeScaleFactor()` calculates `ifcUnitInMeters / rhinoUnitInMeters`
5. **Storey Lookup** — `BuildStoreyLookup()` maps element entity labels to storey names via `IfcRelContainedInSpatialStructure`

### Mesh Mode (primary path):
6. **xBIM Tessellation** — `Xbim3DModelContext.CreateContext()` tessellates all geometry
7. **Shape Iteration** — Filters `ShapeInstances` by `OpeningsAndAdditionsExcluded` OR `OpeningsAndAdditionsIncluded`
8. **Mesh Conversion** — `ConvertToRhinoMesh()` reads binary triangulation data, applies xBIM transform matrix and scale factor
9. **Element Lookup** — Maps shape instance to `IIfcProduct` via `IfcProductLabel`
10. **Add to Document** — `AddMeshToDoc()` creates layer hierarchy, sets all metadata as user text

### Brep Mode:
6. **Product Iteration** — Iterates all `IIfcProduct` entities (product-first, not geometry-first)
7. **World Placement** — `GetObjectPlacementTransform()` walks the full `IfcLocalPlacement.PlacementRelTo` chain
8. **Geometry Extraction** — `TryConvertRepItemToBrep()` handles `IfcExtrudedAreaSolid`, `IfcFacetedBrep`, `IfcBooleanResult`, and `IfcMappedItem` (recursive)
9. **Brep Construction** — `ConvertExtrudedAreaSolidToBrep()` builds from planar quad faces (side faces per profile edge + top/bottom caps); `ConvertFacetedBrepToBrep()` uses `Brep.CreatePlanarBreps` per face + `Brep.JoinBreps`
10. **Transform** — Applies local `Position` transform then world `ObjectPlacement` transform
11. **Add to Document** — `AddBrepToDoc()` with full metadata

### Metadata (both modes):
- `SetAllMetadata()` writes to Rhino user text: IfcType, GlobalId, Name, Description, ObjectType, Tag, PredefinedType
- All `IfcPropertySet` properties as `PsetName.PropertyName = value`
- All `IfcElementQuantity` values as `QsetName.QuantityName = value [unit]`
- Type properties from `IfcRelDefinesByType`
- Material associations (single, layer sets, profile sets, constituents)

## Export Pipeline (step by step)

1. **Selection** — User selects Rhino objects
2. **Export Dialog** — Mode (General IFC / SE-Cbot), IFC Units, Tolerance, Schema, Author, Organization
3. **Model Creation** — `IfcStore.Create()` with InMemoryModel, sets up project, units, rep context, spatial hierarchy

### Per Object:
4. **Element Creation** — `IfcMetadataWriter.CreateElement()` reads `IfcType` user text → creates correct IFC entity (IfcBeam, IfcColumn, etc.). Falls back to layer name if no user text
5. **Metadata Roundtrip** — `IfcMetadataWriter.ApplyMetadata()` reads all Rhino user text → writes GlobalId, Description, ObjectType, Tag, property sets, quantity sets, materials (deduplicated)
6. **Storey Grouping** — Top-level Rhino layer → `IfcBuildingStorey`

### General IFC (Brep geometry):
7. `GeometryHelper.GetTriangulatedMesh()` converts geometry to triangulated mesh (SimplePlanes=true for Breps)
8. `CreateFacetedBrep()` writes mesh as `IfcFacetedBrep` (IfcCartesianPoint + IfcPolyLoop + IfcFace per triangle)

### SE-Cbot (Solid geometry):
7. `ExtractMemberGeometry()` extracts axis direction, length, and 2D profile from Brep cap faces, Extrusion path, or mesh face normals
8. `CreateProfileDef()` writes profile as `IfcArbitraryClosedProfileDef` polyline
9. Geometry written as `IfcExtrudedAreaSolid` with `IfcAxis2Placement3D` placement

10. **Save** — `model.SaveAs(filePath, StorageType.Ifc)`

## Known Issues and Limitations

- **IFC4 export not implemented** — Schema dropdown exists but both exporters write IFC2x3
- **Boolean operations not supported** — `IfcBooleanClippingResult` is handled on import (recurses into first operand) but the boolean subtraction is not applied to the Brep
- **Profile types limited** — Only Rectangle, Circle, C, U, L, I, and ArbitraryClosedProfile are supported. Missing: T-shape, hollow sections, Z-shape, asymmetric I
- **Mesh → SE-Cbot profile extraction** — Works by analyzing face normals but may produce imprecise profiles for complex tessellations. Brep import is preferred for SE-Cbot export
- **Circle profiles** — Brep import uses true NURBS circles; mesh fallback uses 24-segment polygon
- **No curved member support** — Only straight extrusions (IfcExtrudedAreaSolid), no swept or revolved geometry
- **xBIM Geometry Engine dependency** — The primary mesh import path requires the native C++ geometry engine DLL (Xbim.Geometry.Engine64.dll). If it fails to load, the fallback direct geometry reader is used

## What Still Needs To Be Done

- **Yak packaging** — Package as a Rhino Yak plugin for easy distribution via Rhino's package manager
- **Mac testing** — Eto.Forms UI should work cross-platform but hasn't been tested on Mac Rhino
- **IFC4 export** — Currently only exports IFC2x3 regardless of schema selection
- **Profile recognition on export** — SE-Cbot export uses arbitrary polyline profiles; could detect standard profiles (C, I, etc.) and write proper IfcCShapeProfileDef for better interop
- **Boolean geometry** — Apply boolean subtractions on Brep import for elements with openings
- **Large file performance** — Import of 1500+ member files works but could be optimized with progress reporting

## Version History

| Version | Type | Description |
|---------|------|-------------|
| v1.0.0 | Major | Initial working build — basic IFC import/export |
| v1.1.0 | Minor | FrameCAD/Constructobot exporter added |
| v1.2.0 | Minor | C-shape tessellation and steel profile support |
| v1.2.1 | Patch | Fix IFC import producing no geometry (RepresentationType filter) |
| v1.2.2 | Patch | Fix unit conversion (conversion-based units), add import units dialog |
| v1.3.0 | Minor | Comprehensive IFC metadata import as Rhino user text |
| v1.4.0 | Minor | Brep/polysurface import mode |
| v1.4.1 | Patch | Fix Brep placement and metadata (product-first iteration) |
| v1.4.2 | Patch | Fix Brep extrusion orientation (match mesh path) |
| v1.4.3 | Patch | Fix C-shape profile centering per IFC spec |
| v1.4.4 | Patch | Fix plugin GUID and startup load |
| v1.5.0 | Minor | Full IFC metadata roundtrip on export, remove Frame Name Prefix |
| v1.5.1 | Patch | Mesh profile extraction for SE-Cbot export |
| v1.5.2 | Patch | Export unit/tolerance selection dialog |
| v1.5.3 | Patch | Delete stale xbim temp file before export |
| v1.5.4 | Patch | Fix Brep export crash (InMemoryModel + SimplePlanes) |
| v1.5.5 | Patch | Fix assembly-level GUID for Rhino PlugInManager |
| v1.5.6 | Patch | Rename commands to SEIfcImport/SEIfcExport |

## How To Build and Release

### Prerequisites
- .NET Framework 4.8 SDK
- Visual Studio or `dotnet` CLI
- Rhino 7+ (for testing)
- GitHub CLI (`gh`) for releases

### Build
```bash
dotnet build SERhinoIFC.csproj -c Release
```

Output: `bin/Release/net48/SERhinoIFC.rhp` + all dependencies

### Install in Rhino
1. Uninstall any previous version (especially if GUID changed)
2. Drag `SERhinoIFC.rhp` into Rhino, or use `PlugInManager` → Install
3. Restart Rhino (plugin loads at startup)

### Release a New Version
```bash
# 1. Build
dotnet build SERhinoIFC.csproj -c Release

# 2. Package
powershell -Command "Compress-Archive -Path 'bin/Release/net48/*' -DestinationPath 'SERhinoIFC-vX.Y.Z.zip' -Force"

# 3. Commit and tag
git add <changed files>
git commit -m "Description"
git tag -a vX.Y.Z -m "vX.Y.Z - Description"
git push origin master --tags

# 4. Create GitHub release
gh release create vX.Y.Z SERhinoIFC-vX.Y.Z.zip --title "vX.Y.Z" --notes "Release notes"
```

### Versioning Rules
- **MAJOR** (x.0.0): Breaking changes
- **MINOR** (x.y.0): New features (e.g. new import mode, new profile type)
- **PATCH** (x.y.z): Bug fixes only — don't inflate version numbers

### Plugin Identity
- **GUID**: `C06A81B4-E2D0-4732-9AAC-601B0592B58C` (assembly + class level)
- **Load Time**: At Startup
- **Organization**: Sixth Eye
- **GitHub**: https://github.com/andrew-sixtheye/SERhinoIFC
