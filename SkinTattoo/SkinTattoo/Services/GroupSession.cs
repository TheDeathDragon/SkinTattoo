using System;
using System.Collections.Generic;
using System.Numerics;
using SkinTattoo.Core;
using SkinTattoo.Http;

namespace SkinTattoo.Services;

// Single-owner state for one TargetGroup's preview pipeline. Replaces the
// legacy fan-out across parallel mtrl-keyed dictionaries; keying by object
// reference avoids past collisions when groups share a DiffuseGamePath.
internal sealed class GroupSession
{
    public enum SessionState
    {
        Uninitialized,
        Initializing,
        Ready,
        Painting,
        Invalidated,
        Disposed,
    }

    public TargetGroup Group { get; }
    public SessionState State { get; private set; } = SessionState.Uninitialized;
    public DateTime LastTransitionUtc { get; private set; } = DateTime.UtcNow;
    public string? LastTransitionReason { get; private set; }

    public string? MtrlGamePath { get; set; }
    public string? MtrlDiskPath { get; set; }
    public readonly HashSet<string> InitializedRedirectKeys = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, string> PreviewDiskPaths = new(StringComparer.OrdinalIgnoreCase);

    public int EmissiveCBufferOffset { get; set; } = -1;

    public Vector3? LastAppliedEmissiveColor { get; set; }
    public EmissiveAnimMode LastAppliedAnimMode { get; set; }
    public float LastAppliedAnimSpeed { get; set; }
    public float LastAppliedAnimAmp { get; set; }
    public bool HasLastApplied { get; set; }

    public bool UsesSkinCt { get; set; }
    public RowPairAllocator? RowAlloc { get; set; }
    public string? IndexMapGamePath { get; set; }

    public GroupSession(TargetGroup group)
    {
        Group = group;
    }

    public void TransitionTo(SessionState next, string reason)
    {
        if (State == next)
            return;
        if (!IsLegalTransition(State, next))
        {
            DebugServer.AppendLog(
                $"[GroupSession] ILLEGAL transition {Group.Name}: {State} -> {next} (reason={reason})");
#if DEBUG
            throw new InvalidOperationException(
                $"Illegal session transition {State} -> {next} for group {Group.Name}");
#else
            return;
#endif
        }
        var prev = State;
        State = next;
        LastTransitionUtc = DateTime.UtcNow;
        LastTransitionReason = reason;
        DebugServer.AppendLog(
            $"[GroupSession] {Group.Name}: {prev} -> {next} (reason={reason})");

        var violations = AssertConsistent();
        foreach (var v in violations)
            DebugServer.AppendLog($"[GroupSession] INVARIANT {Group.Name}: {v}");
    }

    private static bool IsLegalTransition(SessionState from, SessionState to)
    {
        if (to == SessionState.Disposed) return from != SessionState.Disposed;
        return (from, to) switch
        {
            (SessionState.Uninitialized, SessionState.Initializing) => true,
            (SessionState.Initializing, SessionState.Ready) => true,
            (SessionState.Initializing, SessionState.Invalidated) => true,
            (SessionState.Ready, SessionState.Painting) => true,
            (SessionState.Painting, SessionState.Ready) => true,
            (SessionState.Ready, SessionState.Invalidated) => true,
            (SessionState.Painting, SessionState.Invalidated) => true,
            (SessionState.Invalidated, SessionState.Initializing) => true,
            (SessionState.Ready, SessionState.Initializing) => true,
            _ => false,
        };
    }

    // Mirrors legacy InvalidateEmissiveForGroup: drops mtrl + norm redirect keys
    // and emissive caches, keeps diffuse redirect + UsesSkinCt + RowAlloc.
    public void Invalidate(string reason)
    {
        if (!string.IsNullOrEmpty(MtrlGamePath))
        {
            InitializedRedirectKeys.Remove(MtrlGamePath);
            PreviewDiskPaths.Remove(MtrlGamePath);
        }
        if (!string.IsNullOrEmpty(Group.NormGamePath))
        {
            InitializedRedirectKeys.Remove(Group.NormGamePath);
            PreviewDiskPaths.Remove(Group.NormGamePath);
        }
        EmissiveCBufferOffset = -1;
        LastAppliedEmissiveColor = null;
        HasLastApplied = false;
        TransitionTo(SessionState.Invalidated, reason);
    }

    public void Dispose(string reason)
    {
        InitializedRedirectKeys.Clear();
        PreviewDiskPaths.Clear();
        EmissiveCBufferOffset = -1;
        LastAppliedEmissiveColor = null;
        HasLastApplied = false;
        UsesSkinCt = false;
        RowAlloc = null;
        IndexMapGamePath = null;
        TransitionTo(SessionState.Disposed, reason);
    }

    public IReadOnlyList<string> AssertConsistent()
    {
        var violations = new List<string>();
        if (State == SessionState.Disposed)
            return violations;

        if (UsesSkinCt && string.IsNullOrEmpty(MtrlGamePath))
            violations.Add($"S1 UsesSkinCt without MtrlGamePath ({Group.Name})");
        if (HasLastApplied && State == SessionState.Invalidated)
            violations.Add($"S2 HasLastApplied lingers in Invalidated state ({Group.Name})");
        if (EmissiveCBufferOffset > 0 && string.IsNullOrEmpty(MtrlGamePath))
            violations.Add($"S3 EmissiveCBufferOffset set without MtrlGamePath ({Group.Name})");
        if (RowAlloc != null && !UsesSkinCt)
            violations.Add($"S4 RowAlloc set but not UsesSkinCt ({Group.Name})");

        return violations;
    }

    public object DumpDiagnostics()
    {
        var redirects = new List<string>(InitializedRedirectKeys);
        redirects.Sort(StringComparer.OrdinalIgnoreCase);
        return new
        {
            group = Group.Name,
            state = State.ToString(),
            lastTransition = LastTransitionUtc.ToString("HH:mm:ss.fff"),
            lastReason = LastTransitionReason,
            mtrlGamePath = MtrlGamePath,
            mtrlDiskPath = MtrlDiskPath,
            usesSkinCt = UsesSkinCt,
            emissiveCBufferOffset = EmissiveCBufferOffset,
            hasLastApplied = HasLastApplied,
            lastEmColor = LastAppliedEmissiveColor.HasValue
                ? new[] { LastAppliedEmissiveColor.Value.X, LastAppliedEmissiveColor.Value.Y, LastAppliedEmissiveColor.Value.Z }
                : null,
            redirectKeys = redirects,
            previewDiskPathCount = PreviewDiskPaths.Count,
            indexMapGamePath = IndexMapGamePath,
        };
    }
}
