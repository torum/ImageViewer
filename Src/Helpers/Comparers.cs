using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ImageViewer.Helpers;

internal sealed partial class ExplorerLogicalSortComparer : IComparer<string>
{
    private static readonly NaturalSortComparer _fallbackComparer = new();

    public int Compare(string? x, string? y)
    {
        if (x is null && y is null) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        return OperatingSystem.IsWindows()
            ? NativeMethods.StrCmpLogicalW(x, y)
            : _fallbackComparer.Compare(x, y);
    }

    private static partial class NativeMethods
    {
        [LibraryImport("shlwapi.dll", EntryPoint = "StrCmpLogicalW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int StrCmpLogicalW(string x, string y);
    }
}

internal sealed class LinuxFilesComparer : IComparer<FileSystemInfo>
{
    private readonly NaturalSortComparer _nameComparer = new();

    public int Compare(FileSystemInfo? x, FileSystemInfo? y)
    {
        if (x == null || y == null) return 0;

        bool xIsDirectory = x.Attributes.HasFlag(FileAttributes.Directory);
        bool yIsDirectory = y.Attributes.HasFlag(FileAttributes.Directory);

        // Sort directories before files 
        if (xIsDirectory && !yIsDirectory) return 1;
        if (!xIsDirectory && yIsDirectory) return -1;

        // Both are directories or both are files, so sort by name naturally
        return _nameComparer.Compare(x.Name, y.Name);
    }
}

internal sealed partial class NaturalSortComparer : IComparer<string>, IComparer
{
    private static readonly Regex _regex = CompRegex();

    /// <summary>
    /// Compares two strings using natural sorting rules and case-sensitive current-culture text comparison.
    /// </summary>
    /// <param name="x">The first string to compare.</param>
    /// <param name="y">The second string to compare.</param>
    /// <returns>
    /// A signed integer that indicates the relative values of <paramref name="x"/> and <paramref name="y"/>.
    /// </returns>
    public int Compare(string? x, string? y)
    {
        // Handle null values
        if (x == null && y == null) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        // Split strings into parts
        var xParts = _regex.Split(x);
        var yParts = _regex.Split(y);

        int minLength = Math.Min(xParts.Length, yParts.Length);

        for (int i = 0; i < minLength; i++)
        {
            // Try to parse both parts as numbers
            if (int.TryParse(xParts[i], out int xNum) && int.TryParse(yParts[i], out int yNum))
            {
                int numComparison = xNum.CompareTo(yNum);
                if (numComparison != 0)
                {
                    return numComparison;
                }
            }
            // If at least one part is not a number, compare as strings
            else
            {
                int stringComparison = string.Compare(xParts[i], yParts[i], StringComparison.CurrentCulture);
                if (stringComparison != 0)
                {
                    return stringComparison;
                }
            }
        }

        // If all parts were equal, the longer string is "greater"
        return xParts.Length.CompareTo(yParts.Length);
    }

    /// <summary>
    /// Compares two objects (expected to be strings) using natural sorting rules.
    /// </summary>
    /// <param name="x">The first object to compare.</param>
    /// <param name="y">The second object to compare.</param>
    /// <returns>
    /// A signed integer that indicates the relative values of <paramref name="x"/> and <paramref name="y"/>.
    /// </returns>
    int IComparer.Compare(object? x, object? y)
    {
        return Compare(x as string, y as string);
    }

    [GeneratedRegex("([0-9]+)", RegexOptions.Compiled)]
    private static partial Regex CompRegex();
}
