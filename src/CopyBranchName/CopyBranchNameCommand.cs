using System;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using Task = System.Threading.Tasks.Task;

namespace CopyBranchName
{
    /// <summary>
    /// Copies the branch checked out in the repository of, in order: the item selected in the active
    /// tool window (Solution Explorer), the active document, or the open solution/folder. The command
    /// is only shown while Visual Studio's Git provider has an active repository.
    /// </summary>
    internal sealed class CopyBranchNameCommand
    {
        private readonly AsyncPackage _package;

        private readonly UIContext _repositoryOpen = UIContext.FromUIContextGuid(VSConstants.UICONTEXT.RepositoryOpen_guid);

        /// <summary>
        /// Visual Studio re-queries visible commands on idle. While a branch-row menu is open, the mouse and the
        /// keyboard focus are on the menu, so a row captured this recently is kept rather than hiding the entry.
        /// </summary>
        private static readonly TimeSpan CaptureLifetime = TimeSpan.FromSeconds(60);

        private SelectedBranch _capturedBranch;

        private DateTime _capturedAt;

        private CopyBranchNameCommand(AsyncPackage package)
        {
            _package = package;
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (await package.GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                var command = new CopyBranchNameCommand(package);
                var menuCommand = new OleMenuCommand(command.Execute, new CommandID(PackageGuids.CopyBranchNameCmdSet, PackageIds.CopyBranchNameCommand));
                menuCommand.BeforeQueryStatus += command.OnBeforeQueryStatus;
                commandService.AddCommand(menuCommand);

                // Placed on the context menus of branch rows; copies the right-clicked branch.
                var selectedCommand = new OleMenuCommand(command.ExecuteSelected, new CommandID(PackageGuids.CopyBranchNameCmdSet, PackageIds.CopySelectedBranchNameCommand));
                selectedCommand.BeforeQueryStatus += command.OnBeforeQueryStatusSelected;
                commandService.AddCommand(selectedCommand);
            }
        }

        private void OnBeforeQueryStatus(object sender, EventArgs e)
        {
            // Once the package is loaded Visual Studio no longer applies the .vsct VisibilityConstraints,
            // so the same UI context is checked here.
            ThreadHelper.ThrowIfNotOnUIThread();
            var command = (OleMenuCommand)sender;
            command.Visible = command.Enabled = _repositoryOpen.IsActive;
        }

        private void OnBeforeQueryStatusSelected(object sender, EventArgs e)
        {
            // Visual Studio builds a context menu with the right-clicked row still under the mouse, so the row is
            // captured here for the Execute that may follow. No row means the entry stays hidden.
            ThreadHelper.ThrowIfNotOnUIThread();
            SelectedBranch found = _repositoryOpen.IsActive ? SelectedBranchFinder.TryFind() : null;
            if (found != null)
            {
                _capturedBranch = found;
                _capturedAt = DateTime.UtcNow;
            }
            else if (DateTime.UtcNow - _capturedAt > CaptureLifetime)
            {
                _capturedBranch = null;
            }

            var command = (OleMenuCommand)sender;
            command.Visible = command.Enabled = _capturedBranch != null;
        }

        private void Execute(object sender, EventArgs e)
        {
            _package.JoinableTaskFactory.RunAsync(ExecuteAsync).FileAndForget("CopyBranchName/Execute");
        }

        private void ExecuteSelected(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            SelectedBranch captured = _capturedBranch;
            _capturedBranch = null;
            _package.JoinableTaskFactory.RunAsync(() => ExecuteSelectedAsync(captured)).FileAndForget("CopyBranchName/ExecuteSelected");
        }

        private async Task ExecuteSelectedAsync(SelectedBranch captured)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            // The row seen while the menu was built is the one that was right-clicked. The lookups below only
            // matter when this very command loaded the package, so nothing was captured.
            SelectedBranch branch = captured ?? SelectedBranchFinder.TryFind();
            if (branch == null)
            {
                await Task.Delay(250);
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                branch = SelectedBranchFinder.TryFind();
            }

            if (branch == null)
            {
                await SetStatusTextAsync("Could not determine the selected Git branch.");
                return;
            }

            if (TrySetClipboardText(branch.Name, out string error))
            {
                string repository = RepositoryName(branch.RepositoryPath);
                await SetStatusTextAsync(repository == null
                    ? $"Copied Git branch name '{branch.Name}' to the clipboard."
                    : $"Copied Git branch name '{branch.Name}' to the clipboard ({repository}).");
            }
            else
            {
                await SetStatusTextAsync(error);
            }
        }

        private async Task ExecuteAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            string selectedItemPath = await GetSelectedItemPathAsync();
            string documentPath = await GetActiveDocumentPathAsync();
            string solutionPath = await GetSolutionPathAsync();

            // Walking up the directory tree touches the file system, which may be a slow network share.
            await TaskScheduler.Default;
            GitHead head = GitHeadReader.TryRead(selectedItemPath) ?? GitHeadReader.TryRead(documentPath) ?? GitHeadReader.TryRead(solutionPath);

            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (head == null)
            {
                await SetStatusTextAsync("No Git repository found for the selected item, the active document or the open solution.");
                return;
            }

