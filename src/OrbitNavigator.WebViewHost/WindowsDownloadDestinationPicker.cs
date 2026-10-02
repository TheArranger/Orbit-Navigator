using System.IO;
using Microsoft.Win32;

namespace OrbitNavigator.WebViewHost;

public interface IDownloadDestinationPicker
{
    string? PickDestination(string suggestedPath);
}

public sealed class WindowsDownloadDestinationPicker : IDownloadDestinationPicker
{
    public string? PickDestination(string suggestedPath)
    {
        var fileName = string.Empty;
        var initialDirectory = string.Empty;
        try
        {
            fileName = Path.GetFileName(suggestedPath);
            var directory = Path.GetDirectoryName(suggestedPath);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                initialDirectory = directory;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or PathTooLongException)
        {
            // The WebView suggestion is optional; the picker can start blank.
        }

        var dialog = new SaveFileDialog
        {
            AddExtension = false,
            CheckPathExists = true,
            FileName = fileName,
            InitialDirectory = initialDirectory,
            OverwritePrompt = true,
            Title = "Save download — Orbit Navigator",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
