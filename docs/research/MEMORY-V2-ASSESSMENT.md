# Memory v2 — Đánh giá toàn diện & phương án nâng cấp MCP Memory

> **Ngày:** 2026-07-07
> **Tác giả:** Claude Code (Fable 5) — viết từ góc nhìn *người dùng trực tiếp* của memory subsystem
> **Vai trò:** Bản phân tích nền cho các ADR Memory v2 (dự kiến ADR-024…027). Thuộc lớp **L2 — MEMORY** trong [VISION.md](../VISION.md).
> **Bằng chứng sống:** Được viết ngay sau khi chẩn đoán sự cố save_memory 2026-07-07 (§3.R1).

---

## 1. Vai trò của memory trong tầm nhìn

Memory là lớp khiến CP khác mọi code-intelligence platform khác: **không nền tảng lớn nào
(Sourcegraph, Glean, Copilot) có bộ nhớ xuyên dự án gắn với code graph**. Kịch bản giá trị
cốt lõi — đúng như người vận hành mô tả:

> *CC đang ở dự án A gặp vấn đề X → recall work-memory → thấy CC ở dự án B đã xử lý X
> tháng trước → áp dụng ngay, khỏi dò lại từ đầu.*

Kịch bản này **đã hoạt động** (105+ memories, recall scope:"all" xuyên 12 repo) nhưng đang
bị giới hạn bởi 6 nhóm khiếm khuyết phân tích ở §3. Mục tiêu Memory v2: từ "kho ghi chú
có decay" → **bộ nhớ làm việc chung đáng tin cậy** (shared, trustworthy working memory)
cho một fleet agent.

## 2. Hiện trạng (facts, verified 2026-07-07)

**Nền tảng đã đúng — giữ nguyên:**

| Thành phần | Thiết kế | Đánh giá |
|---|---|---|
| Storage | 1 bảng `agent_memories` trên Postgres dùng chung (ADR-010) | Đúng — zero hạ tầng mới |
| Scope | session / project / global, CHECK constraint (ADR-011) | Đúng mô hình 3 tầng |
| Decay | Weibull k=1.5, λ theo topic, score = importance × decay (ADR-012) | Tương đương công thức retrieval của Generative Agents (recency × importance × relevance) — thiết kế có cơ sở học thuật |
| Reinforcement | Recall bump `last_accessed_at` → memory hữu dụng sống lâu | Ý tưởng đúng, thi hành có lỗ hổng (§3.R3) |
| Opt-in + PII scan | ADR-013, `ISecretsScanner` chặn secrets | Đúng |
| UX resolution | `repository` name thay UUID (v0.8.3), staleness-aware | Đúng, đã tôi luyện qua R21–R25 |

**Bề mặt tool (5):** `save_memory`, `recall_memory`, `list_memories`, `forget_memory`,
`clear_session`. Đáng chú ý những gì **không có**: update, feedback, link, consolidate,
export.

## 3. Sáu nhóm khiếm khuyết — từ trải nghiệm dùng thật

### R — Reliability (bằng chứng sống hôm nay)

**R1 · Lỗi opaque, không log, không phân loại.** 2026-07-07: `save_memory` fail 3 lần với
đúng một dòng `"An error occurred invoking 'save_memory'"`. Chẩn đoán mất ~20 phút qua
SSH + JSON-RPC + docker logs mới ra: Postgres crash-loop do filesystem `emergency_ro`.
Nguyên nhân tầng code: `SaveMemory` chỉ catch `ArgumentException`
(`MemoryTools.cs:111-114`) — `NpgsqlException` xuyên thủng lên SDK, SDK nuốt thành message
rỗng, **server không log gì**. Một agent không có SSH sẽ hoàn toàn bó tay.
→ Vi phạm trực tiếp nguyên tắc "friendly param errors" mà R21/R25 đã thiết lập cho search tools.

