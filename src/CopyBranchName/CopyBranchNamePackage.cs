using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace CopyBranchName
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration(Vsix.Name, Vsix.Description, Vsix.Version)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // Load in the background once a repository is active, so the QueryStatus handler that reads the right-clicked
    // branch row exists before the first branch context menu opens. Without it, the first use after startup would
    // run before the row could be captured.
    [ProvideAutoLoad(VSConstants.UICONTEXT.RepositoryOpen_string, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuids.CopyBranchNameString)]
    public sealed class CopyBranchNamePackage : AsyncPackage
    {
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            await CopyBranchNameCommand.InitializeAsync(this);
        }
    }
}
