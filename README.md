# HttpSharp

`HttpSharp` 是面向 ERB 的通用 HTTP 插件，使用方式接近 Python `requests`，但受 `CALLSHARP` 的参数限制：所有响应都必须写回字符串或整数变量。

将bin/Debug/net8.0-windows/HttpSharp.dll放入".\你的era的游戏目录\plugins"即可使用

本插件更多面向于想在口上或其它玩法功能上接入ai的创作者

本插件附带readme（就是这个文件，，，） vibe coding时可以将本文件发送给ai

## 快速开始

```erb
CALLSHARP HttpSet("https://api.example.com")
CALLSHARP HttpHeader("Authorization", "Bearer YOUR_KEY")
CALLSHARP HttpGet("/health", RESPONSE)
CALLSHARP HttpStatus(STATUS)
CALLSHARP HttpResponse(HTTP_CODE)
```

基础 URL 只在当前 Emuera 进程内保存。传入绝对 URL 时会忽略基础 URL。

## 请求函数

```erb
CALLSHARP HttpGet(url, output)
CALLSHARP HttpPost(url, body, output)
CALLSHARP HttpPut(url, body, output)
CALLSHARP HttpDelete(url, output)
```

`HttpPost` 和 `HttpPut` 的 body 按 UTF-8 发送，默认 Content-Type 为 `application/json`。需要发送其它格式时请使用服务器允许的 JSON 或另行扩展插件。

```erb
CALLSHARP HttpPost("/echo", "{\"message\":\"hello\"}", RESPONSE)
```

> 所有请求都在后台线程执行，等待期间插件会周期性重置引擎的「无限循环」计时器。所以长时间请求既不会卡死界面，也不会弹出「可能发生了无限循环」警告。

## 流式响应（SSE）

服务器以 `data:` 帧持续推送内容时（典型场景：AI 对话的 `"stream":true`），用这两个函数：

```erb
CALLSHARP HttpPostStream(url, body, output)
CALLSHARP HttpGetStream(url, output)
```

`HttpPostStream` 的 body 按 UTF-8 JSON 发送；`HttpGetStream` 不带请求体。

```erb
CALLSHARP HttpPostStream("https://api.deepseek.com/chat/completions", "{\"model\":\"deepseek-flash\",\"messages\":[{\"role\":\"user\",\"content\":\"介绍一下东方Project\"}],\"stream\":true}", REPLY)
```

行为：

- **边收边打印**到游戏界面，函数返回时把完整文本写入 `output`。
- 每个 `data:` 帧会自动尝试提取文本字段，顺序为：`choices[0].delta.content` → `choices[0].delta.text` → `choices[0].text` → `choices[0].message.content` → `delta.text`（Anthropic 风格）→ `content` / `response` / `text` / `message`。都不是 JSON 时，该帧内容原样追加。
- 服务器若不支持流式、直接返回普通文本或 JSON，插件会在结束后整体返回，不会报错。
- 想关闭实时打印（只要最终文本）：`CALLSHARP HttpStreamPrint(0)`；恢复用 `HttpStreamPrint(1)`（默认开启）。

```erb
CALLSHARP HttpStreamPrint(0)
CALLSHARP HttpPostStream("/chat", JSON_BODY, REPLY)
CALLSHARP HttpStreamPrint(1)
```

流式解析失败时，`HttpError` 会附带诊断信息，例如 `data行=0 首行=...`，便于判断服务器实际返回了什么格式。

## Cookie 会话

插件内部使用 `CookieContainer`，服务器返回的 `Set-Cookie` 会**自动保存**，后续请求自动带上。

手动管理：

```erb
CALLSHARP HttpCookieSet(name, value)
CALLSHARP HttpCookieGet(name, output)
CALLSHARP HttpCookieList(output)
CALLSHARP HttpCookieClear()
```

```erb
CALLSHARP HttpSet("https://example.com")
CALLSHARP HttpCookieSet("session", "abc123")
CALLSHARP HttpGet("/api/me", RESPONSE)

CALLSHARP HttpCookieGet("session", VALUE)
CALLSHARP HttpCookieList(ALL_COOKIES)     ; 格式：name=value; name2=value2
CALLSHARP HttpCookieClear()               ; 清空所有 Cookie（相当于退出登录）
```

