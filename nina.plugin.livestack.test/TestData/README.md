# Alignment regression catalogs

These compressed JSONL fixtures preserve raw detector output before Livestack's
star filtering and spatial selection. Tests reconstruct `DetectedStar` values
through the shared `ArchiveStarCatalog` DTO. Each record includes the source
SHA-256, image dimensions, detector version and detector settings. Source labels
are portable basenames. Ordinary tests need no original image files, network
share or installed Hocus Focus plugin.

## HocusFocusSh2126.jsonl.gz

Two 6248 by 4176 RGGB exposures of Sh2 126 were processed through the production
OSC replay path with Hocus Focus 4.0.0.20 and hot-pixel removal disabled. The
fixture contains the red-channel catalogs used for alignment, with 9850 and
10325 raw detections. The source exposures are:

* `Sh2 126-2026-10-08_21-45-48_Luminance_-19.90_300.00s_0202.fits`
* `Sh2 126-2026-10-08_21-50-48_Luminance_-20.00_300.00s_0203.fits`

The baseline plugin rejected the second exposure after its ordinary 1000-star
selection. `HocusFocusRegressionTests` checks both alignment directions against
an independent transform estimated by windowed phase correlation on five raw
red-CFA image patches. The accepted transform must agree within 0.75 pixels at
the four corners and center. The oracle is stored in the test rather than
derived from the alignment result.

## M15SparseBuiltin.jsonl.gz

Five 6224 by 4168 mono catalogs come from the production replay path with the
built-in detector and ordinary hot-pixel removal enabled. They contain the first
blue and green references followed by three weak exposures, in this order:

| Record | Filter | Raw detections | Source exposure |
| --- | --- | ---: | --- |
| 0 | B | 400 | `2022-08-06_22-46-43_LIGHT_B_-9.90_120.00s_0000_421_0.54.xisf` |
| 1 | G | 504 | `2022-08-06_22-44-40_LIGHT_G_-10.20_120.00s_0000_526_0.73.xisf` |
| 2 | B | 80 | `2022-08-07_01-29-14_LIGHT_B_-9.90_120.00s_0024_82_0.79.xisf` |
| 3 | G | 116 | `2022-08-07_01-33-23_LIGHT_G_-10.00_120.00s_0025_124_0.67.xisf` |
| 4 | B | 84 | `2022-08-07_01-35-27_LIGHT_B_-9.90_120.00s_0025_87_0.71.xisf` |

Each weak exposure carries `OracleMatrix`, `OracleReferenceFile`,
`OracleMethod`, measured patch displacements and affine-fit residuals. The
independent oracle uses windowed phase correlation on five 1024-pixel normalized
image patches. Matrices map the fixed reference into the incoming image.

`M15SparseRegressionTests` requires both blue exposures to align in both
directions within 0.75 pixels at the corners and center. The green exposure may
be rejected, but any accepted result must meet the same accuracy limit. This
distinguishes useful sparse recovery from an inaccurate fit with a small RMS.

See the benchmark project's README for detector extraction and image replay
commands. Extraction resets the stack after each image; use replay to reproduce
consecutive-frame acceptance and stack pixels.
