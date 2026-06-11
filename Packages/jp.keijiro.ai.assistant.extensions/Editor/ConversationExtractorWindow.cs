using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace AIAssistantExtensions {

// Editor window over ConversationExtractorCore: lists the (Unity-hosted) AI Assistant
// conversations, extracts whichever one you pick to Markdown, and copies or saves it.
// The live provider only exists while an AI Assistant window is open, so that window
// must be open for this to work.
class ConversationExtractorWindow : EditorWindow
{
    const string AssetDir = "Packages/jp.keijiro.ai.assistant.extensions/Editor/";
    const string UxmlPath = AssetDir + "ConversationExtractorWindow.uxml";
    const string RowUxmlPath = AssetDir + "ConversationRow.uxml";
    const string UssPath = AssetDir + "ConversationExtractorWindow.uss";

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
        title.text = (item.Favorite ? "★ " : "") +
          ConversationExtractorCore.SingleLine(ConversationExtractorCore.DisplayTitle(item.Title));

        var date = element.Q<Label>("row-date");
        var formatted = ConversationExtractorCore.FormatDate(item.Timestamp);
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
        _markdown = ConversationExtractorCore.BuildMarkdown(_loadedConversation, _includeToolCalls);
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
        _busy = true;
        _listStatus = null;
        UpdateToolbar();
        UpdateList();

        try
        {
            var list = await new ConversationExtractorCore().ListConversationsAsync();
            _conversations.Clear();
            _conversations.AddRange(list);
            _loadedConversation = null;
            _markdown = "";
            _status = null;
            _listView?.SetSelectionWithoutNotify(Array.Empty<int>());
        }
        catch (ConversationExtractorException e)
        {
            _listStatus = e.Message;
        }
        catch (Exception e)
        {
            _listStatus = $"Failed to refresh: {e.Message}";
            Debug.LogException(e);
        }
        finally
        {
            _busy = false;
            UpdateToolbar();
            UpdateList();
            UpdatePreview();
        }
    }

    async Task ExtractConversation(ConversationItem item)
    {
        _busy = true;
        _extracting = true;
        _status = $"Extracting \"{ConversationExtractorCore.DisplayTitle(item.Title)}\"...";
        UpdateToolbar();
        UpdatePreview();

        try
        {
            var conversation = await new ConversationExtractorCore().LoadConversationAsync(item);
            _loadedConversation = conversation;
            _markdown = ConversationExtractorCore.BuildMarkdown(conversation, _includeToolCalls);
            _status = null;
        }
        catch (ConversationExtractorException e)
        {
            _loadedConversation = null;
            _markdown = "";
            _status = e.Message;
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

        var fileName = "conversation";
        if (hasSelection)
        {
            var item = _conversations[index];
            // Name by the conversation's start time; fall back to the list's
            // last-message timestamp if the loaded conversation is unavailable.
            var startTimestamp = ConversationExtractorCore.ConversationStartTimestamp(_loadedConversation);
            if (startTimestamp <= 0) startTimestamp = item.Timestamp;
            fileName = ConversationExtractorCore.BuildFileName(item.Title, item.Id, startTimestamp);
        }

        var defaultPath = Path.Combine(projectRoot, "Logs", $"{fileName}.md");
        var path = EditorUtility.SaveFilePanel("Save extracted conversation", Path.GetDirectoryName(defaultPath), Path.GetFileName(defaultPath), "md");
        if (string.IsNullOrEmpty(path)) return;

        File.WriteAllText(path, _markdown ?? "", Encoding.UTF8);
        ShowNotification(new GUIContent($"Saved to {Path.GetFileName(path)}"));
    }
}

} // namespace AIAssistantExtensions
