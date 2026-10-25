-- Where the shared base types may be (user decision): a building or an outdoor area at the top of the location tree,
-- a level under a building, a room under a level; a process line at the top or under another line; equipment anywhere.
-- "" = the top of the tree; NULL = anywhere in its tree.
UPDATE node_type SET allowed_parents = '[""]' WHERE organization_id IS NULL AND code IN ('building', 'outdoor_area');
UPDATE node_type SET allowed_parents = '["building"]' WHERE organization_id IS NULL AND code = 'level';
UPDATE node_type SET allowed_parents = '["level"]' WHERE organization_id IS NULL AND code = 'room';
UPDATE node_type SET allowed_parents = '["", "process_line"]' WHERE organization_id IS NULL AND code = 'process_line';