            string repository = RepositoryName(head.WorkTreeRoot);
            switch (head.Kind)
            {
                case GitHeadKind.Branch:
                    if (TrySetClipboardText(head.Value, out string error))
                    {
                        string state = head.IsRebasing ? ", rebase in progress" : string.Empty;
                        await SetStatusTextAsync($"Copied Git branch name '{head.Value}' to the clipboard ({repository}{state}).");
                    }
                    else
                    {
                        await SetStatusTextAsync(error);
                    }

                    break;

                case GitHeadKind.Detached:
                    if (TrySetClipboardText(head.Value, out error))
                    {
                        await SetStatusTextAsync($"HEAD is detached in {repository}; copied commit {head.Value.Substring(0, 7)} to the clipboard instead of a branch name.");
                    }
                    else
                    {
                        await SetStatusTextAsync(error);
                    }

                    break;

                default:
                    await SetStatusTextAsync($"Could not determine the current Git branch of {repository}: unsupported repository format.");
                    break;
            }
        }

        /// <summary>
        /// Path of the item selected in the active tool window (Solution Explorer), or null when a document
        /// window is active. Right-clicking an item in Solution Explorer activates that window, and in a
        /// multi-repository solution the item can belong to a different repository than the active document.
        /// </summary>
        private async Task<string> GetSelectedItemPathAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!(await _package.GetServiceAsync(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection monitorSelection) ||
                ErrorHandler.Failed(monitorSelection.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_WindowFrame, out object frame)) ||
                !(frame is IVsWindowFrame activeFrame) ||
                ErrorHandler.Failed(activeFrame.GetProperty((int)__VSFPROPID.VSFPROPID_Type, out object frameType)) ||
                Convert.ToInt32(frameType) != (int)__WindowFrameTypeFlags.WINDOWFRAMETYPE_Tool)
            {
                return null;
            }

            IntPtr hierarchyPtr = IntPtr.Zero;
            IntPtr containerPtr = IntPtr.Zero;
            try
            {
                if (ErrorHandler.Failed(monitorSelection.GetCurrentSelection(out hierarchyPtr, out uint itemId, out _, out containerPtr)) ||
                    hierarchyPtr == IntPtr.Zero ||
                    itemId == VSConstants.VSITEMID_NIL ||
                    itemId == VSConstants.VSITEMID_SELECTION)
                {
                    return null;
                }

                // For files, folders and projects this is the full path; the solution node yields the .sln path.
                return Marshal.GetObjectForIUnknown(hierarchyPtr) is IVsHierarchy hierarchy &&
                    ErrorHandler.Succeeded(hierarchy.GetCanonicalName(itemId, out string name))
                    ? name
                    : null;
            }
            finally
            {
                if (hierarchyPtr != IntPtr.Zero)
                {
                    Marshal.Release(hierarchyPtr);
                }

                if (containerPtr != IntPtr.Zero)
                {
                    Marshal.Release(containerPtr);
                }
            }
        }

        private async Task<string> GetActiveDocumentPathAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!(await _package.GetServiceAsync(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection monitorSelection) ||
                ErrorHandler.Failed(monitorSelection.GetCurrentElementValue((uint)VSConstants.VSSELELEMID.SEID_DocumentFrame, out object frame)))
            {
                return null;
            }

            return frame is IVsWindowFrame windowFrame &&
                ErrorHandler.Succeeded(windowFrame.GetProperty((int)__VSFPROPID.VSFPROPID_pszMkDocument, out object moniker))
                ? moniker as string
                : null;
        }

        private async Task<string> GetSolutionPathAsync()
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!(await _package.GetServiceAsync(typeof(SVsSolution)) is IVsSolution solution) ||
                ErrorHandler.Failed(solution.GetSolutionInfo(out string directory, out string file, out _)))
            {
                return null;
            }

            // In Open Folder mode there is no solution file, only the folder.
            return string.IsNullOrEmpty(directory) ? file : directory;
        }

        private static string RepositoryName(string workTreeRoot)
        {
            if (string.IsNullOrEmpty(workTreeRoot))
            {
                return null;
            }

            string name = Path.GetFileName(workTreeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrEmpty(name) ? workTreeRoot : name;
        }

        private static bool TrySetClipboardText(string text, out string error)
        {
            try
            {
                // WPF retries internally while another process holds the clipboard open.
                Clipboard.SetText(text);
                error = null;
                return true;
            }
            catch (ExternalException ex)
            {
                error = $"Could not copy the Git branch name to the clipboard: {ex.Message}";
                return false;
            }
        }

        private async Task SetStatusTextAsync(string text)
        {
            await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (await _package.GetServiceAsync(typeof(SVsStatusbar)) is IVsStatusbar statusBar)
            {
                statusBar.IsFrozen(out int frozen);
                if (frozen != 0)
                {
                    statusBar.FreezeOutput(0);
                }

                statusBar.SetText(text);
            }
        }
    }
}
