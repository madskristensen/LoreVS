[marketplace]: <https://marketplace.visualstudio.com/items?itemName=MadsKristensen.LoreVS>
[vsixgallery]: <https://www.vsixgallery.com/extension/LoreVS.4beb5277-15de-4fff-bcaa-82af106d7233>
[repo]: <https://github.com/madskristensen/LoreVS>

# Lore for Visual Studio

[![Build](https://github.com/madskristensen/LoreVS/actions/workflows/build.yaml/badge.svg)](https://github.com/madskristensen/LoreVS/actions/workflows/build.yaml)
[![Install from VSIX Gallery](https://www.vsixgallery.com/badge/LoreVS.4beb5277-15de-4fff-bcaa-82af106d7233.png)][vsixgallery]
[![GitHub Sponsors](https://img.shields.io/github/sponsors/madskristensen)](https://github.com/sponsors/madskristensen)

Download this extension from the [Visual Studio Marketplace][marketplace]
or get the latest CI build from [Open VSIX Gallery][vsixgallery].

----------------------------------------------

Bring [Lore](https://github.com/EpicGames/lore) source control directly into
Visual Studio. Onboard a solution or folder, see file status at a glance, and
commit - all without leaving the IDE or dropping to a terminal.

## Add a solution to Lore

Right-click the solution node (or the Open Folder root) and choose
**Lore > Add to Lore Source Control**. Lore creates a repository on the
configured server, binds the loaded projects to the provider, and persists the
binding in the solution file so it is restored automatically the next time you
open the solution.

## See status at a glance

Once a solution is controlled, Visual Studio shows Lore status glyphs next to
files in Solution Explorer. Saving a document refreshes its glyph automatically,
and you can force a refresh any time with **Lore > Refresh Lore Status**.

## Commit from the IDE

Choose **Lore > Commit to Lore...**, enter a message, and Lore records the
change. Enable **Push after commit** in the options to automatically push every
successful commit to the remote.

## Manage a local Lore server

Lore operations talk to a `loreserver` instance. The extension can manage a
local server for you:

- A local server is started automatically before operations that need it.
- A single server is shared across multiple Visual Studio instances, so it is
  reused if one is already running.
- Start or stop it manually with **Lore > Start Local Lore Server** and
  **Lore > Stop Local Lore Server**.

By default the server keeps running after Visual Studio closes so other
instances can keep using it. Enable **Stop server on exit** to shut down the
server you started when the last instance closes.

## Install the Lore tools

The extension needs the `lore` and `loreserver` executables. If they are
missing, Lore offers to install them on startup by running the official
install script, which places the binaries in `%USERPROFILE%\bin`. You can also
install on demand with **Lore > Install Lore Tools**.

## Settings

Configure everything under **Tools > Options > Lore**:

| Setting | Description |
| ------- | ----------- |
| Identity | Commit identity (e.g. `you@example.com`) passed to `lore`. |
| Lore CLI path | Path to the `lore` executable, or `lore` to resolve from PATH. |
| Prompt to install tools | Offer to install the tools on startup when missing. |
| Push after commit | Automatically push every successful commit. |
| Manage a local server | Start a local `loreserver` automatically when needed. |
| Stop server on exit | Stop the local server when Visual Studio closes. |
| Lore server path | Path to the `loreserver` executable, or `loreserver` to resolve from PATH. |
| Server port (gRPC/QUIC) | gRPC/QUIC port used to build repository URLs. Default `41337`. |
| Server HTTP port | HTTP port polled for the health check. Default `41339`. |
| Persistent store path | Directory for a persistent server store so repositories survive restarts. |

## Get involved

Found a bug? Have an idea? Head to the [issue tracker][repo] - pull
requests are always welcome.
