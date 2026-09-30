# Livestack

## 1.1.4.0

- Fixed dark master selection when gain or offset is set to "Ignore" so the exposure time must still match.
- Fixed automatic color-pattern detection for connected one-shot color cameras.
- Fixed bright pixels turning dark in stack previews.
- Stopping or restarting live stacking now waits for incoming frames to finish cleanup, preventing leftover temporary files or frames carrying over into a new session.
- Quality gates now follow the active N.I.N.A. profile correctly.
- Flat stacking now keeps repeated sequence blocks separate and correctly handles cancellation and the "Wait for flat stack?" option.
- Faster calibration and color preview updates without keeping extra full-size images in memory.
- Fixed growing memory use in the replay viewer when processing many frames or resetting stacks.
- Added a standalone replay viewer to test saved captures without opening N.I.N.A., with step-by-step stacking and alignment feedback.
- Better protection against poor alignment matches that could stretch or skew an image and spoil the stack.
- Stacking now requires at least eight usable stars and skips frames with incompatible camera settings to protect the current stack.
- Improved image detail when aligning small shifts and corrected darkening along edges where frames do not fully overlap.
- Reduced memory usage during calibration, stacking and preview updates, especially with large images.
- Preview updates recover more reliably after cancellation or failure while keeping the last successful preview visible.
- Closing a stack also removes color combinations that depend on it.

## 1.1.3.0
- Fixed live-stack alignment star selection when detector brightness metadata is unusable.
    - Alignment now falls back to valid detected star centroids when MaxBrightness is saturated, invalid, or NaN, instead of producing zero filtered stars.
- Improved "not enough alignment stars" logging with reject-reason diagnostics for invalid position, outside frame, invalid brightness, saturated brightness, HFR outlier, and fallback candidate count.

## 1.1.2.0
- Added more logging for failure cases
- Added experimental background extraction for stack previews, using automatic sample boxes, global rejection, and polynomial background modeling. This affects preview rendering only.

## 1.1.1.0
- Improved star selection consistency so reference stars are more likely to match across frames
- Added more robust star matching for sparse fields, very dense fields, and meridian-flip-like upside-down frames

## 1.1.0.0
- Saving and reading FITS files now use the CFITSIO diskfile APIs so square or curly bracket characters in file names work correctly
- Image alignment now uses RANSAC to improve robustness against outliers
- Sparse affine matches now fall back to a direct least-squares solve instead of failing when only a few good correspondences are available
- Improved star selection and triangle matching for more robust and faster alignment
- Improved live stack performance with vectorized stack accumulation and faster affine image transforms
- Improved live stack performance under low-memory constraints by avoiding redundant OSC RGB realignment, skipping hidden RGB composite refresh work, and reducing flat-stacking allocations during percentile clipping
- Improved Image calibration to utilize SIMD instructions for better performance and reduced memory usage
- Fixed zero-star analysis fallback so live stacking correctly re-runs star detection when a frame reports no detected stars

## 1.0.1.7
- A broadcast on IMessageBroker will be sent with the topic "Livestack_LivestackDockable_StatusBroadcast" when the plugin's running status changes.

## 1.0.1.6
- Calibration frames that no longer exist will automatically get removed from the profile

## 1.0.1.4
- Fix start and stop live stack via message broker to not reflect properly in the User Interface
- Added a reset to default button for the stretch popouts

## 1.0.1.3
- Prevent live stack to be started multiple times when called via message broker

## 1.0.1.0
- Handle cases for calibration masters where the floating point data is not normalized to 0..1 but instead defined via DATAMIN and DATAMAX keywords

## 1.0.0.9
- The plugin now subscribes to the following messages in order to start and stop the live stacking from outside the plugin:
    - Livestack_LivestackDockable_StartLiveStack
    - Livestack_LivestackDockable_StopLiveStack
- A broadcast on IMessageBroker will be sent with the topic "Livestack_LivestackDockable_StackUpdateBroadcast" when a stack tab gets updated.

## 1.0.0.8
- Master frames now show "Ignore" instead of "-1" for gain and offset. If "Ignore" is specified the image calibration will consider these frames if no exact match for the image gain and offset is found.
- Added an info log that shows with which calibration masters an image gets calibrated with

## 1.0.0.7
- Added separate save options on the stack tabs
- Metadata of first frame in stack will now also be put into the keywords of the save stack

## 1.0.0.6
- Fix plugin failure to load when one of the library files was deleted
- Add Quality Gate for RMSAbsolute threshold

## 1.0.0.5
- Small bugfix to properly dispose file access to calibration files

## 1.0.0.4
- Light frames are now calibrated as 32-bit floating point images
- New options:
    - **Save calibrated Flats**: When enabled the calibrated Flat frames won't get deleted
    - **Save calibrated Lights**: When enabled the calibrated Light frames will get saved to disk.  
        *Keep in mind that this will increase the time to process a frame.*
    - **Save stacked Lights**: When enabled the stacked frames will get saved to disk including the color combination.  
        *Keep in mind that this will increase the time to process a frame.*

## 1.0.0.3
- Calibration masters that are not saved in 32-bit floating point format are now read correctly

## 1.0.0.2
- One shot color stacking improvements
    - The stacker will no longer detect stars on all three channels but will use the red channel for reference stars.
    - When one of the RGB tabs is closed, all other channels for this target are closed too as the star reference is no longer valid
- Stacking tabs now show the number of reference stars used


## 1.0.0.1
- When taking snapshots with star detection disabled and autostretch enabled the plugin now properly performs the star detection

## 1.0.0.0
- Initial release
