# Panorama orientation

`IMAGE_HEADING_OFFSET` (panorama-viewer.ts) aligns CesiumJS's `EquirectangularPanorama` with our convention:
`panorama.heading_deg` is the direction of the **image centre** from geographic north, clockwise (spec 7.2), and image
yaw grows to the right.

Measured with CesiumJS 1.146 (2026-10-23): with a transform of heading `T`, the image yaw `y` appears at the absolute
direction `T + y − 90°`, unmirrored. So `T = heading − (−90°)`, i.e. the offset is **−90°**.

Re-check after a CesiumJS upgrade: render a labelled test image (label = image yaw at its column, e.g. "0" in the
middle, "+90" at three quarters) with heading 0 and look north (camera heading 0): "0" must be in the middle and
turning east (camera heading 90) must show "+90", readable (not mirrored).
