using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Poe2Pricer.Core;
using Poe2Pricer.Core.Runes;

namespace Poe2Pricer.Ui.ViewModels;

// The Runes tab: the 33 runes of the remnant list, each with its picture, name and effect in
// the window's language and the class the player gives it (RuneClass). "Save" writes the
// classes with the settings (RuneBook.SetOwn); "Default classes" puts the program's back
// into the rows, saved with the next save like any change.
public sealed class RunesViewModel : ObservableObject
{
    private readonly RuneBook? book;

    // book null: no runes (a window built without a pipeline, the tests); the tab stays empty.
    public RunesViewModel(RuneBook? book)
    {
        this.book = book;
        // The runes that do something for the loot first, in the game's order within a class;
        // the order follows the program's defaults, not the player's classes (rows must not
        // jump while one is being set).
        foreach (var r in (book?.Runes ?? []).OrderBy(r => Array.IndexOf(RuneRowViewModel.Order, r.Default)))
            Rows.Add(new RuneRowViewModel(r));
        ResetCommand = new RelayCommand(() =>
        {
            foreach (var row in Rows)
                row.Class = row.Rune.Default;
        });
        ResetChainCommand = new RelayCommand(() => ChainReset?.Invoke());
        Texts.Changed += () =>
        {
            foreach (var row in Rows)
                row.Reworded();
        };
    }

    public ObservableCollection<RuneRowViewModel> Rows { get; } = [];
    public IRelayCommand ResetCommand { get; }

    // "Reset rune chain": the chain of this map is forgotten (RuneChain.Reset, wired by the
    // program) — for a map the program did not see begin.
    public IRelayCommand ResetChainCommand { get; }
    public event Action? ChainReset;

    // The classes were saved and count in the game from now on.
    public event Action? Changed;

    // The classes into the book and its file. Returns why they did not reach the file, if
    // they did not; they count either way.
    public Told? Save()
    {
        if (book is null)
            return null;
        // Nothing changed: no file written or deleted, no relabel of an open list (review 05.11.2026).
        if (Rows.All(r => r.Class == r.Rune.Class))
            return null;
        var notWritten = book.SetOwn(Rows.ToDictionary(r => r.Rune.Id, r => r.Class));
        Changed?.Invoke();
        return notWritten;
    }
}

// One rune: picture, name and effect as the game shows them in the window's language, and
// its class as a choice among the four, named in that language.
public sealed class RuneRowViewModel(Rune rune) : ObservableObject
{
    public static readonly RuneClass[] Order = [RuneClass.Loot, RuneClass.Weaker, RuneClass.Neutral, RuneClass.Danger];
    private RuneClass @class = rune.Class;

    public Rune Rune { get; } = rune;
    public string Icon { get; } = System.IO.Path.Combine(AppContext.BaseDirectory, "data", "runes", "tome", rune.Id + ".png");
    public string Name => Rune.Name(Texts.Language);
    public string Effect => Rune.Effect(Texts.Language);

    public IReadOnlyList<string> Choices => Order.Select(Label).ToArray();

    public static string Label(RuneClass c) => c switch
    {
        RuneClass.Loot => Texts.Get("RuneClassLoot"),
        RuneClass.Weaker => Texts.Get("RuneClassWeaker"),
        RuneClass.Neutral => Texts.Get("RuneClassNeutral"),
        _ => Texts.Get("RuneClassDanger"),
    };

    public RuneClass Class
    {
        get => @class;
        set
        {
            if (SetProperty(ref @class, value))
            {
                OnPropertyChanged(nameof(Choice));
                OnPropertyChanged(nameof(Ground));
            }
        }
    }

    // The class as shown in the list of choices.
    public string Choice
    {
        get => Label(@class);
        set
        {
            int at = Array.IndexOf(Choices.ToArray(), value);
            if (at >= 0)
                Class = Order[at];
        }
    }

    public Brush Ground => RuneColours.Ground(@class);

    // The language of the window changed.
    public void Reworded()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Effect));
        OnPropertyChanged(nameof(Choices));
        OnPropertyChanged(nameof(Choice));
    }
}
