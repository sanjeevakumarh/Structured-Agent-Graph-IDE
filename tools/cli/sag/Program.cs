using System.CommandLine;
using System.Net.Http.Json;
using System.Text.Json;

// SAG CLI: thin REST client for the SAGIDE backend (http://localhost:5100)
// Commands: health | submit | results | prompts | status | cancel

var baseUrlOption = new Option<string>(
    new[] { "--base-url", "-u" },
    getDefaultValue: () => "http://localhost:5100",
    description: "SAG service base URL");

var root = new RootCommand("SAG CLI - submit tasks and query results from the SAGIDE backend");
root.AddGlobalOption(baseUrlOption);

// ── health ─────────────────────────────────────────────────────────────────────
var healthCmd = new Command("health", "Check service liveness");
healthCmd.SetHandler(async (string url) =>
{
    try
    {
        var resp = await MakeClient(url).GetAsync("/api/health");
        Console.WriteLine(resp.IsSuccessStatusCode
            ? "[ok] " + await resp.Content.ReadAsStringAsync()
            : $"[error] HTTP {(int)resp.StatusCode}");
        if (!resp.IsSuccessStatusCode) Environment.Exit(1);
    }
    catch (Exception ex) { Console.Error.WriteLine("[error] " + ex.Message); Environment.Exit(1); }
}, baseUrlOption);
root.AddCommand(healthCmd);

// ── submit ─────────────────────────────────────────────────────────────────────
var submitPromptOpt = new Option<string>("--prompt", "Prompt key as domain/name") { IsRequired = true };
var submitVarOpt    = new Option<string[]>("--var", "key=value variable override (repeatable)")
    { Arity = ArgumentArity.ZeroOrMore };
var submitCmd = new Command("submit", "Run a prompt from the registry");
submitCmd.AddOption(submitPromptOpt);
submitCmd.AddOption(submitVarOpt);
submitCmd.SetHandler(async (string url, string prompt, string[] vars) =>
{
    var parts = prompt.Split('/', 2);
    if (parts.Length != 2)
    {
        Console.Error.WriteLine("[error] --prompt must be 'domain/name'");
        Environment.Exit(1); return;
    }

    var variables = new Dictionary<string, string>();
    foreach (var v in vars)
    {
        var eq = v.IndexOf('=');
        if (eq > 0) variables[v[..eq]] = v[(eq + 1)..];
        else Console.Error.WriteLine($"[warn] ignoring malformed --var '{v}' (expected key=value)");
    }

    try
    {
        var body = variables.Count > 0 ? (object)variables : new { };
        var resp = await MakeClient(url).PostAsync(
            $"/api/prompts/{parts[0]}/{parts[1]}/run",
            JsonContent.Create(body));
        var text = await resp.Content.ReadAsStringAsync();
        Console.WriteLine(PrettyJson(text));
        if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 202) Environment.Exit(1);
    }
    catch (Exception ex) { Console.Error.WriteLine("[error] " + ex.Message); Environment.Exit(1); }
}, baseUrlOption, submitPromptOpt, submitVarOpt);
root.AddCommand(submitCmd);

// ── results ────────────────────────────────────────────────────────────────────
var tagOpt   = new Option<string?>("--tag",   "Filter by source tag");
var sinceOpt = new Option<string?>("--since", "Filter since ISO date or 'today'");
var limitOpt = new Option<int>("--limit", getDefaultValue: () => 20, description: "Max results");

var resultsCmd = new Command("results", "List task results");
resultsCmd.AddOption(tagOpt);
resultsCmd.AddOption(sinceOpt);
resultsCmd.AddOption(limitOpt);
resultsCmd.SetHandler(async (string url, string? tag, string? since, int limit) =>
{
    var qs = $"limit={limit}";
    if (!string.IsNullOrEmpty(tag))   qs += $"&tag={Uri.EscapeDataString(tag)}";
    if (!string.IsNullOrEmpty(since)) qs += $"&since={Uri.EscapeDataString(NormalizeSince(since))}";
    try
    {
        var resp = await MakeClient(url).GetAsync($"/api/results?{qs}");
        var body = await resp.Content.ReadAsStringAsync();
        if (resp.IsSuccessStatusCode) Console.WriteLine(PrettyJson(body));
        else { Console.Error.WriteLine($"[error] HTTP {(int)resp.StatusCode}: {body}"); Environment.Exit(1); }
    }
    catch (Exception ex) { Console.Error.WriteLine("[error] " + ex.Message); Environment.Exit(1); }
}, baseUrlOption, tagOpt, sinceOpt, limitOpt);
root.AddCommand(resultsCmd);

// ── prompts ────────────────────────────────────────────────────────────────────
var domainArg  = new Argument<string?>("domain", () => null, "Optional domain filter (e.g. 'finance')");
var promptsCmd = new Command("prompts", "List available prompts");
promptsCmd.AddArgument(domainArg);
promptsCmd.SetHandler(async (string url, string? domain) =>
{
    var path = string.IsNullOrEmpty(domain) ? "/api/prompts" : $"/api/prompts/{domain}";
    try
    {
        var resp = await MakeClient(url).GetAsync(path);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"[error] HTTP {(int)resp.StatusCode}");
            Environment.Exit(1); return;
        }
        var opt   = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var items = JsonSerializer.Deserialize<JsonElement[]>(body, opt);
        if (items is null || items.Length == 0) { Console.WriteLine("(no prompts)"); return; }

        Console.WriteLine($"{"DOMAIN",-12} {"NAME",-30} {"SCHEDULE",-18} SUBTASKS");
        Console.WriteLine(new string('-', 72));
        foreach (var item in items)
        {
            var hasSubs = item.TryGetProperty("hasSubtasks", out var hv) && hv.GetBoolean() ? "yes" : "no";
            Console.WriteLine(
                $"{Str(item, "domain"),-12} {Str(item, "name"),-30} {Str(item, "schedule") ?? "-",-18} {hasSubs}");
        }
    }
    catch (Exception ex) { Console.Error.WriteLine("[error] " + ex.Message); Environment.Exit(1); }
}, baseUrlOption, domainArg);
root.AddCommand(promptsCmd);

