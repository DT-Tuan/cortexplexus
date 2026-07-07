# CortexPlexus — Biến mã nguồn thành Knowledge Graph cho AI

**Nền tảng Code Intelligence mã nguồn mở, 100% self-hosted, miễn phí. Hoạt động với C#, TypeScript, JavaScript, Python, Java, Go, Rust, PHP.**

AI coding assistants (Claude, Cursor, Copilot) đọc codebase như văn bản thuần túy — chúng **không hiểu cấu trúc** của code. Kết quả: agent phải `grep` rồi `read` hàng chục file để suy ra quan hệ class/method, tốn token, trả lời sai, miss context.

**CortexPlexus sửa điều đó — cho mọi ngôn ngữ phổ biến.** Parse code bằng Tree-sitter (TypeScript, JavaScript, Python, Java, Go, Rust, PHP) cộng tầng semantic sâu cho C# qua Roslyn, dựng Knowledge Graph (class/function, call graph, API routes, DI wiring, test coverage, config usage, dependencies…), phục vụ cho AI agent qua **Model Context Protocol** — **1 tool call thay cho 10+ lệnh grep/read**.

---

## Giá trị cốt lõi — đo được, không tiếp thị suông

| Trước CortexPlexus | Với CortexPlexus | Lợi ích |
|---|---|---|
| Agent grep tên hàm → đọc 5-10 file → ghép thủ công | `GetCallers("app.services.billing.charge")` — FQN Python, TS, C#, Go… đều được | **1 call thay 10+ call** |
| Hiểu 1 service: đọc class + tests + dependencies (15+ file) | `ExploreTopic("PaymentService")` | **1 call thay 15+ call** |
| Trace 1 API request: entrypoint → handler → downstream | `GetDataFlow("/api/orders")` | **1 call thay 8+ call** |
| Đánh giá impact refactor: grep → đọc callers → đọc caller-của-caller | `GetImpactAnalysis(fqn, depth: 3)` | **1 call thay 10+ call** |
| Onboard dự án mới: đọc 20+ file thủ công | `OnboardProject(repo)` | **1 call thay 20+ call** |

**Hệ quả thực tế với AI agent**:
- Giảm 80-90% token/chi phí inference
- Agent trả lời chính xác vì có structured context, không phải đoán từ snippet
- Onboard dự án lạ trong dưới 30 giây
- **Memory xuyên dự án**: bài học agent học được ở dự án A, agent ở dự án B recall lại được

---

## 34 công cụ MCP — phủ đủ 5 nhóm nhu cầu

**Tìm kiếm & điều hướng (cả 8 ngôn ngữ)** — `search_code` (hybrid full-text + vector), `semantic_search` (ngôn ngữ tự nhiên), `get_callers` / `get_callees`, `get_implementations`, `get_class_hierarchy` (directional, không leak sibling), `get_dependencies`, `get_impact_analysis`.

**Framework intelligence (đa stack)** — `get_api_endpoints` (ASP.NET, FastAPI, Flask, NestJS, Express), `get_di_registrations` (ASP.NET, Spring, NestJS), `get_dependency_audit` (npm / pip / go / cargo / composer / maven / nuget), `get_config_usage` (`.env`, `appsettings.json`, `process.env`, `os.environ`… trên 8 ngôn ngữ), `get_architecture`.

**Tầng sâu .NET (Roslyn)** — `get_entity_mapping` (DbContext → entity), `get_data_flow` (endpoint → handler → DB), `get_middleware_pipeline` (thứ tự thực thi ASP.NET). Ba tool này chỉ dành cho C#; mọi tool phía trên là đa ngôn ngữ.

**Chất lượng & observability** — `get_test_coverage` (8 framework: xUnit, NUnit, pytest, Jest, JUnit, Go test, Rust `#[test]`, PHPUnit), `get_dead_code` (loại trừ HTTP endpoint, event subscriber, test method), `get_circular_dependencies` (DFS trên `DependsOn` graph).

**Composite & memory** — `explore_topic` / `onboard_project` (1 call thay 5-20), `get_help` (tự tài liệu hóa), và bộ **agent memory** opt-in (`save_memory` / `recall_memory` / `list_memories` / `forget_memory`) — kho tri thức chung có decay, span mọi repo đã index.

---

## Hiệu năng đã đo trên dự án thật

**R18 — HNSW bulk-load (đo trên pgvector pg17):**
> Vector index phase: **51 phút → 5.5 giây (~556× nhanh hơn)** cho batch ≥500 symbols.
> Chiến lược: drop HNSW → INSERT hàng loạt → rebuild HNSW, thay vì duy trì index live.

**Scale (CortexFlow — hệ full-stack .NET, đang chạy production):**
> 23,000+ symbols / 15,700+ embeddings, index và live-sync liên tục bằng watch agent.
> Embedding provider: **Ollama** (all-local, mặc định), **Gemini** (free tier), **Vertex AI** (đo 26.4 texts/s — ADR-017).

