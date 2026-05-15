---
name: game-view-capture
description: Capture the Unity Game view in Edit Mode so Assistant can visually inspect the rendered game output without entering Play Mode.
tools:
  - Unity.RunCommand
  - Unity.FindProjectAssets
  - Unity.GetImageAssetContent
  - Unity.GetConsoleLogs
enabled: true
---

# Game View Capture

Use this skill when the user wants Assistant to visually inspect what the Unity Game view renders. This includes camera output, composition, lighting, materials, sprites, post-processing, and screen-space overlays.

This workflow is intended for Edit Mode. Do not enter Play Mode unless the user explicitly asks for runtime-only behavior.

## When To Use

- Use this skill when Scene View screenshots are insufficient because they do not show the same camera composition, post-processing, or screen-space overlays as the Game view.
- Use this skill to verify visual output from a camera, including framing, object visibility, lighting, material appearance, sprite layout, and render pipeline effects.
- Use this skill for quick visual feedback loops while editing scenes, prefabs, visual effects, UI, or camera settings.
- Use this skill for UI checks when the user asks to inspect uGUI canvases, UI Toolkit `UIDocument` output, HUDs, menus, panels, text rendering, clipping, or overlay placement.

## Workflow

1. Use `Unity.RunCommand` to focus and repaint the Game view, then call `ScreenCapture.CaptureScreenshot(path)`.
2. Save the capture at `Assets/GameViewCapture.png`, overwriting the previous capture unless the user asks to preserve multiple captures.
3. Because `ScreenCapture.CaptureScreenshot` writes asynchronously, do not assume the texture is immediately importable in the same command.
4. After the capture command finishes, use a second step to import or locate the generated PNG.
5. During import, set texture import options so the screenshot keeps its original dimensions. In particular, disable NPOT scaling.
6. Use `Unity.FindProjectAssets` to find the screenshot asset if you need its instance ID.
7. Use `Unity.GetImageAssetContent` on the screenshot asset so Assistant can inspect the image visually.
8. Report the visible Game view state and any concrete issues found. Mention if the image is blank, stale, or appears to show the wrong view.

## Recommended Capture Command

Use this as the default `Unity.RunCommand` script:

```csharp
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

internal class CommandScript : IRunCommand
{
    public void Execute(ExecutionResult result)
    {
        const string path = "Assets/GameViewCapture.png";

        var gameViewType = Type.GetType("UnityEditor.GameView,UnityEditor");
        var gameView = gameViewType != null ? EditorWindow.GetWindow(gameViewType) : null;
        if (gameView != null)
        {
            gameView.Focus();
            gameView.Repaint();
        }

        ScreenCapture.CaptureScreenshot(path);
        result.Log("ScreenshotPath: {0}", path);
        result.Log("Wait briefly, then import or locate this asset before reading image content.");
    }
}
```

## Follow-Up Import Command

If the screenshot is not visible as an asset yet, use this command after the capture step:

```csharp
using System.IO;
using UnityEditor;

internal class CommandScript : IRunCommand
{
    public void Execute(ExecutionResult result)
    {
        const string path = "Assets/GameViewCapture.png";

        if (!File.Exists(path))
            throw new FileNotFoundException("The Game view screenshot has not been written yet.", path);

        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Default;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.maxTextureSize = 16384;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }

        var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Texture2D>(path);
        if (asset == null)
            throw new FileLoadException("The screenshot exists but could not be loaded as a Texture2D.", path);

        result.Log("ScreenshotAssetPath: {0}", path);
        result.Log("ScreenshotSize: {0}x{1}", asset.width, asset.height);
        result.Log("ScreenshotInstanceID: {0}", asset.GetInstanceID());
    }
}
```

## Constraints

- This skill captures the Game view, not the whole desktop and not the Scene view.
- Captures are written to `Assets/GameViewCapture.png` so they can be imported and inspected with `Unity.GetImageAssetContent`.
- Reuse `Assets/GameViewCapture.png` for repeated captures unless the user asks to preserve separate capture files.
- It does not use `superSize` or `stereoCaptureMode`.
- Keep the imported texture at its original pixel dimensions. Always set `TextureImporter.npotScale = TextureImporterNPOTScale.None` before reading the image content.
- The screenshot can be stale if the Game view has not repainted. Always focus and repaint the Game view before capture.
- The result depends on the Game view resolution, selected aspect ratio, active camera setup, and currently rendered scene state.
- Screen-space UI may depend on the Game view resolution and selected aspect ratio. Edit Mode UI Toolkit output requires an active `UIDocument` and a valid `PanelSettings` asset.
- If capture or import fails, inspect `Unity.GetConsoleLogs` before retrying.
