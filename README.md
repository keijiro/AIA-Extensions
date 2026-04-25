# AIA Extensions

**AIA Extensions** is a custom Unity package that extends Unity AI Assistant.

## IME Proxy

<img width="400" height="284" alt="IME Proxy" src="https://github.com/user-attachments/assets/a6b5146d-1b63-47dd-ab4d-633357996974" />

**IME Proxy** is a custom window that works around several IME-related issues,
especially when entering Japanese text. Open it from
`Window > AI > IME Proxy`. It automatically copies the contents of its text
field to the AI Assistant chat field and sends the message when you press
`⌘ + Enter`.

## Conversation Extractor

<img width="400" height="312" alt="Conversation Extractor" src="https://github.com/user-attachments/assets/aba49e99-3751-47de-a7fd-7b9a4667866f" />

**Conversation Extractor** is a utility that extracts the conversation log
between the user and Unity AI Assistant from `Logs/relay.txt`.

## Font Modifier

This package includes `AssistantFontModifier`, which replaces the fonts used in
the AI Assistant chat window with Noto Sans JP. This helps prevent the font
atlas overflow issue that occurs in the Unity Editor on macOS.
