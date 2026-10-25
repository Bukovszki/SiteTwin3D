import {
  Cartesian2,
  Cartesian3,
  HeadingPitchRange,
  Math as CesiumMath,
  Matrix4,
  Transforms,
  Viewer,
} from 'cesium';

/** Steps of the navigation buttons (user request: more 3D tools to find one's way). */
const ROTATE_STEP = CesiumMath.toRadians(22.5);
const TILT_STEP = CesiumMath.toRadians(15);
const ZOOM_FACTOR = 0.6;
const MIN_PITCH = CesiumMath.toRadians(-89.9);
const MAX_PITCH = CesiumMath.toRadians(-3);
const MIN_RANGE = 2;

/** What the camera looks at: the point under the screen centre (model or ground map), else the fallback. */
function target(viewer: Viewer, fallback: Cartesian3): Cartesian3 {
  const canvas = viewer.scene.canvas;
  const centre = new Cartesian2(canvas.clientWidth / 2, canvas.clientHeight / 2);
  const picked = viewer.scene.pickPositionSupported ? viewer.scene.pickPosition(centre) : undefined;
  return picked && Cartesian3.magnitude(picked) > 0 ? picked : fallback;
}

/** The camera around the target: heading, pitch and distance changed, the target stays in the middle. */
function orbit(
  viewer: Viewer,
  fallback: Cartesian3,
  change: (view: { heading: number; pitch: number; range: number }) => { heading: number; pitch: number; range: number },
): void {
  const camera = viewer.camera;
  const centre = target(viewer, fallback);
  const view = change({
    heading: camera.heading,
    pitch: camera.pitch,
    range: Cartesian3.distance(camera.positionWC, centre),
  });
  camera.lookAtTransform(
    Transforms.eastNorthUpToFixedFrame(centre),
    new HeadingPitchRange(
      view.heading,
      CesiumMath.clamp(view.pitch, MIN_PITCH, MAX_PITCH),
      Math.max(view.range, MIN_RANGE),
    ),
  );
  // Back to the normal mouse control (lookAtTransform would keep the camera locked on the point).
  camera.lookAtTransform(Matrix4.IDENTITY);
}

export type NavAction = 'zoomIn' | 'zoomOut' | 'left' | 'right' | 'up' | 'down' | 'top' | 'north';

export function navigate(viewer: Viewer, action: NavAction, fallback: Cartesian3): void {
  orbit(viewer, fallback, (v) => {
    switch (action) {
      case 'zoomIn':
        return { ...v, range: v.range * ZOOM_FACTOR };
      case 'zoomOut':
        return { ...v, range: v.range / ZOOM_FACTOR };
      case 'left':
        return { ...v, heading: v.heading - ROTATE_STEP };
      case 'right':
        return { ...v, heading: v.heading + ROTATE_STEP };
      // Up: the camera rises towards a view from above.
      case 'up':
        return { ...v, pitch: v.pitch - TILT_STEP };
      case 'down':
        return { ...v, pitch: v.pitch + TILT_STEP };
      case 'top':
        return { ...v, pitch: MIN_PITCH };
      case 'north':
        return { ...v, heading: 0 };
    }
  });
}
