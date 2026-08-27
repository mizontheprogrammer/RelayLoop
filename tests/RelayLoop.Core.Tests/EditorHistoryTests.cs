using RelayLoop.Core;

namespace RelayLoop.Core.Tests;

public sealed class EditorHistoryTests
{
    [Fact]
    public void PushUndoRedo_TraversesSnapshots()
    {
        var history = new EditorHistory<int>(0);
        history.Push(1);
        history.Push(2);

        Assert.Equal(1, history.Undo());
        Assert.Equal(0, history.Undo());
        Assert.Equal(1, history.Redo());
        Assert.Equal(2, history.Redo());
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void NewEditAfterUndo_ClearsRedoBranch()
    {
        var history = new EditorHistory<string>("a", static value => value);
        history.Push("b");
        history.Push("c");
        history.Undo();

        history.Push("replacement");

        Assert.False(history.CanRedo);
        Assert.Equal("replacement", history.Current);
        Assert.Throws<InvalidOperationException>(() => history.Redo());
    }

    [Fact]
    public void Capacity_DropsOldestUndoSnapshot()
    {
        var history = new EditorHistory<int>(0, capacity: 2);
        history.Push(1);
        history.Push(2);
        history.Push(3);

        Assert.Equal(2, history.UndoCount);
        Assert.Equal(2, history.Undo());
        Assert.Equal(1, history.Undo());
        Assert.False(history.TryUndo(out var current));
        Assert.Equal(1, current);
    }

    [Fact]
    public void CloneFunction_IsolatesStoredAndReturnedMutableState()
    {
        static List<int> Clone(List<int> values) => [.. values];
        var original = new List<int> { 1 };
        var history = new EditorHistory<List<int>>(original, Clone);
        original.Add(99);

        var next = history.Apply(values =>
        {
            values.Add(2);
            return values;
        });
        next.Add(100);

        Assert.Equal([1, 2], history.Current);
        Assert.Equal([1], history.Undo());
    }

    [Fact]
    public void Reset_ClearsBothStacksAndRaisesChanged()
    {
        var history = new EditorHistory<int>(0);
        var notifications = 0;
        history.Changed += (_, _) => notifications++;
        history.Push(1);
        history.Undo();

        history.Reset(42);

        Assert.Equal(3, notifications);
        Assert.Equal(42, history.Current);
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void ClearAllEvents_IsOneUndoableEdit()
    {
        var original = TestMacros.Create();
        var history = new EditorHistory<MacroDocument>(original, static document => document.DeepClone());
        var cleared = original.DeepClone();
        cleared.Events.Clear();

        history.Push(cleared);

        Assert.Empty(history.Current.Events);
        Assert.Equal(1, history.UndoCount);
        var restored = history.Undo();
        Assert.Equal(original.Events.Select(static item => item.Kind), restored.Events.Select(static item => item.Kind));
        Assert.Equal(original.Events.Select(static item => item.DelayMicroseconds), restored.Events.Select(static item => item.DelayMicroseconds));
        Assert.Empty(history.Redo().Events);
    }
}
