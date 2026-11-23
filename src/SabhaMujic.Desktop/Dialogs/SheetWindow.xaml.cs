using System.Windows;
using SabhaMujic.Desktop.Services;
using SabhaMujic.Desktop.ViewModels.Infrastructure;

namespace SabhaMujic.Desktop.Dialogs;

/// <summary>Modal host for a <see cref="SheetViewModel"/> (category checklists). Closes when the sheet asks to.</summary>
public partial class SheetWindow : Window
{
    private readonly SheetViewModel _sheet;

    public SheetWindow(SheetViewModel sheet)
    {
        _sheet = sheet ?? throw new ArgumentNullException(nameof(sheet));
        InitializeComponent();
        DialogChrome.Attach(this);
        DataContext = sheet;
        sheet.CloseRequested += OnCloseRequested;
        Closed += (_, _) => sheet.CloseRequested -= OnCloseRequested;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    private void OnCloseRequested(bool saved)
    {
        if (IsLoaded && IsVisible)
        {
            DialogResult = saved;
        }
    }

    public bool Saved => _sheet.WasSaved;
}
