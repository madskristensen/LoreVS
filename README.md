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

## Review and commit in the Lore Changes window

Open **Lore > Lore Changes** for a dedicated panel, similar to the built-in Git
Changes window. The window shows the current branch with incoming/outgoing
indicators and lists every changed file with a status badge (M, A, D, C). From
here you can:

- Write a commit message and **Commit All**, or **Commit All and Push** in one
  step. Tick **Amend latest revision** to fold the changes into the previous
  revision instead.
- Double-click a file (or right-click > **Open Diff**) to compare the working
  copy against the committed version in the native Visual Studio diff viewer.
- Right-click a file and choose **Discard Changes...** to reset it to the last
  committed state.
- Use the toolbar to **Pull** (sync the latest remote revisions), **Push**
  committed revisions, or **Refresh** the change list.

## Settings

Configure everything under **Tools > Options > Lore**:

| Setting | Description |
| ------- | ----------- |
| Identity | Commit identity (e.g. `you@example.com`). |
| Push after commit | Automatically push every successful commit. |
| Server port (gRPC/QUIC) | gRPC/QUIC port used to build repository URLs. Default `41337`. |

## Get involved

Found a bug? Have an idea? Head to the [issue tracker][repo] - pull
requests are always welcome.
