# Copy Branch Name

A Visual Studio 2022 / 2026 extension that copies the current Git branch name to the clipboard. It implements the Developer Community suggestion [Copy git branch name to clipboard](https://developercommunity.visualstudio.com/t/Copy-git-branch-name-to-clipboard/1555830).

## Where to find it

| Entry point | Location |
|---|---|
| **Git > Copy Branch Name** | Next to *New Branch…*, *View Branch History* and *Manage Branches* |
| **Document tab context menu** | Right-click an editor tab, next to *Copy Full Path* / *Copy Relative Path* |
| **Right-click > Git submenu** | In the code editor, in Solution Explorer (files, projects, solution) and on document tabs, next to *Copy GitHub Permalink* |
| **Git Changes window "…" menu** | Next to *New Branch…* / *Manage Branches* |
| **Right-click a branch row** | In the Git Repository window's Branches pane and in the branch picker (status bar / Git Changes dropdown). Copies the **clicked** branch, not the current one; see below. |
| **Keyboard shortcut** | **Ctrl+Shift+Alt+B** (global). Rebind it under *Tools > Options > Environment > Keyboard* by searching for `CopyBranchName.CopyBranchName`. |
| **Command window** | `CopyBranchName.CopyBranchName` |

`Ctrl+Shift+Alt+B` is unbound in a default Visual Studio 2026 install. Visual Studio's Git shortcuts use the `Ctrl+0` chord, but the obvious `Ctrl+0, Ctrl+B` is already taken by Team Explorer's *Go To Builds*. When no repository is active, the shortcut does nothing.

The status bar confirms what was copied, for example `Copied Git branch name 'feature/ABC-123' to the clipboard (MyRepo).`

The command is **hidden (and disabled) unless Visual Studio has an active Git repository**. That's the same condition under which the status bar shows a branch, tracked by the `RepositoryOpen` UI context (`VSConstants.UICONTEXT.RepositoryOpen_guid`).
- **Before the extension loads:** a `.vsct` visibility constraint applies the rule, so the extension doesn't load just to decide whether to show itself.
- **After it loads:** `BeforeQueryStatus` checks the same context.

## Which repository is used

1. When a tool window such as Solution Explorer is active, the repository containing the **selected item**. Right-clicking an item activates that window, so in a multi-repo or submodule solution the right-clicked project wins over the active document.
2. Otherwise, the repository containing the **active document**.
3. Otherwise, the repository containing the **open solution or folder**.

Special cases:

- **Rebase in progress:** copies the branch being rebased (from `rebase-merge/head-name` or `rebase-apply/head-name`).
- **Detached HEAD:** copies the full commit SHA, and the status bar says HEAD is detached.
- **Linked worktrees and submodules:** supported. Their `.git` files point to the real Git directory.
- **Reftable repositories:** reported as unsupported (`git init --ref-format=reftable`).

## Copying a branch other than the current one

Right-clicking a branch row offers a second command, `CopyBranchName.CopySelectedBranchName`, that copies the right-clicked branch (for remote branches as displayed, e.g. `origin/feature/x`). It is placed only on the branch-row context menus and has no default shortcut.

Visual Studio hands the clicked branch only to its own context-menu handlers and never publishes it to the selection service, so the extension reads it from the list itself: the row under the mouse (or with keyboard focus, for menus opened with Shift+F10) while the menu is being built, through the row object's `GitRef.Name` (or `DisplayName` for the "common branches" rows of multi-repository solutions). Those are internal members of `Microsoft.TeamFoundation.Git.Controls`, identical in VS 2022 and VS 2026. If a Visual Studio update renames them, the entry simply disappears from the branch-row menus; nothing else is affected and the current-branch command never guesses.

For the row to be captured, the extension loads in the background as soon as a repository becomes active (`ProvideAutoLoad` on the `RepositoryOpen` UI context).

## Design notes

The branch is read directly from the repository's `HEAD` file. Visual Studio's own Git APIs were evaluated against VS 18.11 and rejected:

- **`IVsSccCurrentBranch.BranchName`** (the status-bar API) returns display text, not the raw branch name. It can return the localized "Checking out…" override during a checkout, or "Multiple branches" in multi-repo mode.
- **`IGitExt.CurrentBranch.Name`** returns a localized "(HEAD detached at …)" string for a detached HEAD. It also lives in `Microsoft.TeamFoundation.Git.Provider`, whose assembly version changes with each VS major version (17.0.0.0 / 18.0.0.0). A compile-time reference therefore can't target both VS 2022 and VS 2026.

The Git menu placement uses the Git provider's command set `{57735d06-c920-4415-a2e0-7d6e6fbdfa99}`, group `0xE006`, taken from its compiled command table. If the Git provider is disabled, the command still appears in the document tab menu.

## Existing alternatives (checked before implementing)

- **Visual Studio:** no built-in command as of VS 2026 18.11. The Git provider has *Copy Commit ID* and *Copy Commit Details*, but nothing for branches. No Visual Studio Marketplace extension for the full IDE does this.
- **VS Code:** several extensions exist, for example [Copy Branch Name (PKief)](https://marketplace.visualstudio.com/items?itemName=PKief.copy-branch-name). They don't install in Visual Studio.

## Build

Requires Visual Studio 2022 or later with the *Visual Studio extension development* workload.

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe" CopyBranchName.sln -restore -p:Configuration=Release
dotnet test test\CopyBranchName.Tests
```

The VSIX is written to `src\CopyBranchName\bin\Release\CopyBranchName.vsix`. Double-click it to install. Building also deploys it to the Experimental instance (`devenv /rootsuffix Exp`), and F5 in Visual Studio debugs it there.

## Limitations

- Visibility follows Visual Studio's notion of an active repository. A loose file opened from a Git repo, with no repository active in VS, doesn't show the command.
- The branch-row entries depend on internal Visual Studio members (see above). The Git menu, shortcut, tab, Git submenu and Git Changes entries do not.
