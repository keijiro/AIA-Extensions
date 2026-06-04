using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace AIAssistantExtensions {

// Lists the (Unity-hosted) conversations from the AI Assistant package's in-process
// API and extracts whichever one you pick. Everything in that API is internal and
// our assembly is not on its InternalsVisibleTo allowlist, so we reach it through
// reflection. The live IAssistantProvider only exists while an AI Assistant window
// is open, so that window must be open for this to work.
class ConversationExtractorWindow : EditorWindow
{
    const string AssistantWindowTypeName = "Unity.AI.Assistant.UI.Editor.Scripts.AssistantWindow";
    const int EventTimeoutMs = 20000;

    const BindingFlags InstanceMembers =
      BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    struct ConversationItem
    {
        public object IdBoxed;   // boxed AssistantConversationId, passed straight back to the API
        public string Id;        // its string value, used to match the load callback
        public string Title;
        public long Timestamp;
        public bool Favorite;
    }

    readonly List<ConversationItem> _conversations = new();
    int _selectedIndex = -1;

    Vector2 _listScroll;
    Vector2 _textScroll;
    string _markdown;
    string _status;
    bool _busy;

    // Event plumbing shared with the reflection callbacks.
    Delegate _refreshedHandler;
    Delegate _loadedHandler;
    TaskCompletionSource<object> _refreshTcs;
    TaskCompletionSource<object> _loadTcs;
    string _pendingLoadId;

    [MenuItem("Window/AI/Conversation Extractor")]
    static void Open()
    {
        var window = GetWindow<ConversationExtractorWindow>();
        window.titleContent = new GUIContent("Conversation Extractor");
        window.minSize = new Vector2(480, 360);
    }

    void OnGUI()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            using (new EditorGUI.DisabledScope(_busy))
            {
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton))
                    _ = RefreshList();
            }

            GUILayout.FlexibleSpace();

            using (new EditorGUI.DisabledScope(_busy || string.IsNullOrEmpty(_markdown)))
            {
                if (GUILayout.Button("Copy", EditorStyles.toolbarButton))
                    CopyToClipboard();
                if (GUILayout.Button("Save", EditorStyles.toolbarButton))
                    SaveMarkdown();
            }
        }

        if (!string.IsNullOrEmpty(_status))
            EditorGUILayout.HelpBox(_status, _busy ? MessageType.Info : MessageType.None);

        DrawConversationList();

        EditorGUILayout.LabelField("Extracted Markdown", EditorStyles.boldLabel);
        using var scroll = new EditorGUILayout.ScrollViewScope(_textScroll);
        _textScroll = scroll.scrollPosition;
        EditorGUILayout.TextArea(_markdown ?? "", GUILayout.ExpandHeight(true));
    }

    void DrawConversationList()
    {
        EditorGUILayout.LabelField($"Conversations ({_conversations.Count})", EditorStyles.boldLabel);

        using var scroll = new EditorGUILayout.ScrollViewScope(
          _listScroll, GUILayout.Height(Mathf.Max(80f, position.height * 0.35f)));
        _listScroll = scroll.scrollPosition;

        using (new EditorGUI.DisabledScope(_busy))
        {
            for (var i = 0; i < _conversations.Count; i++)
            {
                var item = _conversations[i];
                var label = (item.Favorite ? "★ " : "") + DisplayTitle(item.Title);
                var date = FormatDate(item.Timestamp);
                if (!string.IsNullOrEmpty(date)) label += $"    ({date})";

                var selected = i == _selectedIndex;
                if (GUILayout.Toggle(selected, label, "Button") && !selected)
                {
                    _selectedIndex = i;
                    _ = ExtractConversation(item);
                }
            }
        }
    }

    // --- Operations ---------------------------------------------------------

    async Task RefreshList()
    {
        object provider = null;
        _busy = true;
        _status = "Refreshing conversation list...";
        Repaint();

        try
        {
            provider = GetLiveProvider(out var error);
            if (provider == null) { _status = error; return; }

            Subscribe(provider);

            _conversations.Clear();
            _conversations.AddRange((await RefreshConversations(provider))
              .OrderByDescending(c => c.Timestamp));
            _selectedIndex = -1;
            _markdown = "";
            _status = $"Loaded {_conversations.Count} conversation(s). Select one to extract.";
        }
        catch (Exception e)
        {
            _status = $"Failed to refresh: {e.Message}";
            Debug.LogException(e);
        }
        finally
        {
            Unsubscribe(provider);
            _busy = false;
            Repaint();
        }
    }

    async Task ExtractConversation(ConversationItem item)
    {
        object provider = null;
        _busy = true;
        _status = $"Extracting \"{DisplayTitle(item.Title)}\"...";
        Repaint();

        try
        {
            provider = GetLiveProvider(out var error);
            if (provider == null) { _status = error; return; }

            Subscribe(provider);

            var conversation = await LoadConversation(provider, item);
            _markdown = BuildConversationMarkdown(conversation);
            _status = $"Extracted \"{DisplayTitle(item.Title)}\".";
        }
        catch (Exception e)
        {
            _markdown = "";
            _status = $"Extraction failed: {e.Message}";
            Debug.LogException(e);
        }
        finally
        {
            Unsubscribe(provider);
            _busy = false;
            Repaint();
        }
    }

    void CopyToClipboard()
    {
        EditorGUIUtility.systemCopyBuffer = _markdown ?? "";
        _status = "Copied extracted markdown to the clipboard.";
    }

    void SaveMarkdown()
    {
        var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
        var name = _selectedIndex >= 0 ? SanitizeFileName(_conversations[_selectedIndex].Title) : "conversation";
        var defaultPath = Path.Combine(projectRoot, "Logs", $"{name}.md");
        var path = EditorUtility.SaveFilePanel("Save extracted conversation", Path.GetDirectoryName(defaultPath), Path.GetFileName(defaultPath), "md");
        if (string.IsNullOrEmpty(path)) return;

        File.WriteAllText(path, _markdown ?? "", Encoding.UTF8);
        _status = $"Saved extracted markdown to {path}";
    }

    // --- Live provider acquisition -----------------------------------------

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
            throw new TimeoutException("Timed out waiting for the AI Assistant API to respond.");
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

    static string BuildConversationMarkdown(object conversation)
    {
        if (conversation == null) return "";

        var builder = new StringBuilder();
        var title = GetMember(conversation, "Title") as string;
        builder.Append("# ").Append(DisplayTitle(title));

        if (GetMember(conversation, "Messages") is IEnumerable messages)
        {
            foreach (var message in messages)
            {
                var text = ExtractMessageText(message);
                if (string.IsNullOrWhiteSpace(text)) continue;

                var role = GetMember(message, "Role") as string;
                builder.Append("\n\n## ").Append(FormatRole(role)).Append("\n\n");
                builder.Append(text.Trim());
            }
        }

        return builder.ToString();
    }

    static string ExtractMessageText(object message)
    {
        if (GetMember(message, "Blocks") is not IEnumerable blocks) return "";

        var parts = new List<string>();
        foreach (var block in blocks)
        {
            var text = BlockText(block);
            if (!string.IsNullOrWhiteSpace(text)) parts.Add(text.Trim());
        }
        return string.Join("\n\n", parts);
    }

    // Pulls text out of each block. Text-bearing blocks return their content;
    // FunctionCallBlock is rendered as the tool name plus its parameters. The ACP
    // tool-call / plan blocks carry structured data and are skipped.
    static string BlockText(object block)
    {
        if (block == null) return null;
        return block.GetType().Name switch
        {
            "PromptBlock" => GetMember(block, "Content") as string,
            "AnswerBlock" => GetMember(block, "Content") as string,
            "ThoughtBlock" => GetMember(block, "Content") as string,
            "ErrorBlock" => GetMember(block, "Error") as string,
            "InfoBlock" => GetMember(block, "Message") as string,
            "FunctionCallBlock" => FormatFunctionCall(GetMember(block, "Call")),
            _ => null
        };
    }

    // Renders an AssistantFunctionCall (e.g. CodeEdit) as a tool name plus its
    // parameters. Parameters is a Newtonsoft JObject, whose ToString() yields
    // indented JSON, so no compile-time dependency on Newtonsoft is needed.
    static string FormatFunctionCall(object call)
    {
        if (call == null) return null;

        var name = GetMember(call, "FunctionId") as string;
        var builder = new StringBuilder();
        builder.Append("**Tool call: ").Append(string.IsNullOrEmpty(name) ? "(unknown)" : name).Append("**");

        var parameters = GetMember(call, "Parameters")?.ToString();
        if (!string.IsNullOrWhiteSpace(parameters))
            builder.Append("\n\n```json\n").Append(parameters).Append("\n```");

        return builder.ToString();
    }

    // --- Misc helpers -------------------------------------------------------

    static string DisplayTitle(string title)
      => string.IsNullOrEmpty(title) ? "(Untitled)" : title;

    static string FormatRole(string role)
    {
        if (string.IsNullOrEmpty(role)) return "Message";
        return char.ToUpperInvariant(role[0]) + role.Substring(1);
    }

    static string FormatDate(long timestamp)
    {
        if (timestamp <= 0) return "";
        try
        {
            var time = timestamp > 1_000_000_000_000L
              ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
              : DateTimeOffset.FromUnixTimeSeconds(timestamp);
            return time.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        }
        catch
        {
            return "";
        }
    }

    static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "conversation";
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(sanitized) ? "conversation" : sanitized;
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