**Search quality (hybrid fusion):**
> Apache AGE Cypher (graph) + pgvector HNSW (vector, ef_search=100 cho ~99% recall) + tsvector BM25 (full-text), gộp bằng Reciprocal Rank Fusion, kèm HyDE + multi-query expansion.

**Incremental indexing:**
> SHA-256 content hash + file watcher → re-index chỉ file thay đổi. Vòng lặp code → re-index 1 file dưới 1 giây.

---

## 6 ngữ cảnh ứng dụng điển hình

1. **Onboard dự án mới** — agent vừa kết nối đã có bản đồ kiến trúc, không cần "học" bằng cách đọc README + lang thang file. React PWA, FastAPI service hay .NET solution enterprise đều như nhau.
2. **Debug bug phức tạp** — `get_data_flow("/api/failing-endpoint")` trả về chuỗi handler → service → repository → DB, xác định được pha nào breakpoint.
3. **Đánh giá impact trước merge** — `get_impact_analysis(method, depth:3)` chỉ ra chính xác bao nhiêu callers sẽ break nếu đổi signature.
4. **Audit test coverage** — tìm test phủ cho method bất kỳ trên 8 test framework, cảnh báo hot-path không có test trong CI.
5. **Clean up codebase** — `get_dead_code` + `get_circular_dependencies` phát hiện vùng loại khỏi được trong 1 call; thay vì chạy tool riêng (NDepend, SonarQube) đắt đỏ.
6. **Tri thức xuyên dự án** — bộ memory cho phép agent ở dự án B recall workaround mà agent ở dự án A đã tìm ra tháng trước, thay vì tự dò lại từ đầu.

---

## Tại sao tin tưởng được

| Tiêu chí | CortexPlexus |
|---|---|
| **Tests** | 800+ test đạt (unit + integration + performance), coverage ~85% |
| **Ngôn ngữ** | 8 — TypeScript, JavaScript, Python, Java, Go, Rust, PHP qua Tree-sitter; C# tầng sâu qua Roslyn |
| **Deployment** | 2 container Docker, < 2 GB RAM, < 2 GB disk |
| **License** | MIT (thương mại hóa tự do) |
| **Dependency ngoài** | Zero bắt buộc (Ollama offline là default; Gemini / Vertex AI là optional) |
| **Kiến trúc DB** | 1 PostgreSQL 17 + AGE + pgvector + tsvector — không cần Redis, RabbitMQ, Elasticsearch |

---

## So sánh nhanh với alternatives

| | GitHub Copilot | Cursor search | Sourcegraph | **CortexPlexus** |
|---|:---:|:---:|:---:|:---:|
| Mã nguồn mở | Không | Không | 1 phần | **Có (MIT)** |
| Self-hosted | Không | Không | Có (Enterprise) | **Có (miễn phí)** |
| Code graph đa ngôn ngữ | Không | Không | Có | **Có (8 ngôn ngữ)** |
| Roslyn deep C# | Không | Không | Không | **Có** |
| Memory xuyên dự án cho agent | Không | Không | Không | **Có** |
| MCP native | Không | 1 phần | Không | **Có (34 tool)** |
| Chi phí năm / 20 dev | ~$5,000 | ~$5,000 | $10,000+ | **$0** |

---

## Bắt đầu trong 3 lệnh

```bash
git clone https://github.com/DT-Tuan/CortexPlexus.git
cd cortexplexus
docker compose up -d
```

Thêm `.mcp.json` ở gốc dự án của bạn, trỏ `http://localhost:8080/mcp`, restart IDE, và agent của bạn đã có 34 tool code intelligence. Toàn bộ source code vẫn ở máy bạn — Local Agent chỉ upload metadata.

---

## Dành cho ai

- **Team polyglot và dev solo** — một server self-hosted index cả React frontend, Python services, Go tooling vào một graph duy nhất mà AI agent thực sự dùng được.
- **Team dev C# / .NET** muốn tầng sâu nhất: DI container, EF Core, middleware stack được hiểu semantic, không trả phí SaaS hàng năm.
- **Tech lead** cần audit impact / dead code / test coverage trong CI, không muốn mua công cụ $10K/năm.
- **Researcher / OSS contributor** cần nền tảng để thí nghiệm RAG-on-code, Knowledge Graph, hybrid search.

---

**Repo**: https://github.com/DT-Tuan/CortexPlexus · **License**: MIT · **Stack**: .NET 10 + PostgreSQL 17 (AGE + pgvector + tsvector) + Roslyn + Tree-sitter + Ollama/Gemini/Vertex embedding + ModelContextProtocol SDK.

> English version: [INTRODUCTION.md](./INTRODUCTION.md).
