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
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace AIAssistantExtensions {

// Lists the (Unity-hosted) conversations from the AI Assistant package's in-process
// API and extracts whichever one you pick. Everything in that API is internal and
// our assembly is not on its InternalsVisibleTo allowlist, so we reach it through
// reflection. The live IAssistantProvider only exists while an AI Assistant window
// is open, so that window must be open for this to work.
class ConversationExtractorWindow : EditorWindow
{
    const string AssetDir = "Packages/jp.keijiro.ai.assistant.extensions/Editor/";
    const string UxmlPath = AssetDir + "ConversationExtractorWindow.uxml";
    const string RowUxmlPath = AssetDir + "ConversationRow.uxml";
    const string UssPath = AssetDir + "ConversationExtractorWindow.uss";

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

    object _loadedConversation;  // last loaded conversation, for rebuilding without re-fetching
    string _markdown;
    string _status;      // right pane: extraction lifecycle only
    string _listStatus;  // left pane: empty-state message
    bool _busy;
    bool _extracting;
    bool _includeToolCalls = true;

    // UI elements (resolved in CreateGUI).
    VisualTreeAsset _rowTemplate;
    ToolbarButton _refreshButton;
    Toggle _toolCallsToggle;
    ToolbarButton _copyButton;
    ToolbarButton _saveButton;
    Label _listHeader;
    Label _emptyLabel;
    ListView _listView;
    TextField _preview;

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
        window.minSize = new Vector2(640, 360);
    }

    // --- UI construction ----------------------------------------------------

    void CreateGUI()
    {
        var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(UxmlPath);
        if (tree == null)
        {
            rootVisualElement.Add(new Label($"Layout asset not found: {UxmlPath}"));
            return;
        }

        tree.CloneTree(rootVisualElement);

        var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(UssPath);
        if (styleSheet != null)
            rootVisualElement.styleSheets.Add(styleSheet);

        _rowTemplate = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(RowUxmlPath);

        _refreshButton = rootVisualElement.Q<ToolbarButton>("refresh-button");
        _toolCallsToggle = rootVisualElement.Q<Toggle>("toolcalls-toggle");
        _copyButton = rootVisualElement.Q<ToolbarButton>("copy-button");
        _saveButton = rootVisualElement.Q<ToolbarButton>("save-button");
        _listHeader = rootVisualElement.Q<Label>("list-header");
        _emptyLabel = rootVisualElement.Q<Label>("empty-label");
        _listView = rootVisualElement.Q<ListView>("conversation-list");
        _preview = rootVisualElement.Q<TextField>("preview");

        _refreshButton.clicked += () => { if (!_busy) _ = RefreshList(); };
        _copyButton.clicked += CopyToClipboard;
        _saveButton.clicked += SaveMarkdown;

        _toolCallsToggle.SetValueWithoutNotify(_includeToolCalls);
        _toolCallsToggle.RegisterValueChangedCallback(evt => OnToolCallsChanged(evt.newValue));

        _listView.itemsSource = _conversations;
        _listView.makeItem = MakeRow;
        _listView.bindItem = BindRow;
        _listView.selectionChanged += OnSelectionChanged;

        // Show the initial hint; RefreshList drives the toolbar and list state.
        UpdatePreview();
        _ = RefreshList();
    }

    VisualElement MakeRow()
    {
        if (_rowTemplate != null)
            return _rowTemplate.Instantiate();

        var container = new VisualElement();
        container.Add(new Label { name = "row-title" });
        container.Add(new Label { name = "row-date" });
        return container;
    }

    void BindRow(VisualElement element, int index)
    {
        var item = _conversations[index];

        var title = element.Q<Label>("row-title");
        // Titles can contain embedded newlines; collapse them so the row stays
        // single-line (white-space:nowrap doesn't strip explicit line breaks).
        title.text = (item.Favorite ? "★ " : "") + SingleLine(DisplayTitle(item.Title));

        var date = element.Q<Label>("row-date");
        var formatted = FormatDate(item.Timestamp);
        date.text = formatted;
        date.style.display = string.IsNullOrEmpty(formatted) ? DisplayStyle.None : DisplayStyle.Flex;
    }

    void OnSelectionChanged(IEnumerable<object> selection)
    {
        if (_busy) return;

        var index = _listView.selectedIndex;
        if (index >= 0 && index < _conversations.Count)
            _ = ExtractConversation(_conversations[index]);
    }

    void OnToolCallsChanged(bool include)
    {
        _includeToolCalls = include;

        // Rebuild from the already-loaded conversation; no need to re-fetch.
        if (_busy || _loadedConversation == null) return;
        _markdown = BuildConversationMarkdown(_loadedConversation, _includeToolCalls);
        UpdateToolbar();
        UpdatePreview();
    }

    // --- State -> UI --------------------------------------------------------

    void UpdateToolbar()
    {
        _refreshButton?.SetEnabled(!_busy);

        var hasMarkdown = !_busy && !string.IsNullOrEmpty(_markdown);
        _copyButton?.SetEnabled(hasMarkdown);
        _saveButton?.SetEnabled(hasMarkdown);
    }

    void UpdateList()
    {
        if (_listView == null) return;

        _listHeader.text = $"Conversations ({_conversations.Count})";
        _listView.Rebuild();

        var empty = _conversations.Count == 0;
        _listView.style.display = empty ? DisplayStyle.None : DisplayStyle.Flex;
        _emptyLabel.style.display = empty ? DisplayStyle.Flex : DisplayStyle.None;
        if (empty)
            _emptyLabel.text = _busy
              ? "Loading..."
              : (string.IsNullOrEmpty(_listStatus) ? "No conversations." : _listStatus);
    }

    void UpdatePreview()
    {
        if (_preview == null) return;

        // While extracting show the status text; otherwise the extracted markdown,
        // or a hint when there is none.
        string content;
        if (_extracting)
            content = _status;
        else if (!string.IsNullOrEmpty(_markdown))
            content = _markdown;
        else
            content = string.IsNullOrEmpty(_status) ? "Select a conversation to extract." : _status;

        _preview.SetValueWithoutNotify(content ?? "");
    }

    // --- Operations ---------------------------------------------------------

    async Task RefreshList()
    {
        object provider = null;
        _busy = true;
        _listStatus = null;
        UpdateToolbar();
        UpdateList();

        try
        {
            provider = GetLiveProvider(out var error);
            if (provider == null) { _listStatus = error; return; }

            Subscribe(provider);

            _conversations.Clear();
            _conversations.AddRange((await RefreshConversations(provider))
              .OrderByDescending(c => c.Timestamp));
            _loadedConversation = null;
            _markdown = "";
            _status = null;
            _listView?.SetSelectionWithoutNotify(Array.Empty<int>());
        }
        catch (Exception e)
        {
            _listStatus = $"Failed to refresh: {e.Message}";
            Debug.LogException(e);
        }
        finally
        {
            Unsubscribe(provider);
            _busy = false;
            UpdateToolbar();
            UpdateList();
            UpdatePreview();
        }
    }

    async Task ExtractConversation(ConversationItem item)
    {
        object provider = null;
        _busy = true;
        _extracting = true;
        _status = $"Extracting \"{DisplayTitle(item.Title)}\"...";
        UpdateToolbar();
        UpdatePreview();

        try
        {
            provider = GetLiveProvider(out var error);
            if (provider == null) { _markdown = ""; _status = error; return; }

            Subscribe(provider);

            var conversation = await LoadConversation(provider, item);
            _loadedConversation = conversation;
            _markdown = BuildConversationMarkdown(conversation, _includeToolCalls);
            _status = null;
        }
        catch (Exception e)
        {
            _loadedConversation = null;
            _markdown = "";
            _status = $"Extraction failed: {e.Message}";
            Debug.LogException(e);
        }
        finally
        {
            Unsubscribe(provider);
            _busy = false;
            _extracting = false;
            UpdateToolbar();
            UpdatePreview();
        }
    }

    void CopyToClipboard()
    {
        EditorGUIUtility.systemCopyBuffer = _markdown ?? "";
        ShowNotification(new GUIContent("Copied to clipboard"));
    }

    void SaveMarkdown()
    {
        var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
        var index = _listView?.selectedIndex ?? -1;
        var hasSelection = index >= 0 && index < _conversations.Count;
        var name = hasSelection ? SanitizeFileName(_conversations[index].Title) : "conversation";
        var stamp = hasSelection ? FormatFileTimestamp(_conversations[index].Timestamp) : "";
        var fileName = string.IsNullOrEmpty(stamp) ? name : $"{stamp} {name}";
        var defaultPath = Path.Combine(projectRoot, "Logs", $"{fileName}.md");
        var path = EditorUtility.SaveFilePanel("Save extracted conversation", Path.GetDirectoryName(defaultPath), Path.GetFileName(defaultPath), "md");
        if (string.IsNullOrEmpty(path)) return;

        File.WriteAllText(path, _markdown ?? "", Encoding.UTF8);
        ShowNotification(new GUIContent($"Saved to {Path.GetFileName(path)}"));
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

    static string BuildConversationMarkdown(object conversation, bool includeToolCalls)
    {
        if (conversation == null) return "";

        var builder = new StringBuilder();
        var title = GetMember(conversation, "Title") as string;
        builder.Append("# ").Append(DisplayTitle(title));

        if (GetMember(conversation, "Messages") is IEnumerable messages)
        {
            foreach (var message in messages)
            {
                var text = ExtractMessageText(message, includeToolCalls);
                if (string.IsNullOrWhiteSpace(text)) continue;

                var role = GetMember(message, "Role") as string;
                builder.Append("\n\n## ").Append(FormatRole(role)).Append("\n\n");
                builder.Append(text.Trim());
            }
        }

        return builder.ToString();
    }

    static string ExtractMessageText(object message, bool includeToolCalls)
    {
        if (GetMember(message, "Blocks") is not IEnumerable blocks) return "";

        var parts = new List<string>();
        foreach (var block in blocks)
        {
            var text = BlockText(block, includeToolCalls);
            if (!string.IsNullOrWhiteSpace(text)) parts.Add(text.Trim());
        }
        return string.Join("\n\n", parts);
    }

    // Pulls text out of each block. Text-bearing blocks return their content;
    // FunctionCallBlock is rendered as the tool name plus its parameters (only when
    // tool calls are included). The ACP tool-call / plan blocks carry structured
    // data and are skipped.
    static string BlockText(object block, bool includeToolCalls)
    {
        if (block == null) return null;
        return block.GetType().Name switch
        {
            "PromptBlock" => GetMember(block, "Content") as string,
            "AnswerBlock" => GetMember(block, "Content") as string,
            "ThoughtBlock" => GetMember(block, "Content") as string,
            "ErrorBlock" => GetMember(block, "Error") as string,
            "InfoBlock" => GetMember(block, "Message") as string,
            "FunctionCallBlock" => includeToolCalls ? FormatFunctionCall(GetMember(block, "Call")) : null,
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

    // Collapses CR/LF runs to single spaces so a value renders on one line.
    static string SingleLine(string text)
      => string.IsNullOrEmpty(text)
         ? text
         : System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    static string FormatRole(string role)
    {
        if (string.IsNullOrEmpty(role)) return "Message";
        return char.ToUpperInvariant(role[0]) + role.Substring(1);
    }

    static string FormatDate(long timestamp)
      => TryGetLocalTime(timestamp, out var time) ? time.ToString("yyyy-MM-dd HH:mm") : "";

    // Date and time for filenames; colon-free so it is filesystem-safe and sortable.
    static string FormatFileTimestamp(long timestamp)
      => TryGetLocalTime(timestamp, out var time) ? time.ToString("yyyy-MM-dd HH-mm") : "";

    static bool TryGetLocalTime(long timestamp, out DateTimeOffset time)
    {
        time = default;
        if (timestamp <= 0) return false;
        try
        {
            time = (timestamp > 1_000_000_000_000L
              ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
              : DateTimeOffset.FromUnixTimeSeconds(timestamp)).ToLocalTime();
            return true;
        }
        catch
        {
            return false;
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
