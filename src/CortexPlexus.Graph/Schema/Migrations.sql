-- CortexPlexus Database Schema
-- Applied on first startup via CortexPlexus.App init command

-- Extensions
CREATE EXTENSION IF NOT EXISTS age;
CREATE EXTENSION IF NOT EXISTS vector;

-- Create relational tables FIRST (in public schema, before AGE search_path change)

-- Repository registry
CREATE TABLE IF NOT EXISTS public.repositories (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name        TEXT NOT NULL,
    path        TEXT NOT NULL UNIQUE,
    created_at  TIMESTAMPTZ DEFAULT NOW(),
    last_indexed TIMESTAMPTZ
);

-- Code symbols (companion table for vector + FTS)
CREATE TABLE IF NOT EXISTS public.code_symbols (
    id          BIGSERIAL PRIMARY KEY,
    fqn         TEXT UNIQUE NOT NULL,
    name        TEXT NOT NULL,
    kind        TEXT NOT NULL,
    signature   TEXT,
    file_path   TEXT,
    start_line  INT,
    end_line    INT,
    repo_id     UUID NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    indexed_at  TIMESTAMPTZ DEFAULT NOW(),
    embedding   vector(768),
    search_text tsvector GENERATED ALWAYS AS (
        setweight(to_tsvector('english', coalesce(name, '')), 'A') ||
        setweight(to_tsvector('english', coalesce(fqn, '')), 'B') ||
        setweight(to_tsvector('english', coalesce(signature, '')), 'C')
    ) STORED
);

-- Vector search index (HNSW for approximate nearest neighbor)
CREATE INDEX IF NOT EXISTS idx_symbols_embedding
    ON public.code_symbols USING hnsw (embedding vector_cosine_ops);

-- Full-text search index
CREATE INDEX IF NOT EXISTS idx_symbols_fts
    ON public.code_symbols USING gin (search_text);

-- Property indexes
CREATE INDEX IF NOT EXISTS idx_symbols_fqn ON code_symbols (fqn);
CREATE INDEX IF NOT EXISTS idx_symbols_repo ON code_symbols (repo_id);
CREATE INDEX IF NOT EXISTS idx_symbols_kind ON code_symbols (kind);
CREATE INDEX IF NOT EXISTS idx_symbols_file ON code_symbols (file_path);

-- Content hash table for incremental indexing
CREATE TABLE IF NOT EXISTS public.file_hashes (
    file_path    TEXT PRIMARY KEY,
    repo_id      UUID NOT NULL REFERENCES repositories(id) ON DELETE CASCADE,
    content_hash TEXT NOT NULL,
    indexed_at   TIMESTAMPTZ DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_file_hashes_repo ON public.file_hashes (repo_id);

-- Watch-agent liveness (zombie-watch fix): one row per repo fed by `cortexplexus-agent watch`.
-- last_heartbeat = agent process alive; last_sync = last confirmed successful upload / no-change
-- check. Fresh heartbeat + old last_sync + consecutive_failures > 0 = watch is running but
-- failing — list_repositories renders this as Watch: FAILING instead of silently serving stale data.
CREATE TABLE IF NOT EXISTS public.agent_watch_status (
    repo_id              UUID PRIMARY KEY REFERENCES repositories(id) ON DELETE CASCADE,
    agent_version        TEXT NOT NULL DEFAULT 'unknown',
    last_heartbeat       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_sync            TIMESTAMPTZ,
    consecutive_failures INT NOT NULL DEFAULT 0,
    last_error           TEXT
);

-- Embedding-space stamp (ADR-018): which (provider, model, dim) produced this repo's vectors.
-- NULL = legacy/unknown (pre-migration); vector reads still run but tools surface an "unknown" hint.
-- Stamped on full re-index only; incremental sync is refused on mismatch to prevent mixed spaces.
ALTER TABLE public.repositories
    ADD COLUMN IF NOT EXISTS embedding_provider TEXT,
    ADD COLUMN IF NOT EXISTS embedding_model    TEXT,
    ADD COLUMN IF NOT EXISTS embedding_dim      INT;

-- Documentation + Summary columns (P0a/P0b)
ALTER TABLE public.code_symbols ADD COLUMN IF NOT EXISTS documentation TEXT;
ALTER TABLE public.code_symbols ADD COLUMN IF NOT EXISTS summary TEXT;

-- Test method flag (P1a: Test-to-Code mapping)
ALTER TABLE public.code_symbols ADD COLUMN IF NOT EXISTS is_test_method BOOLEAN DEFAULT FALSE;

-- Accessibility (Round 15: required by QueryDeadCodeAsync to filter public/internal methods)
ALTER TABLE public.code_symbols ADD COLUMN IF NOT EXISTS accessibility TEXT;
CREATE INDEX IF NOT EXISTS idx_symbols_accessibility ON public.code_symbols (accessibility);

-- Recreate search_text to include documentation (weight D) — only if not already updated
DO $$
BEGIN
    -- Check if documentation is already part of the generated column
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'code_symbols' AND column_name = 'search_text'
        AND generation_expression LIKE '%documentation%'
    ) THEN
        ALTER TABLE public.code_symbols DROP COLUMN IF EXISTS search_text;
        ALTER TABLE public.code_symbols ADD COLUMN search_text tsvector GENERATED ALWAYS AS (
            setweight(to_tsvector('english', coalesce(name, '')), 'A') ||
            setweight(to_tsvector('english', coalesce(fqn, '')), 'B') ||
            setweight(to_tsvector('english', coalesce(signature, '')), 'C') ||
            setweight(to_tsvector('english', coalesce(documentation, '')), 'D')
        ) STORED;
        -- Recreate FTS index
        DROP INDEX IF EXISTS idx_symbols_fts;
        CREATE INDEX idx_symbols_fts ON public.code_symbols USING gin (search_text);
    END IF;
