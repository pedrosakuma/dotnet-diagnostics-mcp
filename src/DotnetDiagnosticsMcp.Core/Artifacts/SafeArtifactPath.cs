using System.Runtime.InteropServices;

namespace DotnetDiagnosticsMcp.Core.Artifacts;

/// <summary>
/// Path sandbox shared by every tool that writes an artifact to disk
/// (<c>collect_process_dump</c>, <c>capture_method_bytes</c>, and future B6 additions).
///
/// Policy:
/// <list type="bullet">
///   <item>The artifact root is set by the operator (see <see cref="IArtifactRootProvider"/>).</item>
///   <item>Caller-supplied paths are interpreted as <b>relative</b> sub-paths under that root.</item>
///   <item>Absolute paths, <c>..</c> traversal, and symlink escapes are rejected with
///         <see cref="ArtifactPathException"/>.</item>
///   <item>Directories are created with POSIX mode <c>0700</c>; files written with
///         <see cref="SetRestrictiveFilePermissions(string)"/> end up <c>0600</c>.</item>
/// </list>
///
/// On Windows the POSIX file-mode primitives are no-ops; ACL hardening relies on the
/// parent root being placed on a restricted directory by the operator (or the K8s
/// emptyDir / PVC mount in the sidecar topology). This matches how other tools in the
/// codebase treat Windows ACLs as out of scope.
/// </summary>
public static class SafeArtifactPath
{
    /// <summary>
    /// Resolves a sub-directory under <paramref name="root"/> for an artifact.
    /// </summary>
    /// <param name="root">Absolute, fully-qualified artifact-root path (from
    /// <see cref="IArtifactRootProvider.Root"/>).</param>
    /// <param name="requestedRelative">Optional caller-supplied relative path. When null
    /// or whitespace, <paramref name="defaultRelative"/> is used.</param>
    /// <param name="defaultRelative">Fallback relative sub-path (must not be absolute).</param>
    /// <param name="parameterName">Name of the parameter the caller supplied, for error
    /// messages.</param>
    /// <returns>The canonical absolute directory path. The directory is created with
    /// POSIX <c>0700</c> permissions on Unix.</returns>
    /// <exception cref="ArtifactPathException">The supplied path is absolute, escapes the
    /// root via <c>..</c> normalisation, or resolves through a symlink to a location
    /// outside the root.</exception>
    public static string ResolveDirectory(
        string root,
        string? requestedRelative,
        string defaultRelative,
        string parameterName = "outputDirectory")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(defaultRelative);

        var canonicalRoot = CanonicaliseRoot(root);
        var relative = string.IsNullOrWhiteSpace(requestedRelative) ? defaultRelative : requestedRelative;

        ValidateRelativeShape(relative, parameterName);

        var combined = Path.GetFullPath(Path.Combine(canonicalRoot, relative));
        EnsureUnderRoot(combined, canonicalRoot, parameterName);

        // Resolve any symlinked ancestors BEFORE creating the directory. CreateDirectory
        // would happily follow an attacker-controlled symlink in the middle of the path
        // and materialise a directory outside the root, leaving the side effect even if
        // we caught the escape afterwards.
        var resolvedBeforeCreate = ResolveSymlinks(combined);
        EnsureUnderRoot(resolvedBeforeCreate, canonicalRoot, parameterName);

        Directory.CreateDirectory(combined);

        // Re-check after CreateDirectory in case a TOCTOU race replaced an ancestor
        // with a symlink between the pre-check and the create call.
        var resolvedAfterCreate = ResolveSymlinks(combined);
        EnsureUnderRoot(resolvedAfterCreate, canonicalRoot, parameterName);

