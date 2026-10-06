# HttpSharp

`HttpSharp` 是面向 ERB 的通用 HTTP 插件，使用方式接近 Python `requests`，但受 `CALLSHARP` 的参数限制：所有响应都必须写回字符串或整数变量。

## 快速开始

将bin/Debug/net8.0-windows/HttpSharp.dll放入".\你的era的游戏目录\plugins"即可使用 

本插件更多面向于想在口上或其它玩法功能上接入ai的创作者

本插件附带readme（就是这个文件，，，） vibe coding时可以将本文件发送给ai

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
CALLSHARP HttpPost("/echo", "{""message"":""hello""}", RESPONSE)
```

## JSON 请求

```erb
CALLSHARP HttpJsonGet("/api/data", JSON)
CALLSHARP HttpJsonPost("/api/data", "{""name"":""Alice""}", JSON)
CALLSHARP HttpJsonPath("data.name", NAME)
```

`HttpJsonGet`、`HttpJsonPost` 会先验证 JSON；`HttpJsonPath` 读取最近一次 HTTP 响应中的简单对象路径，例如 `data.user.name`。不支持完整 JSONPath、数组过滤或表达式。

## 文件下载

```erb
CALLSHARP HttpDownload("https://example.com/a.png", "C:\\Game\\resources\\a.png")
```

保存路径必须是绝对路径，父目录会自动创建。插件只下载 HTTP/HTTPS 内容，不执行下载内容。

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

- `ChatCallSharp`：AI 对话、JSON 提取和响应解析。
- `ImageGenSharp`：ComfyUI 工作流、生图、轮询和图片下载。
- `HttpSharp`：通用 HTTP 请求、JSON 接口、文件下载和简单响应处理。

HttpSharp 不会自动等待异步任务，不会解析 ComfyUI 工作流，也不会把网络图片直接显示在 Emuera 中。显示图片仍需下载到 `resources`，再使用 `GCREATEFROMFILE`、`SPRITECREATE` 和 `PRINT_IMG`。

## 限制

- 请求是同步的，ERB 会等待 HTTP 请求结束。
- 每次请求只保留最近一次响应正文、HTTP 状态码和错误。
- 不提供 Cookie 浏览器、重定向策略、multipart 上传、流式响应或后台请求。
- 默认超时为 60 秒。
- `HttpGet`、`HttpPost` 等输出是纯文本；JSON 专用函数只增加 JSON 合法性验证。
- `HttpJsonPath` 当前只支持对象属性，不支持数组下标；复杂 JSON 建议使用已有 JSON 工具或服务端提供专用字段。
