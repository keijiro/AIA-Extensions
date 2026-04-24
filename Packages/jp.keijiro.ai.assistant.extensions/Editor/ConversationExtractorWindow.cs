using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace AIAssistantExtensions {

class ConversationExtractorWindow : EditorWindow
{
    const string RelayLogRelativePath = "Logs/relay.txt";

    static readonly Regex MessageRegex = new(
      "\"\\$type\":\"(?<type>[^\"]+)\".*?\"message_id\":\"(?<messageId>[^\"]+)\".*?(?:\"last_message\":(?<lastMessage>true|false).*?)?\"markdown\":\"(?<markdown>(?:\\\\.|[^\"\\\\])*)\"",
      RegexOptions.Compiled);

    Vector2 _scroll;
    string _markdown;
    string _status;
    bool _includeEmpty;

    [MenuItem("Window/AI/Conversation Extractor")]
    static void Open()
    {
        var window = GetWindow<ConversationExtractorWindow>();
        window.titleContent = new GUIContent("Conversation Extractor");
        window.minSize = new Vector2(480, 320);
        window.Extract();
    }

    void OnGUI()
    {
        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            var includeEmpty = GUILayout.Toggle(_includeEmpty, "Include Empty", EditorStyles.toolbarButton);
            if (includeEmpty != _includeEmpty) _includeEmpty = includeEmpty;

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Extract", EditorStyles.toolbarButton))
                Extract();
            if (GUILayout.Button("Copy", EditorStyles.toolbarButton))
                CopyToClipboard();
            if (GUILayout.Button("Save", EditorStyles.toolbarButton))
                SaveMarkdown();
        }

        if (!string.IsNullOrEmpty(_status))
            EditorGUILayout.HelpBox(_status, MessageType.Info);

        using var scroll = new EditorGUILayout.ScrollViewScope(_scroll);
        _scroll = scroll.scrollPosition;
        EditorGUILayout.TextArea(_markdown ?? "", GUILayout.ExpandHeight(true));
    }

    void Extract()
    {
        var path = GetRelayLogPath();
        if (!File.Exists(path))
        {
            _markdown = "";
            _status = $"Relay log not found: {path}";
            return;
        }

        var pairs = ExtractPairs(path);
        _markdown = BuildMarkdown(pairs, _includeEmpty);
        _status = $"Extracted {pairs.Count} conversation pair(s) from {RelayLogRelativePath}.";
    }

    void CopyToClipboard()
    {
        EditorGUIUtility.systemCopyBuffer = _markdown ?? "";
        _status = "Copied extracted markdown to the clipboard.";
    }

    void SaveMarkdown()
    {
        var projectRoot = GetProjectRootPath();
        var defaultPath = Path.Combine(projectRoot, "Logs", "conversations.md");
        var path = EditorUtility.SaveFilePanel("Save extracted conversations", Path.GetDirectoryName(defaultPath), Path.GetFileName(defaultPath), "md");
        if (string.IsNullOrEmpty(path)) return;

        File.WriteAllText(path, _markdown ?? "", Encoding.UTF8);
        _status = $"Saved extracted markdown to {path}";
    }

    static List<ConversationPair> ExtractPairs(string path)
    {
        var pairs = new List<ConversationPair>();
        ConversationPair currentPair = null;
        string activeResponseId = null;

        var lines = File.ReadAllLines(path, Encoding.UTF8);
        for (var i = 0; i < lines.Length; i++)
        {
            var parsed = ParseLine(lines[i]);
            if (parsed == null) continue;

            if (parsed.Type == "CHAT_ACKNOWLEDGMENT_V1" && !string.IsNullOrWhiteSpace(parsed.Markdown))
            {
                if (currentPair != null && currentPair.ResponseParts.Count > 0)
                    pairs.Add(currentPair);

                currentPair = new ConversationPair(parsed.Markdown.Trim(), i + 1, parsed.MessageId);
                activeResponseId = null;
                continue;
            }

            if (parsed.Type != "CHAT_RESPONSE_V1" || currentPair == null) continue;
            if (string.IsNullOrEmpty(parsed.Markdown) || parsed.Markdown.StartsWith("<")) continue;

            if (activeResponseId == null)
            {
                activeResponseId = parsed.MessageId;
                currentPair.ResponseMessageId = activeResponseId;
            }

            if (parsed.MessageId == activeResponseId)
            {
                currentPair.ResponseParts.Add(parsed.Markdown);
                currentPair.ResponseLines.Add(i + 1);
            }

            if (parsed.LastMessage && parsed.MessageId == activeResponseId)
            {
                pairs.Add(currentPair);
                currentPair = null;
                activeResponseId = null;
            }
        }

        if (currentPair != null)
            pairs.Add(currentPair);

        return pairs;
    }

    static ParsedLine ParseLine(string line)
    {
        var match = MessageRegex.Match(line);
        if (!match.Success) return null;

        return new ParsedLine
        (
            match.Groups["type"].Value,
            match.Groups["messageId"].Value,
            match.Groups["lastMessage"].Value == "true",
            DecodeMarkdown(match.Groups["markdown"].Value)
        );
    }

    static string DecodeMarkdown(string value)
      => JsonUtility.FromJson<JsonStringWrapper>("{\"value\":\"" + value + "\"}")?.value ?? "";

    static string BuildMarkdown(List<ConversationPair> pairs, bool includeEmpty)
    {
        var builder = new StringBuilder();

        foreach (var pair in pairs)
        {
            var response = pair.Response.Trim();
            if (!includeEmpty && string.IsNullOrEmpty(response)) continue;

            if (builder.Length > 0)
                builder.Append("\n\n");

            builder.Append("## User\n\n");
            builder.Append(pair.Prompt);
            builder.Append("\n\n## Assistant\n\n");
            builder.Append(string.IsNullOrEmpty(response) ? "(No extracted response)" : response);
        }

        return builder.ToString();
    }

    static string GetProjectRootPath()
      => Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();

    static string GetRelayLogPath()
      => Path.Combine(GetProjectRootPath(), RelayLogRelativePath);

    [Serializable]
    class JsonStringWrapper
    {
        public string value = "";
    }

    class ConversationPair
    {
        public readonly string Prompt;
        public readonly int PromptLine;
        public readonly string PromptMessageId;
        public readonly List<string> ResponseParts = new();
        public readonly List<int> ResponseLines = new();
        public string ResponseMessageId;

        public string Response => string.Concat(ResponseParts);

        public ConversationPair(string prompt, int promptLine, string promptMessageId)
        {
            Prompt = prompt;
            PromptLine = promptLine;
            PromptMessageId = promptMessageId;
        }
    }

    class ParsedLine
    {
        public readonly string Type;
        public readonly string MessageId;
        public readonly bool LastMessage;
        public readonly string Markdown;

        public ParsedLine(string type, string messageId, bool lastMessage, string markdown)
        {
            Type = type;
            MessageId = messageId;
            LastMessage = lastMessage;
            Markdown = markdown;
        }
    }
}

} // namespace AIAssistantExtensions
