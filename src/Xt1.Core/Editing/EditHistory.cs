namespace Xt1.Core.Editing;

public interface IEditCommand
{
    string Name { get; }
    void Do();
    void Undo();
}

/// <summary>Undo/redo stack for all editing operations (field, raw byte, add/remove, encode).</summary>
public sealed class EditHistory
{
    private readonly List<IEditCommand> _undo = new();
    private readonly List<IEditCommand> _redo = new();

    public event Action? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public string? UndoName => _undo.Count > 0 ? _undo[^1].Name : null;
    public string? RedoName => _redo.Count > 0 ? _redo[^1].Name : null;

    /// <summary>Execute a command and push it onto the undo stack (clearing redo).</summary>
    public void Execute(IEditCommand command)
    {
        command.Do();
        _undo.Add(command);
        _redo.Clear();
        Changed?.Invoke();
    }

    /// <summary>Record an edit that has already been applied.</summary>
    public void Push(IEditCommand command)
    {
        _undo.Add(command);
        _redo.Clear();
        Changed?.Invoke();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var command = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        command.Undo();
        _redo.Add(command);
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var command = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        command.Do();
        _undo.Add(command);
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }
}

/// <summary>Command built from two delegates.</summary>
public sealed class DelegateCommand : IEditCommand
{
    private readonly Action _do;
    private readonly Action _undo;

    public DelegateCommand(string name, Action doAction, Action undoAction)
    {
        Name = name;
        _do = doAction;
        _undo = undoAction;
    }

    public string Name { get; }

    public void Do() => _do();

    public void Undo() => _undo();
}
