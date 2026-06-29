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
**Lore > Add to Lore Source Control**. Enter a Lore server URL (e.g.
`lore://127.0.0.1:41337`) to create the repository on that server and bind a
remote so you can push, or leave it blank to create a fully offline repository.
Lore binds the loaded projects to the provider; the `.lore` folder records the
binding so it is restored automatically the next time you open the solution.

## Clone an existing repository

Right-click the solution node and choose **Lore > Clone Lore Repository...**.
Paste a repository URL (e.g. `lore://127.0.0.1:41337/my-project`) and pick a
local folder; Lore checks the server is reachable, clones the working tree, and
records the remote in `.lore` so push and pull work immediately.

![Clone Repository](art/clone-repo.png)

## See status at a glance

Once a solution is controlled, Visual Studio shows Lore status glyphs next to
files in Solution Explorer. Saving a document refreshes its glyph automatically,
and you can force a refresh any time with the **Refresh** button in the Lore
Changes window.

![Solution Explorer](art/solution-explorer.png)

Right-click a file under **Lore** for **Undo Changes** (revert edits to the
committed version), **Compare with Unmodified** (diff against the committed
version), and **Ignore and Untrack Item** (adds it to `.loreignore`).

## Review and commit in the Lore Changes window

Choose **Lore > Commit to Lore...** to open a dedicated panel, similar to the built-in Git
Changes window.

![Lore Changes Window](art/lore-changes-window.png)

The window shows the current branch with incoming/outgoing
indicators and arranges every changed file in a folder tree back to the
repository root, each file showing a status badge (M, A, D, C) on the right.
From here you can:

- Write a commit message and **Commit All**, or **Commit All and Push** in one
  step. Tick **Amend latest revision** to fold the changes into the previous
  revision instead.
- Expand or collapse folders, and double-click a file (or right-click >
  **Open Diff**) to compare the working
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
