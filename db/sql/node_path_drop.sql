-- Down part of node_path.sql.
DROP TRIGGER IF EXISTS node_path_cascade ON node;
DROP TRIGGER IF EXISTS node_path_move ON node;
DROP TRIGGER IF EXISTS node_path_insert ON node;
DROP FUNCTION IF EXISTS node_move_subtree();
DROP FUNCTION IF EXISTS node_set_path();