END $$;

-- NOW setup AGE graph (after relational tables are in public schema)
LOAD 'age';
SET search_path = ag_catalog, "$user", public;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM ag_catalog.ag_graph WHERE name = 'code_graph') THEN
        PERFORM create_graph('code_graph');
    END IF;
END $$;

-- Pre-create known vertex labels so we can add B-tree indexes on their `fqn` property.
-- Without these indexes, MERGE (n {fqn:X}) does a full table scan per lookup — a
-- catastrophic bottleneck for edge upserts on large projects (100K+ relationships).
-- With the indexes, edge MERGEs become O(log n) and a 18K-symbol / 122K-edge project
-- like CortexFlow indexes in ~30s instead of timing out at 5min.
--
-- IMPORTANT: ag_catalog.create_vlabel expects cstring arguments (NOT text/name).
-- agtype type must be fully qualified as ag_catalog.agtype inside DO blocks.
DO $$
DECLARE
    vertex_labels TEXT[] := ARRAY[
        'class', 'method', 'interface', 'struct', 'record', 'enum',
        'property', 'constructor', 'event', 'field', 'trait', 'type',
        'function', 'namespace', 'dbcontext', 'di_registration',
        'api_endpoint', 'middleware', 'document', 'section', 'config_key',
        'Unknown'
    ];
    lbl TEXT;
BEGIN
    FOREACH lbl IN ARRAY vertex_labels
    LOOP
        BEGIN
            EXECUTE format(
                'SELECT ag_catalog.create_vlabel(%L::cstring, %L::cstring)',
                'code_graph', lbl);
        EXCEPTION
            WHEN OTHERS THEN NULL; -- label already exists
        END;
    END LOOP;
END $$;

-- GIN index on properties column for each vertex label table.
-- AGE's MERGE/MATCH compiles to `properties @> '{"fqn":X}'::agtype` (containment operator),
-- which requires a GIN index — NOT btree on the -> access operator. PostgreSQL inheritance
-- means child-table GIN indexes also serve unlabeled queries on the parent (_ag_label_vertex).
--
-- Before GIN indexes: edge upsert for CortexPlexus (2317 nodes + 8032 edges) = 508s (graph=507s)
-- After GIN indexes:  expected <30s (100x speedup based on local EXPLAIN: 7.4ms → 0.79ms per lookup)
DO $$
DECLARE
    vertex_labels TEXT[] := ARRAY[
        'class', 'method', 'interface', 'struct', 'record', 'enum',
        'property', 'constructor', 'event', 'field', 'trait', 'type',
        'function', 'namespace', 'dbcontext', 'di_registration',
        'api_endpoint', 'middleware', 'document', 'section', 'config_key',
        'Unknown'
    ];
    lbl TEXT;
BEGIN
    FOREACH lbl IN ARRAY vertex_labels
    LOOP
        BEGIN
            EXECUTE format(
                'CREATE INDEX IF NOT EXISTS %I ON code_graph.%I USING gin (properties)',
                'idx_graph_' || lbl || '_props',
                lbl
            );
        EXCEPTION
            WHEN OTHERS THEN NULL; -- label table may not exist yet
        END;
    END LOOP;
END $$;

-- Drop old useless btree indexes (previous migration attempt — they're on the wrong operator)
DO $$
DECLARE
    old_labels TEXT[] := ARRAY[
        'class', 'method', 'interface', 'struct', 'record', 'enum',
        'property', 'constructor', 'event', 'field', 'trait', 'type',
        'function', 'namespace', 'dbcontext', 'di_registration',
        'api_endpoint', 'middleware', 'document', 'section', 'config_key',
        'Unknown'
    ];
    lbl TEXT;
BEGIN
    FOREACH lbl IN ARRAY old_labels
    LOOP
        EXECUTE format('DROP INDEX IF EXISTS code_graph.%I', 'idx_graph_' || lbl || '_fqn');
    END LOOP;
END $$;

-- Reset search path back to include public
SET search_path = public, ag_catalog, "$user";
