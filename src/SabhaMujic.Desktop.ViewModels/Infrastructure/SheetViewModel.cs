using CommunityToolkit.Mvvm.Input;

namespace SabhaMujic.Desktop.ViewModels.Infrastructure;

/// <summary>
/// A modal editor (e.g. a checklist) shown with <see cref="IDialogService.ShowSheetAsync"/>. The sheet does its own
/// service calls and closes itself with <see cref="Close"/>; errors stay inside the sheet as <see cref="ViewModelBase.ErrorMessage"/>.
/// </summary>
public abstract partial class SheetViewModel : ViewModelBase
{
    public abstract string Title { get; }

    /// <summary>Raised with true after a successful save, false on cancel.</summary>
    public event Action<bool>? CloseRequested;

    /// <summary>True once the sheet closed after saving.</summary>
    public bool WasSaved { get; private set; }

    protected void Close(bool saved)
    {
        WasSaved = saved;
        CloseRequested?.Invoke(saved);
    }

    [RelayCommand]
    private void Cancel() => Close(false);
}
