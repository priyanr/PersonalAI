using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.DTOs;
using PersonalAI.Core.Services.Tools;
using PersonalAI.Core.Services.Tools.Gmail;
using Microsoft.Extensions.DependencyInjection;

namespace PersonalAI.Core.Tools;

/// <summary>Read-only Gmail access (list/read messages, mark read/unread, add/remove labels — never send or delete),
/// gated on a per-deployment Google OAuth connection made via Settings > Tools. Unlike the workspace
/// file tools, "not connected" is a plain ToolCompleted(IsError: true) — never ToolApprovalRequired,
/// which is reserved for the workspace-sandbox escape-hatch flow (see ToolExecutionResult.cs).
///
/// Takes IServiceScopeFactory rather than IToolConfigRepository directly: this tool lives inside the
/// singleton ToolRegistry, but IToolConfigRepository is Scoped (it wraps the Scoped AppDbContext) —
/// capturing a Scoped instance into a Singleton is a captive-dependency bug that throws at startup
/// under ASP.NET Core's Development-mode scope validation. A short-lived scope is created only when a
/// DB write is actually needed (persisting a refreshed/cleared token); everything else reads synchronously
/// from the singleton ToolConfigCache, same as every other tool.</summary>
public sealed class GmailTool(
    GmailOAuthClient oauthClient, ToolConfigCache configCache,
    IServiceScopeFactory scopeFactory, IHttpClientFactory httpClientFactory) : ITool
{
    public const string HttpClientName = "gmail-tool";
    private const string ApiBase = "https://gmail.googleapis.com/gmail/v1/users/me";
    private static readonly HashSet<string> ValidActions = new(StringComparer.Ordinal)
    {
        "list_messages", "get_message", "mark_read", "set_label"
    };

    public string Name => "gmail";

    public string Description =>
        "Read-only access to the connected Gmail account: list messages, read a message, mark read/unread, " +
        "add/remove a label. list_messages is paginated — use nextPageToken from the response (as pageToken) " +
        "to get more results for the same query instead of narrowing with date ranges. Use unreadOnly to " +
        "restrict to unread mail. Requires the user to connect Gmail in Settings > Tools first.";

    public string ParametersSchemaJson => """
        {
          "type": "object",
          "properties": {
            "action": { "type": "string", "enum": ["list_messages", "get_message", "mark_read", "set_label"], "description": "Which Gmail operation to perform." },
            "query": { "type": "string", "description": "Gmail search query for list_messages, e.g. 'is:unread from:example.com'. Combined with unreadOnly if both are set. Ignored for other actions." },
            "unreadOnly": { "type": "boolean", "description": "For list_messages: restrict to unread messages only (adds 'is:unread' to the query). Prefer this over guessing date ranges to page through mail." },
            "maxResults": { "type": "integer", "description": "Max messages to return for list_messages (default 20, max 50). To see more than one page, pass back the previous response's nextPageToken — do not narrow the query with date ranges to work around the page size." },
            "pageToken": { "type": "string", "description": "For list_messages: the nextPageToken from a previous list_messages call, to fetch the next page of the same query." },
            "messageId": { "type": "string", "description": "Gmail message id, required for get_message, mark_read and set_label." },
            "read": { "type": "boolean", "description": "For mark_read: true to mark the message read, false to mark it unread." },
            "label": { "type": "string", "description": "For set_label: the exact Gmail label name to add or remove, e.g. 'Important' or a custom label. Case-insensitive." },
            "add": { "type": "boolean", "description": "For set_label: true to add the label to the message, false to remove it." }
          },
          "required": ["action"]
        }
        """;

    public IReadOnlyList<ToolSettingField> SettingsFields =>
    [
        new("ClientId", "Google OAuth Client ID", ToolSettingFieldType.Text, Required: true),
        new("ClientSecret", "Google OAuth Client Secret", ToolSettingFieldType.Secret, Required: true),
        new("Connect", "Google account", ToolSettingFieldType.Custom, Required: false, CustomComponentKey: "gmailOAuthConnect")
    ];

    public bool IsConnected(ToolConfigCache cache) => !string.IsNullOrEmpty(cache.GetValue("gmail", "RefreshToken"));

    public async Task<ToolExecutionResult> ExecuteAsync(JsonElement arguments, ToolExecutionContext context, CancellationToken ct)
    {
        var action = arguments.GetStringArg("action");
        if (string.IsNullOrEmpty(action) || !ValidActions.Contains(action))
            return new ToolCompleted($"'action' must be one of: {string.Join(", ", ValidActions)}.", IsError: true);

        var refreshToken = configCache.GetValue("gmail", "RefreshToken");
        if (string.IsNullOrEmpty(refreshToken))
            return new ToolCompleted("Gmail is not connected — connect it in Settings > Tools.", IsError: true);

        string accessToken;
        try
        {
            accessToken = await EnsureAccessTokenAsync(refreshToken, ct);
        }
        catch (GmailOAuthInvalidGrantException)
        {
            var cleared = new Dictionary<string, string?>
            {
                ["RefreshToken"] = null,
                ["AccessToken"] = null,
                ["TokenExpiresAtUtc"] = null
            };
            await PersistToolConfigAsync(cleared, ct);
            return new ToolCompleted("Gmail connection has expired or was revoked — reconnect it in Settings > Tools.", IsError: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ToolCompleted($"Failed to refresh Gmail access token: {ex.Message}", IsError: true);
        }

        var client = httpClientFactory.CreateClient(HttpClientName);

        try
        {
            return action switch
            {
                "list_messages" => await ListMessagesAsync(client, accessToken, arguments, ct),
                "get_message" => await GetMessageAsync(client, accessToken, arguments, ct),
                "mark_read" => await MarkReadAsync(client, accessToken, arguments, ct),
                "set_label" => await SetLabelAsync(client, accessToken, arguments, ct),
                _ => new ToolCompleted($"Unsupported action '{action}'.", IsError: true)
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (GmailApiException ex)
        {
            return new ToolCompleted(ex.Message, IsError: true);
        }
        catch (Exception ex)
        {
            return new ToolCompleted($"Gmail tool failed: {ex.Message}", IsError: true);
        }
    }

    private async Task<string> EnsureAccessTokenAsync(string refreshToken, CancellationToken ct)
    {
        var accessToken = configCache.GetValue("gmail", "AccessToken");
        var expiresAtRaw = configCache.GetValue("gmail", "TokenExpiresAtUtc");
        var expiresAt = DateTime.TryParse(
            expiresAtRaw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTime.MinValue;

        if (!string.IsNullOrEmpty(accessToken) && expiresAt > DateTime.UtcNow.AddSeconds(60))
            return accessToken;

        var refreshed = await oauthClient.RefreshAsync(refreshToken, ct)
            ?? throw new InvalidOperationException("Google did not return a token response while refreshing.");

        var updates = new Dictionary<string, string?>
        {
            ["AccessToken"] = refreshed.AccessToken,
            ["TokenExpiresAtUtc"] = DateTime.UtcNow.AddSeconds(refreshed.ExpiresIn).ToString("o")
        };
        // Google only returns refresh_token on the very first exchange — never overwrite the stored one with null/empty.
        if (!string.IsNullOrEmpty(refreshed.RefreshToken))
            updates["RefreshToken"] = refreshed.RefreshToken;

        await PersistToolConfigAsync(updates, ct);

        return refreshed.AccessToken;
    }

    private async Task PersistToolConfigAsync(IReadOnlyDictionary<string, string?> updates, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IToolConfigRepository>();
        await repo.SetValuesAsync("gmail", updates, ct);
        configCache.MergeTool("gmail", updates);
    }

    private const int MaxSnippetChars = 160;

    private async Task<ToolExecutionResult> ListMessagesAsync(HttpClient client, string accessToken, JsonElement arguments, CancellationToken ct)
    {
        var query = arguments.GetStringArg("query");
        var unreadOnly = GetBoolArg(arguments, "unreadOnly") ?? false;
        if (unreadOnly)
            query = string.IsNullOrWhiteSpace(query) ? "is:unread" : $"is:unread {query}";

        var maxResults = Math.Clamp(arguments.GetIntArg("maxResults") ?? 20, 1, 50);
        var pageToken = arguments.GetStringArg("pageToken");

        var url = $"{ApiBase}/messages?maxResults={maxResults}";
        if (!string.IsNullOrWhiteSpace(query))
            url += $"&q={Uri.EscapeDataString(query)}";
        if (!string.IsNullOrWhiteSpace(pageToken))
            url += $"&pageToken={Uri.EscapeDataString(pageToken)}";

        using var listDoc = await GetJsonAsync(client, url, accessToken, ct);
        var listRoot = listDoc.RootElement;
        var results = new List<object?>();

        if (listRoot.TryGetProperty("messages", out var messagesEl) && messagesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var msgRef in messagesEl.EnumerateArray())
            {
                var id = msgRef.GetProperty("id").GetString()!;
                var metaUrl = $"{ApiBase}/messages/{id}?format=metadata&metadataHeaders=Subject&metadataHeaders=From&metadataHeaders=Date";
                using var metaDoc = await GetJsonAsync(client, metaUrl, accessToken, ct);
                var root = metaDoc.RootElement;

                var headers = root.TryGetProperty("payload", out var payload) && payload.TryGetProperty("headers", out var headersEl)
                    ? headersEl
                    : default;

                results.Add(new
                {
                    id,
                    subject = GetHeader(headers, "Subject"),
                    from = GetHeader(headers, "From"),
                    date = GetHeader(headers, "Date"),
                    snippet = CleanSnippet(root.TryGetProperty("snippet", out var snippetEl) ? snippetEl.GetString() : null),
                    isUnread = root.TryGetProperty("labelIds", out var labelsEl) && labelsEl.ValueKind == JsonValueKind.Array &&
                        labelsEl.EnumerateArray().Any(l => l.GetString() == "UNREAD")
                });
            }
        }

        var nextPageToken = listRoot.TryGetProperty("nextPageToken", out var npt) ? npt.GetString() : null;
        var resultSizeEstimate = listRoot.TryGetProperty("resultSizeEstimate", out var rse) ? rse.GetInt32() : (int?)null;

        // Pagination metadata goes BEFORE messages: history trimming (ToolResultMaxChars) blindly truncates
        // this serialized string from the end, and a full page of messages can itself exceed that cap — if
        // nextPageToken were last, a large page would truncate it away right when it's most needed.
        return new ToolCompleted(JsonSerializer.Serialize(new
        {
            resultSizeEstimate,
            nextPageToken,
            note = nextPageToken is not null
                ? "More messages match this query — pass nextPageToken as pageToken to fetch the next page. Do not re-query with narrower date ranges to get more results."
                : null,
            messages = results
        }));
    }

    /// <summary>Collapses runs of whitespace (marketing mail pads snippets with hundreds of literal/zero-width
    /// spacer characters to defeat clipping in email clients) and caps length, so one bloated snippet can't
    /// eat the whole ToolResultMaxChars budget and starve the rest of the page.</summary>
    // U+034F COMBINING GRAPHEME JOINER is category Mn (not Cf) but is the go-to invisible padding character for
    // several senders seen in practice (LinkedIn, VOX Cinemas); stripping all of Mn would also eat legitimate
    // combining diacritics in non-Latin snippets (e.g. Arabic bank alerts), so it's blocked explicitly instead.
    private static readonly char[] ExplicitInvisibleChars = ['͏'];

    private static string? CleanSnippet(string? snippet)
    {
        if (string.IsNullOrEmpty(snippet)) return snippet;

        // Marketing mail pads snippets with runs of zero-width/format characters (ZWNJ, ZWSP, etc. —
        // Unicode category Cf) as an anti-clipping trick; those aren't whitespace so a plain Split misses them.
        var noFormatChars = new string(snippet.Where(c =>
            CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format &&
            !ExplicitInvisibleChars.Contains(c)).ToArray());
        var collapsed = string.Join(' ', noFormatChars.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= MaxSnippetChars ? collapsed : collapsed[..MaxSnippetChars] + "...";
    }

    private async Task<ToolExecutionResult> GetMessageAsync(HttpClient client, string accessToken, JsonElement arguments, CancellationToken ct)
    {
        var messageId = arguments.GetStringArg("messageId");
        if (string.IsNullOrEmpty(messageId))
            return new ToolCompleted("'messageId' is required for get_message.", IsError: true);

        var url = $"{ApiBase}/messages/{Uri.EscapeDataString(messageId)}?format=full";
        using var doc = await GetJsonAsync(client, url, accessToken, ct);
        var root = doc.RootElement;

        var payload = root.TryGetProperty("payload", out var p) ? p : default;
        var headers = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("headers", out var h) ? h : default;

        var body = ExtractPlainTextBody(payload);

        var result = new
        {
            id = messageId,
            subject = GetHeader(headers, "Subject"),
            from = GetHeader(headers, "From"),
            date = GetHeader(headers, "Date"),
            snippet = root.TryGetProperty("snippet", out var s) ? s.GetString() : null,
            body
        };
        return new ToolCompleted(JsonSerializer.Serialize(result));
    }

    private async Task<ToolExecutionResult> MarkReadAsync(HttpClient client, string accessToken, JsonElement arguments, CancellationToken ct)
    {
        var messageId = arguments.GetStringArg("messageId");
        if (string.IsNullOrEmpty(messageId))
            return new ToolCompleted("'messageId' is required for mark_read.", IsError: true);

        var read = GetBoolArg(arguments, "read");
        if (read is null)
            return new ToolCompleted("'read' (boolean) is required for mark_read.", IsError: true);

        var body = read.Value
            ? """{"removeLabelIds":["UNREAD"]}"""
            : """{"addLabelIds":["UNREAD"]}""";

        var url = $"{ApiBase}/messages/{Uri.EscapeDataString(messageId)}/modify";
        using var _ = await PostJsonAsync(client, url, accessToken, body, ct);

        return new ToolCompleted(JsonSerializer.Serialize(new { messageId, read = read.Value }));
    }

    private async Task<ToolExecutionResult> SetLabelAsync(HttpClient client, string accessToken, JsonElement arguments, CancellationToken ct)
    {
        var messageId = arguments.GetStringArg("messageId");
        if (string.IsNullOrEmpty(messageId))
            return new ToolCompleted("'messageId' is required for set_label.", IsError: true);

        var label = arguments.GetStringArg("label");
        if (string.IsNullOrWhiteSpace(label))
            return new ToolCompleted("'label' is required for set_label.", IsError: true);

        var add = GetBoolArg(arguments, "add");
        if (add is null)
            return new ToolCompleted("'add' (boolean) is required for set_label: true to add the label, false to remove it.", IsError: true);

        using var labelsDoc = await GetJsonAsync(client, $"{ApiBase}/labels", accessToken, ct);
        string? labelId = null;
        var knownNames = new List<string>();
        if (labelsDoc.RootElement.TryGetProperty("labels", out var labelsEl) && labelsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in labelsEl.EnumerateArray())
            {
                var name = l.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null) continue;
                knownNames.Add(name);
                if (string.Equals(name, label, StringComparison.OrdinalIgnoreCase))
                    labelId = l.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            }
        }

        if (labelId is null)
            return new ToolCompleted(
                $"No Gmail label named '{label}'. Existing labels: {string.Join(", ", knownNames)}.", IsError: true);

        var body = add.Value
            ? JsonSerializer.Serialize(new { addLabelIds = new[] { labelId } })
            : JsonSerializer.Serialize(new { removeLabelIds = new[] { labelId } });

        var url = $"{ApiBase}/messages/{Uri.EscapeDataString(messageId)}/modify";
        using var _ = await PostJsonAsync(client, url, accessToken, body, ct);

        return new ToolCompleted(JsonSerializer.Serialize(new { messageId, label, added = add.Value }));
    }

    private static string? GetHeader(JsonElement headers, string name)
    {
        if (headers.ValueKind != JsonValueKind.Array) return null;
        foreach (var h in headers.EnumerateArray())
        {
            if (h.TryGetProperty("name", out var n) && string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase))
                return h.TryGetProperty("value", out var v) ? v.GetString() : null;
        }
        return null;
    }

    private static string? ExtractPlainTextBody(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;

        if (payload.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("mimeType", out var mt) && mt.GetString() == "text/plain" &&
                    part.TryGetProperty("body", out var b) && b.TryGetProperty("data", out var d))
                    return DecodeBase64Url(d.GetString());

                // Recurse for nested multipart (e.g. multipart/alternative inside multipart/mixed).
                var nested = ExtractPlainTextBody(part);
                if (nested is not null) return nested;
            }
            return null;
        }

        if (payload.TryGetProperty("body", out var body) && body.TryGetProperty("data", out var data))
            return DecodeBase64Url(data.GetString());

        return null;
    }

    private static string? DecodeBase64Url(string? data)
    {
        if (string.IsNullOrEmpty(data)) return null;
        var normalized = data.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized
        };
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool? GetBoolArg(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) &&
        (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : null;

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string url, string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new GmailApiException($"Gmail API request failed ({(int)response.StatusCode}): {Truncate(body, 500)}");
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    private static async Task<JsonDocument> PostJsonAsync(HttpClient client, string url, string accessToken, string jsonBody, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new GmailApiException($"Gmail API request failed ({(int)response.StatusCode}): {Truncate(body, 500)}");
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private sealed class GmailApiException(string message) : Exception(message);
}
