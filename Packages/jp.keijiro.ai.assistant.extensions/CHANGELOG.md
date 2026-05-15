# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](http://keepachangelog.com/en/1.0.0/)
and this project adheres to [Semantic Versioning](http://semver.org/spec/v2.0.0.html).

## [1.1.3] - 2026-05-15

### Fixed

- Fixed package skill registration for AI Assistant 2.8.0-pre.1 by using the built-in package skill discovery path.

### Changed

- Updated project and package metadata for AI Assistant 2.8.0-pre.1.

## [1.1.2] - 2026-05-08

### Fixed

- Fixed `game-view-capture` skill compatibility with AI Assistant 2.7.0-pre.2 by removing redundant package requirement metadata.

### Changed

- Updated project and package metadata for AI Assistant 2.7.0-pre.2.

## [1.1.1] - 2026-05-02

### Fixed

- Fixed package skill re-registration after AI Assistant skill rescans.

### Changed

- Updated project and package metadata for AI Assistant 2.7.0-pre.1.
- Updated README installation and issue reporting documentation.

## [1.1.0] - 2026-04-25

### Added

- Added the `game-view-capture` skill for capturing the Unity Game view through AI Assistant.
- Added test UI assets for validating Game view and UI capture workflows.

### Changed

- Updated README documentation for the current package features.

## [1.0.1] - 2026-04-24

### Changed

- Reimplemented Conversation Extractor as a Unity Editor window that reads `Logs/relay.txt` and lets you review, copy, and save extracted conversations.

## [1.0.0] - 2025-04-23

- Initial version.
