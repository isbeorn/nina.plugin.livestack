# Live Stack Replay

A small Windows WPF app for checking captured frames without starting N.I.N.A.
Requires the .NET 8 SDK to build, or the .NET 8 Desktop Runtime to run a built copy.

From the repository root:

```powershell
git submodule update --init nina.plugin.livestack.test/External
.\run-replay.cmd
.\run-replay.cmd "D:\Captures\M101\LIGHT"
.\run-replay.cmd "D:\Captures\first.fits" "D:\Captures\second.xisf"
.\run-replay.cmd --paused "D:\Captures\M101\LIGHT"
```

Paths supplied on the command line start playing automatically. Use `--paused`
to load them without starting. The app also accepts files or folders through
its pickers, the path box (one path per line) and drag-and-drop. Network paths
work as long as the current Windows user can access them.

Folders are searched recursively for FITS, compressed FITS and XISF files.
Files are deduplicated and ordered by a N.I.N.A.-style timestamp in the filename,
falling back to modification time. Use LIGHT folders to avoid loading calibration
and processed outputs. Frames identified as other image types are skipped;
missing image-type metadata is treated as LIGHT.

## Controls

- **Play** processes the remaining files. **Step** processes one file.
- **Stop after frame** finishes the current frame and pauses before the next one.
  Closing the window also waits for that frame, so a committed image is never
  silently replayed twice after an interrupted preview.
- **Reset stacks** keeps the inputs and calibration masters but starts again
  from the first frame. **Clear files** also removes the inputs. Adding more paths
  resets the stacks so the complete input list can be reordered consistently.
- Select **Mono** for mono captures or the correct Bayer pattern for raw OSC
  captures. The loader uses a 16-bit input setting. Hot-pixel cleanup defaults to
  the plugin's normal setting and can be switched off for comparisons.
- The tabs show the plugin's stack previews. The processing log includes its
  match counts, residuals and rejection reasons, with the latest 300 messages kept.
- In **Calibration masters**, choose BIAS, DARK or FLAT and add FITS masters.
  Filter, gain, offset and exposure are editable to correct incomplete headers.
  Selection and calibration use the production calibration library. Reset stacks
  after changing calibration to compare complete runs under the new settings.

Unreadable files are reported individually and the replay continues. Source
images and masters are opened for reading. Temporary FITS copies are created
under a unique `%TEMP%\Livestack-Replay` directory, removed after each frame and
cleaned up on exit. No N.I.N.A. profile is loaded or saved. This tool does not
autosave its previews or stacks.

## Shared pipeline

`LivestackDockable.ProcessImageAsync` prepares and processes decoded files using
the same methods as the live capture queue: statistics, star detection, temporary
FITS writing, quality gates, calibration, hot-pixel cleanup, mono/OSC alignment,
weighted accumulation and preview rendering. Alignment and pixel math are not
copied into this project. N.I.N.A.'s profile, camera, status and broker interfaces
are substituted by a small in-memory host; N.I.N.A.'s image implementations are real.

The UI does not configure quality gates, mono channel combinations or autosave.
Raw OSC captures use the pipeline's automatic color tab. The bundled N.I.N.A.
libraries can report a missing optional `External/JPLEPH` ephemeris while writing
FITS metadata; stacking does not require it.

Building this project sets `SkipPluginDeploy=true` on its plugin reference, so
it does not copy a development DLL into the installed N.I.N.A. plugin directory.
The required native imaging libraries are linked from the existing test submodule.
Initialize it once with the command above when using a fresh checkout.
For a portable build folder:

```powershell
dotnet publish nina.plugin.livestack.replay -c Release -o artifacts\replay
```

Copy that complete folder to another Windows machine with the .NET 8 Desktop
Runtime and launch `Livestack.Replay.exe`. Keep its native library subfolders.
