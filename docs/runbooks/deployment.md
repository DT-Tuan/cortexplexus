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
