using System.Collections.Concurrent;
using System.Text.Json;

namespace Aurora86.Core.Dos;

/// <summary>
/// Persists the DOS file attribute byte beside a shared host directory.
/// DOS applications have used R/H/S/A since the FAT directory entry acquired
/// them; host permission bits cannot represent that byte portably. An owned
/// sidecar keeps guest attributes without chmod or renaming the user's files.
/// </summary>
internal sealed class HostDosAttributeStore
{
    // The sidecar keeps the name and format identifier it had before the
    // project was renamed Aurora86: shares created earlier already hold this
    // file, and a new name would leave the old one visible to DOS as an
    // ordinary user file while its attributes were silently lost.
    internal const string FileName = ".aurora386-dos-attributes.json";
    private const string FormatName = "Aurora386 DOS attributes";
    private const int FormatVersion = 1;
    internal const RedirectedFileAttributes MutableAttributes =
        RedirectedFileAttributes.ReadOnly | RedirectedFileAttributes.Hidden |
        RedirectedFileAttributes.System | RedirectedFileAttributes.Archive;

    private static readonly ConcurrentDictionary<string, object> DirectoryLocks = new();
    private readonly string _root;
    private readonly string _path;
    private readonly object _gate;

    /// <summary>The identifying header and portable path-to-attribute records written to disk.</summary>
    private sealed record Document(string Format, int Version, Dictionary<string, byte> Attributes);

    private enum Ownership
    {
        Missing,
        Owned,
        Collision
    }

    internal HostDosAttributeStore(string root)
    {
        _root = Path.GetFullPath(root);
        _path = Path.Combine(_root, FileName);
        _gate = DirectoryLocks.GetOrAdd(_root, _ => new object());
    }

    internal RedirectedFileAttributes? Get(string hostPath)
    {
        lock (_gate)
        {
            (Ownership ownership, Dictionary<string, byte> attributes) = Read();
            return ownership == Ownership.Owned && attributes.TryGetValue(Key(hostPath), out byte value)
                ? (RedirectedFileAttributes)value : null;
        }
    }

    internal bool IsPrivate(string hostPath)
    {
        if (!IsReservedPath(hostPath))
        {
            return false;
        }

        lock (_gate)
        {
            if (Read().ownership != Ownership.Owned)
            {
                return false;
            }

            string requestedName = Path.GetFileName(hostPath);
            if (requestedName == FileName)
            {
                return true;
            }

            // On a case-sensitive filesystem a differently cased sibling is
            // a separate user file. On a case-insensitive filesystem the same
            // spelling can instead be another route to our owned file.
            string[] names = Directory.GetFileSystemEntries(_root)
                .Select(Path.GetFileName)
                .OfType<string>()
                .ToArray();
            return !names.Contains(FileName, StringComparer.Ordinal) ||
                !names.Contains(requestedName, StringComparer.Ordinal);
        }
    }

    internal void RequireGuestPath(string hostPath)
    {
        // A colliding user file remains visible and usable. It is never
        // mistaken for our metadata; only persistence is refused in that case.
        if (IsPrivate(hostPath) ||
            (IsReservedPath(hostPath) && !File.Exists(hostPath) && !Directory.Exists(hostPath)))
        {
            throw new UnauthorizedAccessException("DOS attribute metadata is not a guest file.");
        }
    }

    internal void Set(string hostPath, RedirectedFileAttributes value, Action? hostMutation = null)
    {
        lock (_gate)
        {
            (Ownership ownership, Dictionary<string, byte> original) = Read();
            if (ownership == Ownership.Collision)
            {
                throw new IOException("The DOS attribute sidecar path belongs to another host file.");
            }

            string key = Key(hostPath);
            byte stored = (byte)(value & MutableAttributes);
            if (hostMutation is null && original.TryGetValue(key, out byte previous) && previous == stored)
            {
                return;
            }

            var changed = new Dictionary<string, byte>(original) { [key] = stored };
            PersistBeforeMutation(ownership, original, changed, hostMutation);
        }
    }

    internal void Remove(string hostPath, Action hostMutation)
    {
        ChangePaths(hostPath, null, hostMutation);
    }

    internal void Rename(string source, string target, Action hostMutation)
    {
        ChangePaths(source, target, hostMutation);
    }

