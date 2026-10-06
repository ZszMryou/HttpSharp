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
        methods.Add(new HttpStatusMethod());
        methods.Add(new HttpResponseMethod());
        methods.Add(new HttpErrorMethod());
        methods.Add(new HttpJsonGetMethod());
        methods.Add(new HttpJsonPostMethod());
        methods.Add(new HttpJsonPathMethod());
        methods.Add(new HttpUrlEncodeMethod());
    }

    public override string PluginName => "HttpSharp";
    public override string PluginDescription => "面向 ERB 的 requests 风格 HTTP 工具：配置会话、请求、读取响应、JSON 和下载文件。详细说明见 HttpSharp/README.md。";
    public override string PluginVersion => "1.0";
    public override string PluginAuthor => "zsz";
}

internal static class HttpState
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static string baseUrl = "";
    private static string lastBody = "";
    private static string lastError = "";
    private static long lastStatus;
    private static long lastHttpStatus;
    private static readonly Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);

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

    public static string Request(HttpMethod method, string endpoint, string body = "", string contentType = "application/json")
    {
        try
        {
            string url = Resolve(endpoint);
            using var request = new HttpRequestMessage(method, url);
            foreach (KeyValuePair<string, string> header in Headers)
            {
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    request.Content ??= new StringContent("");
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            if (method != HttpMethod.Get && method != HttpMethod.Delete)
                request.Content = new StringContent(body ?? "", Encoding.UTF8, contentType);
            using HttpResponseMessage response = Client.Send(request);
            lastHttpStatus = (long)response.StatusCode;
            lastBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
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
            byte[] data = Client.GetByteArrayAsync(Resolve(endpoint)).GetAwaiter().GetResult();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, data);
            SetResult(0);
        }
        catch (Exception ex) { SetResult(1, ex.Message); }
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
