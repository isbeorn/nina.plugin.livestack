# Livestack

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