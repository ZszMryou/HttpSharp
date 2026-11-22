using MinorShift.Emuera.Runtime.Utils.PluginSystem;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HttpSharp;

public class PluginManifest : PluginManifestAbstract
{
    public PluginManifest()
    {
        methods.Add(new HttpSetMethod());
        methods.Add(new HttpHeaderMethod());
        methods.Add(new HttpGetMethod());
        methods.Add(new HttpPostMethod());
        methods.Add(new HttpPutMethod());
        methods.Add(new HttpDeleteMethod());
        methods.Add(new HttpDownloadMethod());
        methods.Add(new HttpPostDownloadMethod());
        methods.Add(new HttpPostStreamMethod());
        methods.Add(new HttpGetStreamMethod());
        methods.Add(new HttpStreamPrintMethod());
        methods.Add(new HttpCookieSetMethod());
        methods.Add(new HttpCookieGetMethod());
        methods.Add(new HttpCookieListMethod());
        methods.Add(new HttpCookieClearMethod());
        methods.Add(new HttpStatusMethod());
        methods.Add(new HttpResponseMethod());
        methods.Add(new HttpErrorMethod());
        methods.Add(new HttpJsonGetMethod());
        methods.Add(new HttpJsonPostMethod());
        methods.Add(new HttpJsonPathMethod());
        methods.Add(new HttpUrlEncodeMethod());
    }

    public override string PluginName => "HttpSharp";
    public override string PluginDescription => "面向 ERB 的 requests 风格 HTTP 工具：配置会话、Cookie、请求、流式响应、JSON 和下载文件。详细说明见 HttpSharp/README.md。";
    public override string PluginVersion => "1.1";
    public override string PluginAuthor => "zsz";
}

