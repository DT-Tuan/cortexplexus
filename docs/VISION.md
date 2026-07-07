# CortexPlexus — Vision & Strategic Direction

> **Ngày:** 2026-07-07
> **Tác giả:** Claude Code (Fable 5) — viết từ góc nhìn *người dùng chính* của CP
> **Vai trò tài liệu:** Kim chỉ nam (north star) cho các phase tiếp theo. ROADMAP.md ghi *cái gì đã/sẽ làm*; tài liệu này ghi *tại sao và theo hướng nào*.

---

## 1. Sứ mệnh (không đổi)

> **Cung cấp cho AI coding agent bản đồ tra cứu toàn dự án — giảm thời gian grep, giảm token, giảm nhiễu ngữ cảnh → tăng hiệu quả, giảm chi phí.**

Thước đo thành công duy nhất: **số token + số tool-call mà agent tiết kiệm được cho mỗi task thực tế**, không phải số lượng tool hay số ngôn ngữ hỗ trợ. Mọi feature phải trả lời được: *"nó thay được bao nhiêu lần Grep/Read?"*

---

## 2. CP đang ở đâu (đánh giá trung thực, 2026-07)

### 2.1 Điểm mạnh đã có — đừng đánh mất

| Năng lực | Trạng thái | Ghi chú |
|---|---|---|
| Tri-store hybrid (AGE graph + pgvector + FTS, RRF) | ✅ Trưởng thành | Nền tảng đúng ngay từ đầu — hiếm tool OSS nào có cả 3 |
| Query expansion (HyDE + multi-query) | ✅ Phase 5 | Đi trước phần lớn code-search OSS |
| C# depth (Roslyn): DI, EF, endpoints, middleware, data-flow, exceptions, metrics | ✅ Sâu nhất thị trường self-hosted | Là moat thật sự với .NET |
| 8 ngôn ngữ + framework intelligence đa stack (ADR-016) | ✅ C1–C4 | endpoints/DI/dep-audit cho Python/TS/Java |
| Local Agent — source không rời máy dev | ✅ | Điểm bán hàng privacy lớn nhất vs SaaS |
| Memory xuyên dự án (scope, Weibull decay, opt-in) | ✅ v0.8.x | Không platform nào tương đương có |
| Staleness labels + tự phát hiện index cũ | ✅ v0.8.3 | Sinh ra từ pain thật |
| Ops: GHCR CI/CD, slim image, restart policy, watch units | ✅ | Vừa tôi luyện qua sự cố reboot LXC |
| Test discipline | ✅ 808+ tests | Từ ~25% → ~85% coverage |

### 2.2 Gap thật — đúc từ trải nghiệm sử dụng hằng ngày (quan trọng nhất tài liệu này)

Đây là những chỗ **chính tôi (Claude Code) vấp phải** khi dùng CP làm việc thật, xếp theo độ đau:

**GAP-1 · Vector-space mismatch âm thầm (P0 — đang là bug tiềm ẩn).**
Fleet hiện có repo embed bằng Vertex và repo còn vector Ollama. Query giờ embed bằng Vertex → `semantic_search`/`recall_memory` trên repo Ollama trả về similarity rác **mà không có bất kỳ cảnh báo nào**. Schema không lưu `embedding_provider/model` per repo/per memory. Agent không thể tự biết kết quả đang bị lệch không gian vector.

**GAP-2 · Chi phí orient đầu phiên còn cao.**
Mỗi session tôi phải: `list_repositories` (~100 dòng text) → `recall_memory` → vài `search_code` thăm dò. 3–5 call, vài nghìn token, lặp lại mỗi phiên. Chưa có một call "cho tôi bản đồ + trí nhớ liên quan + trạng thái, gói trong N token".

