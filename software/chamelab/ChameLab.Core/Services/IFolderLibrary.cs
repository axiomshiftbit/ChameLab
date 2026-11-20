using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ChameLab.Models;

namespace ChameLab.Services;

/// <summary>
/// A library whose items are filed in nested folders (see <see cref="FolderPath"/>). Folders are
/// kept apart from the items, so a folder stays while it is empty.
/// </summary>
public interface IFolderLibrary
{
    /// <summary>Every folder's path, in tree order (a folder, then the folders in it).</summary>
    IReadOnlyList<string> Folders { get; }

    /// <summary>The folder each item is in (empty for none), one entry per item.</summary>
    IEnumerable<string> ItemFolders { get; }

    /// <summary>The folder at that path as it is spelled here (names ignore case), or null.</summary>
    string? FindFolder(string? path);

    /// <summary>Creates a folder (and those it is in); its path, or null when it is empty or there already.</summary>
    string? AddFolder(string path);

    /// <summary>Renames a folder, refiling what is in it; its new path, or null when the name is empty or taken.</summary>
    string? RenameFolder(string path, string newName);

    /// <summary>Moves a folder, with what is in it, into another (empty for the top); its new path, or null when refused.</summary>
    string? MoveFolder(string path, string parent);

    /// <summary>Deletes a folder and the folders in it; their items move up to the folder that held it.</summary>
    void RemoveFolder(string path);

    event Action? Changed;
}

