# Livestack Repository Guide

## Purpose

This repository contains the `Livestack` plugin for N.I.N.A. - Nighttime Imaging 'N' Astronomy. It adds live stacking for astrophotography frames inside N.I.N.A., including:

- live stacking of LIGHT and SNAPSHOT frames
- calibration with bias, dark, and flat masters
- mono and one-shot color (OSC) workflows
- color recombination tabs
- sequencer instructions to start/stop stacking and build session flat masters
- message-broker integration for external control and stack/status broadcasts

The minimum N.I.N.A. version is `3.2.0.9001`. See `Properties/AssemblyInfo.cs`.

## Repo Layout

- `nina.plugin.livestack.csproj`
  Main WPF plugin project targeting `net8.0-windows7.0`.
- `Livestack.cs`
  Plugin manifest entrypoint and profile-scoped settings surface.
- `LivestackMediator.cs`
  Static service locator/registry for plugin-wide state and concrete implementations.
- `LivestackDockables/`
  Dockable UI viewmodels and templates for the main live stack panel.
- `Instructions/`
  Sequencer instructions such as start/stop live stacking and flat stacking.
- `Image/`
  Core image-processing logic: calibration, alignment, stacking math, metadata models.
- `QualityGate/`
  Per-frame acceptance gates based on HFR, RMS, and star count.
- `Options.xaml`
  Plugin options UI and prompt templates.
- `nina.plugin.livestack.test/`
  NUnit test project.
- `nina.plugin.livestack.benchmark/`
  BenchmarkDotNet project plus sample FITS data.
- `.github/workflows/github-action.yaml`
  GitHub release packaging workflow.
- `bitbucket-pipelines.yml`
  Older Bitbucket packaging pipeline.

## Architectural Overview

### Plugin bootstrap

`Livestack.cs` exports `IPluginManifest`, exposes plugin settings, and registers shared singletons through `LivestackMediator`.

Important registrations:

- plugin instance
- plugin settings accessor
- `CalibrationVM`
- dockable instance later, when the dockable is constructed

### Main runtime flow

The runtime core is `LivestackDockables/LivestackDockable.cs`.

High-level flow:

1. `StartLiveStack` subscribes to `IImageSaveMediator.BeforeFinalizeImageSaved`.
2. Each captured LIGHT or SNAPSHOT is written to a temp FITS file in the configured working directory.
3. A `LiveStackItem` is queued with metadata and star-detection results.
4. Quality gates are evaluated.
5. The frame is calibrated using registered masters from `CalibrationVM`.
6. Optional hot-pixel cleanup is applied.
7. The frame is aligned and added to the correct target/filter stack.
8. Tabs are refreshed and optional autosaves are written.
9. Status and stack updates are published over `IMessageBroker`.

### Calibration

`Image/CalibrationManagerSimd.cs` is the production implementation selected by
`LivestackMediator`. It streams calibration masters through rented row buffers by
default, supports optional full master caching and uses span-based FITS reads.
Light and flat calibration share a frame loop with SIMD processing and a scalar
fallback.

The historical scalar reference lives in
`nina.plugin.livestack.test/Reference/CalibrationReference.cs`. The benchmark
project links that same file; it is excluded from the plugin assembly. When
changing calibration behavior, compare against this reference and run the tests.

### Alignment

Alignment logic is in `Image/ImageTransformer2.cs` and its `.Alignment.cs` partial.

It uses:

- star filtering, grid-based bright-star seeds and spatial selection
- triangle and quad matching to propose correspondences
- bounded RANSAC and least-squares refinement
- final validation of unique matches, spatial coverage, residuals and geometry
- similarity transforms with a constrained affine correction when justified

Meridian flips are handled as rotations in the solved matrix. All candidates
pass the same acceptance policy before stack accumulation. See
`docs/live-stacking-alignment.md` for thresholds and rejection behavior.

This is the active transformer returned by `LivestackMediator.GetImageTransformer()`.

### Rendering and stack tabs

- `LivestackDockables/LiveStackTab.cs`
  Monochrome stack tabs. Renders a stretched grayscale preview and can save FITS stacks.
- `LivestackDockables/ColorCombinationTab.cs`
  RGB composite tab. Aligns channels to the red reference, applies per-channel stretch, optional green denoise, and saves PNG output.
- `Image/LiveStackBag.cs`
  Stores the current stack buffer, reference stars, metadata, and image count.

### Sequencer instructions

Important sequence items:

- `Instructions/StartLivestacking.cs`
- `Instructions/StopLivestacking.cs`
- `Instructions/StackFlats.cs`

`StackFlats` listens for FLAT frames inside a sequence block, calibrates them in the background, stacks them with percentile clipping, writes a master flat FITS, and adds it to the session flat library.

`Instructions/CameraSimulatorDirectory.cs` is a test/helper instruction for repointing the N.I.N.A. simulator camera to a local directory of files.

### FITS and native interop

Low-level FITS I/O is implemented here:

- `CFitsioFITSReader.cs`
- `CFitsioFITSExtendedWriter.cs`
- `CFitsioExtensions.cs`

Key details:

