namespace LaserPilot.App.Models;

/// <summary>A saved copy of the whole design, for undo/redo.</summary>
public sealed record DesignSnapshot(List<DesignItem> Items, List<int> Selected, int Primary);

/// <summary>Snapshot-based undo/redo. Item clones share their (immutable) geometry and pixels, so snapshots are cheap.</summary>
public sealed class UndoStack
{
    private const int Limit = 100;
    private readonly List<DesignSnapshot> _undo = new();
    private readonly List<DesignSnapshot> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Push(DesignSnapshot before)
    {
        _undo.Add(before);
        if (_undo.Count > Limit) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>Returns the state to restore, given the current state (kept for redo).</summary>
    public DesignSnapshot? Undo(DesignSnapshot current)
    {
        if (_undo.Count == 0) return null;
        var s = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(current);
        return s;
    }

    public DesignSnapshot? Redo(DesignSnapshot current)
    {
        if (_redo.Count == 0) return null;
        var s = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(current);
        return s;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