    private void ChangePaths(string source, string? target, Action hostMutation)
    {
        lock (_gate)
        {
            (Ownership ownership, Dictionary<string, byte> original) = Read();
            if (ownership != Ownership.Owned)
            {
                hostMutation();
                return;
            }

            string from = Key(source);
            string? to = target is null ? null : Key(target);
            var changed = new Dictionary<string, byte>(original);
            string[] affected = original.Keys.Where(key => key == from ||
                key.StartsWith(from + "/", StringComparison.Ordinal)).ToArray();
            foreach (string key in affected)
            {
                byte value = changed[key];
                changed.Remove(key);
                if (to is not null)
                {
                    changed[to + key[from.Length..]] = value;
                }
            }

            if (affected.Length == 0)
            {
                hostMutation();
                return;
            }

            PersistBeforeMutation(ownership, original, changed, hostMutation);
        }
    }

    /// <summary>
    /// Persists the new metadata before touching host contents or names. A
    /// collision or unwritable sidecar therefore cannot truncate a host file.
    /// If the host operation fails, restore the old metadata before reporting
    /// the failure; report a rollback failure too instead of claiming success.
    /// </summary>
    private void PersistBeforeMutation(Ownership ownership, Dictionary<string, byte> original,
        Dictionary<string, byte> changed, Action? hostMutation)
    {
        Save(changed);
        if (hostMutation is null)
        {
            return;
        }

        try
        {
            hostMutation();
        }
        catch (Exception operationError) when (operationError is IOException or
                   UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            try
            {
                if (ownership == Ownership.Owned)
                {
                    Save(original);
                }
                else
                {
                    (Ownership currentOwnership, Dictionary<string, byte> current) = Read();
                    if (currentOwnership != Ownership.Owned || current.Count != changed.Count ||
                        !changed.All(entry => current.TryGetValue(entry.Key, out byte value) && value == entry.Value))
                    {
                        throw new IOException("DOS metadata changed before the failed operation could be rolled back.");
                    }

                    File.Delete(_path);
                }
            }
            catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
            {
                throw new IOException("Both the host operation and DOS metadata rollback failed.",
                    new AggregateException(operationError, rollbackError));
            }

            throw;
        }
    }

    private string Key(string hostPath)
    {
        string key = Path.GetRelativePath(_root, Path.GetFullPath(hostPath)).Replace(Path.DirectorySeparatorChar, '/');
        if (!ValidKey(key))
        {
            throw new UnauthorizedAccessException("Attribute paths must remain within their shared directory.");
        }

        // The existing resolver supplies actual host names, not DOS aliases.
        // Windows treats casing as equivalent; Unix preserves the resolved case.
        return OperatingSystem.IsWindows() ? key.ToUpperInvariant() : key;
    }

    private bool IsReservedPath(string hostPath)
    {
        return string.Equals(Path.GetFullPath(hostPath), _path, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidKey(string key)
    {
        if (string.IsNullOrEmpty(key) || Path.IsPathRooted(key))
        {
            return false;
        }

        return key.Split('/').All(part => part.Length != 0 && part is not "." and not "..");
    }

    private (Ownership ownership, Dictionary<string, byte> attributes) Read()
    {
        if (!File.Exists(_path))
        {
            return (Directory.Exists(_path) ? Ownership.Collision : Ownership.Missing, new());
        }

        try
        {
            Document? document = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path));
            if (document is not null && document.Format == FormatName &&
                document.Version == FormatVersion && document.Attributes is not null &&
                document.Attributes.All(entry => ValidKey(entry.Key) &&
                    (entry.Value & ~(byte)MutableAttributes) == 0))
            {
                return (Ownership.Owned, document.Attributes);
            }
        }
        catch (JsonException)
        {
            // Malformed or unrelated files belong to the user. Neither hide
            // them nor overwrite them merely because their names collide.
        }

        return (Ownership.Collision, new());
    }

    private void Save(Dictionary<string, byte> attributes)
    {
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new Document(FormatName, FormatVersion, attributes));
                stream.Flush(flushToDisk: true);
            }

            Ownership ownership = Read().ownership;
            if (ownership == Ownership.Collision)
            {
                throw new IOException("The DOS attribute sidecar path belongs to another host file.");
            }

            File.Move(temporary, _path, overwrite: ownership == Ownership.Owned);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
