**[简体中文](README.md) | English**

# Local Agents

Send tasks to an available local Codex, DeepSeek Harness or Antigravity CLI and view replies and approvals in Alpha Island.

- Version: **0.1.30**
- Plugin ID: `ai.local-agents`
- Minimum host plugin API: **7**

## Requirements

The selected tool must be installed, signed in and usable. Detecting its executable does not verify the account or access permissions.

## Features

- Choose a provider and working directory for a conversation owned by this plugin.
- Attach up to 16 files or folders from the file shelf, then enter an instruction.
- View streamed output, handle supported approvals and cancel tasks started by the plugin.

## Install and use

Follow the [common installation steps](../../README.en.md#install) first, then:

1. Install and enable the plugin, then detect available tools in its settings. Select the Codex executable manually if needed.
2. Open the Local Agents card and choose a provider and working directory.
3. Enter and send an instruction. To attach files, use Send to Local Agent from the file shelf.

## Notes and limits

- The plugin reuses the tool’s existing sign-in and consumes the corresponding account’s model allowance.
- It does not take over conversations already open in other apps. Local chat and external agent status detection are separate features.
- Documents and folders use local path references; Codex images use local-image input. Original files stay in place and existing access approvals still apply.

## Build from source

Run from the root of the plugin source checkout:

```powershell
dotnet build examples/LocalAgentsPlugin/LocalAgentsPlugin.csproj -c Release
```

Requires the .NET 8 SDK on Windows. A normal build only generates files; install the complete plugin folder manually. Workstation packaging commands are maintained outside this repository.

[Back to the plugin catalog](../../README.en.md#local-agents)
