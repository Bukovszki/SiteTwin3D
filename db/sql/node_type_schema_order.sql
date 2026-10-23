-- attr_schema becomes json (not jsonb): jsonb reorders object keys, so the form and grid columns would not follow the
-- order of the schema's properties. Re-sets the shared base types' schemas in their intended order.
UPDATE node_type SET attr_schema = '{
  "type": "object",
  "properties": {
    "elevation_m": { "type": "number", "title": "Padlómagasság az alapmagasságtól (m)" },
    "height_m":    { "type": "number", "title": "Belmagasság (m)", "exclusiveMinimum": 0 }
  },
  "required": ["elevation_m"]
}'::json WHERE organization_id IS NULL AND code = 'level';

UPDATE node_type SET attr_schema = '{
  "type": "object",
  "properties": {
    "manufacturer": { "type": "string", "title": "Gyártó" },
    "model":        { "type": "string", "title": "Típus" },
    "power_kw":     { "type": "number", "title": "Teljesítmény (kW)", "minimum": 0 },
    "install_year": { "type": "integer", "title": "Telepítés éve", "minimum": 1900, "maximum": 2100 }
  }
}'::json WHERE organization_id IS NULL AND code = 'equipment';
