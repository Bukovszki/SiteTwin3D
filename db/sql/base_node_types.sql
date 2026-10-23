-- Shared base node types (organization_id NULL: visible to every organization; FR-H2).
-- Organizations add their own types through the API. attr_schema: JSON Schema of the technical data (FR-H5).
INSERT INTO node_type (organization_id, code, name, aspect, is_level, attr_schema) VALUES
(NULL, 'building', 'Épület', 'location', false, NULL),
(NULL, 'level', 'Szint', 'location', true, '{
  "type": "object",
  "properties": {
    "elevation_m": { "type": "number", "title": "Padlómagasság az alapmagasságtól (m)" },
    "height_m":    { "type": "number", "title": "Belmagasság (m)", "exclusiveMinimum": 0 }
  },
  "required": ["elevation_m"]
}'),
(NULL, 'room', 'Helyiség', 'location', false, NULL),
(NULL, 'outdoor_area', 'Kültéri terület', 'location', false, NULL),
(NULL, 'process_line', 'Technológiai vonal', 'function', false, NULL),
(NULL, 'equipment', 'Berendezés', 'function', false, '{
  "type": "object",
  "properties": {
    "manufacturer": { "type": "string", "title": "Gyártó" },
    "model":        { "type": "string", "title": "Típus" },
    "power_kw":     { "type": "number", "title": "Teljesítmény (kW)", "minimum": 0 },
    "install_year": { "type": "integer", "title": "Telepítés éve", "minimum": 1900, "maximum": 2100 }
  }
}');
