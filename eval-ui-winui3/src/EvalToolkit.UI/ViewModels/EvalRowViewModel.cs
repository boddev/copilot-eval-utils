using System;
using CommunityToolkit.Mvvm.ComponentModel;
using EvalToolkit.UI.Editor;

namespace EvalToolkit.UI.ViewModels;

/// <summary>
/// One editable row in the Step 4 row editor. Snapshot semantics: the
/// row remembers its original values at load (or after Save) and
/// computes <see cref="IsDirty"/> as any field differing from the
/// snapshot. Raises <see cref="DirtyChanged"/> on dirty transitions
/// so the parent editor can keep an aggregate dirty count without
/// subscribing to per-row PropertyChanged.
/// </summary>
public sealed partial class EvalRowViewModel : ObservableObject
{
    private string _origPrompt;
    private string _origExpected;
    private string _origSource;
    private string _origActual;
    private bool _wasDirty;

    public EvalRowViewModel(EvalRowRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        _origPrompt = Prompt = record.Prompt ?? string.Empty;
        _origExpected = ExpectedAnswer = record.ExpectedAnswer ?? string.Empty;
        _origSource = SourceLocation = record.SourceLocation ?? string.Empty;
        _origActual = ActualAnswer = record.ActualAnswer ?? string.Empty;
    }

    [ObservableProperty]
    public partial string Prompt { get; set; }

    [ObservableProperty]
    public partial string ExpectedAnswer { get; set; }

    [ObservableProperty]
    public partial string SourceLocation { get; set; }

    [ObservableProperty]
    public partial string ActualAnswer { get; set; }

    public bool IsDirty =>
        !string.Equals(Prompt, _origPrompt, StringComparison.Ordinal) ||
        !string.Equals(ExpectedAnswer, _origExpected, StringComparison.Ordinal) ||
        !string.Equals(SourceLocation, _origSource, StringComparison.Ordinal) ||
        !string.Equals(ActualAnswer, _origActual, StringComparison.Ordinal);

    /// <summary>Fires when <see cref="IsDirty"/> transitions value.</summary>
    public event EventHandler? DirtyChanged;

    /// <summary>Revert all four fields to their snapshot values.</summary>
    public void Revert()
    {
        Prompt = _origPrompt;
        ExpectedAnswer = _origExpected;
        SourceLocation = _origSource;
        ActualAnswer = _origActual;
    }

    /// <summary>
    /// Snapshot current values as the new "clean" state. Called by editor
    /// after Save succeeds. Returns true if the row was actually accepted.
    /// </summary>
    public void AcceptChanges()
    {
        _origPrompt = Prompt;
        _origExpected = ExpectedAnswer;
        _origSource = SourceLocation;
        _origActual = ActualAnswer;
        UpdateDirty();
    }

    /// <summary>
    /// Conditionally accept changes only if the row's current values
    /// still match <paramref name="written"/> (the snapshot that was
    /// just written to disk). Returns true when the row was accepted.
    /// If the user edited the row between the save snapshot and now,
    /// the row stays dirty so the unsaved edits aren't silently lost.
    /// </summary>
    public bool AcceptChangesIfMatches(EvalRowRecord written)
    {
        ArgumentNullException.ThrowIfNull(written);
        if (!string.Equals(Prompt, written.Prompt, StringComparison.Ordinal) ||
            !string.Equals(ExpectedAnswer, written.ExpectedAnswer, StringComparison.Ordinal) ||
            !string.Equals(SourceLocation, written.SourceLocation, StringComparison.Ordinal) ||
            !string.Equals(ActualAnswer, written.ActualAnswer, StringComparison.Ordinal))
        {
            return false;
        }
        AcceptChanges();
        return true;
    }

    public EvalRowRecord ToRecord() =>
        new(Prompt, ExpectedAnswer, SourceLocation, ActualAnswer);

    partial void OnPromptChanged(string value) => UpdateDirty();
    partial void OnExpectedAnswerChanged(string value) => UpdateDirty();
    partial void OnSourceLocationChanged(string value) => UpdateDirty();
    partial void OnActualAnswerChanged(string value) => UpdateDirty();

    private void UpdateDirty()
    {
        bool now = IsDirty;
        if (now != _wasDirty)
        {
            _wasDirty = now;
            OnPropertyChanged(nameof(IsDirty));
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