        ApplyDirectoryPermissions(combined);
        return combined;
    }

    /// <summary>
    /// Applies <c>0600</c> POSIX permissions to a newly-written artifact file. No-op on
    /// Windows (see class summary). On POSIX a chmod failure is fatal: it propagates as
    /// <see cref="ArtifactPathException"/> so the tool surfaces a structured error
    /// envelope rather than silently leaving a world-readable artifact on disk.
    /// </summary>
    public static void SetRestrictiveFilePermissions(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new ArtifactPathException(filePath,
                $"failed to apply 0600 permissions to artifact ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    /// <summary>
    /// Opens a brand-new file in a sandboxed directory with <c>0600</c> POSIX permissions
    /// applied at creation time (via <see cref="FileStreamOptions.UnixCreateMode"/>) so
    /// the file never exists at a permissive umask-derived mode. Fails if the file
    /// already exists (defends against a symlink swap at the leaf between
    /// <see cref="ResolveDirectory"/> and the open call).
    /// </summary>
    public static FileStream CreateRestrictedFile(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        return new FileStream(filePath, options);
    }

    private static string CanonicaliseRoot(string root)
    {
        var full = Path.GetFullPath(root);
        var resolved = ResolveSymlinks(full);
        // Normalise trailing separator so prefix comparison is unambiguous.
        return resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void ValidateRelativeShape(string relative, string parameterName)
    {
        if (Path.IsPathRooted(relative))
        {
            throw new ArtifactPathException(parameterName,
                "absolute paths are not permitted; supply a path relative to the artifact root.");
        }

        // Inspect raw segments before normalisation so '..' is rejected even if
        // Path.GetFullPath would have collapsed it harmlessly within the root.
        var separators = new[] { '/', '\\' };
        foreach (var segment in relative.Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..")
            {
                throw new ArtifactPathException(parameterName,
                    "'..' segments are not permitted in artifact paths.");
            }
        }
    }

    private static void EnsureUnderRoot(string candidate, string canonicalRoot, string parameterName)
    {
        var withSep = canonicalRoot + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(withSep, cmp) &&
            !string.Equals(candidate, canonicalRoot, cmp))
        {
            throw new ArtifactPathException(parameterName,
                "resolved path escapes the artifact root.");
        }
    }

    private static string ResolveSymlinks(string path)
    {
        // ResolveLinkTarget only follows the leaf — a symlink in the middle of the path
        // is invisible to it. Walk the full chain segment-by-segment so a symlinked
        // ancestor (the classic 'evil-link/data' escape) is collapsed before the prefix
        // check runs.
        var fullPath = Path.GetFullPath(path);
        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var segments = fullPath.Split(separators, StringSplitOptions.RemoveEmptyEntries);

        string resolved;
        if (OperatingSystem.IsWindows())
        {
            // Preserve the drive letter / UNC root.
            var rootLen = Path.GetPathRoot(fullPath)?.Length ?? 0;
            resolved = rootLen > 0 ? fullPath[..rootLen] : string.Empty;
        }
        else
        {
            resolved = Path.IsPathRooted(fullPath) ? "/" : string.Empty;
        }

        foreach (var segment in segments)
        {
            // Skip segments that were consumed by the root prefix on Windows
            // (e.g. "C:" appearing as the first split segment).
            if (OperatingSystem.IsWindows() && resolved.Length > 0
                && segment.Length == 2 && segment[1] == ':') continue;

            var next = resolved.Length == 0
                ? segment
                : Path.Combine(resolved, segment);

            try
            {
                if (Directory.Exists(next))
                {
                    var di = new DirectoryInfo(next);
                    var target = di.ResolveLinkTarget(returnFinalTarget: true);
                    resolved = target?.FullName ?? di.FullName;
                }
                else if (File.Exists(next))
                {
                    var fi = new FileInfo(next);
                    var target = fi.ResolveLinkTarget(returnFinalTarget: true);
                    resolved = target?.FullName ?? fi.FullName;
                }
                else
                {
                    // Non-existent leaf — symlinks below this point are impossible.
                    resolved = next;
                }
            }
            catch (IOException)
            {
                resolved = next;
            }
            catch (UnauthorizedAccessException)
            {
                resolved = next;
            }
        }

        return Path.GetFullPath(resolved);
    }

    private static void ApplyDirectoryPermissions(string path)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new ArtifactPathException(path,
                $"failed to apply 0700 permissions to artifact directory ({ex.GetType().Name}: {ex.Message}).");
        }
    }
}
