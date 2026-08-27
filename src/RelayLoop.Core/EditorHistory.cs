namespace RelayLoop.Core;

/// <summary>A bounded, thread-safe snapshot history suitable for macro editor operations.</summary>
public class EditorHistory<T>
{
    private readonly object _sync = new();
    private readonly Func<T, T> _clone;
    private readonly List<T> _undo = [];
    private readonly List<T> _redo = [];
    private T _current;

    /// <param name="clone">
    /// Creates an isolated snapshot. Reference types, including <see cref="MacroDocument"/>, must
    /// provide this explicitly; immutable reference types may consciously pass an identity clone.
    /// </param>
    public EditorHistory(T initialState, Func<T, T>? clone = null, int capacity = 100)
    {
        if (capacity is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (clone is null && !typeof(T).IsValueType)
        {
            throw new ArgumentNullException(
                nameof(clone),
                "A clone function is required for reference-type editor snapshots.");
        }

        _clone = clone ?? Identity;
        _current = _clone(initialState);
        Capacity = capacity;
    }

    public event EventHandler? Changed;

    public int Capacity { get; }

    public T Current
    {
        get
        {
            lock (_sync)
            {
                return _clone(_current);
            }
        }
    }

    public bool CanUndo
    {
        get
        {
            lock (_sync)
            {
                return _undo.Count != 0;
            }
        }
    }

    public bool CanRedo
    {
        get
        {
            lock (_sync)
            {
                return _redo.Count != 0;
            }
        }
    }

    public int UndoCount
    {
        get
        {
            lock (_sync)
            {
                return _undo.Count;
            }
        }
    }

    public int RedoCount
    {
        get
        {
            lock (_sync)
            {
                return _redo.Count;
            }
        }
    }

    /// <summary>Records a new editor snapshot and clears the redo branch.</summary>
    public void Push(T state)
    {
        EventHandler? handler;
        lock (_sync)
        {
            // Complete cloning before mutating either stack so a failing clone leaves history intact.
            var previousSnapshot = _clone(_current);
            var nextSnapshot = _clone(state);

            AddUndoSnapshot(previousSnapshot);
            _current = nextSnapshot;
            _redo.Clear();
            handler = Changed;
        }

        handler?.Invoke(this, EventArgs.Empty);
    }

    public T Apply(Func<T, T> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        EventHandler? handler;
        T result;
        lock (_sync)
        {
            // Keep the read/edit/push sequence in one critical section. Splitting this across Apply
            // and Push allowed a concurrent edit to be silently overwritten.
            var previousSnapshot = _clone(_current);
            var workingSnapshot = _clone(_current);
            var next = edit(workingSnapshot);
            var nextSnapshot = _clone(next);
            result = _clone(nextSnapshot);

            AddUndoSnapshot(previousSnapshot);
            _current = nextSnapshot;
            _redo.Clear();
            handler = Changed;
        }

        handler?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public bool TryUndo(out T state)
    {
        EventHandler? handler;
        lock (_sync)
        {
            if (_undo.Count == 0)
            {
                state = _clone(_current);
                return false;
            }

            _redo.Add(_clone(_current));
            var lastIndex = _undo.Count - 1;
            _current = _undo[lastIndex];
            _undo.RemoveAt(lastIndex);
            state = _clone(_current);
            handler = Changed;
        }

        handler?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool TryRedo(out T state)
    {
        EventHandler? handler;
        lock (_sync)
        {
            if (_redo.Count == 0)
            {
                state = _clone(_current);
                return false;
            }

            AddUndoSnapshot(_clone(_current));

            var lastIndex = _redo.Count - 1;
            _current = _redo[lastIndex];
            _redo.RemoveAt(lastIndex);
            state = _clone(_current);
            handler = Changed;
        }

        handler?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public T Undo()
    {
        if (!TryUndo(out var state))
        {
            throw new InvalidOperationException("There is no editor action to undo.");
        }

        return state;
    }

    public T Redo()
    {
        if (!TryRedo(out var state))
        {
            throw new InvalidOperationException("There is no editor action to redo.");
        }

        return state;
    }

    public void Reset(T state)
    {
        EventHandler? handler;
        lock (_sync)
        {
            _current = _clone(state);
            _undo.Clear();
            _redo.Clear();
            handler = Changed;
        }

        handler?.Invoke(this, EventArgs.Empty);
    }

    private static T Identity(T value) => value;

    private void AddUndoSnapshot(T snapshot)
    {
        _undo.Add(snapshot);
        if (_undo.Count > Capacity)
        {
            _undo.RemoveAt(0);
        }
    }
}

/// <summary>Compatibility name for consumers that prefer the conventional undo/redo terminology.</summary>
public sealed class UndoRedoHistory<T> : EditorHistory<T>
{
    public UndoRedoHistory(T initialState, Func<T, T>? clone = null, int capacity = 100)
        : base(initialState, clone, capacity)
    {
    }
}
