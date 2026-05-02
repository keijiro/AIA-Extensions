# AIA Extensions

**AIA Extensions** is a custom Unity package that extends Unity AI Assistant.

## Installation

Send the following prompt to the AI Assistant:

```
Install jp.keijiro.ai.assistant.extensions via Package Manager.
If the Keijiro scoped registry is missing, add:
Name Keijiro, URL https://registry.npmjs.com, Scope jp.keijiro.
```

## IME Proxy

<img width="400" height="284" alt="IME Proxy" src="https://github.com/user-attachments/assets/a6b5146d-1b63-47dd-ab4d-633357996974" />

**IME Proxy** is a custom window that works around several IME-related issues
([UUM-134009]) ([UUM-136872]), especially when entering Japanese text. Open it
from `Window > AI > IME Proxy`. It automatically copies the contents of its
text field to the AI Assistant chat field and sends the message when you press
`⌘ + Enter`.

[UUM-134009]: https://issuetracker.unity3d.com/issues/enter-key-submits-prompt-when-confirming-japanese-ime-composition
[UUM-136872]: https://issuetracker.unity3d.com/issues/argumentoutofrangeexception-is-thrown-when-using-japanese-ime-and-rewriting-the-field-during-composition

## Conversation Extractor

<img width="400" height="312" alt="Conversation Extractor" src="https://github.com/user-attachments/assets/aba49e99-3751-47de-a7fd-7b9a4667866f" />

**Conversation Extractor** is a utility that extracts the conversation log
between the user and Unity AI Assistant from `Logs/relay.txt`.

## Game View Capture Skill

**Game View Capture Skill** lets Unity AI Assistant capture the Game view and
inspect the rendered image. It is useful for checking camera composition,
visual effects, and screen-space UI without entering Play Mode.

## Font Modifier

This package includes `AssistantFontModifier`, which replaces the fonts used in
the AI Assistant chat window with Noto Sans JP. This helps prevent the font
atlas overflow issue that occurs in the Unity Editor on macOS ([UUM-133847]).

[UUM-133847]: https://issuetracker.unity3d.com/issues/japanese-glyphs-are-missing-when-text-is-overflown