**GAP-3 · Mỗi câu hỏi mới = một tool C# mới + release.**
30 tools là 30 câu hỏi đóng cứng. Khi tôi cần câu hỏi thứ 31 ("class nào vừa có Publishes vừa không có test?"), không có đường thoát — phải grep thủ công hoặc chờ CP thêm tool. Graph đã có sẵn dữ liệu; thiếu **cổng truy vấn mở** (read-only Cypher).

**GAP-4 · Không diff-aware.**
CP chỉ biết trạng thái *đã index*. Câu hỏi giá trị nhất trước mỗi commit — *"diff chưa commit này ảnh hưởng gì?"* — CP chưa trả lời được. Toàn bộ nghi thức `/code-review`, `/analyze` của tôi vẫn phải tự lắp từ get_callers rời rạc.

**GAP-5 · Edge upsert vẫn là nút cổ chai indexing.**
Số đo mới nhất (MyFin, 2026-07-02): 485 symbols embed xong trong 19–83s, nhưng **4 402 relationships mất 673s (~6.5 edge/s)**. Với repo lớn (CortexFlow 88 phút full-index) phần lớn wall-time là edge upsert, không phải embedding (đã xác minh R27/ADR-017). ADR-009 giải quyết một lần nhưng số liệu cho thấy còn ~2 bậc độ lớn để cải thiện.

**GAP-6 · Kết quả tool là text tự do, tốn token.**
Output verbose, không có `format: compact/json`, không pagination. `list_repositories` 12 repo ≈ 1 300 token cho thông tin có thể nén còn ~200. MCP đã hỗ trợ `structuredContent` — CP chưa dùng.

**GAP-7 · Watch/agent lifecycle còn thủ công.**
Sự cố tháng 6–7: reconcile allowlist hardcode tên repo, watch không tự bật cho repo mới, server sập 3 ngày không ai biết. v0.8.4 vá bằng recipe VS Code; gốc rễ (tự đăng ký, tự giám sát, tự cảnh báo) chưa giải.

**GAP-8 · Cross-repo topology chưa nối.**
`HttpCalls` edges đã có URL, `api_endpoint` nodes đã có route — nhưng chưa **nối hai đầu qua ranh giới repo**. "Sửa endpoint này của CortexPlexus thì CortexBridge/CortexFlow chỗ nào gãy?" là câu hỏi tôi bị hỏi thường xuyên và CP im lặng.

**GAP-9 · Bề mặt adoption thiên .NET → agent từ chối dùng CP trên project khác.**
Template CLAUDE.md yêu cầu "dotnet SDK" vô điều kiện, FQN examples kiểu C#, INTRODUCTION mở đầu bằng Roslyn/EF — agent trên repo Python/TS đọc xong kết luận "không phải tool cho mình" và quay về grep. Bằng chứng: người vận hành phải vá bằng client-side hook ("works for C# AND Python/TS/Go"). Kèm theo: ~2K token mô tả tool nạp vào mọi session của client không defer schema.

### 2.3 Việc nên NGỪNG ưu tiên (non-goals ngắn hạn)

- **RBAC/multi-user (Phase 11)** — thực tế là single-user; chỉ làm khi có deployment nhiều người thật.
- **Thread-scoped rewriting phía server** — MCP client (tôi) đã giữ context hội thoại; đầu tư phía server là trùng lặp.
- **Thêm ngôn ngữ mới theo bề rộng** — 8 ngôn ngữ đủ; giá trị giờ nằm ở *chiều sâu framework* (Django/gin/Spring endpoints — tail của ADR-016) chứ không phải ngôn ngữ thứ 9.
- **Migrate FTS5/SQLite** — tsvector tương đương hoặc hơn (đã kết luận trong research doc).

---

## 3. Đối chiếu landscape (CP đứng đâu giữa các nền tảng lớn)