// ── status ─────────────────────────────────────────────────────────────────────
var taskIdArg = new Argument<string?>("task-id", () => null, "Task ID (omit to list recent tasks)");
var statusCmd = new Command("status", "Get task status");
statusCmd.AddArgument(taskIdArg);
statusCmd.SetHandler(async (string url, string? taskId) =>
{
    var path = string.IsNullOrEmpty(taskId) ? "/api/tasks?limit=20" : $"/api/tasks/{taskId}";
    try
    {
        var resp = await MakeClient(url).GetAsync(path);
        var body = await resp.Content.ReadAsStringAsync();
        if (resp.IsSuccessStatusCode)
            Console.WriteLine(PrettyJson(body));
        else
            Console.Error.WriteLine($"[{((int)resp.StatusCode == 404 ? "not found" : "error")}] {body}");
        if (!resp.IsSuccessStatusCode) Environment.Exit(1);
    }
    catch (Exception ex) { Console.Error.WriteLine("[error] " + ex.Message); Environment.Exit(1); }
}, baseUrlOption, taskIdArg);
root.AddCommand(statusCmd);

// ── cancel ─────────────────────────────────────────────────────────────────────
var cancelIdArg = new Argument<string>("task-id", "Task ID to cancel");
var cancelCmd   = new Command("cancel", "Cancel a running or queued task");
cancelCmd.AddArgument(cancelIdArg);
cancelCmd.SetHandler(async (string url, string taskId) =>
{
    try
    {
        var resp = await MakeClient(url).DeleteAsync($"/api/tasks/{taskId}");
        var body = await resp.Content.ReadAsStringAsync();
        Console.WriteLine(resp.IsSuccessStatusCode ? $"[ok] Cancelled {taskId}" : $"[error] {body}");
        if (!resp.IsSuccessStatusCode) Environment.Exit(1);
    }
    catch (Exception ex) { Console.Error.WriteLine("[error] " + ex.Message); Environment.Exit(1); }
}, baseUrlOption, cancelIdArg);
root.AddCommand(cancelCmd);

// ── reports ────────────────────────────────────────────────────────────────────
var reportsDomainArg = new Argument<string?>("domain", () => null, "Domain filter (e.g. 'finance'). Omit to list all domains.");
var reportsFileArg   = new Argument<string?>("file",   () => null, "Filename within domain. Omit to list files.");
var reportsHtml      = new Option<bool>("--html", "Render as HTML (default: plain text)");
var reportsCmd       = new Command("reports", "Browse generated reports");
reportsCmd.AddArgument(reportsDomainArg);
reportsCmd.AddArgument(reportsFileArg);
reportsCmd.AddOption(reportsHtml);
reportsCmd.SetHandler(async (string url, string? domain, string? file, bool html) =>
{
    try
    {
        string path;
        if (string.IsNullOrEmpty(domain))
            path = "/api/reports";
        else if (string.IsNullOrEmpty(file))
            path = $"/api/reports/{Uri.EscapeDataString(domain)}";
        else
        {
            var fmt = html ? "?format=html" : string.Empty;
            path = $"/api/reports/{Uri.EscapeDataString(domain)}/{Uri.EscapeDataString(file)}{fmt}";
        }

        var resp = await MakeClient(url).GetAsync(path);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"[error] HTTP {(int)resp.StatusCode}: {body}");
            Environment.Exit(1); return;
        }

        // File content: print as-is (plain text or HTML)
        if (!string.IsNullOrEmpty(domain) && !string.IsNullOrEmpty(file))
        {
            Console.WriteLine(body);
        }
        else
        {
            Console.WriteLine(PrettyJson(body));
        }
    }
    catch (Exception ex) { Console.Error.WriteLine("[error] " + ex.Message); Environment.Exit(1); }
}, baseUrlOption, reportsDomainArg, reportsFileArg, reportsHtml);
root.AddCommand(reportsCmd);

return await root.InvokeAsync(args);

// ── Helpers ────────────────────────────────────────────────────────────────────

static HttpClient MakeClient(string baseUrl) => new() { BaseAddress = new Uri(baseUrl) };

static string PrettyJson(string json)
{
    try
    {
        return JsonSerializer.Serialize(
            JsonDocument.Parse(json),
            new JsonSerializerOptions { WriteIndented = true });
    }
    catch { return json; }
}

static string NormalizeSince(string? since) =>
    since?.Equals("today", StringComparison.OrdinalIgnoreCase) == true
        ? DateTime.UtcNow.Date.ToString("O")
        : since ?? string.Empty;

static string? Str(JsonElement el, string prop) =>
    el.TryGetProperty(prop, out var v) ? v.GetString() : null;
