using System;
using System.IO;
using System.Linq;
using System.Text;

namespace CopyBranchName
{
    internal enum GitHeadKind
    {
        /// <summary>HEAD points at a local branch.</summary>
        Branch,

        /// <summary>HEAD points directly at a commit.</summary>
        Detached,

        /// <summary>HEAD exists but its format is not understood (for example a reftable repository).</summary>
        Unknown,
    }

    internal sealed class GitHead
    {
        public GitHead(string workTreeRoot, GitHeadKind kind, string value, bool isRebasing = false)
        {
            WorkTreeRoot = workTreeRoot;
            Kind = kind;
            Value = value;
            IsRebasing = isRebasing;
        }

        public string WorkTreeRoot { get; }

        public GitHeadKind Kind { get; }

        /// <summary>The branch name for <see cref="GitHeadKind.Branch"/>, the commit SHA for <see cref="GitHeadKind.Detached"/>.</summary>
        public string Value { get; }

        /// <summary>True when HEAD is detached by a rebase and <see cref="Value"/> is the branch being rebased.</summary>
        public bool IsRebasing { get; }
    }

    /// <summary>
    /// Reads the checked-out branch straight from the repository's HEAD file. This avoids depending on
    /// Visual Studio's Git provider, whose public API only exposes display strings (such as
    /// "(HEAD detached at 1a2b3c4)") and whose assembly version changes with every Visual Studio release.
    /// </summary>
    internal static class GitHeadReader
    {
        private const string SymbolicRefPrefix = "ref:";
        private const string GitDirPrefix = "gitdir:";
        private const string BranchRefPrefix = "refs/heads/";

        // Repositories using the reftable backend keep this placeholder in HEAD; the real value lives in the reftable.
        private const string ReftablePlaceholder = "refs/heads/.invalid";

        /// <summary>
        /// Finds the Git working tree containing <paramref name="path"/> (a file or directory) and reads its HEAD.
        /// Returns null when the path is not inside a Git working tree.
        /// </summary>
        public static GitHead TryRead(string path)
        {
            string directory = GetStartDirectory(path);
            for (; directory != null; directory = Path.GetDirectoryName(directory))
            {
                string dotGit = Path.Combine(directory, ".git");
                if (Directory.Exists(dotGit))
                {
                    return ReadHead(directory, dotGit);
                }

                if (File.Exists(dotGit))
                {
                    // Linked worktrees and submodules have a ".git" file pointing at the real Git directory.
                    string gitDir = ResolveGitFile(dotGit, directory);
                    return gitDir != null
                        ? ReadHead(directory, gitDir)
                        : new GitHead(directory, GitHeadKind.Unknown, null);
                }
            }

            return null;
        }

        private static string GetStartDirectory(string path)
        {
            try
            {
                // Document monikers are not always file system paths (for example URLs or virtual documents),
                // and a relative path would silently resolve against devenv's current directory.
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                {
                    return null;
                }

                string fullPath = Path.GetFullPath(path);
                if (!Directory.Exists(fullPath))
                {
                    return Path.GetDirectoryName(fullPath);
                }

                // Visual Studio reports the solution directory with a trailing separator; keep it only for a drive root.
                string trimmed = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return trimmed.Length >= Path.GetPathRoot(fullPath).Length ? trimmed : fullPath;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static GitHead ReadHead(string workTreeRoot, string gitDir)
        {
            string head = ReadFirstLine(Path.Combine(gitDir, "HEAD"));
            if (head == null)
            {
                return new GitHead(workTreeRoot, GitHeadKind.Unknown, null);
            }

            if (head.StartsWith(SymbolicRefPrefix, StringComparison.Ordinal))
            {
                string refName = head.Substring(SymbolicRefPrefix.Length).Trim();
                string branch = refName == ReftablePlaceholder ? null : GetBranchName(refName);
                return branch != null
                    ? new GitHead(workTreeRoot, GitHeadKind.Branch, branch)
                    : new GitHead(workTreeRoot, GitHeadKind.Unknown, refName);
            }

            if (IsObjectId(head))
            {
                // A rebase detaches HEAD but records the branch being rebased.
                string rebasedBranch =
                    GetBranchName(ReadFirstLine(Path.Combine(gitDir, "rebase-merge", "head-name"))) ??
                    GetBranchName(ReadFirstLine(Path.Combine(gitDir, "rebase-apply", "head-name")));
                return rebasedBranch != null
                    ? new GitHead(workTreeRoot, GitHeadKind.Branch, rebasedBranch, isRebasing: true)
                    : new GitHead(workTreeRoot, GitHeadKind.Detached, head);
            }

            return new GitHead(workTreeRoot, GitHeadKind.Unknown, head);
        }

        private static string ResolveGitFile(string dotGitFile, string workTreeRoot)
        {
            string line = ReadFirstLine(dotGitFile);
            if (line == null || !line.StartsWith(GitDirPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                // The path may be relative to the working tree (submodules) or absolute (worktrees).
                string gitDir = Path.GetFullPath(Path.Combine(workTreeRoot, line.Substring(GitDirPrefix.Length).Trim()));
                return Directory.Exists(gitDir) ? gitDir : null;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException)
            {
                return null;
            }
        }

        private static string GetBranchName(string refName)
        {
            return refName != null && refName.StartsWith(BranchRefPrefix, StringComparison.Ordinal) && refName.Length > BranchRefPrefix.Length
                ? refName.Substring(BranchRefPrefix.Length)
                : null;
        }

        private static bool IsObjectId(string value)
        {
            // SHA-1 or SHA-256 object names.
            return (value.Length == 40 || value.Length == 64) && value.All(Uri.IsHexDigit);
        }

        private static string ReadFirstLine(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadLine()?.Trim();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
