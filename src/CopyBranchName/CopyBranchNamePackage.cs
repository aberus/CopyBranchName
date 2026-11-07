using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace CopyBranchName
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Copy Branch Name", "Copies the current Git branch name to the clipboard.", "1.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // Load in the background once a repository is active, so the QueryStatus handler that reads the right-clicked
    // branch row exists before the first branch context menu opens. Without it, the first use after startup would
    // run before the row could be captured.
    [ProvideAutoLoad(VSConstants.UICONTEXT.RepositoryOpen_string, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuidString)]
    public sealed class CopyBranchNamePackage : AsyncPackage
    {
        public const string PackageGuidString = "fd86fbd7-d932-4a69-812a-4b2084f3f754";

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            await CopyBranchNameCommand.InitializeAsync(this);
        }
    }
}
