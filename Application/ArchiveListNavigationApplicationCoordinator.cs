using MidFD.Commands;
using MidFD.Models;
using MidFD.Services;

namespace MidFD.Runtime;

internal sealed class ArchiveListNavigationApplicationCoordinator
{
    private readonly Dictionary<Guid, ArchiveListNavigationSession> _sessions = new();

    public Guid BeginSession(IReadOnlyList<ArchiveEntry> entries)
        => BeginSession(string.Empty, null, entries);

    public Guid BeginSession(string archivePath, string? sevenZipPath, IReadOnlyList<ArchiveEntry> entries)
    {
        Guid sessionId = Guid.NewGuid();
        _sessions.Add(sessionId, new ArchiveListNavigationSession(
            archivePath,
            sevenZipPath,
            entries.ToArray(),
            string.Empty));
        return sessionId;
    }

    public bool TryPrepareNavigation(
        ArchiveListNavigationCommandContext? request,
        out ArchiveListNavigationExecution proposal)
    {
        proposal = default!;
        if (request == null || !_sessions.TryGetValue(request.SessionId, out ArchiveListNavigationSession? session) ||
            session.PendingNavigationId != null)
        {
            return false;
        }

        string nextPath;
        switch (request.Action)
        {
            case ArchiveListNavigationAction.Parent:
                if (session.CurrentPath.Length == 0)
                {
                    return false;
                }

                string withoutTrailingSlash = session.CurrentPath.TrimEnd('/');
                int parentSeparator = withoutTrailingSlash.LastIndexOf('/');
                nextPath = parentSeparator < 0
                    ? string.Empty
                    : withoutTrailingSlash[..(parentSeparator + 1)];
                break;

            case ArchiveListNavigationAction.EnterDirectory:
                if (!TryResolveVisibleDirectory(session, request.TargetEntryPath, out nextPath))
                {
                    return false;
                }
                break;

            default:
                return false;
        }

        if (string.Equals(nextPath, session.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Guid proposalId = Guid.NewGuid();
        proposal = new ArchiveListNavigationExecution(
            request.SessionId,
            proposalId,
            session.Version,
            session.CurrentPath,
            nextPath);
        session.PendingNavigationId = proposalId;
        return true;
    }

    public ArchiveListNavigationCommitResult CompleteNavigation(
        ArchiveListNavigationExecution proposal,
        bool uiApplied)
    {
        if (!_sessions.TryGetValue(proposal.SessionId, out ArchiveListNavigationSession? session) ||
            session.PendingNavigationId != proposal.ProposalId)
        {
            return ArchiveListNavigationCommitResult.Stale;
        }

        session.PendingNavigationId = null;
        if (!uiApplied)
        {
            return ArchiveListNavigationCommitResult.UiRejected;
        }

        if (session.Version != proposal.ExpectedVersion ||
            !string.Equals(session.CurrentPath, proposal.PreviousPath, StringComparison.OrdinalIgnoreCase))
        {
            return ArchiveListNavigationCommitResult.Stale;
        }

        session.CurrentPath = proposal.CurrentPath;
        session.Version++;
        return ArchiveListNavigationCommitResult.Committed;
    }

    public bool TryCreateTextPreview(
        ArchiveTextPreviewCommandContext? request,
        out ArchiveTextPreviewExecution execution)
    {
        execution = default!;
        if (request == null || !_sessions.TryGetValue(request.SessionId, out ArchiveListNavigationSession? session))
        {
            return false;
        }

        if (session.Version != request.NavigationVersion || session.PendingNavigationId != null)
        {
            return false;
        }

        if (request.Operation == ArchiveTextPreviewOperation.RefreshExisting && request.EntryPath == null &&
            request.RawEntryPath == null && request.DisplayName == null)
        {
            execution = CreatePlaceholderExecution(
                request.SessionId,
                request.Operation,
                session.Version,
                "なし",
                "[プレビューするファイルが選択されていません。]");
            return true;
        }

        if (!TryResolveVisibleEntry(
                session,
                request,
                allowSyntheticDirectory: request.Operation == ArchiveTextPreviewOperation.RefreshExisting,
                out ArchiveEntry? entry))
        {
            return false;
        }

        string title = entry.Name;
        string archiveExtension = Path.GetExtension(session.ArchivePath);
        bool isZip = string.Equals(archiveExtension, ".zip", StringComparison.OrdinalIgnoreCase);
        bool isSevenZip = string.Equals(archiveExtension, ".7z", StringComparison.OrdinalIgnoreCase);
        bool supportedArchive = isZip || isSevenZip;

        if (entry.IsDirectory || entry.IsSyntheticDirectory)
        {
            if (request.Operation == ArchiveTextPreviewOperation.Open)
            {
                return false;
            }

            execution = CreatePlaceholderExecution(
                request.SessionId,
                request.Operation,
                session.Version,
                title,
                "[フォルダはプレビューできません。]",
                entry.EntryPath);
            return true;
        }

        bool isText = ArchiveEntryPreviewService.IsTextFile(entry.EntryPath);
        if (!supportedArchive || !isText)
        {
            if (request.Operation == ArchiveTextPreviewOperation.Open)
            {
                return false;
            }

            string placeholder = !supportedArchive
                ? "[ZIP/7z以外のファイルはプレビュー対象外です。]"
                : "[このファイル形式はテキストプレビュー対象外です。]";
            execution = CreatePlaceholderExecution(
                request.SessionId,
                request.Operation,
                session.Version,
                title,
                placeholder,
                entry.EntryPath);
            return true;
        }

        string backendPath = string.IsNullOrWhiteSpace(entry.RawEntryPath)
            ? entry.EntryPath
            : entry.RawEntryPath;
        ArchiveEntryPreviewResult result = isZip
            ? ArchiveEntryPreviewService.GetZipEntryTextPreview(session.ArchivePath, backendPath)
            : ArchiveEntryPreviewService.Get7zEntryTextPreview(session.ArchivePath, backendPath, session.SevenZipPath);

        execution = new ArchiveTextPreviewExecution(
            request.SessionId,
            session.Version,
            request.Operation,
            entry.EntryPath,
            title,
            result);
        return true;
    }

    public bool TryGetCurrentPath(Guid sessionId, out string currentPath)
    {
        if (_sessions.TryGetValue(sessionId, out ArchiveListNavigationSession? session))
        {
            currentPath = session.CurrentPath;
            return true;
        }

        currentPath = string.Empty;
        return false;
    }

    public void EndSession(Guid sessionId) => _sessions.Remove(sessionId);

    private static ArchiveTextPreviewExecution CreatePlaceholderExecution(
        Guid sessionId,
        ArchiveTextPreviewOperation operation,
        long navigationVersion,
        string title,
        string text,
        string? entryPath = null)
        => new(
            sessionId,
            navigationVersion,
            operation,
            entryPath,
            title,
            new ArchiveEntryPreviewResult { IsSupported = false, Text = text });

    private static bool TryResolveVisibleEntry(
        ArchiveListNavigationSession session,
        ArchiveTextPreviewCommandContext request,
        bool allowSyntheticDirectory,
        out ArchiveEntry entry)
    {
        entry = default!;
        if (!TryNormalizeArchivePath(request.EntryPath, out string normalizedRequest))
        {
            return false;
        }

        ArchiveEntry? match = session.Entries.FirstOrDefault(candidate =>
            TryNormalizeArchivePath(candidate.EntryPath, out string normalizedCandidate) &&
            string.Equals(normalizedCandidate, normalizedRequest, StringComparison.OrdinalIgnoreCase));
        if (match == null && allowSyntheticDirectory && normalizedRequest.EndsWith('/') &&
            IsDirectChild(session.CurrentPath, normalizedRequest, isDirectory: true) &&
            session.Entries.Any(candidate =>
                TryNormalizeArchivePath(candidate.EntryPath, out string candidatePath) &&
                candidatePath.StartsWith(normalizedRequest, StringComparison.OrdinalIgnoreCase)))
        {
            match = new ArchiveEntry
            {
                EntryPath = normalizedRequest,
                RawEntryPath = normalizedRequest,
                Name = normalizedRequest.TrimEnd('/').Split('/').Last(),
                IsDirectory = true,
                IsSyntheticDirectory = true
            };
        }

        if (match == null || !IsDirectChild(session.CurrentPath, normalizedRequest, match.IsDirectory))
        {
            return false;
        }

        string expectedRawPath = string.IsNullOrWhiteSpace(match.RawEntryPath)
            ? match.EntryPath
            : match.RawEntryPath;
        string requestedRawPath = request.RawEntryPath ?? string.Empty;
        if (!TryNormalizeArchivePath(expectedRawPath, out string normalizedRawPath) ||
            !string.Equals(normalizedRawPath, normalizedRequest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expectedRawPath, requestedRawPath, StringComparison.Ordinal) ||
            !string.Equals(match.Name, request.DisplayName, StringComparison.Ordinal))
        {
            return false;
        }

        entry = match;
        return true;
    }

    private static bool TryResolveVisibleDirectory(
        ArchiveListNavigationSession session,
        string? requestedEntryPath,
        out string nextPath)
    {
        nextPath = string.Empty;
        if (!TryNormalizeArchivePath(requestedEntryPath, out string normalizedTarget))
        {
            return false;
        }

        string targetPath = normalizedTarget.TrimEnd('/') + "/";
        if (!targetPath.StartsWith(session.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string relativePath = targetPath[session.CurrentPath.Length..].TrimEnd('/');
        if (relativePath.Length == 0 || relativePath.Contains('/'))
        {
            return false;
        }

        bool directoryExists = session.Entries.Any(entry =>
        {
            if (!TryNormalizeArchivePath(entry.EntryPath, out string entryPath))
            {
                return false;
            }

            string entryWithoutTrailingSlash = entryPath.TrimEnd('/');
            return (entry.IsDirectory && string.Equals(
                        entryWithoutTrailingSlash,
                        targetPath.TrimEnd('/'),
                        StringComparison.OrdinalIgnoreCase)) ||
                   entryPath.StartsWith(targetPath, StringComparison.OrdinalIgnoreCase);
        });

        if (!directoryExists)
        {
            return false;
        }

        nextPath = targetPath;
        return true;
    }

    private static bool IsDirectChild(string currentPath, string entryPath, bool isDirectory)
    {
        string normalizedCurrent = currentPath.Replace('\\', '/');
        string normalizedEntry = entryPath.Replace('\\', '/').TrimEnd('/');
        if (normalizedCurrent.Length > 0 && !normalizedEntry.StartsWith(normalizedCurrent, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string relative = normalizedEntry[normalizedCurrent.Length..];
        if (relative.Length == 0 || relative.Contains('/'))
        {
            return false;
        }

        return !isDirectory || !string.IsNullOrWhiteSpace(relative);
    }

    private static bool TryNormalizeArchivePath(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string candidate = value.Replace('\\', '/');
        if (candidate.StartsWith('/'))
        {
            return false;
        }

        string[] segments = candidate.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            return false;
        }

        normalized = string.Join('/', segments) + (candidate.EndsWith('/') ? "/" : string.Empty);
        return true;
    }

    private sealed class ArchiveListNavigationSession(
        string archivePath,
        string? sevenZipPath,
        IReadOnlyList<ArchiveEntry> entries,
        string currentPath)
    {
        public string ArchivePath { get; } = archivePath;
        public string? SevenZipPath { get; } = sevenZipPath;
        public IReadOnlyList<ArchiveEntry> Entries { get; } = entries;
        public string CurrentPath { get; set; } = currentPath;
        public long Version { get; set; }
        public Guid? PendingNavigationId { get; set; }
    }
}

internal enum ArchiveListNavigationCommitResult
{
    Committed,
    UiRejected,
    Stale
}

internal sealed record ArchiveListNavigationExecution(
    Guid SessionId,
    Guid ProposalId,
    long ExpectedVersion,
    string PreviousPath,
    string CurrentPath);

internal sealed record ArchiveTextPreviewExecution(
    Guid SessionId,
    long NavigationVersion,
    ArchiveTextPreviewOperation Operation,
    string? EntryPath,
    string DisplayName,
    ArchiveEntryPreviewResult Result);
