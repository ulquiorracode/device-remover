# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-05-20

### Added
- **Low-Level Win32 setupapi.dll and cfgmgr32.dll P/Invoke Wrappers**:
  - Memory-optimized, allocation-free string marshaling with `stackalloc` and `ArrayPool`.
  - Proper device status query via `CM_Get_DevNode_Status` (`DN_DISABLEABLE`, `CM_PROB_DISABLED`).
  - Safe state-change executor via `DIF_PROPERTYCHANGE` with automatic reboot requirement checks.
- **Reflection-Free Configuration Service**:
  - System.Text.Json Source Generators for compile-time serialization.
  - Native AOT warning-free compilation.
  - Flexible Custom Converter supporting both string-based and object-based alias declarations.
- **Robust CLI and Command Router**:
  - Manual allocation-free CLI parser (`disable`, `enable`, `search`, `status`, `alias`, `profile`).
  - Highlights matching query tokens using Spectre.Console in live searches.
- **Automatic Administrative Elevation**:
  - Integrated UAC privilege checks.
  - Auto-relaunching elevated command executor when modifying device states.
- **Interactive TUI Dashboard**:
  - Arrow-key menu interface for instant profile switches and manual device status toggling.
- **Project Structure**:
  - Set up `.NET 10.0` console app optimized for Native AOT, speed optimizations, and maximum trimming.
  - MIT License and project files configured.