| Nền tảng | Mô hình | CP học được gì | CP thắng ở đâu |
|---|---|---|---|
| **Sourcegraph / SCIP** | Precise code intel + cross-repo, index protocol chuẩn (SCIP), batch changes | Cross-repo linking; chuẩn hoá index format; "code insights" dashboards | Self-hosted miễn phí thật sự; MCP-native; memory; C# sâu hơn (Sourcegraph C# support hạng hai) |
| **Glean (Meta)** | Kho *facts* + ngôn ngữ truy vấn (Angle/Datalog) — mọi câu hỏi là một query, không phải một tool | **Cổng truy vấn mở** thay vì tool cứng — chính là GAP-3 | Glean không OSS-friendly, không memory, không semantic |
| **GitHub Copilot / code search** | Embedding + keyword trên SaaS khổng lồ | Chunking/ranking scale | Privacy (source không rời máy), graph quan hệ thật thay vì chỉ text |
| **Serena (LSP MCP)** | LSP symbols per-session, không persist | Nhẹ, zero-setup | CP persist graph + cross-repo + memory + semantic — Serena mù kiến trúc |
| **Aider repo-map** | Bản đồ repo nén theo token-budget, chọn symbol bằng PageRank trên call graph | **Token-budgeted context pack** — chính là GAP-2 | CP có graph giàu hơn nhiều để xây map tốt hơn Aider |
| **Microsoft GraphRAG** | Community detection + hierarchical summaries → trả lời câu hỏi "toàn cục" | Louvain/Leiden + AI summary per module → `onboard_project` thế hệ 2 | CP realtime-incremental; GraphRAG batch, đắt |
| **Zilliz claude-context** | Vector-only MCP code search, merkle-tree sync | Đơn giản hoá sync | CP có graph + FTS + memory — vector-only là tập con của CP |

**Định vị:** CP là giao của 3 vòng tròn mà chưa ai chiếm: **(1) code-graph sâu self-hosted, (2) MCP-native cho agent, (3) memory xuyên dự án**. Sourcegraph có (1), Serena có (2), không ai có (3) gắn với code intelligence. Giữ chặt giao điểm này.

---

## 4. Kim chỉ nam — 4 nguyên tắc sản phẩm

1. **Token là đơn vị tiền tệ.** Mỗi feature đo bằng token saved/task. Output mặc định phải nén; verbose là opt-in. Một tool trả 2 000 token cho câu trả lời 100 token là tool hỏng.
2. **Agent là người dùng, không phải con người.** UI là tool description + output format. Đầu tư vào: câu trả lời máy-đọc-được, error message tự chữa ("có phải bạn muốn X?"), staleness tự khai. Con người chỉ cần Web UI ở mức "đủ debug".
3. **Đúng và tự biết mình sai.** Kết quả sai âm thầm (stale index, vector mismatch, FQN đoán mò) phá hoại niềm tin nhanh hơn thiếu feature. Mọi kết quả phải mang metadata tin cậy được (freshness, embedding-space, coverage).
4. **Bản đồ sống, không phải snapshot.** Giá trị của CP tỉ lệ thuận với độ tươi của index. Mọi đầu tư vào auto-watch, incremental speed, self-healing đều compound.

---

## 5. Vision — CP như "Context OS" cho coding agents

Tầm nhìn 12–18 tháng: CP tiến hoá từ **bộ tool tra cứu** thành **hệ điều hành ngữ cảnh** — tầng đứng giữa agent và code, chịu trách nhiệm *cấp phát ngữ cảnh đúng, đủ, tươi* cho mọi phiên làm việc.

```
┌─────────────────────────────────────────────────────────────┐
│  L4  FLEET   — topology xuyên repo, impact xuyên service     │
│  L3  CONTEXT — context packs theo task + token budget,       │
│               diff-aware, working-set per session            │
│  L2  MEMORY  — episodic (session) + semantic (decay),        │
│               versioned theo embedding-space                 │
│  L1  MAP     — code graph đa ngôn ngữ, framework-aware,      │
│               truy vấn mở (Cypher), luôn tươi (watch)        │
│  L0  TRUST   — freshness, space-version, coverage metadata   │
│               trên MỌI kết quả                               │
└─────────────────────────────────────────────────────────────┘
```

