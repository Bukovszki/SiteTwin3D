/** API data shapes (backend: /api/v1, camelCase JSON; geometries as GeoJSON in EPSG:4326). */

export type Role = 'PlatformAdmin' | 'OrgAdmin' | 'Editor' | 'Viewer';

export interface Me {
  id: number;
  username: string;
  displayName: string;
  role: Role;
  organizationId: number | null;
  organizationCode: string | null;
  organizationName: string | null;
  mustChangePassword: boolean;
  /** Sites the user may see / edit; null: every site of the organization (spec 3.2). */
  readableSiteIds?: number[] | null;
  editableSiteIds?: number[] | null;
}

export interface SiteRole {
  siteId: number;
  role: 'Editor' | 'Viewer';
}

export interface User {
  id: number;
  username: string;
  displayName: string;
  role: Role;
  active: boolean;
  mustChangePassword: boolean;
  lastLogin: string | null;
  siteRoles: SiteRole[];
}

export interface Organization {
  id: number;
  code: string;
  name: string;
  active: boolean;
  createdAt: string;
  userCount: number;
  mapmanagerUrl: string | null;
  mapmanagerTokenRef: string | null;
}

export interface GeoJsonPoint {
  type: 'Point';
  coordinates: [number, number];
}

export interface GeoJsonPolygon {
  type: 'Polygon';
  coordinates: [number, number][][];
}

export interface GeoJsonGeometry {
  type: string;
  coordinates: unknown;
}

export interface Site {
  id: number;
  name: string;
  refPoint: GeoJsonPoint;
  baseHeightM: number;
  extent: GeoJsonPolygon | null;
  attrs: Record<string, unknown>;
}

/** JSON Schema subset used for technical data (FR-H5). */
export interface JsonSchemaProperty {
  type?: 'string' | 'number' | 'integer' | 'boolean';
  title?: string;
  enum?: (string | number)[];
  minimum?: number;
  maximum?: number;
  exclusiveMinimum?: number;
}

export interface AttrSchema {
  type?: 'object';
  properties?: Record<string, JsonSchemaProperty>;
  required?: string[];
}

export interface NodeType {
  id: number;
  code: string;
  name: string;
  aspect: 'location' | 'function';
  isLevel: boolean;
  shared: boolean;
  attrSchema: AttrSchema | null;
}

export interface Node {
  id: number;
  siteId: number;
  parentId: number | null;
  path: string;
  depth: number;
  typeId: number;
  typeCode: string;
  aspect: 'location' | 'function';
  code: string | null;
  name: string;
  sortOrder: number;
  geom: GeoJsonGeometry | null;
  attrs: Record<string, unknown> & { mapmanager_ref?: { layer: string; featureId: string } };
  /** Finalized (null: draft): changes then need a reason and are kept in the history. */
  finalizedAt: string | null;
}

/** A recorded change of a finalized node (or of one of its anchors). */
export interface ChangeView {
  id: number;
  entity: 'node' | 'anchor';
  entityId: number;
  action: 'finalize' | 'create' | 'update' | 'delete';
  /** yyyy-MM-dd: since when it is so in reality. */
  validFrom: string;
  recordedAt: string;
  recordedByName: string | null;
  reason: string | null;
  fields: string[];
  before: Record<string, unknown> | null;
  after: Record<string, unknown> | null;
}

/** The node as valid on a day (rebuilt from the history); tracked false: before its finalization. */
export interface NodeState {
  asOf: string;
  tracked: boolean;
  node: { name: string; code: string | null; attrs: Record<string, unknown> } | null;
  anchors: (Record<string, unknown> & { id: number; kind: string; position: [number, number]; levelId: number | null; zRelM: number })[];
}

export interface GridRow {
  node: Node;
  anchorKinds: string[];
  linked: boolean;
}

export interface GridPage {
  total: number;
  rows: GridRow[];
}

export type FilterOp = 'contains' | 'eq' | 'gte' | 'lte' | 'empty';

export interface GridFilter {
  field: string;
  op: FilterOp;
  value?: string | number | boolean;
}

export interface LayerField {
  name: string;
  kind: 'text' | 'number' | 'boolean' | 'date' | 'geometry';
}

export interface LayerGridRow {
  featureId: string;
  nodeId: number | null;
  nodeName: string | null;
  properties: Record<string, unknown>;
}

export interface LayerGridPage {
  total: number | null;
  fields: LayerField[];
  rows: LayerGridRow[];
}

export interface LinkedLayer {
  layer: string;
  count: number;
}

export interface SyncResult {
  features: number;
  linked: number;
  unlinked: number;
  orphans: { nodeId: number; name: string; featureId: string }[];
}

export interface ImportRow {
  code?: string;
  name?: string;
  attrs: Record<string, unknown>;
}

export interface ImportError {
  row: number;
  code: string;
  field: string | null;
  message: string;
}

export interface MapLayer {
  id: number;
  siteId: number | null;
  source: 'mapmanager' | 'external_wms';
  name: string;
  layer: string;
  wmsParams: Record<string, unknown>;
  externalUrl: string | null;
  sortOrder: number;
  visibleDefault: boolean;
}

export interface LevelInfo {
  id: number;
  name: string;
  code: string | null;
  parentId: number | null;
  elevationM: number;
  heightM: number | null;
  floorplanCount: number;
  panoramaCount: number;
}

