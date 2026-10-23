-- Maintains node.path (ltree of ids) from parent_id (docs/SPECIFICATION.md 8.3).
-- Applied by the EF Core migration "Schema"; changes need a new migration that re-runs this script.
-- Errors carry their own constraint names so the API can tell them apart:
--   node_parent_exists (parent not found), node_no_cycle (move under own subtree).

CREATE OR REPLACE FUNCTION node_set_path() RETURNS trigger AS $$
DECLARE
    parent_path ltree;
BEGIN
    IF NEW.parent_id IS NULL THEN
        NEW.path := NEW.id::text::ltree;
    ELSE
        SELECT path INTO parent_path FROM node WHERE id = NEW.parent_id;
        IF parent_path IS NULL THEN
            RAISE EXCEPTION 'Parent node % not found', NEW.parent_id
                USING ERRCODE = 'foreign_key_violation', CONSTRAINT = 'node_parent_exists';
        END IF;
        IF TG_OP = 'UPDATE' AND parent_path <@ OLD.path THEN
            RAISE EXCEPTION 'Cycle: node % cannot be moved under its own subtree', NEW.id
                USING ERRCODE = 'check_violation', CONSTRAINT = 'node_no_cycle';
        END IF;
        NEW.path := parent_path || NEW.id::text;
    END IF;
    RETURN NEW;
END $$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS node_path_insert ON node;
CREATE TRIGGER node_path_insert
    BEFORE INSERT ON node
    FOR EACH ROW EXECUTE FUNCTION node_set_path();

-- Only runs when the parent changes (updating the descendants' path does not fire it again).
DROP TRIGGER IF EXISTS node_path_move ON node;
CREATE TRIGGER node_path_move
    BEFORE UPDATE OF parent_id ON node
    FOR EACH ROW EXECUTE FUNCTION node_set_path();

CREATE OR REPLACE FUNCTION node_move_subtree() RETURNS trigger AS $$
BEGIN
    IF NEW.path IS DISTINCT FROM OLD.path THEN
        UPDATE node
           SET path = NEW.path || subpath(path, nlevel(OLD.path))
         WHERE path <@ OLD.path AND id <> NEW.id;
    END IF;
    RETURN NULL;
END $$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS node_path_cascade ON node;
CREATE TRIGGER node_path_cascade
    AFTER UPDATE OF parent_id ON node
    FOR EACH ROW EXECUTE FUNCTION node_move_subtree();