L0–L2 đã có nền; L3 là chiến trường chính 2026H2; L4 là 2027.

---

## 6. Lộ trình đề xuất (xếp hạng theo *token-saved × độ tin cậy × effort*)

### Tier 1 — Now (≤3 tháng) · "Trust & Economy"

| # | Feature | Giải quyết | Effort | Ghi chú thiết kế |
|---|---|---|---|---|
| **T1.1** | **Embedding-space versioning** — cột `embedding_provider/model/dim` trên `repositories` + `memories`; `semantic_search`/`recall_memory` cảnh báo hoặc loại repo lệch space; `list_repositories` hiện space | GAP-1 (P0) | Thấp | Migration + guard ~2 ngày. Chặn đứng lớp bug "similarity rác" |
| **T1.2** | **`get_context_pack(task, budget_tokens)`** — 1 call trả: repo-map nén (PageRank trên call-graph chọn symbol), memories liên quan, config/DI/endpoints chạm tới task, tất cả trong budget | GAP-2 | Trung | Học Aider repo-map nhưng dùng graph thật. Đây là feature ROI cao nhất toàn roadmap |
| **T1.3** | **`graph_query`** — read-only Cypher (whitelist MATCH/RETURN, timeout, row-limit, chặn ghi) | GAP-3 | Thấp-Trung | Biến 30 tools thành ∞ câu hỏi. Học mô hình Glean. Guardrail là phần chính của effort |
| **T1.4** | **Compact output mode** — `format: "compact"` mặc định cho agent (structuredContent JSON), verbose opt-in; pagination cho list tools | GAP-6 | Thấp | Đo trước/sau bằng token count trên 20 call mẫu |
| **T1.5** | **Edge upsert bulk-load v2** — UNWIND batch lớn + tạm drop index như HNSW bulk (R18 đã chứng minh pattern 556× cho vector; áp cho AGE edges) | GAP-5 | Trung | Mục tiêu: 6.5 → ≥100 edge/s; MyFin relationship phase 673s → <30s |
| **T1.6** | **Watch self-service** — agent tự `systemd enable` (hoặc recipe per-OS) khi index lần đầu; server ping "agent chết >24h" qua `list_repositories` warning | GAP-7 | Thấp | Nốt ruột từ sự cố tháng 6 |
| **T1.7** | **Memory M0 — reliability & honesty** — error envelope phân loại + log, degraded save (embedding backfill), compact recall output | Sự cố save_memory 2026-07-07 | Thấp | ADR-024; phân tích đầy đủ: [MEMORY-V2-ASSESSMENT](research/MEMORY-V2-ASSESSMENT.md) |
| **T1.8** | **Language-neutral adoption surface** — sửa template/docs/tool-descriptions thiên .NET, languages-per-repo trong list_repositories, description token budget, deprecate `get_nu_get_audit` | GAP-9 | Thấp | ADR-028; behavioral eval trên repo TS/Python |

### Tier 2 — Next (3–9 tháng) · "Diff-aware & Fleet"

