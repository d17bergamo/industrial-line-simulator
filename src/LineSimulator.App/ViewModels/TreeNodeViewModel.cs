using System.Collections.ObjectModel;
using LineSimulator.App.Mvvm;

namespace LineSimulator.App.ViewModels;

/// <summary>Nodo dell'albero della linea.</summary>
public sealed class TreeNodeViewModel : ObservableObject
{
    private bool _isExpanded = true;
    private bool _isSelected;
    private string _header;

    public TreeNodeViewModel(string header, object? model, string kind)
    {
        _header = header;
        Model = model;
        Kind = kind;
    }

    public string Header { get => _header; set => Set(ref _header, value); }
    public object? Model { get; }
    /// <summary>Categoria (es. "Sezioni"): usata per i comandi di aggiunta/eliminazione.</summary>
    public string Kind { get; }
    public ObservableCollection<TreeNodeViewModel> Children { get; } = new();
    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public IEnumerable<TreeNodeViewModel> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.SelfAndDescendants())
                yield return d;
    }
}