`HttpCookieSet` / `HttpCookieGet` / `HttpCookieList` 使用**基础地址**（未设置时用最近一次请求的地址）来确定 Cookie 所属域。所以要先 `HttpSet`，或至少发起过一次请求。

## JSON 请求

```erb
CALLSHARP HttpJsonGet("/api/data", JSON)
CALLSHARP HttpJsonPost("/api/data", "{\"name\":\"Alice\"}", JSON)
CALLSHARP HttpJsonPath("data.name", NAME)
```

`HttpJsonGet`、`HttpJsonPost` 会先验证 JSON；`HttpJsonPath` 读取最近一次 HTTP 响应中的简单对象路径，例如 `data.user.name`。不支持完整 JSONPath、数组过滤或表达式。

## 文件下载

```erb
CALLSHARP HttpDownload("https://example.com/a.png", "C:\\Game\\resources\\a.png")
```

保存路径必须是绝对路径，父目录会自动创建。插件只下载 HTTP/HTTPS 内容，不执行下载内容。

POST 接口直接返回 WAV 等二进制文件时，使用：

```erb
CALLSHARP HttpPostDownload("http://127.0.0.1:9880/tts", JSON_BODY, "C:\\Game\\sound\\tts.wav")
CALLSHARP HttpStatus(STATUS)
CALLSHARP HttpResponse(HTTP_CODE)
CALLSHARP HttpError(ERROR)
```

请求体按 UTF-8 JSON 发送，成功响应完整写入指定绝对路径；HTTP 失败时不会覆盖已有文件。此方法同步等待，默认超时 60 秒。

## 状态和错误

```erb
CALLSHARP HttpStatus(STATUS)
CALLSHARP HttpResponse(HTTP_CODE)
CALLSHARP HttpError(ERROR)
```

状态码：

- `0`：成功。
- `1`：网络、HTTP 或文件错误。
- `2`：JSON、参数或路径错误。

HTTP 状态码单独通过 `HttpResponse` 读取；网络失败时为 `0`。

## 请求头和 URL 编码

```erb
CALLSHARP HttpHeader("Accept", "application/json")
CALLSHARP HttpHeader("X-Token", TOKEN)
CALLSHARP HttpUrlEncode("hello world?x=1", ENCODED)
CALLSHARP HttpGet("/search?q=" + ENCODED, RESPONSE)
```

请求头会保留并用于后续请求。需要切换会话时重新设置同名请求头即可。

## 与专用插件的区别

- `ChatCallSharp`：AI 对话、JSON 提取、流式对话和响应解析。
- `ImageGenSharp`：ComfyUI 工作流、生图、轮询和图片下载。
- `HttpSharp`：通用 HTTP 请求、Cookie 会话、流式响应、JSON 接口、文件下载和简单响应处理。

HttpSharp 不会自动等待异步任务，不会解析 ComfyUI 工作流，也不会把网络图片直接显示在 Emuera 中。显示图片仍需下载到 `resources`，再使用 `GCREATEFROMFILE`、`SPRITECREATE` 和 `PRINT_IMG`。

## 限制

- 请求是同步的，ERB 会等待 HTTP 请求结束；但等待期间界面保持响应，且不会触发无限循环警告。
- 流式响应由插件内部解析，`output` 仍然只在函数返回时写入完整文本，ERB 侧拿不到逐字中间值。
- 每次请求只保留最近一次响应正文、HTTP 状态码和错误。
- Cookie 只保存在内存中，不写入存档或文件；重启游戏后需要重新登录或重新设置。
- 不提供 Cookie 浏览器、重定向策略、multipart 上传或后台任务。
- 默认超时为 60 秒。
- `HttpGet`、`HttpPost` 等输出是纯文本；JSON 专用函数只增加 JSON 合法性验证。
- `HttpJsonPath` 当前只支持对象属性，不支持数组下标；复杂 JSON 建议使用已有 JSON 工具或服务端提供专用字段。