internal static class HttpState
{
    // 共享的 Cookie 容器：由 HttpClientHandler 自动收发 Set-Cookie
    private static readonly CookieContainer Cookies = new();
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        CookieContainer = Cookies,
        UseCookies = true
    })
    { Timeout = TimeSpan.FromSeconds(60) };

    private static string baseUrl = "";
    private static string lastBody = "";
    private static string lastError = "";
    private static long lastStatus;
    private static long lastHttpStatus;
    private static string lastUrl = "";
    private static bool streamPrint = true;
    private static readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);

    private const int PumpIntervalMs = 50;

    public static long Status => lastStatus;
    public static long HttpStatus => lastHttpStatus;
    public static string Body => lastBody;
    public static string Error => lastError;

    public static void SetResult(long status, string error = "")
    {
        lastStatus = status;
        lastError = error;
    }

    public static void Configure(string url)
    {
        baseUrl = url?.Trim().TrimEnd('/') ?? "";
        SetResult(0);
    }

    public static void SetHeader(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name)) { SetResult(2, "请求头名称不能为空"); return; }
        Headers[name.Trim()] = value ?? "";
        SetResult(0);
    }

    public static void SetStreamPrint(bool on)
    {
        streamPrint = on;
        SetResult(0);
    }

    public static string Request(HttpMethod method, string endpoint, string body = "", string contentType = "application/json")
    {
        try
        {
            string url = Resolve(endpoint);
            lastUrl = url;
            using var request = new HttpRequestMessage(method, url);
            if (method != HttpMethod.Get && method != HttpMethod.Delete)
                request.Content = new StringContent(body ?? "", Encoding.UTF8, contentType);
            ApplyHeaders(request);
            using HttpResponseMessage response = PumpUntil(Client.SendAsync(request, HttpCompletionOption.ResponseContentRead));
            lastHttpStatus = (long)response.StatusCode;
            lastBody = PumpUntil(response.Content.ReadAsStringAsync());
            if (!response.IsSuccessStatusCode)
            {
                SetResult(1, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {lastBody}");
                return "";
            }
            SetResult(0);
            return lastBody;
        }
        catch (Exception ex)
        {
            lastHttpStatus = 0;
            lastBody = "";
            SetResult(1, ex.Message);
            return "";
        }
    }

    public static void Download(string endpoint, string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("保存路径必须是绝对路径");
            string url = Resolve(endpoint);
            lastUrl = url;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyHeaders(request);
            using HttpResponseMessage response = PumpUntil(Client.SendAsync(request, HttpCompletionOption.ResponseContentRead));
            lastHttpStatus = (long)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                string err = PumpUntil(response.Content.ReadAsStringAsync());
                SetResult(1, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {err}");
                return;
            }
            byte[] data = PumpUntil(response.Content.ReadAsByteArrayAsync());
            WriteFile(path, data);
            lastBody = "";
            SetResult(0);
        }
        catch (Exception ex) { SetResult(1, ex.Message); }
    }

    public static void PostDownload(string endpoint, string body, string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("保存路径必须是绝对路径");
            string url = Resolve(endpoint);
            lastUrl = url;
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(body ?? "", Encoding.UTF8, "application/json");
            ApplyHeaders(request);
            using HttpResponseMessage response = PumpUntil(Client.SendAsync(request, HttpCompletionOption.ResponseContentRead));
            lastHttpStatus = (long)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                lastBody = PumpUntil(response.Content.ReadAsStringAsync());
                SetResult(1, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {lastBody}");
                return;
            }
            byte[] data = PumpUntil(response.Content.ReadAsByteArrayAsync());
            WriteFile(path, data);
            lastBody = "";
            SetResult(0);
        }
        catch (Exception ex)
        {
            lastHttpStatus = 0;
            lastBody = "";
            SetResult(1, ex.Message);
        }
    }

    // 流式请求：后台线程读 SSE 的 data: 行，主线程泵引擎并实时打印。
    // 服务器若忽略 stream 直接返回普通文本/JSON，会在结束后整体返回。
    public static string Stream(HttpMethod method, string endpoint, string body)
    {
        var state = new StreamState();
        string url;
        try { url = Resolve(endpoint); }
        catch (Exception ex) { lastHttpStatus = 0; lastBody = ""; SetResult(1, ex.Message); return ""; }
        lastUrl = url;

        _ = Task.Run(() =>
        {
            try
            {
                using var request = new HttpRequestMessage(method, url);
                if (method != HttpMethod.Get && method != HttpMethod.Delete)
                    request.Content = new StringContent(body ?? "", Encoding.UTF8, "application/json");
                ApplyHeaders(request);
                using HttpResponseMessage response = Client.Send(request, HttpCompletionOption.ResponseHeadersRead);
                lastHttpStatus = (long)response.StatusCode;
                if (!response.IsSuccessStatusCode)
                {
                    string err = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    lock (state.Sync)
                    {
                        state.Error = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {err}";
                        state.Done = true;
                    }
                    return;
                }

                using var stream = response.Content.ReadAsStream();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        lock (state.Sync) state.Raw.Append(line).Append('\n');
                        continue;
                    }

                    string data = line.Substring(5).Trim();
                    lock (state.Sync)
                    {
                        state.SawSse = true;
                        state.DataLines++;
                        if (state.FirstLine.Length == 0 && data.Length > 0)
                            state.FirstLine = data.Length > 160 ? data.Substring(0, 160) : data;
                    }
                    if (data.Length == 0 || data == "[DONE]")
                        continue;

                    string text = ExtractStreamText(data);
                    if (text.Length > 0)
                        lock (state.Sync) state.Text.Append(text);
                }
            }
            catch (Exception ex)
            {
                lock (state.Sync) state.Error = ex.Message;
            }
            finally
            {
                lock (state.Sync) state.Done = true;
            }
        });

        PluginManager manager = PluginManager.GetInstance();
        while (true)
        {
            bool done;
            lock (state.Sync) done = state.Done;
            if (done) break;
            if (streamPrint) EmitPending(state);
            try { manager.Await(PumpIntervalMs); }
            catch { Thread.Sleep(PumpIntervalMs); }
        }
        if (streamPrint) EmitPending(state);

        string error, streamed, raw, firstLine;
        bool sawSse;
        int dataLines;
        lock (state.Sync)
        {
            error = state.Error;
            streamed = state.Text.ToString();
            raw = state.Raw.ToString();
            sawSse = state.SawSse;
            dataLines = state.DataLines;
            firstLine = state.FirstLine;
        }

        if (error != "")
        {
            lastBody = "";
            SetResult(1, error);
            return "";
        }

        if (!sawSse)
        {
            lastBody = raw.Trim();
            if (lastBody.Length == 0) { SetResult(1, "响应为空"); return ""; }
            SetResult(0);
            return lastBody;
        }

        if (streamed.Length == 0)
        {
            lastBody = "";
            SetResult(1, $"流式响应中没有找到文本（data行={dataLines} 首行={firstLine}）");
            return "";
        }

        lastBody = streamed;
        SetResult(0);
        return streamed;
    }

    private sealed class StreamState
    {
        public readonly object Sync = new();
        public readonly StringBuilder Text = new();
        public readonly StringBuilder Raw = new();
        public int Emitted;
        public int DataLines;
        public string FirstLine = "";
        public bool SawSse;
        public bool Done;
        public string Error = "";
    }

    private static void EmitPending(StreamState state)
    {
        string chunk;
        lock (state.Sync)
        {
            if (state.Text.Length <= state.Emitted) return;
            chunk = state.Text.ToString(state.Emitted, state.Text.Length - state.Emitted);
            state.Emitted = state.Text.Length;
        }
        PrintDelta(chunk);
    }

    // 从一帧 data: 里尽可能通用地取出文本；不是 JSON 时原样返回。
    private static string ExtractStreamText(string data)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(data); }
        catch { return data; }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return "";

            if (root.TryGetProperty("choices", out JsonElement choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                JsonElement choice = choices[0];
                if (choice.TryGetProperty("delta", out JsonElement delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    if (delta.TryGetProperty("content", out JsonElement dc) && dc.ValueKind == JsonValueKind.String)
                        return dc.GetString() ?? "";
                    if (delta.TryGetProperty("text", out JsonElement dt) && dt.ValueKind == JsonValueKind.String)
                        return dt.GetString() ?? "";
                }
                if (choice.TryGetProperty("text", out JsonElement ct) && ct.ValueKind == JsonValueKind.String)
                    return ct.GetString() ?? "";
                if (choice.TryGetProperty("message", out JsonElement cm) && cm.ValueKind == JsonValueKind.Object
                    && cm.TryGetProperty("content", out JsonElement cmc) && cmc.ValueKind == JsonValueKind.String)
                    return cmc.GetString() ?? "";
            }

            // Anthropic 风格：{"type":"content_block_delta","delta":{"text":"..."}}
            if (root.TryGetProperty("delta", out JsonElement rootDelta) && rootDelta.ValueKind == JsonValueKind.Object
                && rootDelta.TryGetProperty("text", out JsonElement rootText) && rootText.ValueKind == JsonValueKind.String)
                return rootText.GetString() ?? "";

            foreach (string key in new[] { "content", "response", "text", "message" })
            {
                if (root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? "";
            }
        }
        return "";
    }

    private static void PrintDelta(string chunk)
    {
        try
        {
            PluginManager manager = PluginManager.GetInstance();
            string[] parts = chunk.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) manager.PrintNewLine();
                if (parts[i].Length > 0) manager.PrintPlain(parts[i]);
            }
            manager.FlushConsole(true);
        }
        catch
        {
            // 界面不可用时忽略实时显示，完整文本仍会返回
        }
    }

    // ---- Cookie ----

    public static void SetCookie(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name)) { SetResult(2, "Cookie 名称不能为空"); return; }
        if (!TryGetCookieUri(out Uri? uri))
        {
            SetResult(2, "还没有设置基础地址或发起过请求，无法确定 Cookie 所属域");
            return;
        }
        Cookies.SetCookies(uri, $"{name.Trim()}={value ?? ""}");
        SetResult(0);
    }

    public static string GetCookie(string name)
    {
        if (!TryGetCookieUri(out Uri? uri)) return "";
        foreach (Cookie cookie in Cookies.GetCookies(uri))
            if (string.Equals(cookie.Name, name, StringComparison.OrdinalIgnoreCase))
                return cookie.Value;
        return "";
    }

    public static string ListCookies()
    {
        if (!TryGetCookieUri(out Uri? uri)) return "";
        var builder = new StringBuilder();
        foreach (Cookie cookie in Cookies.GetCookies(uri))
        {
            if (builder.Length > 0) builder.Append("; ");
            builder.Append(cookie.Name).Append('=').Append(cookie.Value);
        }
        return builder.ToString();
    }

    public static void ClearCookies()
    {
        foreach (Cookie cookie in Cookies.GetAllCookies())
        {
            string scheme = cookie.Secure ? "https" : "http";
            string path = string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path;
            if (!Uri.TryCreate($"{scheme}://{cookie.Domain.TrimStart('.')}{path}", UriKind.Absolute, out Uri? uri))
                continue;
            Cookies.SetCookies(uri, $"{cookie.Name}=; path={path}; expires=Thu, 01 Jan 1970 00:00:00 GMT");
        }
        SetResult(0);
    }

    private static bool TryGetCookieUri(out Uri? uri)
    {
        string target = string.IsNullOrWhiteSpace(baseUrl) ? lastUrl : baseUrl;
        return Uri.TryCreate(target, UriKind.Absolute, out uri);
    }

    // ---- 公共 ----

    private static void ApplyHeaders(HttpRequestMessage request)
    {
        foreach (KeyValuePair<string, string> header in Headers)
        {
            if (request.Headers.TryAddWithoutValidation(header.Key, header.Value)) continue;
            request.Content ??= new StringContent("");
            request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private static void WriteFile(string path, byte[] data)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, data);
    }

    // 等待后台请求完成的同时，用 ERB 的 WAIT 指令同款原语 Await 重置引擎的无限循环计时器并泵消息。
    private static T PumpUntil<T>(Task<T> task)
    {
        if (!task.IsCompleted)
        {
            PluginManager manager = PluginManager.GetInstance();
            while (!task.IsCompleted)
            {
                try { manager.Await(PumpIntervalMs); }
                catch { Thread.Sleep(PumpIntervalMs); }
            }
        }
        return task.GetAwaiter().GetResult();
    }

    private static string Resolve(string endpoint)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? absolute)) return absolute.ToString();
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new InvalidOperationException("未设置基础地址，且 endpoint 不是绝对 URL");
        return baseUrl + "/" + (endpoint ?? "").TrimStart('/');
    }
}

