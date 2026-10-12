# Alignment benchmarks

Build with deployment disabled:

```powershell
dotnet build nina.plugin.livestack.benchmark/nina.plugin.livestack.benchmark.csproj -c Release -p:SkipPluginDeploy=true
```

The catalog benchmarks call the production `ComputeAlignment` API. Setup checks
acceptance or rejection and compares accepted transforms with the known transform
at nine points, including the corners and center, with a 0.75 pixel limit. An inaccurate or rejected positive case is not
reported as a fast result.

```powershell
dotnet run --project nina.plugin.livestack.benchmark -c Release --no-build -- --filter '*CatalogAlignmentBench*'
```

`PrepareCurrentCatalog` measures current-frame filtering and catalog creation.
`AlignPreparedCatalog` excludes preparation. `PrepareAndAlignCurrent` includes it
with a prepared fixed reference, as in an existing stack. Reference preparation is
one-time stack setup and is excluded from these per-frame measurements. These
benchmarks allocate no image arrays and do not measure image resampling.

The deterministic scenarios cover 20, 300, 3000, 12000 and 20000 raw reference
detections, roughly 26, 61 and 102 MP image dimensions, extra detections, subpixel
noise, a bad prediction, a meridian flip, unrelated fields, a dense 1000x1000
negative field and alternating stack
references. `Normal` supplies a previous-frame prediction. `NoPrediction` measures
acquisition with a deliberately invalid zero matrix, rejected by the ordinary
plausibility check before geometric matching. Passing null would enable the
production identity prediction, so it would not disable that path.
`Support4000` limits the prepared support pool through
the ordinary catalog constructor. Its preparation time includes that additional
benchmark-only copy. It does not introduce a production option. Disabled prediction
does not clear the transformer's reference cache; alternating references exercise
cache replacement separately.

For shorter repeatable comparisons:

```powershell
dotnet run --project nina.plugin.livestack.benchmark -c Release --no-build -- --compare-alignment --scenario all --mode all --iterations 3 --samples 5 --output obj/alignment-verification/results/current.json
```

Use `--scenario Typical300 --mode Normal` for a smoke check. Each sample records
wall time, process CPU time and whole-process managed allocations per operation.
The JSON includes plugin and runner SHA-256 hashes, plugin version, runtime,
processor count and the actual selected pool sizes. Use `--source-revision` to
record the commit or working-tree diff identifier
alongside the binary hashes. Run on an idle machine and
compare distributions across repeated samples. Short operations can fall below
the process CPU clock's resolution; increase `--iterations` before interpreting
zero CPU samples.

For a baseline with an older assembly version, preserve the candidate plugin DLL
and PDB first, inspect the baseline DLL with `AssemblyName.GetAssemblyName` and
build the benchmark runner with `-p:AssemblyVersion=<baseline-assembly-version>`.
The assembly version can differ from its package version.
Copy this common runner and its dependencies into separate baseline and candidate
directories. Replace only `nina.plugin.livestack.dll` and its matching PDB in each
copy with the preserved baseline or candidate. Both runs must use the identical
runner. Restore the ordinary Release build afterward. A runner compiled against
a newer plugin assembly version cannot simply load the older DLL. Run
the isolated executables with the same arguments, CPU affinity and environment. The
runner resolves catalog support at runtime and falls back to the baseline's list
`ComputeAlignment` API. Baseline rows have `CatalogApi=false`; prediction and
support variants then use the same legacy path. Keep the corresponding baseline
PDB with its DLL when collecting traces. Do not replace DLLs in the active output
directory while another verification run is using it.

Do not compare timing for a `correctness_failed` row with a verified result.
Compare `PrepareAndAlignCurrent` on workloads the baseline accepts, alongside
prepared alignment and allocation results. The target is no more than 5% median
CPU regression on those workloads, no extra image allocations, at most 160 KiB
retained point catalogs per reference and at most 1 MiB additional alignment
scratch. The timing runner measures allocated bytes, not live retained memory or
peak process memory. Verify those ceilings with the archive/replay runner and
runtime measurements. CPU affinity to four logical processors does not simulate
all properties of a four-core machine or enforce a 16 GB memory limit.

