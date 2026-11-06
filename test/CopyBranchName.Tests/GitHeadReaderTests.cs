using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace CopyBranchName.Tests
{
    public sealed class GitHeadReaderTests : IDisposable
    {
        private const string Sha1 = "0123456789abcdef0123456789abcdef01234567";

        private readonly string _root = Path.Combine(Path.GetTempPath(), "CopyBranchNameTests", Guid.NewGuid().ToString("N"));

        public GitHeadReaderTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                // Git marks object files read-only.
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }

        [Fact]
        public void ReadsBranchFromFilePathDeepInsideWorkTree()
        {
            string repo = CreateFakeRepo("repo", "ref: refs/heads/feature/ABC-123-copy-branch\n");
            string file = WriteFile(Path.Combine(repo, "src", "Deep", "Program.cs"), "class P {}");

            GitHead head = GitHeadReader.TryRead(file);

            Assert.Equal(GitHeadKind.Branch, head.Kind);
            Assert.Equal("feature/ABC-123-copy-branch", head.Value);
            Assert.Equal(repo, head.WorkTreeRoot);
            Assert.False(head.IsRebasing);
        }

        [Fact]
        public void ReadsBranchFromDirectoryPath()
        {
            string repo = CreateFakeRepo("repo", "ref: refs/heads/main");

            GitHead head = GitHeadReader.TryRead(repo);

            Assert.Equal(GitHeadKind.Branch, head.Kind);
            Assert.Equal("main", head.Value);
        }

        [Fact]
        public void WorkTreeRootHasNoTrailingSeparatorWhenGivenSolutionDirectory()
        {
            // IVsSolution.GetSolutionInfo returns the solution directory with a trailing backslash.
            string repo = CreateFakeRepo("repo", "ref: refs/heads/main");

            GitHead head = GitHeadReader.TryRead(repo + Path.DirectorySeparatorChar);

            Assert.Equal(repo, head.WorkTreeRoot);
            Assert.Equal("repo", Path.GetFileName(head.WorkTreeRoot));
        }

        [Fact]
        public void ReportsDetachedHeadWithCommitSha()
        {
            string repo = CreateFakeRepo("repo", Sha1 + "\n");

            GitHead head = GitHeadReader.TryRead(repo);

            Assert.Equal(GitHeadKind.Detached, head.Kind);
            Assert.Equal(Sha1, head.Value);
        }

        [Theory]
        [InlineData("rebase-merge")]
        [InlineData("rebase-apply")]
        public void ReportsBranchBeingRebased(string rebaseDirectory)
        {
            string repo = CreateFakeRepo("repo", Sha1);
            WriteFile(Path.Combine(repo, ".git", rebaseDirectory, "head-name"), "refs/heads/topic\n");

            GitHead head = GitHeadReader.TryRead(repo);

            Assert.Equal(GitHeadKind.Branch, head.Kind);
            Assert.Equal("topic", head.Value);
            Assert.True(head.IsRebasing);
        }

        [Fact]
        public void RebaseOfDetachedHeadStaysDetached()
        {
            string repo = CreateFakeRepo("repo", Sha1);
            WriteFile(Path.Combine(repo, ".git", "rebase-merge", "head-name"), "detached HEAD\n");

            GitHead head = GitHeadReader.TryRead(repo);

            Assert.Equal(GitHeadKind.Detached, head.Kind);
        }

        [Fact]
        public void FollowsAbsoluteGitDirFileOfLinkedWorktree()
        {
            string worktreeGitDir = Path.Combine(_root, "main", ".git", "worktrees", "wt");
            WriteFile(Path.Combine(worktreeGitDir, "HEAD"), "ref: refs/heads/worktree-branch\n");
            string worktree = Path.Combine(_root, "wt");
            WriteFile(Path.Combine(worktree, ".git"), "gitdir: " + worktreeGitDir.Replace('\\', '/') + "\n");

            GitHead head = GitHeadReader.TryRead(Path.Combine(worktree, "file.txt"));

            Assert.Equal(GitHeadKind.Branch, head.Kind);
            Assert.Equal("worktree-branch", head.Value);
            Assert.Equal(worktree, head.WorkTreeRoot);
        }

        [Fact]
        public void FollowsRelativeGitDirFileOfSubmodule()
        {
            string superproject = CreateFakeRepo("super", "ref: refs/heads/main");
            WriteFile(Path.Combine(superproject, ".git", "modules", "lib", "HEAD"), Sha1);
            string submodule = Path.Combine(superproject, "lib");
            WriteFile(Path.Combine(submodule, ".git"), "gitdir: ../.git/modules/lib\n");

            GitHead head = GitHeadReader.TryRead(Path.Combine(submodule, "lib.cs"));

            Assert.Equal(GitHeadKind.Detached, head.Kind);
            Assert.Equal(submodule, head.WorkTreeRoot);
        }

        [Fact]
        public void BrokenGitFileIsUnknownInsteadOfFallingBackToParentRepo()
        {
            string parent = CreateFakeRepo("parent", "ref: refs/heads/main");
            string child = Path.Combine(parent, "child");
            WriteFile(Path.Combine(child, ".git"), "gitdir: does-not-exist\n");

            GitHead head = GitHeadReader.TryRead(child);

            Assert.Equal(GitHeadKind.Unknown, head.Kind);
            Assert.Equal(child, head.WorkTreeRoot);
        }

        [Fact]
        public void ReftablePlaceholderIsUnknown()
        {
            string repo = CreateFakeRepo("repo", "ref: refs/heads/.invalid\n");

            Assert.Equal(GitHeadKind.Unknown, GitHeadReader.TryRead(repo).Kind);
        }

        [Fact]
        public void ReturnsNullOutsideAnyRepository()
        {
            string directory = Path.Combine(_root, "not-a-repo");
            Directory.CreateDirectory(directory);

            Assert.Null(GitHeadReader.TryRead(directory));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("https://example.com/file.cs")]
        [InlineData("vsls:/remote/file.cs")]
        public void ReturnsNullForMissingOrNonFileSystemMonikers(string moniker)
        {
            Assert.Null(GitHeadReader.TryRead(moniker));
        }

        [Fact]
        public void MatchesRealGitBranch()
        {
            string repo = Path.Combine(_root, "real");
            Directory.CreateDirectory(repo);
            Git(repo, "init", "--quiet", "--initial-branch=main");
            Git(repo, "checkout", "--quiet", "-b", "feature/real-branch");

            GitHead head = GitHeadReader.TryRead(repo);

            Assert.Equal(GitHeadKind.Branch, head.Kind);
            Assert.Equal(Git(repo, "branch", "--show-current"), head.Value);
        }

        [Fact]
        public void MatchesRealGitDetachedHeadAndWorktree()
        {
            string repo = Path.Combine(_root, "real");
            Directory.CreateDirectory(repo);
            Git(repo, "init", "--quiet", "--initial-branch=main");
            Git(repo, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "--quiet", "--allow-empty", "-m", "init");
            string worktree = Path.Combine(_root, "real-wt");
            Git(repo, "worktree", "add", "--quiet", "-b", "wt-branch", worktree);
            Git(repo, "checkout", "--quiet", "--detach");

            GitHead mainHead = GitHeadReader.TryRead(repo);
            GitHead worktreeHead = GitHeadReader.TryRead(worktree);

            Assert.Equal(GitHeadKind.Detached, mainHead.Kind);
            Assert.Equal(Git(repo, "rev-parse", "HEAD"), mainHead.Value);
            Assert.Equal(GitHeadKind.Branch, worktreeHead.Kind);
            Assert.Equal("wt-branch", worktreeHead.Value);
        }

        [Fact]
        public void RealReftableRepositoryIsUnknown()
        {
            string repo = Path.Combine(_root, "reftable");
            Directory.CreateDirectory(repo);
            Git(repo, "init", "--quiet", "--ref-format=reftable", "--initial-branch=main");

            Assert.Equal(GitHeadKind.Unknown, GitHeadReader.TryRead(repo).Kind);
        }

        private string CreateFakeRepo(string name, string headContent)
        {
            string repo = Path.Combine(_root, name);
            WriteFile(Path.Combine(repo, ".git", "HEAD"), headContent);
            return repo;
        }

        private static string WriteFile(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            return path;
        }

        private static string Git(string workingDirectory, params string[] args)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using Process process = Process.Start(startInfo);
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, $"git {string.Join(" ", args)} failed: {error}");
            return output.Trim();
        }
    }
}
