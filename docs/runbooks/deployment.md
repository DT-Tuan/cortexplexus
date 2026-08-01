# Runbook: Deployment (Docker Compose)

## Prerequisites
- Docker Desktop hoặc Docker Engine + Docker Compose
- Google Gemini API Key (free)

## Steps

### 1. Clone & configure
```bash
git clone https://github.com/user/cortexplexus.git
cd cortexplexus
cp .env.example .env
```

### 2. Edit `.env`
```env
GEMINI_API_KEY=your_gemini_api_key_here
WORKSPACE_PATH=/path/to/your/code/workspace
```

### 3. Deploy
```bash
docker compose up -d
```

### 4. Index your project
```bash
docker exec cortexplexus-cortexplexus-1 cortexplexus index /workspace
```

### 5. Verify
```bash
docker exec cortexplexus-cortexplexus-1 cortexplexus status
```

### 6. Connect IDE
```bash
# Claude Code
claude mcp add cortexplexus --transport http http://localhost:8080/mcp

# Cursor (settings.json)
# Add to mcpServers: { "cortexplexus": { "url": "http://localhost:8080/mcp" } }
```

## Stopping
```bash
docker compose down        # Stop (keep data)
docker compose down -v     # Stop + delete data (full reset)
```

## Vertex AI embedding with a service account (ADR-029)

Skip this unless `EMBEDDING_PROVIDER=vertex` **and** you cannot mint an express-mode key in
the Vertex AI Studio console. The project-scoped Vertex endpoint rejects API keys on the query
string (`401 UNAUTHENTICATED — "API keys are not supported by this API"`), so a downloaded
service-account key file has exactly one route in: an `Authorization` header.

1. **Put the key on the host, readable by the container's uid — not by the world.** The image
   runs as a non-root uid, so a `0600` file owned by your host user is *unreadable* inside the
   container. Check the uid rather than assuming it (another container on the same host may run
   as root and read the same file fine):

   ```bash
   docker image inspect ghcr.io/<owner>/cortexplexus:main --format '{{.Config.User}}'   # e.g. 1654
   mkdir -p ~/.config/gcp && install -m 0640 /path/to/sa.json ~/.config/gcp/vertex-sa.json
   sudo chown "$USER:1654" ~/.config/gcp/vertex-sa.json      # group-read for the container uid
   ```

2. **Mount it read-only** in `docker-compose.override.yml` (deployment-specific, so it does not
   belong in the committed compose file):

   ```yaml
   services:
     cortexplexus:
       volumes:
         - ~/.config/gcp/vertex-sa.json:/etc/gcp/sa.json:ro
   ```

   Never bake the key into an image — layers are immutable, so a credential added in one layer
   stays recoverable from image history even if a later layer deletes it, and rotating it would
   mean a rebuild.

3. **Point the app at the container path** in `.env`, and clear any express key so the auth
   carrier is unambiguous:

   ```env
   EMBEDDING_PROVIDER=vertex
   VERTEX_PROJECT_ID=your-gcp-project-id
   VERTEX_CREDENTIAL_PATH=/etc/gcp/sa.json
   VERTEX_API_KEY=
   ```

4. **Verify** after `docker compose up -d`. A successful call logs the `:predict` URL with **no**
   `?key=` on it, and embeddings persist:

   ```bash
   docker compose logs cortexplexus | grep -c ':predict'          # >0 once indexing runs
   docker compose logs cortexplexus | grep -i 'unauthenticated\|denied'   # expect no hits
   ```

> **Switching an existing Vertex deployment between identities needs no re-index**, as long as
> the model id does not change: the embedding-space stamp is `(provider, model, dimensions)`,
> and the same model returns bitwise-identical vectors across GCP projects. Changing
> `VERTEX_MODEL_ID` is a different matter entirely — that changes the vector space and every
> stored embedding becomes incomparable.

## Updating

The published images (`ghcr.io/<owner>/cortexplexus:main` and `…-postgres:main`) are built
and pushed by GitHub Actions (`.github/workflows/docker-publish.yml`) on every push to `main`
and on `v*.*.*` tags. The compose file references those prebuilt images (`image:`), so updating
a running deployment is a **pull + recreate** — there is no local build step:

```bash
docker compose pull
docker compose up -d
docker image prune -f   # optional: reclaim old image layers
```

> `docker compose build` does **nothing** here — the services use prebuilt GHCR images, not a
> local `build:` context. (An older `deploy.sh` that built a `cortexplexus-app:slim` tag and
> `docker load`ed it would silently no-op, because the compose file no longer references that
> tag — the container just restarts the old image.)

## Logs
```bash
docker compose logs cortexplexus -f    # App logs
docker compose logs postgres -f        # Database logs
```

## Troubleshooting

| Lỗi | Fix |
|-----|-----|
| Port 5432 already in use | Stop local PostgreSQL: `docker stop` hoặc change port in docker-compose.yml |
| Port 8080 already in use | Change `ports` in docker-compose.yml |
| Workspace not found | Check `WORKSPACE_PATH` in .env — must be absolute path |
| Permission denied on workspace | Đảm bảo Docker có quyền đọc thư mục |
| `postgres failed to start` / `No space left on device` | See [`maintenance.md`](maintenance.md) — disk cleanup |
| Stack has been running for weeks, disk fills up | Weekly prune cron — see [`maintenance.md`](maintenance.md) §2 |
| Agent dies after reboot / logout — have to re-activate | Wire auto-start — see [`agent-auto-start.md`](agent-auto-start.md) (systemd / Task Scheduler / LaunchAgent) |
