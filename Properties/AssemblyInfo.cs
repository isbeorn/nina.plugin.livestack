using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Markup;

// [MANDATORY] The following GUID is used as a unique identifier of the plugin. Generate a fresh one for your plugin!
[assembly: Guid("10bc1716-54af-425e-b307-c0ca1ce10600")]

// [MANDATORY] The assembly versioning
//Should be incremented for each new release build of a plugin
//[assembly: AssemblyVersion("1.0.1.2")]
//[assembly: AssemblyFileVersion("1.0.1.2")]

// [MANDATORY] The name of your plugin
//[assembly: AssemblyTitle("Livestack")]
// [MANDATORY] A short description of your plugin
//[assembly: AssemblyDescription("Live stacking within N.I.N.A.")]

// The following attributes are not required for the plugin per se, but are required by the official manifest meta data

// Your name
//[assembly: AssemblyCompany("Stefan Berg @isbeorn")]
// The product name that this plugin is part of
//[assembly: AssemblyProduct("Livestack")]
//[assembly: AssemblyCopyright("Copyright © 2024-2025 Stefan Berg")]

// The minimum Version of N.I.N.A. that this plugin is compatible with
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.1018")]

// The license your plugin code is using
[assembly: AssemblyMetadata("License", "MPL-2.0")]
// The url to the license
[assembly: AssemblyMetadata("LicenseURL", "https://www.mozilla.org/en-US/MPL/2.0/")]
// The repository where your pluggin is hosted
[assembly: AssemblyMetadata("Repository", "https://github.com/isbeorn/nina.plugin.livestack")]

// The following attributes are optional for the official manifest meta data

//[Optional] Your plugin homepage URL - omit if not applicaple
[assembly: AssemblyMetadata("Homepage", "https://www.patreon.com/stefanberg/")]

//[Optional] Common tags that quickly describe your plugin
[assembly: AssemblyMetadata("Tags", "Livestack")]

//[Optional] A link that will show a log of all changes in between your plugin's versions
[assembly: AssemblyMetadata("ChangelogURL", "https://github.com/isbeorn/nina.plugin.livestack/blob/main/CHANGELOG.md")]

//[Optional] The url to a featured logo that will be displayed in the plugin list next to the name
[assembly: AssemblyMetadata("FeaturedImageURL", "https://github.com/isbeorn/nina.plugin.livestack/blob/main/logo.png?raw=true")]
//[Optional] A url to an example screenshot of your plugin in action
[assembly: AssemblyMetadata("ScreenshotURL", "https://github.com/isbeorn/nina.plugin.livestack/blob/main/featured.jpg?raw=true")]
//[Optional] An additional url to an example example screenshot of your plugin in action
[assembly: AssemblyMetadata("AltScreenshotURL", "https://github.com/isbeorn/nina.plugin.livestack/blob/main/featured2.jpg?raw=true")]
//[Optional] An in-depth description of your plugin
[assembly: AssemblyMetadata("LongDescription", @"This plugin enables live stacking functionality within N.I.N.A. - It allows you to view a live stack of your images, calibrate them, and manage them in real-time during your imaging session.

## Prerequisites

Before using this plugin, ensure you meet the following requirements:

+ **PC Requirements**: Ensure your system has sufficient memory to handle live stacking.
+ **Working Directory**: Set a folder for storing calibration files, temporary files, and live stack files.
+ **Plugin Settings**: Adjust additional settings on the plugin page according to your preferences.
+ **Master Calibration Files**: (Optional) Add Bias, Dark, or Flat master files for calibration.

## Sequencer Instructions

+ **Stack Flats**
  This instruction works in conjunction with your Flat frame captures:
  + Place this instruction within a set of Flat frame capture instructions, positioned after the last Flat frame capture.
  + As Flat frames are captured, the instruction automatically gathers and calibrates them in the background.
  + Once executed, the instruction will stack all the Flat frames that have been gathered and save the result.
  + The stacked result is then automatically added to the session library within the live stack panel.

+ **Start Live Stack**
  Initiates the live stacking process within the **Imaging** tab.

+ **Stop Live Stack**
  Ends the live stacking process within the **Imaging** tab.

## Live Stacking Panel
### Expander Options

The **Live Stack** panel includes several useful features:

+ **Session Flat Master Files**:
  You can manually add individual session Flat master files. Alternatively, the *Stack Flats* instruction can automatically populate this list.

+ **Multi-Session Flat Masters**:
  These are not displayed in the panel but will be used if configured in the plugin settings page.

+ **Quality Gates**:
  You can apply quality gates to filter out frames that don't meet certain criteria for stacking.

+ **Target Color Combination**:
  For a given target, you can specify a color combination to create a color stack after the next frame of that target is processed.

### Live Stacking Process

+ **Start Live Stack**
  Once started, the plugin will process and stack any light frames that are captured in real-time.

  + All captured frames will be calibrated and added to a stack.
  + Filters and target names are automatically recognized and managed. You can create separate stacks for different filters or targets.
  + **Note**: One-shot color images (OSC) are automatically separated into individual channels (Red, Green, Blue) and also combined into a color stack.

+ **Stacked Frame Count**
  Within the stack window, you can view the current number of frames that have been stacked.")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]
// [Unused]
[assembly: AssemblyConfiguration("")]
// [Unused]
[assembly: AssemblyTrademark("")]
// [Unused]
[assembly: AssemblyCulture("")]
#if DEBUG
[assembly: XmlnsDefinition("debug-mode", "Namespace")]
#endif