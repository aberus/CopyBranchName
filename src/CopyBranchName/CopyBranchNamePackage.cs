using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace CopyBranchName
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Copy Branch Name", "Copies the current Git branch name to the clipboard.", "1.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
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
