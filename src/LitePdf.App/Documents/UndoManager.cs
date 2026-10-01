using LitePdf.Core;

namespace LitePdf.App.Documents;

public interface IUndoableAction
{
    string Description { get; }
    Task UndoAsync(DocumentSession session);
    Task RedoAsync(DocumentSession session);
}

public sealed class UndoManager
{
    private readonly Stack<IUndoableAction> _undoStack = new();
    private readonly Stack<IUndoableAction> _redoStack = new();
    private const int MaxHistory = 100;

    public event Action? Changed;

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public string? NextUndoDescription => _undoStack.TryPeek(out var a) ? a.Description : null;
    public string? NextRedoDescription => _redoStack.TryPeek(out var a) ? a.Description : null;

    public void Push(IUndoableAction action)
    {
        _undoStack.Push(action);
        if (_undoStack.Count > MaxHistory)
        {
            var items = _undoStack.ToArray();
            _undoStack.Clear();
            for (int i = Math.Min(items.Length - 1, MaxHistory - 1); i >= 0; i--)
                _undoStack.Push(items[i]);
        }
        _redoStack.Clear();
        Changed?.Invoke();
    }

    public async Task<string?> UndoAsync(DocumentSession session)
    {
        if (_undoStack.Count == 0) return null;
        var action = _undoStack.Pop();
        await action.UndoAsync(session);
        _redoStack.Push(action);
        Changed?.Invoke();
        return action.Description;
    }

    public async Task<string?> RedoAsync(DocumentSession session)
    {
        if (_redoStack.Count == 0) return null;
        var action = _redoStack.Pop();
        await action.RedoAsync(session);
        _undoStack.Push(action);
        Changed?.Invoke();
        return action.Description;
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        Changed?.Invoke();
    }
}

public sealed class AddMarkupAction(
    IReadOnlyList<PdfAnnotation> annotations,
    AnnotationKind kind,
    AnnotationColor color) : IUndoableAction
{
    private IReadOnlyList<PdfAnnotation> _annotations = annotations;

    public string Description => kind switch
    {
        AnnotationKind.Underline => "Underline",
        AnnotationKind.StrikeOut => "Strikethrough",
        AnnotationKind.Squiggly => "Squiggly",
        _ => "Highlight",
    };

    public async Task UndoAsync(DocumentSession session)
    {
        foreach (var annot in _annotations)
            await session.RemoveMatchingAnnotationAsync(annot);
    }

    public async Task RedoAsync(DocumentSession session)
    {
        var newAnnots = new List<PdfAnnotation>();
        foreach (var group in _annotations.GroupBy(a => a.PageIndex))
        {
            int page = group.Key;
            foreach (var annot in group)
            {
                var rects = annot.Quads.Count > 0 ? annot.Quads : [annot.Bounds];
                var created = await session.AddMarkupToPageAsync(page, kind, rects, color);
                newAnnots.Add(created);
            }
        }
        _annotations = newAnnots;
    }
}

public sealed class AddNoteAction(
    PdfAnnotation note,
    int pageIndex,
    PointD position,
    string contents,
    AnnotationColor color) : IUndoableAction
{
    private PdfAnnotation _note = note;

    public string Description => "Note";

    public async Task UndoAsync(DocumentSession session)
    {
        await session.RemoveMatchingAnnotationAsync(_note);
    }

    public async Task RedoAsync(DocumentSession session)
    {
        _note = await session.AddNoteAsync(pageIndex, position, contents, color);
    }
}

public sealed class DeleteAnnotationAction(PdfAnnotation deleted) : IUndoableAction
{
    private PdfAnnotation _deleted = deleted;

    public string Description => _deleted.Kind == AnnotationKind.Note ? "Delete note" : "Delete annotation";

    public async Task UndoAsync(DocumentSession session)
    {
        if (_deleted.IsMarkup)
        {
            var rects = _deleted.Quads.Count > 0 ? _deleted.Quads : [_deleted.Bounds];
            _deleted = await session.AddMarkupToPageAsync(_deleted.PageIndex, _deleted.Kind, rects, _deleted.Color ?? AnnotationColor.Yellow);
        }
        else if (_deleted.Kind == AnnotationKind.Note)
        {
            var pos = new PointD(_deleted.Bounds.Left, _deleted.Bounds.Top);
            _deleted = await session.AddNoteAsync(_deleted.PageIndex, pos, _deleted.Contents, _deleted.Color ?? AnnotationColor.Yellow);
        }
    }

    public async Task RedoAsync(DocumentSession session)
    {
        await session.RemoveMatchingAnnotationAsync(_deleted);
    }
}

public sealed class ChangeColorAction(
    PdfAnnotation target,
    AnnotationColor oldColor,
    AnnotationColor newColor) : IUndoableAction
{
    private PdfAnnotation _target = target;

    public string Description => "Change color";

    public async Task UndoAsync(DocumentSession session)
    {
        var match = await session.FindMatchingAnnotationAsync(_target);
        if (match is not null)
        {
            await session.SetAnnotationColorAsync(match, oldColor);
            _target = match with { Color = oldColor };
        }
    }

    public async Task RedoAsync(DocumentSession session)
    {
        var match = await session.FindMatchingAnnotationAsync(_target);
        if (match is not null)
        {
            await session.SetAnnotationColorAsync(match, newColor);
            _target = match with { Color = newColor };
        }
    }
}

public sealed class EditNoteAction(
    PdfAnnotation target,
    string oldContents,
    string newContents) : IUndoableAction
{
    private PdfAnnotation _target = target;

    public string Description => "Edit note";

    public async Task UndoAsync(DocumentSession session)
    {
        var match = await session.FindMatchingAnnotationAsync(_target);
        if (match is not null)
        {
            await session.SetAnnotationContentsAsync(match, oldContents);
            _target = match with { Contents = oldContents };
        }
    }

    public async Task RedoAsync(DocumentSession session)
    {
        var match = await session.FindMatchingAnnotationAsync(_target);
        if (match is not null)
        {
            await session.SetAnnotationContentsAsync(match, newContents);
            _target = match with { Contents = newContents };
        }
    }
}
