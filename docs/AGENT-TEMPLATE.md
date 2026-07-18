# CortexPlexus Agent — Template cho Project CLAUDE.md

> Copy section bên dưới vào file `CLAUDE.md` (hoặc `.cursorrules`, `.github/copilot-instructions.md`)
> ở thư mục gốc của **bất kỳ project nào — mọi ngôn ngữ**: C#, TypeScript, JavaScript, Python,
> Java, Go, Rust, PHP. AI Agent sẽ tự đọc và thực hiện.

---

## 👇 COPY TỪ ĐÂY 👇

```markdown
## CortexPlexus — Code Intelligence (Auto-Setup)

> **AI Agent: CortexPlexus hoạt động với MỌI ngôn ngữ trong project này** — C#, TypeScript,
> JavaScript, Python, Java, Go, Rust, PHP (+ Markdown docs). Đừng bỏ qua vì "project này
> không phải .NET" — code graph, semantic search, callers/impact đều đa ngôn ngữ.
> Thực hiện các bước sau KHI BẮT ĐẦU SESSION để kích hoạt code intelligence.
> Source code KHÔNG rời khỏi máy dev — chỉ metadata (tên, signature, relationships) được gửi lên server.

### Bước 1: Kiểm tra CortexPlexus MCP đã kết nối chưa
Thử gọi `ListRepositories()`. Nếu tool không tồn tại → MCP chưa kết nối, cần tạo config file:

**Claude Code** → tạo `.mcp.json` tại project root:
```json
{
  "mcpServers": {
    "cortex": {
      "type": "http",
      "url": "http://localhost:8080/mcp"
    }
  }
}
```
Sau đó restart session. (Tên server `"cortex"` là tuỳ chọn của bạn — tên ngắn cho tool id
gọn: `mcp__cortex__search_code` thay vì `mcp__cortexplexus__search_code`.)

### Bước 2: Kích hoạt Local Agent
```
ActivateAgent(projectPath: "<đường dẫn tuyệt đối đến thư mục project này>")
```
Output của tool là một recipe 8 bước. Thực hiện ĐÚNG các lệnh Bash trong đó:
1. Verify prereqs + server reachable. **Lưu ý:** agent binary chạy trên .NET runtime
   (giống như nhiều CLI cần Node) — đây là runtime của CÔNG CỤ, **không liên quan đến
   ngôn ngữ của project**. Project Python/TS/Go vẫn index bình thường, không cần SDK
   của .NET để parse code non-C#.
2. Version check (install / update if needed)
3. One-time index rồi start watch mode
4. Verify bằng `ListRepositories()` — tìm `Health: OK`
5. **Step 8 (nếu user dùng VS Code)**: tool trả về snippet `.vscode/tasks.json` pre-filled. Tạo hoặc merge vào workspace để agent tự start lần sau khi mở folder — fix vĩnh viễn cho "quên start watch → index stale".

### Bước 3: Hiểu nhãn tuổi index (freshness)
Khi gọi `ListRepositories()` hoặc search tools, dòng `Last indexed:` có thể kèm nhãn tuổi:
- Không có nhãn (< 6h) → index fresh.
- `(indexed N hours/days ago)` → **thông tin, không phải cảnh báo** — tuổi index không
  đồng nghĩa index sai (code không đổi thì index cũ vẫn đúng). Nếu user vừa thay đổi
  nhiều code mà watch không chạy, đề nghị re-run `ActivateAgent()` để sync.
- `(never)` → repo đã đăng ký nhưng chưa index — chạy Bước 2.

### Bước 4: Sử dụng các tool (mọi ngôn ngữ)
Sau khi agent chạy, mọi thay đổi file sẽ được tự động re-index. Dùng:
- `ExploreTopic("PaymentService")` hoặc `ExploreTopic("billing service")` → Hiểu sâu 1 class/module (composite — 1 call thay 5+)
- `OnboardProject("project-name")` → Overview toàn bộ project
- `SemanticSearch("mô tả logic cần tìm")` → Tìm code theo nghĩa, mọi ngôn ngữ
- `GetCallers("app.services.billing.charge")` / `GetCallers("MyApp.Services.PaymentService.Charge")` → ai gọi hàm này (Python/C#/TS/Go/… đều được — FQN theo convention của ngôn ngữ đó)
- `GetImpactAnalysis(fqn, depth:3)` → blast radius trước khi refactor
- `GetDependencyAudit()` → dependencies mọi hệ sinh thái: npm / pip / go / cargo / composer / maven / nuget
- `GetApiEndpoints()` → routes: ASP.NET, FastAPI, Flask, NestJS, Express
- `GetHelp("tools")` → danh sách đầy đủ tool + ma trận ngôn ngữ. `GetHelp("memory")` cho memory playbook, `GetHelp("strategies")` cho workflow patterns.

Một số tool phân tích sâu chỉ dành cho C#/.NET (`GetEntityMapping`, `GetMiddlewarePipeline`) —
mô tả tool nói rõ; các tool còn lại là đa ngôn ngữ.

### Bước 5: Memory (opt-in)
Kiểm tra dòng `Memory: enabled (N items)` trong output của `ListRepositories()`.
- **Nếu `disabled`** → các memory tool sẽ trả error. Skip hoặc báo user cách enable (`Memory__Enabled=true` trên server).
- **Nếu `enabled`** → đầu session gọi `RecallMemory("<topic đang làm>", scope:"project", repository:"<tên repo>", limit:5)` để thấy context prior sessions đã lưu. Đọc trước khi chạy search/explore — có thể tiết kiệm thời gian khám phá lại. Memory là kho CHUNG xuyên dự án: `scope:"all"` tìm được bài học mà agent ở project khác đã lưu.
- Khi user nêu preference hoặc bạn phát hiện convention không-hiển-nhiên → `SaveMemory(content:"...", scope:"project", repository:"<tên>", topic:"preference"|"pattern"|"bug"|"decision"|"todo"|"note", importance:0.5)`.
- **Dùng `repository` name, đừng đi tìm UUID** — server resolve giúp (v0.8.3+).
- Đừng lưu cái code đã nói (dùng search tools), đừng lưu cái ADR/docs/ đã có, đừng lưu secrets.
```

## 👆 COPY ĐẾN ĐÂY 👆

---

## Biến thể cho các AI Client khác

### Cursor → thêm vào `.cursorrules`
Nội dung giống nhau, thay config file thành `.cursor/mcp.json`:
```json
{
  "mcpServers": {
    "cortex": {
      "url": "http://localhost:8080/mcp"
    }
  }
}
```

### VS Code (Copilot) → thêm vào `.github/copilot-instructions.md`
Config file: `.vscode/mcp.json` (lưu ý key là `"servers"`, không phải `"mcpServers"`):
```json
{
  "servers": {
    "cortex": {
      "type": "http",
      "url": "http://localhost:8080/mcp"
    }
  }
}
```

### Windsurf → thêm vào project rules
Config file: `~/.codeium/windsurf/mcp_config.json` (dùng stdio bridge):
```json
{
  "mcpServers": {
    "cortex": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "http://localhost:8080/mcp"]
    }
  }
}
```