export interface Floorplan {
  id: number;
  levelId: number;
  source: 'mapmanager' | 'external_wms';
  name: string;
  layer: string;
  cqlFilter: string | null;
  wmsParams: Record<string, unknown>;
  externalUrl: string | null;
  sortOrder: number;
  opacity: number;
}

/** A region to blur, in image angles (yaw 0 = image centre, + right; pitch + up), degrees. */
export interface PrivacyRegion {
  id: string;
  yaw: number;
  pitch: number;
  width: number;
  height: number;
  source: 'auto' | 'manual';
  accepted: boolean;
  score?: number | null;
}

export type PrivacyStatus = 'pending' | 'review' | 'rendering' | 'approved' | 'failed';

export interface Panorama {
  id: number;
  nodeId: number;
  position: GeoJsonPoint;
  levelId: number | null;
  zRelM: number;
  /** Direction of the image centre from geographic north, clockwise; null: not calibrated. */
  headingDeg: number | null;
  capturedAt: string | null;
  privacyStatus: PrivacyStatus;
  processingError: string | null;
  privacyRegions: PrivacyRegion[];
  width: number | null;
  height: number | null;
  siteId?: number | null;
  absoluteHeightM?: number | null;
  imageUrl?: string | null;
  imageIsOriginal?: boolean;
}

export interface UploadTicket {
  key: string;
  url: string;
  contentType: string;
  expiresAt: string;
}

export interface Anchor {
  id: number;
  nodeId: number;
  nodeName: string;
  nodeCode: string | null;
  typeCode: string;
  kind: 'point' | 'panorama_point' | 'region' | 'model_feature';
  panoramaId: number | null;
  /** Image angles of an info point (yaw 0 = image centre). */
  yawDeg: number | null;
  pitchDeg: number | null;
  position: GeoJsonPoint;
  levelId: number | null;
  zRelM: number;
  positionSource: 'manual' | 'panorama_fallback' | null;
  /** Box (kind region): centre = position + zRelM above the level floor; sizes in metres; heading clockwise from north. */
  regionShape: 'box' | 'prism' | null;
  sizeX: number | null;
  sizeY: number | null;
  sizeZ: number | null;
  headingDeg: number | null;
  /** Model element (kind model_feature): the model and the element's STABLE id (never a per-tile feature index). */
  modelAssetId: number | null;
  featureRef: string | null;
}

/** Placement of a GLB without georeference: origin at the point, heightM above the site base, forward (glTF −Z) to headingDeg. */
export interface ModelPlacement {
  lon: number;
  lat: number;
  heightM: number;
  headingDeg: number;
}

export interface ModelAsset {
  id: number;
  siteId: number;
  nodeId: number | null;
  name: string;
  kind: 'gltf' | '3dtiles';
  segmented: boolean;
  placement: ModelPlacement | null;
  elementIdProperty: string | null;
  /** tileset.json through the API: its tile contents are short-lived signed object store URLs. */
  tilesetUrl: string;
}

/** Where a node is: explicit located_in relation, or derived from the smallest location box containing its anchor. */
export interface LocationRef {
  nodeId: number;
  name: string;
  /** Breadcrumb of the place (building › level › room). */
  path: string[];
  source: 'relation' | 'box';
}

export interface MemberLocation {
  nodeId: number;
  name: string;
  code: string | null;
  typeCode: string;
  parentId: number | null;
  depth: number;
  location: LocationRef | null;
  levelId: number | null;
  levelName: string | null;
}

/** A node and its whole subtree (e.g. the elements of a process line) with their places. */
export interface NodeMembers {
  node: MemberLocation;
  members: MemberLocation[];
}

/** Import check of the element ids of a segmented model. */
export interface ElementCheck {
  property: string;
  contentsChecked: number;
  contentsWithoutProperty: number;
  elements: number;
  distinctIds: number;
  missing: number;
  duplicates: string[];
  truncated: boolean;
}

/** Vector geometry of a level (WGS84): snap targets. partial: MapManager was not reachable. */
export interface FloorGeometry {
  type: 'FeatureCollection';
  features: { type: 'Feature'; geometry: GeoJsonGeometry; properties: { source: 'draw' | 'floorplan'; layer: string } }[];
  partial: boolean;
}

export interface ModelUploadTicket {
  prefix: string;
  files: { path: string; url: string; contentType: string }[];
  expiresAt: string;
}

export interface PanoramaNeighbour {
  id: number;
  position: GeoJsonPoint;
  levelId: number | null;
  distanceM: number;
  bearingDeg: number;
  privacyStatus: PrivacyStatus;
}

export interface DrawField {
  name: string;
  type: 'text' | 'integer' | 'number' | 'boolean' | 'date';
  length?: number | null;
  decimals?: number | null;
  alias?: string | null;
}

export interface DrawLayer {
  id: number;
  siteId: number;
  levelId: number | null;
  name: string;
  geometryType: 'Point' | 'MultiLineString' | 'MultiPolygon';
  fields: DrawField[];
  style: { color?: string };
  sortOrder: number;
}

export interface DrawTransaction {
  inserts: { ref?: string; geometry: GeoJsonGeometry; attrs: Record<string, unknown>; nodeId?: number | null }[];
  updates: { id: number; version: number; geometry?: GeoJsonGeometry; attrs?: Record<string, unknown> }[];
  deletes: { id: number; version: number }[];
}

export interface DrawError {
  item: 'insert' | 'update' | 'delete';
  index: number;
  code: string;
  field: string | null;
  message: string;
}
