using System.Windows;
using Microsoft.Win32;

namespace WaveFlume.App.Services;

/// <summary>Dialogs behind an interface so view-models stay testable.</summary>
public interface IDialogService
{
    bool Confirm(string title, string message);
    void Error(string title, string message);
    void Info(string title, string message);
    string? OpenFile(string filter, string? initialDirectory = null);
    string? SaveFile(string filter, string fileName);
}

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow;

    public bool Confirm(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void Error(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void Info(string title, string message) =>
        MessageBox.Show(Owner!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public string? OpenFile(string filter, string? initialDirectory = null)
    {
        var d = new OpenFileDialog { Filter = filter, InitialDirectory = initialDirectory ?? "" };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }

    public string? SaveFile(string filter, string fileName)
    {
        var d = new SaveFileDialog { Filter = filter, FileName = fileName };
        return d.ShowDialog(Owner) == true ? d.FileName : null;
    }
}