**R2 · Save chết cứng khi embedding fail — bất đối xứng với recall.** Recall degrade
gracefully (embed fail → filter-only, `MemoryTools.cs:160-163`); Save thì abort hẳn
(`:87-93`). Đúng lúc hạ tầng yếu nhất (embedding provider down) là lúc agent *không thể
ghi lại* bài học về chính sự cố đó.

### W — Write path (thiếu trí tuệ lúc ghi)

**W1 · Không dedup/reconciliation.** Save là INSERT mù. Không có bước "đã biết điều này
chưa?" — trách nhiệm đẩy hết cho agent (guard trong CLAUDE.md "check for existing
first" là bằng chứng workaround thủ công). Mem0 chứng minh mẫu đúng: mỗi write đối chiếu
với memories tương tự rồi quyết định ADD / UPDATE / NOOP.

**W2 · Không có `update_memory`.** Sửa một memory sai = forget (mất access history,
mất provenance) + save lại. Không có supersede link — phiên bản cũ/mới không nối nhau.

**W3 · Không có provenance.** Schema không ghi: session nào tạo, đang làm việc ở repo
nào khi học được (với global memory), nguồn bài học (verified bằng benchmark? phỏng đoán?).
So sánh: chính file-memory của Claude Code có `originSessionId` + system-reminder
"memory này 43 ngày tuổi, verify trước khi tin" — CP memory không có tương đương.

### T — Trust (kiến thức sai là kiến thức nguy hiểm)

**T1 · Memory sai thì bất tử.** Decay tính theo `last_accessed_at`; recall bump **mọi**
row trả về (`RecordAccessAsync`) bất kể có hữu ích không. Một memory sai-nhưng-khớp-query
được refresh mãi mãi. **Bằng chứng thực: R27** — hai hypothesis sai trong memory
(FK-race, embed-miscount) đánh lạc hướng điều tra *hai lần* trước khi static analysis bác
bỏ; bản thân sự bác bỏ đó không có chỗ ghi ("refuted" không phải trạng thái tồn tại).

