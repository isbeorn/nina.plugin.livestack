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

## Follow-up comparison measurements

`--compare-alignment` defaults to `--measurement BatchMean`. Each sample is a
loop's elapsed time divided by its iteration count, with collection before the
batch. Percentiles across those samples are percentiles of batch averages, not
individual-call tail latency.

Use `--measurement IndividualCall --iterations 1 --samples 30` to record each
call separately. This mode does not force collections between calls. Setup
validates the expected result and warms each operation; every measured result is
checked outside its timed interval. Process CPU measurements can remain below
clock resolution for short calls. Report wall latency and allocations separately.
Neither mode includes detector work or pixel resampling.

`Verification/Compare-AlignmentVersions.ps1` runs identical isolated .NET 8
runners in alternating baseline/candidate process order. It writes each raw
result and a summary paired by round, scenario, mode and operation. It checks
runner hashes and rejects unverified rows. Pairing process rounds does not imply
that individual sequential calls are simultaneous paired observations. Use
several rounds on an idle host, include recovery workloads and report the small
sample size with any p95 estimate.

```powershell
./nina.plugin.livestack.benchmark/Verification/Compare-AlignmentVersions.ps1 -Baseline C:/isolated/baseline/nina.plugin.livestack.benchmark.exe -Candidate C:/isolated/candidate/nina.plugin.livestack.benchmark.exe -OutputDirectory obj/alignment-followup/paired -Scenario Typical300 -Rounds 3 -Samples 30
```

Archive status stages are coarse intervals between UI status updates. `Load`
includes preparation/detection and `Aligning frame` includes catalog preparation
and accumulation. OSC's red alignment stage also includes detection. These labels
are not direct measurements of disk I/O or pure solver time. Memory peaks are
sampled process maxima; `ManagedHeapBytes` is an after-frame heap reading.

## Proposal-route research

`--compare-proposals` compares the existing production acquisition route with
ordered triangles, spatial triangles and quads through the same production final
verifier. Benchmark-only reflection selects the proposal methods and constructs
the ordinary association search. No production option or acceptance rule changes.
Prepared matching/support catalogs and dimensions are identical across routes.
The production route disables prediction with an invalid matrix to compare
acquisition. ProductionIdentity supplies an identity prediction and
ProductionWrongPrediction supplies a 180-degree prediction with translations
width + 300 and height - 200. These exercise the complete prediction/recovery policy;
standalone proposal routes do not.
Each route is initialized once outside timing before measurement. Cold means a
descriptor-cache miss in an initialized process: quad cold runs clear its cache;
warm runs prime the route again first.
The timing includes the association index, proposal and verification, excluding
catalog preparation, detection and pixels.

```powershell
dotnet run --project nina.plugin.livestack.benchmark -c Release --no-build -- --compare-proposals --scenario Typical300 --repetitions 3 --budget-ms 1000 --output obj/alignment-followup/proposals-synthetic.json
```

Synthetic cases have known transform truth. `--both-directions` adds reversed
cases with inverse truth matrices. Archive comparisons accept
`--catalogs` with semicolon-separated JSONL/JSONL.gz paths and `--pairs` with a
JSON array of `{ "Name", "Reference", "Current", "ExpectedAcceptance", "ExpectedMatrix" }`.
Reference/current values must match the exported catalog `File` exactly. The
optional matrix contains nine row-major values. Leave expected acceptance and
matrix absent when independent truth is unavailable: an accepted same-target
pair is then `Unadjudicated`, not a correct success. Declare unrelated pairs as
false only after verifying their scene/metadata. Freeze pairs and parameters
before viewing final evaluation outcomes.

The common wall deadline is cooperative. Uninterruptible work may overrun; the
report records actual elapsed time, cancellation and whether the deadline and
allocation cap were met. An over-budget result is not an in-budget success.
This measures the actual existing routes under common resource ceilings rather
than pretending that they generate equal numbers of hypotheses. Their internal
work caps remain different. It does not establish whole-policy false-acceptance
calibration. No individual route is promoted to production by this harness.
The benchmark-only LocalTriangles alternative uses the same at-most-72 spatial
points as spatial triangles. Each anchor and pairs among its four nearest
neighbors produce at most 432 triangles before deduplication. Opposite side
lengths canonicalize vertices; two side/longest ratios select at most three
descriptor neighbors within 0.02. Longest sides below 12 pixels and shortest/longest
ratios below 0.05 are excluded. Weighted votes select unique pairs deterministically
then the existing robust estimator and final verifier decide acceptance. These
parameters are frozen experimental choices, not new production settings.

## Paired complete-pipeline verification

`Verification/Compare-PipelineVersions.ps1` runs a fixed twelve-frame mono
workload twice per process, with eight paired fresh processes per detector.
Baseline/candidate order alternates between blocks. The first sequence records
startup and transition behavior; analyze the second sequence separately without
assuming that the runtime has reached steady state. File reads before each
process prepare the storage cache consistently.

Pass identical benchmark runners with separate plugin versions, their expected
plugin SHA-256 hashes, a twelve-path input list, a frozen protocol document and
a new output directory. The controller checks runner/runtime configuration
identity, records input and Hocus Focus library hashes and preserves raw outputs.
It uses four logical processors, affinity mask 85 and an 8 GiB managed-heap cap.
It never deploys a plugin or retries a failed measurement automatically.

```powershell
./nina.plugin.livestack.benchmark/Verification/Compare-PipelineVersions.ps1 `
    -Baseline C:/isolated/baseline/nina.plugin.livestack.benchmark.exe `
    -Candidate C:/isolated/candidate/nina.plugin.livestack.benchmark.exe `
    -BaselinePluginSha256 $baselineHash -CandidatePluginSha256 $candidateHash `
    -InputList C:/verification/twelve-frames.txt `
    -Protocol C:/verification/frozen-protocol.md `
    -OutputDirectory obj/pipeline-comparison
```

Pair frames by sequence index and path because paths repeat. Verify decisions,
transforms and complete endpoint pixels/counts before interpreting timings.
Use paired processes as the independent samples, not individual frames. Declare
estimators, margins and fixed sample counts before measuring. A confidence bound
that crosses the declared margin is inconclusive even if its point estimate is
favorable. The existing runner samples private memory every 20 ms during replay;
its frame records do not establish a whole-process lifetime peak.