Archive extraction, catalog checks and image replay use `--verify-archive`.
Use their output to establish behavior on the actual FITS data with each detector
before drawing conclusions from synthetic timing results.

To include the actual Hocus Focus pair, supply its extracted JSONL or JSONL.gz
catalog and an independently checked reference-to-current matrix. The matrix file
may contain a flat nine-number JSON array, a nested 3x3 array or the final JSONL
alignment result containing `Matrix`. The runner checks both forward and reverse
alignment against that oracle at nine image positions before timing.

```powershell
dotnet run --project nina.plugin.livestack.benchmark -c Release --no-build -- --compare-alignment --scenario HocusFocus --catalogs nina.plugin.livestack.test/TestData/HocusFocusSh2126.jsonl.gz --expected-matrix obj/alignment-verification/results/verified-builtin-matrix.json --output obj/alignment-verification/results/hf-current.json
```

For BenchmarkDotNet, set `LIVESTACK_ALIGNMENT_CATALOGS` and
`LIVESTACK_ALIGNMENT_EXPECTED_MATRIX` to absolute paths before starting the runner.
It adds `HocusFocus` and `HocusFocusReverse` to the scenario list. The real pair uses
identity as its initial prediction because it has no preceding accepted frame.

## Archive replay

Create a text file with one absolute FITS path per line. Blank lines and lines
starting with `#` are ignored. Run the production capture and stacking path with
one detector and sensor mode per process:

```powershell
dotnet run --project nina.plugin.livestack.benchmark -c Release --no-build -- --verify-archive --replay --input-list obj/alignment-verification/frames.txt --sensor RGGB --detector hf --output obj/alignment-verification/archive-hf
```

Use `--sensor Mono` for mono frames and `--detector builtin` for N.I.N.A.'s
detector. Hocus Focus loads from its installed plugin directory by default;
`--hf-directory` supplies another installed copy. `--no-hotpixels` disables
hot-pixel cleanup. `--masters` accepts a JSON array of calibration master entries:

```json
[{"Type":"DARK","Path":"C:\\captures\\dark.fits","Gain":100,"Offset":10,"ExposureTime":300}]
```

Each entry requires `Type` (`BIAS`, `DARK` or `FLAT`) and `Path`. Optional `Gain`,
`Offset`, `Filter` and `ExposureTime` override metadata read from that master.

Replace `--replay` with `--extract-catalogs` to export raw detector catalogs.
Extraction removes stack tabs after every frame, so it cannot demonstrate
consecutive stacking. Use `--replay` to check that behavior. Replay writes final
stack pixels and contribution counts as streamed binary files, described by
`stacks.json`.

Both routes write `environment.json`, `frames.jsonl`, `catalogs.jsonl` and
`processing.log`. Frame records include acceptance, status-stage durations,
process CPU time, allocated bytes, GC counts and cumulative process memory peaks.
The elapsed and CPU measurements exclude catalog serialization and source-file
hashing. Candidate replay records the last committed matrix when a stack count
increases. Older baseline assemblies have no stored prediction field, so their
replay matrix is absent. Compare their catalog results and actual replay
acceptance and pixels instead.

Solve exported catalogs without image processing:

```powershell
dotnet run --project nina.plugin.livestack.benchmark -c Release --no-build -- --verify-archive --catalog-check --catalogs obj/alignment-verification/archive-hf/catalogs.jsonl --output obj/alignment-verification/catalog-check
```

`--catalogs` also accepts `.jsonl.gz`. The checker writes `alignment.jsonl`, with
selection and solve durations, fit diagnostics and matrices. It keeps a fixed
reference per target, filter and sensor mode and reuses the last successful
prediction. Supply catalogs from a compatible capture setup; the checker does
not enforce the full replay path's frame metadata compatibility policy. Check
accepted matrices against an independent oracle before using their timing as
performance evidence.