| # | Feature | Giải quyết | Ghi chú |
|---|---|---|---|
| **T2.1** | **`get_impact_of_diff(diff)`** — nhận unified diff (chưa commit), map hunk → symbols → impact graph + tests cần chạy | GAP-4 | Khoá vào quy trình `/code-review`, `/analyze` của agent. Feature "review trước commit" đầu tiên |
| **T2.2** | **Cross-repo service topology** — nối `HttpCalls(url)` ↔ `api_endpoint(route)` xuyên repo (URL template matching); tool `get_service_topology` + impact xuyên service | GAP-8 | Với fleet 12 repo hiện tại là giá trị thật ngay |
| **T2.3** | **Session working-set** — CP ghi lại symbols mà session đã đụng (qua tool calls); `restore_working_set(session)` sau compaction | Compaction pain của chính tôi | Bridge với memory scope `session` đã có |
| **T2.4** | **GraphRAG-lite** — Louvain/Leiden community + AI summary per module (Ollama/Vertex) → `onboard_project` v2 trả lời "hệ này làm gì" trong 1 call | Backlog cũ, đúng thời điểm | Chỉ chạy khi index; cache trong DB |
| **T2.5** | **Ranking v2** — RRF thêm tín hiệu: graph centrality (PageRank), recency (git blame age), test-coverage | Chất lượng top-k | Cần golden-query benchmark trước (T2.6) |
| **T2.6** | **Golden-query eval harness** — bộ query chuẩn per-repo + recall@k chạy trong CI; chặn regression chất lượng search | Đo lường | "Measure before projecting" — bài học R17 thành hạ tầng |
| **T2.7** | ADR-016 tail: Django/gin/Spring-Boot endpoints, `@Bean` DI | Bề sâu framework | Giữ nhịp Tier B |
| **T2.8** | **Memory M1–M4** — write-time reconciliation (dedup/supersede/`update_memory`), hybrid recall (BM25+RRF), invalidation thay deletion (`refuted`+reason), code-drift binding | Memory là L2 của Context OS | ADR-025/026/027; xem [MEMORY-V2-ASSESSMENT](research/MEMORY-V2-ASSESSMENT.md) |

### Tier 3 — Horizon (9+ tháng) · "Context OS hoàn chỉnh"

- **Temporal graph** — trạng thái code tại mọi thời điểm (git-aware); "ai đổi hàm này, ADR nào giải thích"; nối commit ↔ symbol ↔ ADR/memory → trả lời *why*, không chỉ *what*.
- **Proactive context** — server push (MCP notification): "file bạn đang sửa có 3 caller ngoài repo", "index vừa tươi lại".
- **Multi-agent blackboard** — fleet subagent chia sẻ facts qua memory scope `session` với TTL; điều phối review/migration nhiều agent trên cùng graph.
- **Export/Import chuẩn SCIP** — interop với hệ sinh thái Sourcegraph indexers: nuốt SCIP index cho ngôn ngữ CP chưa parse sâu.
- **Code-review intelligence** — graph-pattern rules (backlog cũ): "endpoint mới không có test", "DI đăng ký nhưng không resolve" → chạy như linter trên diff.

---

## 7. Điều KHÔNG làm (để giữ tập trung)

1. **Không xây IDE plugin riêng** — MCP là bề mặt; VS Code/Cursor/Windsurf đã là client.
2. **Không SaaS-hoá** — self-hosted là căn cước; multi-tenant phá vỡ privacy model của Local Agent.
3. **Không LLM-hoá server nặng** — CP cấp *ngữ cảnh*, agent mới là bộ não. Summary/HyDE dùng model nhỏ local là đủ; đừng biến CP thành agent thứ hai.
4. **Không đuổi theo số ngôn ngữ** — thêm ngôn ngữ khi có repo thật cần, không phải để đẹp bảng so sánh.

---

## 8. Thước đo north-star (review mỗi quý)

| Metric | Hiện tại (ước) | Mục tiêu 2026Q4 |
|---|---|---|
| Token/orient đầu phiên (list + recall + thăm dò) | ~4–6k | **<1.5k** (context pack) |
| Tool-call thay thế grep cho 1 câu hỏi cấu trúc | 1 call nhưng câu hỏi phải "trong 30 loại" | 1 call cho **câu hỏi bất kỳ** (graph_query) |
| Kết quả sai âm thầm (stale/space-mismatch) | Có thể xảy ra, có label một phần | **0** — mọi kết quả mang trust metadata |
| Full-index repo 20k symbols | ~90 phút (edge-bound) | **<15 phút** |
| Search quality | Không đo tự động | recall@10 trong CI, không regression |

---

*Tài liệu này nên được cập nhật sau mỗi tier hoàn thành. Khi ROADMAP mâu thuẫn với VISION, sửa một trong hai — đừng để hai nguồn sự thật.*
