using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;

namespace CopyBranchName
{
    internal sealed class SelectedBranch
    {
        public SelectedBranch(string name, string repositoryPath)
        {
            Name = name;
            RepositoryPath = repositoryPath;
        }

        public string Name { get; }

        /// <summary>Working tree of the branch's repository, or null when the row does not say.</summary>
        public string RepositoryPath { get; }
    }

    /// <summary>
    /// Finds the branch row under the mouse, or with keyboard focus, in one of Visual Studio's branch lists: the Branches pane
    /// of the Git Repository window and the branch picker opened from the status bar or the Git Changes window.
    /// Both lists are WPF selectors whose rows expose the branch through a "GitRef" property (VS 2022 and 2026,
    /// Microsoft.TeamFoundation.Git.Controls); multi-repository "common branch" rows expose "CommonBranch" and
    /// "DisplayName" instead. Visual Studio hands the right-clicked branch only to its own context-menu handlers,
    /// so it is read from the view. Everything is looked up by name, and a missing member means "no branch".
    /// </summary>
    internal static class SelectedBranchFinder
    {
        public static SelectedBranch TryFind()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // A menu opened by right-click: the row is under the mouse. Opened from the keyboard (Shift+F10, Apps):
            // the row has keyboard focus. Focus alone is not enough: once a context menu has closed, Visual Studio
            // does not give focus back to the row, so the next right-click would find nothing.
            return FromRow(FindRow(Mouse.DirectlyOver as DependencyObject)) ?? FromRow(FindRow(Keyboard.FocusedElement as DependencyObject));
        }

        private static object FindRow(DependencyObject start)
        {
            object rowFromItem = null;
            for (DependencyObject element = start; element != null; element = GetParent(element))
            {
                if (element is ListBoxItem item)
                {
                    rowFromItem = item.DataContext;
                }
                else if (element is Selector selector)
                {
                    // Right-clicking a row selects it, so the selection is the clicked row.
                    return selector.SelectedItem ?? rowFromItem;
                }
            }

            return null;
        }

        private static DependencyObject GetParent(DependencyObject element)
        {
            // Text runs and other content elements have no visual parent.
            return (element is Visual ? VisualTreeHelper.GetParent(element) : null) ?? LogicalTreeHelper.GetParent(element);
        }

        private static SelectedBranch FromRow(object row)
        {
            if (row == null)
            {
                return null;
            }

            object gitRef = GetProperty(row, "GitRef");
            if (gitRef != null)
            {
                // Tags are rows of the same list and carry a GitRef too.
                return gitRef.GetType().GetInterface("IGitBranch") != null && GetProperty(gitRef, "Name") is string name && name.Length > 0
                    ? new SelectedBranch(name, GetProperty(gitRef, "RepositoryPath") as string)
                    : null;
            }

            return GetProperty(row, "CommonBranch") != null && GetProperty(row, "DisplayName") is string displayName && displayName.Length > 0
                ? new SelectedBranch(displayName, null)
                : null;
        }

        private static object GetProperty(object target, string name)
        {
            try
            {
                return target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);
            }
            catch (Exception ex) when (ex is TargetInvocationException || ex is AmbiguousMatchException || ex is MethodAccessException)
            {
                return null;
            }
        }
    }
}
