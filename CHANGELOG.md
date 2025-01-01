# Livestack

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