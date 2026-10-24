import { HttpClient, HttpContext, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  Anchor,
  ChangeView,
  DrawLayer,
  DrawTransaction,
  ElementCheck,
  FloorGeometry,
  Floorplan,
  GridFilter,
  GridPage,
  BoxImportResult,
  BoxImportRow,
  ImportRow,
  LayerField,
  LayerGridPage,
  LevelInfo,
  LinkedLayer,
  MapLayer,
  Me,
  ModelAsset,
  ModelUploadTicket,
  Node,
  NodeMembers,
  NodeState,
  NodeType,
  Panorama,
  PanoramaNeighbour,
  Place,
  Placement,
  PrivacyRegion,
  Site,
  SyncResult,
  UploadTicket,
} from './models';

const API = '/api/v1';

export interface GridRequest {
  siteId: number;
  typeId?: number | null;
  search?: string;
  filters: GridFilter[];
  sort?: string | null;
  descending?: boolean;
  first: number;
  rows: number;
}

/** Typed access to the backend REST API. Errors are reported by the error interceptor (unless `silent`). */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  // Auth
  me(context?: HttpContext): Observable<Me> {
    return this.http.get<Me>(`${API}/auth/me`, { context });
  }
  login(organization: string, username: string, password: string, context?: HttpContext): Observable<Me> {
    return this.http.post<Me>(`${API}/auth/login`, { organization, username, password }, { context });
  }
  logout(): Observable<void> {
    return this.http.post<void>(`${API}/auth/logout`, {});
  }
  changePassword(currentPassword: string, newPassword: string, context?: HttpContext): Observable<Me> {
    return this.http.post<Me>(`${API}/auth/password`, { currentPassword, newPassword }, { context });
  }

  // Sites
  sites(): Observable<Site[]> {
    return this.http.get<Site[]>(`${API}/sites`);
  }
  createSite(site: Partial<Site>): Observable<Site> {
    return this.http.post<Site>(`${API}/sites`, site);
  }
  updateSite(id: number, patch: Partial<Site>): Observable<Site> {
    return this.http.patch<Site>(`${API}/sites/${id}`, patch);
  }
  deleteSite(id: number): Observable<void> {
    return this.http.delete<void>(`${API}/sites/${id}`);
  }

  // Node types
  nodeTypes(): Observable<NodeType[]> {
    return this.http.get<NodeType[]>(`${API}/node-types`);
  }

  // Nodes
  tree(siteId: number, aspect: string): Observable<Node[]> {
    return this.http.get<Node[]>(`${API}/sites/${siteId}/nodes`, { params: { aspect } });
  }
  node(id: number): Observable<Node> {
    return this.http.get<Node>(`${API}/nodes/${id}`);
  }
  ancestors(id: number): Observable<Node[]> {
    return this.http.get<Node[]>(`${API}/nodes/${id}/ancestors`);
  }
  createNode(node: { siteId: number; parentId: number | null; typeId: number; name: string; code?: string | null; attrs?: object }): Observable<Node> {
    return this.http.post<Node>(`${API}/nodes`, node);
  }
  /** Partial update; `parentId: null` moves the node to the root. */
  updateNode(id: number, patch: Record<string, unknown>, context?: HttpContext): Observable<Node> {
    return this.http.patch<Node>(`${API}/nodes/${id}`, patch, { context });
  }
  /** Finalizes the node (subtree: with all below it); its state becomes the baseline valid from validFrom. */
  finalize(id: number, subtree: boolean, validFrom: string | null): Observable<{ finalized: number }> {
    return this.http.post<{ finalized: number }>(`${API}/nodes/${id}/finalize`, { subtree, validFrom });
  }
  history(id: number): Observable<ChangeView[]> {
    return this.http.get<ChangeView[]>(`${API}/nodes/${id}/history`);
  }
  /** The node as valid on a day (yyyy-MM-dd). */
  stateOn(id: number, asOf: string): Observable<NodeState> {
    return this.http.get<NodeState>(`${API}/nodes/${id}/state`, { params: { asOf } });
  }
  /** The node and its subtree with the place of each element (M8). */
  members(id: number): Observable<NodeMembers> {
    return this.http.get<NodeMembers>(`${API}/nodes/${id}/members`);
  }
  /** The place of every node of the functional tree (for the location tree). */
  placements(siteId: number): Observable<Placement[]> {
    return this.http.get<Placement[]>(`${API}/sites/${siteId}/placements`);
  }
  /** Explicit place (located_in); null clears it. */
  setLocation(id: number, locationId: number | null): Observable<void> {
    return this.http.put<void>(`${API}/nodes/${id}/location`, { locationId });
  }
  deleteNode(id: number, recursive: boolean): Observable<void> {
    return this.http.delete<void>(`${API}/nodes/${id}`, { params: { recursive } });
  }
  grid(request: GridRequest): Observable<GridPage> {
    let params = new HttpParams().set('siteId', request.siteId).set('first', request.first).set('rows', request.rows);
    if (request.typeId != null) params = params.set('typeId', request.typeId);
    if (request.search) params = params.set('search', request.search);
    if (request.filters.length > 0) params = params.set('filter', JSON.stringify(request.filters));
    if (request.sort) params = params.set('sort', request.sort).set('dir', request.descending ? 'desc' : 'asc');
    return this.http.get<GridPage>(`${API}/nodes`, { params });
  }
  importAttributes(siteId: number, typeId: number, parentId: number | null, rows: ImportRow[], context?: HttpContext): Observable<{ created: number; updated: number }> {
    return this.http.post<{ created: number; updated: number }>(`${API}/sites/${siteId}/import/attributes`, { typeId, parentId, rows }, { context });
  }  /** Bounding boxes from a table: a record's rows (by code) replace its boxes; centres in the given system. */
  importBoxes(siteId: number, srs: string, rows: BoxImportRow[], context?: HttpContext): Observable<BoxImportResult> {
    return this.http.post<BoxImportResult>(`${API}/sites/${siteId}/import/boxes`, { srs, rows }, { context });
  }


  // MapManager link
  mapManagerFields(layer: string, context?: HttpContext): Observable<LayerField[]> {
    return this.http.get<LayerField[]>(`${API}/maps/mapmanager/fields`, { params: { layer }, context });
  }
  linkLayer(siteId: number, request: { layer: string; typeId: number; nameField: string; parentId: number | null }, context?: HttpContext): Observable<{ created: number; alreadyLinked: number }> {
    return this.http.post<{ created: number; alreadyLinked: number }>(`${API}/sites/${siteId}/link-layer`, request, { context });
  }
  syncLinks(siteId: number, layer: string): Observable<SyncResult> {
    return this.http.post<SyncResult>(`${API}/sites/${siteId}/sync-links`, { layer });
  }
  linkedLayers(siteId: number): Observable<LinkedLayer[]> {
    return this.http.get<LinkedLayer[]>(`${API}/sites/${siteId}/linked-layers`);
  }
  layerGrid(siteId: number, layer: string, filters: GridFilter[], sort: string | null, descending: boolean, first: number, rows: number, context?: HttpContext): Observable<LayerGridPage> {
    let params = new HttpParams().set('layer', layer).set('first', first).set('rows', rows);
    if (filters.length > 0) params = params.set('filter', JSON.stringify(filters));
    if (sort) params = params.set('sort', sort).set('dir', descending ? 'desc' : 'asc');
    return this.http.get<LayerGridPage>(`${API}/sites/${siteId}/linked-layers/features`, { params, context });
  }
  linkedAttributes(nodeId: number, context?: HttpContext): Observable<Record<string, unknown> | null> {
    return this.http.get<Record<string, unknown> | null>(`${API}/nodes/${nodeId}/linked-attributes`, { context });
  }

  // Levels and floorplans
  levels(siteId: number): Observable<LevelInfo[]> {
    return this.http.get<LevelInfo[]>(`${API}/sites/${siteId}/levels`);
  }
  floorplans(levelId: number): Observable<Floorplan[]> {
    return this.http.get<Floorplan[]>(`${API}/levels/${levelId}/floorplans`);
  }
  createFloorplan(levelId: number, floorplan: Partial<Floorplan>): Observable<Floorplan> {
    return this.http.post<Floorplan>(`${API}/levels/${levelId}/floorplans`, floorplan);
  }
  deleteFloorplan(id: number): Observable<void> {
    return this.http.delete<void>(`${API}/floorplans/${id}`);
  }

  // Panoramas
  panoramas(siteId: number, levelId: number | null): Observable<Panorama[]> {
    return this.http.get<Panorama[]>(`${API}/sites/${siteId}/panoramas`, { params: levelId == null ? { outdoor: true } : { levelId } });
  }
  /** Every panorama of the site, on any level or outdoor (data tree). */
  sitePanoramas(siteId: number): Observable<Panorama[]> {
    return this.http.get<Panorama[]>(`${API}/sites/${siteId}/panoramas`);
  }
  panorama(id: number): Observable<Panorama> {
    return this.http.get<Panorama>(`${API}/panoramas/${id}`);
  }
  panoramaUpload(siteId: number, fileName: string, size: number, context?: HttpContext): Observable<UploadTicket> {
    return this.http.post<UploadTicket>(`${API}/uploads/panorama`, { siteId, fileName, size }, { context });
  }
  createPanorama(body: Record<string, unknown>, context?: HttpContext): Observable<Panorama> {
    return this.http.post<Panorama>(`${API}/panoramas`, body, { context });
  }
  updatePanorama(id: number, patch: Record<string, unknown>): Observable<Panorama> {
    return this.http.patch<Panorama>(`${API}/panoramas/${id}`, patch);
  }
  deletePanorama(id: number): Observable<void> {
    return this.http.delete<void>(`${API}/panoramas/${id}`);
  }
  setPrivacyRegions(id: number, regions: PrivacyRegion[]): Observable<Panorama> {
    return this.http.put<Panorama>(`${API}/panoramas/${id}/privacy-regions`, { regions });
  }
  approvePanorama(id: number): Observable<Panorama> {
    return this.http.post<Panorama>(`${API}/panoramas/${id}/approve`, {});
  }

  // Anchors (info points)
  levelAnchors(siteId: number, levelId: number | null): Observable<Anchor[]> {
    return this.http.get<Anchor[]>(`${API}/sites/${siteId}/anchors`, { params: levelId == null ? {} : { levelId } });
  }
  /** One place per node on a level (null: outdoor), or on the whole site. */
  places(siteId: number, levelId: number | null, allLevels = false): Observable<Place[]> {
    const params: Record<string, string | number | boolean> = allLevels ? { allLevels } : levelId == null ? {} : { levelId };
    return this.http.get<Place[]>(`${API}/sites/${siteId}/places`, { params });
  }
  /** The single place of a node (null: none yet). */
  place(nodeId: number): Observable<Place | null> {
    return this.http.get<Place | null>(`${API}/nodes/${nodeId}/place`);
  }
  panoramaAnchors(panoramaId: number): Observable<Anchor[]> {
    return this.http.get<Anchor[]>(`${API}/panoramas/${panoramaId}/anchors`);
  }
  nodeAnchors(nodeId: number): Observable<Anchor[]> {
    return this.http.get<Anchor[]>(`${API}/nodes/${nodeId}/anchors`);
  }
  createAnchor(nodeId: number, body: Record<string, unknown>): Observable<Anchor> {
    return this.http.post<Anchor>(`${API}/nodes/${nodeId}/anchors`, body);
  }
  updateAnchor(id: number, patch: Record<string, unknown>): Observable<Anchor> {
    return this.http.patch<Anchor>(`${API}/anchors/${id}`, patch);
  }
  deleteAnchor(id: number): Observable<void> {
    return this.http.delete<void>(`${API}/anchors/${id}`);
  }

  // 3D models
  models(siteId: number): Observable<ModelAsset[]> {
    return this.http.get<ModelAsset[]>(`${API}/sites/${siteId}/models`);
  }
  modelUpload(siteId: number, files: { path: string; size: number }[], context?: HttpContext): Observable<ModelUploadTicket> {
    return this.http.post<ModelUploadTicket>(`${API}/uploads/model`, { siteId, files }, { context });
  }
  createModel(siteId: number, body: Record<string, unknown>, context?: HttpContext): Observable<ModelAsset> {
    return this.http.post<ModelAsset>(`${API}/sites/${siteId}/models`, body, { context });
  }
  updateModel(id: number, patch: Record<string, unknown>): Observable<ModelAsset> {
    return this.http.patch<ModelAsset>(`${API}/models/${id}`, patch);
  }
  elementCheck(id: number): Observable<ElementCheck> {
    return this.http.post<ElementCheck>(`${API}/models/${id}/element-check`, {});
  }
  /** Vector geometry of a level (own drawing layers + MapManager floorplan): snap targets. */
  floorGeometry(siteId: number, levelId: number): Observable<FloorGeometry> {
    return this.http.get<FloorGeometry>(`${API}/sites/${siteId}/floors/${levelId}/geometry`);
  }
  /** Every anchor of a kind on the site (e.g. model elements for the 3D view). */
  siteAnchors(siteId: number, kind: Anchor['kind']): Observable<Anchor[]> {
    return this.http.get<Anchor[]>(`${API}/sites/${siteId}/anchors`, { params: { allLevels: true, kind } });
  }
  deleteModel(id: number): Observable<void> {
    return this.http.delete<void>(`${API}/models/${id}`);
  }

  // Walk-through neighbourhood
  neighbours(panoramaId: number): Observable<PanoramaNeighbour[]> {
    return this.http.get<PanoramaNeighbour[]>(`${API}/panoramas/${panoramaId}/links`);
  }
  link(fromId: number, toId: number): Observable<void> {
    return this.http.post<void>(`${API}/panoramas/${fromId}/links`, { toId });
  }
  unlink(fromId: number, toId: number): Observable<void> {
    return this.http.delete<void>(`${API}/panoramas/${fromId}/links/${toId}`);
  }
  autoLink(siteId: number, levelId: number | null, maxDistanceM: number): Observable<number> {
    return this.http.post<number>(`${API}/sites/${siteId}/panorama-links/auto`, { levelId, maxDistanceM });
  }

  // Drawing layers
  drawLayers(siteId: number, levelId: number | null): Observable<DrawLayer[]> {
    return this.http.get<DrawLayer[]>(`${API}/draw-layers`, { params: levelId == null ? { siteId, outdoor: true } : { siteId, levelId } });
  }
  createDrawLayer(layer: Partial<DrawLayer>): Observable<DrawLayer> {
    return this.http.post<DrawLayer>(`${API}/draw-layers`, layer);
  }
  deleteDrawLayer(id: number): Observable<void> {
    return this.http.delete<void>(`${API}/draw-layers/${id}`);
  }
  /** GeoJSON FeatureCollection in EPSG:3857 (each feature: id = record, version, nodeId). */
  drawFeatures(layerId: number): Observable<object> {
    return this.http.get<object>(`${API}/draw-layers/${layerId}/features`);
  }
  drawTransaction(layerId: number, tx: DrawTransaction, context?: HttpContext): Observable<{ inserted: number[]; versions: Record<string, number>; deleted: number }> {
    return this.http.post<{ inserted: number[]; versions: Record<string, number>; deleted: number }>(`${API}/draw-layers/${layerId}/transaction`, tx, { context });
  }

  // Map layers
  mapLayers(siteId: number | null): Observable<MapLayer[]> {
    return this.http.get<MapLayer[]>(`${API}/map-layers`, { params: siteId == null ? {} : { siteId } });
  }
  createMapLayer(layer: Partial<MapLayer>): Observable<MapLayer> {
    return this.http.post<MapLayer>(`${API}/map-layers`, layer);
  }
  deleteMapLayer(id: number): Observable<void> {
    return this.http.delete<void>(`${API}/map-layers/${id}`);
  }
}

/** URL of the WMS proxy for a background layer (OpenLayers adds the WMS parameters). */
export const wmsProxyUrl = (mapLayerId: number) => `${API}/maps/wms?mapLayerId=${mapLayerId}`;

/** URL of the WMS proxy for a floorplan of a level (the layer and its CQL filter are added by the backend). */
export const floorplanWmsUrl = (floorplanId: number) => `${API}/maps/wms?floorplanId=${floorplanId}`;