public class HttpSetMethod : IPluginMethod
{
    public string Name => "HttpSet";
    public string Description => "设置基础 URL：HttpSet(baseUrl)";
    public void Execute(PluginMethodParameter[] args) { if (args.Length < 1) HttpState.SetResult(2, "缺少 URL"); else HttpState.Configure(args[0].strValue); }
}
public class HttpHeaderMethod : IPluginMethod
{
    public string Name => "HttpHeader";
    public string Description => "设置默认请求头：HttpHeader(name, value)";
    public void Execute(PluginMethodParameter[] args) { if (args.Length < 2) HttpState.SetResult(2, "缺少请求头参数"); else HttpState.SetHeader(args[0].strValue, args[1].strValue); }
}
public abstract class RequestMethod : IPluginMethod
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    protected abstract HttpMethod Method { get; }
    public void Execute(PluginMethodParameter[] args)
    {
        if (args.Length < 2) return;
        string body = args.Length >= 3 ? args[1].strValue ?? "" : "";
        PluginMethodParameter output = args.Length >= 3 ? args[2] : args[1];
        output.strValue = HttpState.Request(Method, args[0].strValue ?? "", body);
    }
}
public class HttpGetMethod : RequestMethod { public override string Name => "HttpGet"; public override string Description => "GET 请求：HttpGet(url, output)"; protected override HttpMethod Method => HttpMethod.Get; }
public class HttpPostMethod : RequestMethod { public override string Name => "HttpPost"; public override string Description => "POST 请求：HttpPost(url, body, output)"; protected override HttpMethod Method => HttpMethod.Post; }
public class HttpPutMethod : RequestMethod { public override string Name => "HttpPut"; public override string Description => "PUT 请求：HttpPut(url, body, output)"; protected override HttpMethod Method => HttpMethod.Put; }
public class HttpDeleteMethod : RequestMethod { public override string Name => "HttpDelete"; public override string Description => "DELETE 请求：HttpDelete(url, output)"; protected override HttpMethod Method => HttpMethod.Delete; }
public class HttpDownloadMethod : IPluginMethod
{
    public string Name => "HttpDownload";
    public string Description => "下载到绝对路径：HttpDownload(url, absolutePath)";
    public void Execute(PluginMethodParameter[] args) { if (args.Length >= 2) HttpState.Download(args[0].strValue ?? "", args[1].strValue ?? ""); }
}
public class HttpPostDownloadMethod : IPluginMethod
{
    public string Name => "HttpPostDownload";
    public string Description => "POST JSON 并保存二进制响应：HttpPostDownload(url, json, absolutePath)";
    public void Execute(PluginMethodParameter[] args)
    {
        if (args.Length < 3) { HttpState.SetResult(2, "缺少 URL、JSON 或保存路径"); return; }
        HttpState.PostDownload(args[0].strValue ?? "", args[1].strValue ?? "", args[2].strValue ?? "");
    }
}
public class HttpPostStreamMethod : IPluginMethod
{
    public string Name => "HttpPostStream";
    public string Description => "POST 并读取 SSE 流式响应，边收边显示：HttpPostStream(url, json, output)";
    public void Execute(PluginMethodParameter[] args)
    {
        if (args.Length < 3) { HttpState.SetResult(2, "缺少 URL、JSON 或输出变量"); return; }
        args[2].strValue = HttpState.Stream(HttpMethod.Post, args[0].strValue ?? "", args[1].strValue ?? "");
    }
}
public class HttpGetStreamMethod : IPluginMethod
{
    public string Name => "HttpGetStream";
    public string Description => "GET 并读取 SSE 流式响应，边收边显示：HttpGetStream(url, output)";
    public void Execute(PluginMethodParameter[] args)
    {
        if (args.Length < 2) { HttpState.SetResult(2, "缺少 URL 或输出变量"); return; }
        args[1].strValue = HttpState.Stream(HttpMethod.Get, args[0].strValue ?? "", "");
    }
}
public class HttpStreamPrintMethod : IPluginMethod
{
    public string Name => "HttpStreamPrint";
    public string Description => "开关流式实时打印，默认开启：HttpStreamPrint(on)，0=关 1=开";
    public void Execute(PluginMethodParameter[] args)
    {
        if (args.Length >= 1) HttpState.SetStreamPrint(args[0].intValue != 0);
    }
}
public class HttpCookieSetMethod : IPluginMethod
{
    public string Name => "HttpCookieSet";
    public string Description => "设置 Cookie：HttpCookieSet(name, value)";
    public void Execute(PluginMethodParameter[] args)
    {
        if (args.Length < 2) { HttpState.SetResult(2, "缺少 Cookie 名称或值"); return; }
        HttpState.SetCookie(args[0].strValue ?? "", args[1].strValue ?? "");
    }
}
public class HttpCookieGetMethod : IPluginMethod
{
    public string Name => "HttpCookieGet";
    public string Description => "读取 Cookie：HttpCookieGet(name, output)，不存在时为空";
    public void Execute(PluginMethodParameter[] args)
    {
        if (args.Length < 2) return;
        args[1].strValue = HttpState.GetCookie(args[0].strValue ?? "");
    }
}
public class HttpCookieListMethod : IPluginMethod
{
    public string Name => "HttpCookieList";
    public string Description => "列出全部 Cookie：HttpCookieList(output)，格式 name=value; name2=value2";
    public void Execute(PluginMethodParameter[] args)
    {
        if (args.Length >= 1) args[0].strValue = HttpState.ListCookies();
    }
}
public class HttpCookieClearMethod : IPluginMethod
{
    public string Name => "HttpCookieClear";
    public string Description => "清空所有 Cookie：HttpCookieClear()";
    public void Execute(PluginMethodParameter[] args) { HttpState.ClearCookies(); }
}
public class HttpStatusMethod : IPluginMethod
{
    public string Name => "HttpStatus";
    public string Description => "读取插件状态码：HttpStatus(output)";
    public void Execute(PluginMethodParameter[] args) { if (args.Length >= 1) args[0].intValue = HttpState.Status; }
}
public class HttpResponseMethod : IPluginMethod
{
    public string Name => "HttpResponse";
    public string Description => "读取 HTTP 状态码：HttpResponse(output)";
    public void Execute(PluginMethodParameter[] args) { if (args.Length >= 1) args[0].intValue = HttpState.HttpStatus; }
}
public class HttpErrorMethod : IPluginMethod
{
    public string Name => "HttpError";
    public string Description => "读取错误信息：HttpError(output)";
    public void Execute(PluginMethodParameter[] args) { if (args.Length >= 1) args[0].strValue = HttpState.Error; }
}
public class HttpJsonGetMethod : IPluginMethod
{
    public string Name => "HttpJsonGet";
    public string Description => "GET 并验证 JSON：HttpJsonGet(url, output)";
    public void Execute(PluginMethodParameter[] args)
    {
        if (args.Length < 2) return;
        string body = HttpState.Request(HttpMethod.Get, args[0].strValue ?? "");
        if (HttpState.Status == 0) try { using JsonDocument document = JsonDocument.Parse(body); } catch (Exception ex) { HttpState.SetResult(2, ex.Message); body = ""; }
        args[1].strValue = body;
    }
}
public class HttpJsonPostMethod : IPluginMethod
{
    public string Name => "HttpJsonPost";
    public string Description => "POST JSON 并验证 JSON：HttpJsonPost(url, json, output)";
    public void Execute(PluginMethodParameter[] args)
    {
        if (args.Length < 3) return;
        try { using JsonDocument document = JsonDocument.Parse(args[1].strValue ?? ""); }
        catch (Exception ex) { HttpState.SetResult(2, ex.Message); args[2].strValue = ""; return; }
        args[2].strValue = HttpState.Request(HttpMethod.Post, args[0].strValue ?? "", args[1].strValue ?? "");
    }
}
public class HttpJsonPathMethod : IPluginMethod
{
    public string Name => "HttpJsonPath";
    public string Description => "从最近响应 JSON 读取简单路径：HttpJsonPath(path, output)";
    public void Execute(PluginMethodParameter[] args)
    {
        if (args.Length < 2) return;
        try
        {
            using JsonDocument document = JsonDocument.Parse(HttpState.Body);
            JsonElement current = document.RootElement;
            foreach (string part in (args[0].strValue ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries))
                if (!current.TryGetProperty(part, out current)) throw new KeyNotFoundException(part);
            args[1].strValue = current.ValueKind == JsonValueKind.String ? current.GetString() ?? "" : current.ToString();
            HttpState.SetResult(0);
        }
        catch (Exception ex) { HttpState.SetResult(2, ex.Message); args[1].strValue = ""; }
    }
}
public class HttpUrlEncodeMethod : IPluginMethod
{
    public string Name => "HttpUrlEncode";
    public string Description => "URL 编码：HttpUrlEncode(text, output)";
    public void Execute(PluginMethodParameter[] args) { if (args.Length >= 2) args[1].strValue = Uri.EscapeDataString(args[0].strValue ?? ""); }
}
