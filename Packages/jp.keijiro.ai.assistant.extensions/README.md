# AIA Extensions

**AIA Extensions** is a custom Unity package that provides extensions for Unity
AI Assistant.

## IME Proxy

<img width="400" height="284" alt="IME Proxy" src="https://github.com/user-attachments/assets/a6b5146d-1b63-47dd-ab4d-633357996974" />

**IME Proxy** is a custom window that works around several IME-related issues,
especially when entering Japanese text. You can open it from
`Window > AI > IME Proxy`. It automatically copies the contents of its text
field to the AI Assistant chat field and sends the message when you press
`⌘ + Enter`.

## Font Modifier

This package includes `AssistantFontModifier` that replaces fonts used in the
AI Assistant chat window with the Noto Sans JP font. It suppresses a font atlas
overflow issue that occurs in the Unity Editor on macOS.
