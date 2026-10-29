**[简体中文](README.md) | English**

# API Chat

Chat with a compatible API model in Alpha Island, with streamed replies, TXT personas, voice commands and optional tool calls.

- Version: **0.1.41**
- Plugin ID: `ai.api-chat`
- Minimum host plugin API: **7**

## Requirements

A working service URL, model name and API key. Voice input also needs a configured speech recognition service.

## Features

- Provider presets, fetched or manually entered model names, and connection testing.
- Conversation history and TXT persona imports; start a new conversation after changing a persona.
- Optional desktop tools, natural-language reminders and on-demand status queries.

## Install and use

Follow the [common installation steps](../../README.en.md#install) first, then:

1. After installation, open Settings → Plugins → API Chat → Plugin settings.
2. Enter the endpoint, model and key, save the key, then test the connection. Test again after changing the endpoint.
3. Enable the plugin, open the AI chat card and send a message. Enable tools and on-demand status access separately if needed.

## Notes and limits

- System status is not automatically attached to every message. Queries depend on the access settings and the model’s tool support.
- Messages and approved tool results are sent to the configured service and count toward its usage. Keys are encrypted for the current Windows user.
- The persona is attached to the first successful request of a conversation. A stateless API may not retain the complete persona afterward.

## Build from source

Run from the root of the plugin source checkout:

```powershell
dotnet build examples/ApiChatPlugin/ApiChatPlugin.csproj -c Release
```

Requires the .NET 8 SDK on Windows. A normal build only generates files; install the complete plugin folder manually. Workstation packaging commands are maintained outside this repository.

[Back to the plugin catalog](../../README.en.md#api-chat)
