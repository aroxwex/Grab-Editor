namespace GrabEditor.Services;

/// <summary>Undo / redo by keeping snapshots of the project (they are small).</summary>
public class UndoManager
{
    readonly List<string> _undo = new List<string>();
    readonly List<string> _redo = new List<string>();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Push(string snapshot)
    {
        if (_undo.Count > 0 && _undo[_undo.Count - 1] == snapshot) return;
        _undo.Add(snapshot);
        if (_undo.Count > 150) _undo.RemoveAt(0);
        _redo.Clear();
    }

    public string Undo(string current)
    {
        if (!CanUndo) return null;
        var s = _undo[_undo.Count - 1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(current);
        return s;
    }

    public string Redo(string current)
    {
        if (!CanRedo) return null;
        var s = _redo[_redo.Count - 1];
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