- compressed FITS images may be decompressed to a temp file before reading
- span-based row reads are available and are the preferred path
- output FITS files preserve and repopulate a large amount of N.I.N.A. metadata

If you change the native FITS layer, validate with both the test project and real sample files.

## Build Commands

### Restore

```powershell
dotnet restore
```

### Build solution

```powershell
dotnet build nina.plugin.livestack.sln -c Debug
dotnet build nina.plugin.livestack.sln -c Release
```

### Build only the plugin project

```powershell
dotnet build nina.plugin.livestack.csproj -c Debug
```

## Build Gotchas

- The plugin project has a post-build target in `nina.plugin.livestack.csproj` that copies outputs to `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Livestack`.
- That copy step has `IgnoreExitCode="true"`, so compile output can still succeed even if the copy fails.
- In restricted environments, builds may show `Access denied` during the copy step. Treat that as a deployment issue, not necessarily a compile failure.
- GitHub CI builds the solution with `-p:PostBuildEvent=` in `.github/workflows/github-action.yaml`, but the custom post-build target still exists in the project file. If CI behavior changes, inspect the custom target before assuming the post-build copy is fully disabled.
- Initial restore requires NuGet network access.

## Test Commands

### Run unit tests

```powershell
dotnet test nina.plugin.livestack.test/nina.plugin.livestack.test.csproj -c Debug -v minimal
```

### What is actually covered

The NUnit suite covers calibration against an independent scalar reference,
star selection, alignment acceptance and rejection, bilinear sampling, pixel
weights, buffer ownership and count promotion. Integration tests construct the
dockable and exercise mono rejection, preview rendering, cancellation, failure
recovery and dependent-tab removal. Sample FITS files also exercise native I/O.

These tests do not replace a real N.I.N.A. camera replay or complete broker and
sequencer validation.

### Run benchmarks

```powershell
dotnet run --project nina.plugin.livestack.benchmark/nina.plugin.livestack.benchmark.csproj -c Release
```

## Manual Validation

For meaningful changes, manual validation inside N.I.N.A. is important.

Typical checklist:

1. Build the plugin.
2. Ensure the plugin is available under `%LOCALAPPDATA%\NINA\Plugins\3.0.0\Livestack`.
3. Open N.I.N.A. and verify the `Live Stack` dockable loads.
4. Set a valid working directory in the plugin options.
5. Add calibration masters if the change affects calibration.
6. Start live stacking and capture LIGHT or SNAPSHOT frames.
7. Verify stack tab creation, alignment behavior, autosaves, and message-broker behavior as applicable.
8. If changing flat workflows, run a sequence with `Stack flats`.

## Code Style And Conventions

The main style source is `.editorconfig`.

Important conventions:

- 4-space indentation
- CRLF line endings
- preserve CRLF when editing existing files; do not introduce mixed line endings
- block-scoped namespaces
- braces preferred
- `using` directives outside namespaces
- PascalCase for types, methods, properties
- interfaces prefixed with `I`
- explicit types preferred over `var` in most cases
- expression-bodied properties/accessors are preferred

The repo also includes `CodeMaid.config`, which enables auto-cleanup on save and member reorganization.

### MVVM conventions

Most viewmodels use CommunityToolkit.Mvvm attributes:

- `[ObservableProperty]`
- `[RelayCommand]`

The codebase is mixed, not uniform:

- newer code leans on CommunityToolkit.Mvvm
- `Livestack.cs` still uses manual `INotifyPropertyChanged`
- `Livestack.cs` also still uses `GalaSoft.MvvmLight.Command.RelayCommand`

Follow the style already present in the file you are editing unless there is a clear reason to normalize a whole area.

### Composition conventions

This is a MEF-based N.I.N.A. plugin. Common patterns:

- `[Export(typeof(...))]`
- `[ExportMetadata(...)]`
- `[ImportingConstructor]`

Do not remove or casually refactor export/import attributes without understanding how N.I.N.A. discovers plugins, dockables, and sequence items.

## Dependency And Compatibility Notes

- Main plugin target: `net8.0-windows7.0`
- Main package dependency: `NINA.Plugin` version `3.2.0.9001`
- `AllowUnsafeBlocks` is enabled
- Native CFITSIO DLLs are required for tests and benchmarks

Expected warnings seen during restore/build include `NU1701` warnings for older dependencies such as:

- `ToastNotifications`
- `VVVV.FreeImage`

Treat those as existing compatibility debt, not a new regression by default.

## Practical Guidance For Agents

- Start codebase exploration with:
  - `Livestack.cs`
  - `LivestackDockables/LivestackDockable.cs`
  - `Instructions/StackFlats.cs`
  - `Image/CalibrationManagerSimd.cs`
  - `Image/ImageTransformer2.cs`
- If you touch calibration logic, run `dotnet test` afterward.
- If you touch performance-sensitive image code, inspect `nina.plugin.livestack.benchmark/` and consider benchmarking.
- If you touch plugin packaging, inspect both `.github/workflows/github-action.yaml` and `bitbucket-pipelines.yml`.
- If you touch UI/settings flow, inspect both `Options.xaml` and the corresponding viewmodel class.
- Do not rely on `README.md`; it does not contain meaningful project documentation.
