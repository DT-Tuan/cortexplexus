-- CortexPlexus.Memory schema — agent_memories table.
-- Introduced in v0.8.0. Idempotent; safe to re-run on every startup.
-- See docs/MEMORY-SYSTEM.md, ADR-010, ADR-011.

CREATE TABLE IF NOT EXISTS agent_memories (
    id              UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
    content         TEXT        NOT NULL,
    scope           TEXT        NOT NULL,
    scope_id        TEXT        NULL,
    topic           TEXT        NULL,
    importance      REAL        NOT NULL DEFAULT 0.5,
    related_fqns    TEXT[]      NOT NULL DEFAULT ARRAY[]::TEXT[],
    embedding       vector(768) NULL,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_accessed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    access_count    INT         NOT NULL DEFAULT 0,
    CONSTRAINT agent_memories_scope_check
        CHECK (scope IN ('session', 'project', 'global')),
    CONSTRAINT agent_memories_scope_id_required
        CHECK (scope = 'global' OR scope_id IS NOT NULL),
    CONSTRAINT agent_memories_importance_range
        CHECK (importance >= 0.0 AND importance <= 1.0),
    CONSTRAINT agent_memories_content_nonempty
        CHECK (char_length(content) BETWEEN 1 AND 4000)
);

-- Fast filter by scope + scope_id (the hot path for list/recall).
CREATE INDEX IF NOT EXISTS idx_agent_memories_scope
    ON agent_memories (scope, scope_id);

-- Partial index for topic filters (topic is often NULL).
CREATE INDEX IF NOT EXISTS idx_agent_memories_topic
    ON agent_memories (topic)
    WHERE topic IS NOT NULL;

-- GIN index for related_fqns array membership queries (ANY and @> operators).
CREATE INDEX IF NOT EXISTS idx_agent_memories_related_fqns
    ON agent_memories USING GIN (related_fqns);

-- HNSW index for semantic recall. Same parameters as code_symbols embedding:
-- cosine distance, default m=16, ef_construction=64. Wave 2 queries will set
-- per-session ef_search=100 for ~99% recall.
CREATE INDEX IF NOT EXISTS idx_agent_memories_embedding_hnsw
    ON agent_memories USING hnsw (embedding vector_cosine_ops);

-- Index by last_accessed_at for the reaper scan (Wave 2).
CREATE INDEX IF NOT EXISTS idx_agent_memories_last_accessed
    ON agent_memories (last_accessed_at);

-- Embedding-space stamp (ADR-018): per-row provenance — memories are written one at a time
-- across provider eras, so only per-row stamps are truthful. NULL = no embedding or legacy.
ALTER TABLE agent_memories
    ADD COLUMN IF NOT EXISTS embedding_provider TEXT,
    ADD COLUMN IF NOT EXISTS embedding_model    TEXT,
    ADD COLUMN IF NOT EXISTS embedding_dim      INT;
