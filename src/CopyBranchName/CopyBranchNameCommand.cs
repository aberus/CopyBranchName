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
        public const int CommandId = 0x0100;

        public static readonly Guid CommandSet = new Guid("1c83ad06-4056-49e5-b37e-da2b99968444");

        private readonly AsyncPackage _package;

        private readonly UIContext _repositoryOpen = UIContext.FromUIContextGuid(VSConstants.UICONTEXT.RepositoryOpen_guid);

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
                var menuCommand = new OleMenuCommand(command.Execute, new CommandID(CommandSet, CommandId));
                menuCommand.BeforeQueryStatus += command.OnBeforeQueryStatus;
                commandService.AddCommand(menuCommand);
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

        private void Execute(object sender, EventArgs e)
        {
            _package.JoinableTaskFactory.RunAsync(ExecuteAsync).FileAndForget("CopyBranchName/Execute");
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

            string repository = Path.GetFileName(head.WorkTreeRoot);
            if (string.IsNullOrEmpty(repository))
            {
                repository = head.WorkTreeRoot;
            }

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
