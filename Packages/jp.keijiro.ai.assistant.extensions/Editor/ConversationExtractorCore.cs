using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AIAssistantExtensions {

// One conversation as surfaced by the AI Assistant list API.
struct ConversationItem
{
    public object IdBoxed;   // boxed AssistantConversationId, passed straight back to the API
    public string Id;        // its string value, used to match the load callback
    public string Title;
    public long Timestamp;   // LastMessageTimestamp
    public bool Favorite;
}

// Expected, user-facing failure (provider unavailable, conversation not found,
// timeout). Callers surface the message without logging it as an error.
class ConversationExtractorException : Exception
{
    public ConversationExtractorException(string message) : base(message) { }
}

// Non-UI core shared by the editor window and the MCP tools. Talks to the
// AI Assistant package's in-process API through reflection (everything there is
// internal and our assembly is not on its InternalsVisibleTo allowlist), lists
// and loads conversations, and renders them to Markdown. The live IAssistantProvider
// only exists while an AI Assistant window is open, so that window must be open.
//
// An instance owns one operation's event plumbing; create one per use (operations
// are awaited serially within an instance).
class ConversationExtractorCore
{
    const string AssistantWindowTypeName = "Unity.AI.Assistant.UI.Editor.Scripts.AssistantWindow";
    const int EventTimeoutMs = 20000;

    const BindingFlags InstanceMembers =
      BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // Event plumbing shared with the reflection callbacks.
    Delegate _refreshedHandler;
    Delegate _loadedHandler;
    TaskCompletionSource<object> _refreshTcs;
    TaskCompletionSource<object> _loadTcs;
    string _pendingLoadId;

    // --- High-level API -----------------------------------------------------

    // Lists conversations, newest first. Throws ConversationExtractorException when
    // the live provider is unavailable.
    public async Task<List<ConversationItem>> ListConversationsAsync()
    {
        var provider = RequireProvider();
        Subscribe(provider);
        try
        {
            var list = await RefreshConversations(provider);
            return list.OrderByDescending(c => c.Timestamp).ToList();
        }
        finally
        {
            Unsubscribe(provider);
        }
    }

    // Loads a conversation already identified by a list item (its boxed id is reused).
    public async Task<object> LoadConversationAsync(ConversationItem item)
    {
        var provider = RequireProvider();
        Subscribe(provider);
        try
        {
            return await LoadConversation(provider, item);
        }
        finally
        {
            Unsubscribe(provider);
        }
    }

    // Loads a conversation by its id string. Refreshes the list first to recover the
    // boxed id the API expects. Throws when unavailable or not found.
    public async Task<object> LoadConversationByIdAsync(string conversationId)
    {
        if (string.IsNullOrEmpty(conversationId))
            throw new ConversationExtractorException("Conversation id is required.");

        var provider = RequireProvider();
        Subscribe(provider);
        try
        {
            var list = await RefreshConversations(provider);
            var match = list.FirstOrDefault(c => c.Id == conversationId);
            if (match.IdBoxed == null)
                throw new ConversationExtractorException($"Conversation not found: {conversationId}");
            return await LoadConversation(provider, match);
        }
        finally
        {
            Unsubscribe(provider);
        }
    }

    // --- Live provider acquisition -----------------------------------------

    static object RequireProvider()
    {
        var provider = GetLiveProvider(out var error);
        if (provider == null) throw new ConversationExtractorException(error);
        return provider;
    }

    static object GetLiveProvider(out string error)
    {
        error = null;

        var windowType = FindType(AssistantWindowTypeName);
        if (windowType == null)
        {
            error = "AI Assistant package not found. Is it installed?";
            return null;
        }

        var window = windowType
          .GetMethod("FindExistingWindow", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
          ?.Invoke(null, null);
        if (window == null)
        {
            error = "Open the AI Assistant window first (Window > AI > Assistant), then retry.";
            return null;
        }

        var provider = windowType.GetProperty("AssistantInstance", InstanceMembers)?.GetValue(window);
        if (provider == null)
            error = "AI Assistant instance is not ready yet. Interact with the Assistant window, then retry.";

        return provider;
    }

    // --- Event-driven calls -------------------------------------------------

    async Task<List<ConversationItem>> RefreshConversations(object provider)
    {
        _refreshTcs = new TaskCompletionSource<object>();

        var method = provider.GetType().GetMethod(
          "RefreshConversationsAsync", InstanceMembers,
          null, new[] { typeof(CancellationToken), typeof(bool) }, null);
        if (method == null)
            throw new MissingMethodException("IAssistantProvider.RefreshConversationsAsync not found.");

        await (Task)method.Invoke(provider, new object[] { CancellationToken.None, false });
        var infos = await WithTimeout(_refreshTcs.Task);

        // The event delivers IEnumerable<AssistantConversationInfo> (a boxed struct per item).
        var result = new List<ConversationItem>();
        foreach (var info in (IEnumerable)infos)
        {
            var idBoxed = GetMember(info, "Id");
            result.Add(new ConversationItem
            {
                IdBoxed = idBoxed,
                Id = GetMember(idBoxed, "Value") as string,
                Title = GetMember(info, "Title") as string,
                Timestamp = Convert.ToInt64(GetMember(info, "LastMessageTimestamp") ?? 0L),
                Favorite = GetMember(info, "IsFavorite") is true
            });
        }
        return result;
    }

    async Task<object> LoadConversation(object provider, ConversationItem item)
    {
        _pendingLoadId = item.Id;
        _loadTcs = new TaskCompletionSource<object>();

        var method = provider.GetType().GetMethod("ConversationLoad", InstanceMembers);
        if (method == null)
            throw new MissingMethodException("IAssistantProvider.ConversationLoad not found.");

        await (Task)method.Invoke(provider, new[] { item.IdBoxed, CancellationToken.None });
        return await WithTimeout(_loadTcs.Task);
    }

    static async Task<object> WithTimeout(Task<object> task)
    {
        if (await Task.WhenAny(task, Task.Delay(EventTimeoutMs)) != task)
            throw new ConversationExtractorException("Timed out waiting for the AI Assistant API to respond.");
        return await task;
    }

    void Subscribe(object provider)
    {
        var type = provider.GetType();

        // Bind handlers taking `object` to Action<T> events via delegate contravariance.
        var refreshedEvent = type.GetEvent("ConversationsRefreshed", InstanceMembers);
        _refreshedHandler = Delegate.CreateDelegate(
          refreshedEvent.EventHandlerType, this,
          GetType().GetMethod(nameof(OnConversationsRefreshed), InstanceMembers));
        refreshedEvent.AddEventHandler(provider, _refreshedHandler);

        var loadedEvent = type.GetEvent("ConversationLoaded", InstanceMembers);
        _loadedHandler = Delegate.CreateDelegate(
          loadedEvent.EventHandlerType, this,
          GetType().GetMethod(nameof(OnConversationLoaded), InstanceMembers));
        loadedEvent.AddEventHandler(provider, _loadedHandler);
    }

    void Unsubscribe(object provider)
    {
        if (provider == null) return;

        var type = provider.GetType();
        if (_refreshedHandler != null)
            type.GetEvent("ConversationsRefreshed", InstanceMembers)?.RemoveEventHandler(provider, _refreshedHandler);
        if (_loadedHandler != null)
            type.GetEvent("ConversationLoaded", InstanceMembers)?.RemoveEventHandler(provider, _loadedHandler);

        _refreshedHandler = _loadedHandler = null;
    }

    // Reflection callbacks. May arrive on a background thread; TaskCompletionSource
    // marshals the awaiting continuation back to the captured (main) context.
    void OnConversationsRefreshed(object infos) => _refreshTcs?.TrySetResult(infos);

    void OnConversationLoaded(object conversation)
    {
        var id = GetMember(GetMember(conversation, "Id"), "Value") as string;
        if (_loadTcs != null && id == _pendingLoadId)
            _loadTcs.TrySetResult(conversation);
    }

    // --- Markdown building --------------------------------------------------

    public static string BuildMarkdown(object conversation, bool includeToolCalls)
    {
        if (conversation == null) return "";

        var messages = (GetMember(conversation, "Messages") as IEnumerable)?
          .Cast<object>().ToList() ?? new List<object>();

        var builder = new StringBuilder();

        // Title plus the metadata header.
        var title = GetMember(conversation, "Title") as string;
        builder.Append("# ").Append(DisplayTitle(title)).Append('\n');
        AppendHeader(builder, conversation, messages);
        builder.Append("\n\n---");

        foreach (var message in messages)
        {
            var body = BuildMessageBody(message, includeToolCalls);
            if (string.IsNullOrWhiteSpace(body)) continue;

            var role = GetMember(message, "Role") as string;
            builder.Append("\n\n## ").Append(RoleHeading(role)).Append("\n\n").Append(body);
        }

        return builder.ToString();
    }

    // Emits the Date / Task ID / Messages metadata list.
    static void AppendHeader(StringBuilder builder, object conversation, List<object> messages)
    {
        var date = FormatDate(ConversationStartTimestamp(conversation));
        if (!string.IsNullOrEmpty(date))
            builder.Append("\n- **Date:** ").Append(date);

        var id = GetMember(GetMember(conversation, "Id"), "Value") as string;
        if (!string.IsNullOrEmpty(id))
            builder.Append("\n- **Task ID:** `").Append(id).Append('`');

        builder.Append("\n- **Messages:** ").Append(messages.Count);
    }

    // The conversation's start time: its creation timestamp when available,
    // otherwise the earliest message timestamp.
    public static long ConversationStartTimestamp(object conversation)
    {
        if (conversation == null) return 0;
        var created = Convert.ToInt64(GetMember(conversation, "CreatedTimestamp") ?? 0L);
        return created > 0
          ? created
          : EarliestMessageTimestamp(GetMember(conversation, "Messages") as IEnumerable);
    }

    static long EarliestMessageTimestamp(IEnumerable messages)
    {
        long earliest = 0;
        if (messages != null)
            foreach (var message in messages)
            {
                var ts = Convert.ToInt64(GetMember(message, "Timestamp") ?? 0L);
                if (ts > 0 && (earliest == 0 || ts < earliest)) earliest = ts;
            }
        return earliest;
    }

    public static int MessageCount(object conversation)
      => (GetMember(conversation, "Messages") as IEnumerable)?.Cast<object>().Count() ?? 0;

    static string RoleHeading(string role) => role?.ToLowerInvariant() switch
    {
        "user" => "🧑 User",
        "assistant" => "🤖 Assistant",
        _ => FormatRole(role)
    };

    // Builds one message's body: the primary text (prompt / answer / error / info)
    // first, then any thought and tool-call sections as collapsible <details>
    // blocks. The ACP tool-call / plan blocks are not produced by the standard
    // (non-ACP) models and are skipped.
    static string BuildMessageBody(object message, bool includeToolCalls)
    {
        if (GetMember(message, "Blocks") is not IEnumerable blocks) return "";

        var primary = new List<string>();
        var details = new List<string>();

        foreach (var block in blocks)
        {
            switch (block?.GetType().Name)
            {
                case "PromptBlock":
                case "AnswerBlock":
                    AddText(primary, GetMember(block, "Content") as string);
                    break;
                case "ErrorBlock":
                    AddText(primary, GetMember(block, "Error") as string);
                    break;
                case "InfoBlock":
                    AddText(primary, GetMember(block, "Message") as string);
                    break;
                case "ThoughtBlock":
                    AddText(details, FormatThought(GetMember(block, "Content") as string));
                    break;
                case "FunctionCallBlock":
                    if (includeToolCalls)
                        AddText(details, FormatFunctionCall(GetMember(block, "Call")));
                    break;
            }
        }

        primary.AddRange(details);
        return string.Join("\n\n", primary);
    }

    static void AddText(List<string> parts, string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) parts.Add(text.Trim());
    }

    // Wraps a thought in a collapsible block. The thought data model carries no
    // agent name, so the summary is left unqualified.
    static string FormatThought(string content)
      => string.IsNullOrWhiteSpace(content)
         ? null
         : "<details>\n<summary>💭 思考</summary>\n\n" + content.Trim() + "\n\n</details>";

    // Renders an AssistantFunctionCall as a collapsible block holding its arguments
    // and result. Parameters/Result are Newtonsoft JTokens whose ToString() yields
    // indented JSON, so no compile-time dependency on Newtonsoft is needed.
    static string FormatFunctionCall(object call)
    {
        if (call == null) return null;

        var name = GetMember(call, "FunctionId") as string;
        var agent = GetMember(call, "Agent") as string;

        var summary = "🔧 ツール: " + (string.IsNullOrEmpty(name) ? "(unknown)" : name);
        if (!string.IsNullOrEmpty(agent)) summary += " (" + agent + ")";

        var builder = new StringBuilder();
        builder.Append("<details>\n<summary>").Append(summary).Append("</summary>\n");

        var parameters = GetMember(call, "Parameters")?.ToString();
        if (!string.IsNullOrWhiteSpace(parameters))
            builder.Append("\n**引数**\n\n```json\n").Append(parameters).Append("\n```\n");

        AppendFunctionResult(builder, GetMember(call, "Result"));

        builder.Append("\n</details>");
        return builder.ToString();
    }

    // Appends the result (or error) section from a completed FunctionCallResult.
    static void AppendFunctionResult(StringBuilder builder, object result)
    {
        if (result == null || GetMember(result, "IsDone") is not true) return;

        var payload = GetMember(result, "Result")?.ToString();
        if (string.IsNullOrWhiteSpace(payload)) return;

        var succeeded = GetMember(result, "HasFunctionCallSucceeded") is true;
        builder.Append(succeeded ? "\n**結果**\n\n```\n" : "\n**エラー**\n\n```\n")
               .Append(payload).Append("\n```\n");
    }

    // --- File naming --------------------------------------------------------

    // Builds the "{yyyyMMdd-HHmm}-{Title}-{shortId}" file name (without extension)
    // from a loaded conversation object.
    public static string BuildFileName(object conversation)
      => BuildFileName(
           GetMember(conversation, "Title") as string,
           GetMember(GetMember(conversation, "Id"), "Value") as string,
           ConversationStartTimestamp(conversation));

    // Builds the "{yyyyMMdd-HHmm}-{Title}-{shortId}" file name (without extension):
    // a local date-and-time prefix, the underscore-joined title, and the first
    // segment of the conversation id, so exports sort chronologically and stay
    // uniquely named.
    public static string BuildFileName(string title, string id, long timestamp)
    {
        var parts = new List<string>();

        var date = FormatFileTimestamp(timestamp);
        if (!string.IsNullOrEmpty(date)) parts.Add(date);

        parts.Add(FileTitle(title));

        var shortId = ShortId(id);
        if (!string.IsNullOrEmpty(shortId)) parts.Add(shortId);

        return string.Join("-", parts);
    }

    // Sanitizes the title, collapses whitespace to single underscores, and falls
    // back to "conversation" when nothing usable remains.
    static string FileTitle(string title)
    {
        var name = SanitizeFileName(title);
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", "_");
        name = System.Text.RegularExpressions.Regex.Replace(name, "_+", "_").Trim('_');
        return string.IsNullOrEmpty(name) ? "conversation" : name;
    }

    // First segment of the conversation id (e.g. "3a7bfd64" from a GUID).
    static string ShortId(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        var segment = id.Split('-')[0];
        return segment.Length > 8 ? segment.Substring(0, 8) : segment;
    }

    static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "conversation";
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(sanitized) ? "conversation" : sanitized;
    }

    // --- Misc helpers -------------------------------------------------------

    public static string DisplayTitle(string title)
      => string.IsNullOrEmpty(title) ? "(Untitled)" : title;

    // Collapses CR/LF runs to single spaces so a value renders on one line.
    public static string SingleLine(string text)
      => string.IsNullOrEmpty(text)
         ? text
         : System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    static string FormatRole(string role)
    {
        if (string.IsNullOrEmpty(role)) return "Message";
        return char.ToUpperInvariant(role[0]) + role.Substring(1);
    }

    public static string FormatDate(long timestamp)
      => TryGetTime(timestamp, out var time) ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";

    // Local date-and-time prefix for filenames (yyyyMMdd-HHmm); colon-free so it is
    // filesystem-safe and sortable.
    static string FormatFileTimestamp(long timestamp)
      => TryGetTime(timestamp, out var time) ? time.ToLocalTime().ToString("yyyyMMdd-HHmm") : "";

    // Parses a Unix timestamp (auto-detecting seconds vs. milliseconds) as a UTC
    // DateTimeOffset; callers localize as needed.
    static bool TryGetTime(long timestamp, out DateTimeOffset time)
    {
        time = default;
        if (timestamp <= 0) return false;
        try
        {
            time = timestamp > 1_000_000_000_000L
              ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
              : DateTimeOffset.FromUnixTimeSeconds(timestamp);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static Type FindType(string fullName)
      => AppDomain.CurrentDomain.GetAssemblies()
          .Select(a => a.GetType(fullName, false))
          .FirstOrDefault(t => t != null);

    // Reads a public/internal field or property by name from a live object
    // (works on boxed structs too).
    static object GetMember(object obj, string name)
    {
        if (obj == null) return null;
        var type = obj.GetType();
        var field = type.GetField(name, InstanceMembers);
        if (field != null) return field.GetValue(obj);
        return type.GetProperty(name, InstanceMembers)?.GetValue(obj);
    }
}

} // namespace AIAssistantExtensions