**T2 · Hard-delete ở mọi nơi.** Reaper `DELETE WHERE score < 0.1` (ADR-012, thừa nhận
"no tombstones"); `forget_memory` cũng DELETE. Tri thức âm ("cách X **không** hoạt động,
đã thử, lý do…") — loại tri thức đắt nhất — bị xoá vĩnh viễn thay vì lưu trạng thái.

**T3 · Không gắn với vòng đời code.** `relatedFqns` là soft link không bao giờ được
kiểm tra: symbol bị xoá/rename khi re-index → memory trỏ vào hư không vẫn surface như
thật. Embedding-space mismatch (ADR-018 đã xử phần này). Không có "code drifted" flag.

### Q — Retrieval quality

**Q1 · Vector-only — memory là "con ghẻ" của chính hệ search CP.** Code search là triple
hybrid (graph + vector + BM25 + RRF + HyDE); memory recall chỉ có cosine × decay + SQL
filter. Query chứa định danh chính xác (FQN, mã lỗi, "R27", "ef_search") — thế mạnh của
BM25 — bị miss một cách hệ thống. Hạ tầng RRF/FTS **đã có sẵn trong CP**, chỉ chưa nối
vào memory.

**Q2 · Score đơn nhất, không giải thích.** `score: 0.4132` — không biết do similarity
hay decay hay importance; agent không có cơ sở để tin/nghi.

**Q3 · Output tốn token.** `WriteIndented=true` + full content + đủ 12 field cho mọi hit
(`MemoryTools.cs:25-29`). 10 hits ≈ 1.5–2K token. ADR-021 áp vào đây được ngay.

### C — Consolidation (không có tầng suy tưởng)

**C1 · Chỉ có raw memories, không có synthesis.** 105 items và tăng dần: nhiều mảnh
R17/R18/R19 lẽ ra là *một* bài "embedding throughput playbook". Generative Agents/Letta
đều có tầng reflection — chưng cất insight bậc cao từ cụm quan sát thô. CP không có cả
cơ chế lẫn view hỗ trợ (không clustering, không near-duplicate report).

### S — Sharing (đúng chỗ user muốn đẩy mạnh nhất)

**S1 · Ranking xuyên dự án không nhận biết ngữ cảnh.** scope:"all" trộn 12 repo với cùng
công thức điểm — memory của repo đang làm việc không được ưu tiên hơn memory của repo xa
lạ; global không ưu tiên hơn project-của-người-khác. Provenance (tên repo) có hiển thị —
tốt — nhưng không tham gia xếp hạng.

**S2 · Multi-agent blackboard chưa có quy ước.** Session scope tồn tại nhưng không có
semantics chia sẻ: các subagent trong một phiên không có convention chung sessionId để
đọc/ghi facts cho nhau (VISION Tier-3 "multi-agent blackboard" cần nền này).

**S3 · Không có export/backup/import.** 105 memories chỉ sống trong `pgdata` — sự cố
hôm nay (filesystem RO) suýt là bài test khôi phục thật. Không có JSONL export, không có
runbook backup riêng cho memory.

## 4. Đối chiếu các hệ AI-memory lớn

| Hệ | Ý tưởng đáng học | CP có chưa | Đáng adopt? |
|---|---|---|---|
| **Mem0** | Write-time reconciliation: so với memories cũ → ADD/UPDATE/NOOP; graph memory | ❌ W1/W2 | ✅ **Cốt lõi của v2** — nhưng để *agent* quyết (server trả candidates), không nhét LLM vào server |
| **Zep / Graphiti** | Bi-temporal (valid_at/invalid_at); contradiction → **invalidate, không delete**; hybrid recall + reranker | ❌ T1/T2/Q1 | ✅ Adopt "invalidation thay vì deletion" + hybrid recall; ⏸ full knowledge-graph tạm hoãn |
| **Letta (MemGPT)** | Agent tự biên tập memory (core-memory blocks); sleep-time consolidation | ❌ W2/C1 | ✅ `update_memory` + consolidation session; ❌ core-block always-in-context (đó là việc của client) |
| **Generative Agents** | recency × importance × relevance; reflection tier | ✅ scoring ≈ tương đương; ❌ reflection | ✅ Reflection dưới dạng server-report + agent-merge |
| **Claude Code auto-memory** | Provenance từng memory; nhắc "point-in-time, verify trước khi tin"; supersede discipline; index để rẻ-đọc | ❌ W3/T1 | ✅ Origin metadata + trust framing trong output recall |
| **OpenAI memory** | Auto-capture từ hội thoại | ❌ (manual save + hook nhắc) | ❌ phía server; ✅ phía client bằng hooks (đã có) — giữ nguyên phân công |

**Nguyên tắc chuyển hoá (nhất quán VISION non-goal #3):** server cung cấp **cơ chế**
(similarity, staging, invalidation, clustering, ranking signals) — agent cung cấp
**phán đoán** (merge hay không, refute hay giữ, viết synthesis). Không LLM trong server.

## 5. Phương án Memory v2 — sáu gói việc

### M0 — Reliability & Honesty *(P0 — làm cùng đợt ADR-018/021)*

1. **Error envelope chuẩn cho cả 5 tool**: catch-all → phân loại
   (`db_unavailable` / `embedding_unavailable` / `validation` / `secrets`) + hành động
   khuyến nghị + **log server-side đủ để chẩn đoán không cần SSH**. (Hôm nay: 0 log.)
2. **Degraded save**: embedding fail → lưu với `embedding = NULL` + cờ
   `pending_embedding`, trả cảnh báo "saved, semantic recall sẽ có sau khi backfill".
   Backfill job embed lại các row NULL khi provider hồi phục (recall đã xử lý NULL
   sẵn — `COALESCE 0.5`).
3. ADR-018 stamping cho memories (đã proposed) + ADR-021 compact output cho recall/list.

### M1 — Write-time reconciliation *(trái tim của v2)*

4. **`save_memory` trả về near-duplicates thay vì INSERT mù**: server tìm top-3 memory
   cùng scope có cosine > ngưỡng (~0.83, tune sau); nếu có →
   `{"status":"similar_found", "candidates":[…]}` kèm hướng dẫn chọn:
   `save anyway (force:true)` / `update_memory(id,…)` / bỏ. Một round-trip thêm duy nhất
   khi thật sự nghi trùng.
5. **`update_memory(id, content?, topic?, importance?, relatedFqns?)`** — sửa tại chỗ,
   re-embed, giữ access history; tự ghi `updated_at`.
6. **Supersede**: `update` hoặc `save(supersedes: id)` → row cũ `status='superseded'`
   + link. Chuỗi phiên bản truy được.
7. **Provenance columns**: `origin_session TEXT`, `origin_repo UUID` (repo đang mở khi
   học được — kể cả cho global memory), `source TEXT` (vd `"benchmark"`, `"user"`,
   `"hypothesis"`).

### M2 — Retrieval v2 *(tái dùng hạ tầng search sẵn có)*

8. **Hybrid recall**: thêm generated column `content_fts tsvector` + BM25 leg, fuse bằng
   RRF (code có sẵn trong `CortexPlexus.Search`). Kỳ vọng fix hẳn lớp miss "định danh
   chính xác".
9. **Score giải thích được**: trả `{similarity, decay, importance, boosts}` thay vì một
   số; kèm nhãn gọn kiểu `match: semantic+exact`.
10. **Context-aware ranking (S1)**: boost có trọng số — cùng-repo > global >
    khác-repo (hệ số nhẹ, vd 1.2 / 1.1 / 1.0, đo rồi tune); memory có `status='refuted'`
    xếp cuối kèm nhãn (vẫn trả — tri thức âm hữu ích).
11. **Touch-on-read có chọn lọc (T1)**: recall **không** tự bump nữa; thêm tham số
    `markUseful: [ids]` ở lần recall kế / hoặc tool `memory_feedback(ids, useful)` —
    agent xác nhận memory nào thật sự dùng. (Client hook của CC có thể tự động hoá việc
    này ở cuối turn.)

### M3 — Lifecycle: invalidation thay vì deletion

12. **`status` column**: `active | superseded | refuted | archived`. `forget_memory`
    mặc định → `refuted` + bắt buộc `reason` (giữ tri thức âm — bài học R27);
    `hard:true` mới DELETE thật (PII/secret).
13. **Reaper → Archiver**: dưới ForgetThreshold → `archived` (loại khỏi recall mặc định,
    còn trong `list_memories(includeArchived:true)`), DELETE thật chỉ sau chu kỳ dài
    (vd 180 ngày archived).
14. **Export/backup**: `GET /api/memories/export` (JSONL, không embedding) + runbook
    backup; import tương ứng. Đóng S3 — và sự cố hôm nay là lý do.

### M4 — Code-graph binding *(moat riêng của CP — không hệ memory nào làm được)*

15. **Validate `relatedFqns` lúc save** (tra `code_symbols`, cảnh báo FQN lạ, gợi ý gần
    đúng — tái dùng parent-walk hint của R25).
16. **Drift detection lúc re-index**: symbol biến mất/rename → memory liên quan gắn cờ
    `code_drifted` (surface trong recall: *"⚠️ linked symbol no longer exists"*).
    Đây là ưu thế không ai có: memory tự biết code đã đổi dưới chân nó.
17. **Recall theo working set**: `recall_memory(nearFqns: […])` boost memory link tới
    các symbol agent đang đụng (nối thẳng vào `get_context_pack` §memories, ADR-019).

### M5 — Consolidation (reflection tier)

18. **`get_memory_maintenance_report`**: server clustering (pgvector, không LLM) trả:
    cụm near-duplicate, memory sắp archive, memory `code_drifted`, memory chưa từng được
    recall sau N ngày → agent (phiên `/reflect` định kỳ) đọc report, merge/synthesis
    bằng `update_memory` + `supersedes`. Server đo — agent nghĩ.

### M6 — Sharing v2 (mở rộng đúng hướng user muốn)

19. **Blackboard convention cho multi-agent**: chuẩn hoá `session` scope làm shared
    scratchpad — orchestrator phát `sessionId` cho subagents, `clear_session` khi xong
    (nền cho VISION Tier-3 multi-agent).
20. **Lesson promotion**: workflow "project memory được recall hữu ích từ ≥2 repo khác
    nhau" (đo bằng M2.11 feedback + origin_repo) → maintenance report gợi ý promote lên
    `global`. Bài học tốt tự nổi lên thay vì chờ agent nhớ ra phải save global.

### Schema evolution (toàn bộ additive, theo phong cách migration hiện có)

```sql
ALTER TABLE agent_memories
  ADD COLUMN IF NOT EXISTS status          TEXT NOT NULL DEFAULT 'active',
  ADD COLUMN IF NOT EXISTS supersedes      UUID,
  ADD COLUMN IF NOT EXISTS refuted_reason  TEXT,
  ADD COLUMN IF NOT EXISTS origin_session  TEXT,
  ADD COLUMN IF NOT EXISTS origin_repo     UUID,
  ADD COLUMN IF NOT EXISTS source          TEXT,
  ADD COLUMN IF NOT EXISTS updated_at      TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS code_drifted    BOOLEAN NOT NULL DEFAULT FALSE,
  ADD COLUMN IF NOT EXISTS pending_embedding BOOLEAN NOT NULL DEFAULT FALSE,
  -- ADR-018:
  ADD COLUMN IF NOT EXISTS embedding_provider TEXT,
  ADD COLUMN IF NOT EXISTS embedding_model    TEXT,
  ADD COLUMN IF NOT EXISTS embedding_dim      INT,
  -- M2 hybrid:
  ADD COLUMN IF NOT EXISTS content_fts tsvector
      GENERATED ALWAYS AS (to_tsvector('english', content)) STORED;
```

## 6. Lộ trình & ánh xạ ADR

| Đợt | Gói | ADR dự kiến | Ghép với |
|---|---|---|---|
| **Now** (cùng Tier-1) | M0 (reliability, degraded save) | **ADR-024** | ADR-018 (space), ADR-021 (compact) |
| **Next** | M1 + M2 (reconciliation + hybrid recall + feedback) | **ADR-025** | — |
| **Next** | M3 (status/invalidation, archiver, export) | **ADR-026** | — |
| **Tier-2** | M4 (code-drift binding) + M5 (maintenance report) | **ADR-027** | ADR-019 (context pack), re-index pipeline |
| **Horizon** | M6.19 blackboard, M6.20 promotion; Graphiti-style temporal edges nếu nhu cầu chứng minh | — | VISION Tier-3 |

**Không làm:** LLM extraction/summary phía server (client làm tốt hơn, VISION non-goal);
knowledge-graph memory đầy đủ kiểu Zep ngay bây giờ (đợi M1–M4 chứng minh nhu cầu);
auto-capture hội thoại (hook client đã đảm nhiệm).

## 7. Thước đo thành công (đo trước–sau, theo kỷ luật R17)

| Metric | Baseline (ước từ hiện trạng) | Mục tiêu sau M1–M3 |
|---|---|---|
| Tỉ lệ near-duplicate trong store | chưa đo (nghi ~15–20% của 105) | <5%, đo bằng maintenance report |
| Sự cố "memory sai dẫn lạc hướng" | 2 lần ghi nhận (R27) | 0 — wrong memory bị refute + xếp cuối kèm nhãn |
| Recall miss định danh chính xác | có hệ thống (vector-only) | ~0 nhờ BM25 leg (test bộ golden queries) |
| Token / lần recall (10 hits) | ~1.5–2K | <600 (compact + score gọn) |
| Lỗi opaque | 100% lỗi hạ tầng hôm nay | 0 — mọi lỗi có category + action |
| Khả năng khôi phục store | chỉ pgdata volume | JSONL export + runbook, kiểm chứng bằng restore drill |

---

*Tài liệu này là input cho ADR-024…027. Cập nhật sau mỗi đợt ship; số liệu baseline đo
lại ngay khi server phục hồi sau sự cố 2026-07-07.*
