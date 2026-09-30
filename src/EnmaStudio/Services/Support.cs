using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Data;

namespace EnmaStudio;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// Where Enma Studio keeps its files. Set ENMASTUDIO_HOME to keep everything in one folder instead
/// (portable mode): settings in it, the default workspace in its "workspace" subfolder.
/// </summary>
public static class AppPaths
{
    private static readonly string? Home = Environment.GetEnvironmentVariable("ENMASTUDIO_HOME") is { Length: > 0 } home
        ? Path.GetFullPath(home)
        : null;

    /// <summary>Settings, agent profiles, evals and the encrypted API key (%APPDATA%\EnmaStudio).</summary>
    public static string Data { get; } = Home ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EnmaStudio");

    /// <summary>The default workspace for scripts (Documents\Enma).</summary>
    public static string DefaultWorkspace { get; } = Home != null
        ? Path.Combine(Home, "workspace")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Enma");

    private static string WorkspaceFile => Path.Combine(Data, "workspace.txt");

    public static string LoadWorkspace()
    {
        try
        {
            string saved = File.Exists(WorkspaceFile) ? File.ReadAllText(WorkspaceFile).Trim() : "";
            return saved.Length > 0 && Directory.Exists(saved) ? saved : DefaultWorkspace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DefaultWorkspace;
        }
    }

    public static void SaveWorkspace(string folder)
    {
        try
        {
            Directory.CreateDirectory(Data);
            File.WriteAllText(WorkspaceFile, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Copies the bundled samples into an empty workspace.</summary>
    public static void SeedSamples(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            if (Directory.EnumerateFiles(folder).Any()) return;
            string samples = Path.Combine(AppContext.BaseDirectory, "samples");
            if (!Directory.Exists(samples)) return;
            foreach (string file in Directory.EnumerateFiles(samples, "*.enma"))
                File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public enum LogLevel { Info, Success, Error }

public sealed record LogLine(string Text, LogLevel Level);

/// <summary>The Output panel: timestamped lines, errors in red.</summary>
public static class OutputLog
{
    private const int MaxLines = 500;

    public static ObservableCollection<LogLine> Lines { get; } = [];

    public static void Write(string message, LogLevel? level = null)
    {
        if (Application.Current is { } app && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(() => Write(message, level));
            return;
        }
        var kind = level ?? (message.Contains("] error:", StringComparison.Ordinal) ? LogLevel.Error : LogLevel.Info);
        string stamp = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        foreach (string line in message.Split('\n')) Lines.Add(new LogLine($"[{stamp}] {line.TrimEnd('\r')}", kind));
        while (Lines.Count > MaxLines) Lines.RemoveAt(0);
    }

    public static void Clear() => Lines.Clear();
}

/// <summary>The Anthropic API key, DPAPI-encrypted for the current Windows user in %APPDATA%\EnmaStudio.</summary>
public static class KeyStore
{
    private static string FilePath => Path.Combine(AppPaths.Data, "api.key");

    public static string? Load()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment.Trim();
        try
        {
            if (!File.Exists(FilePath)) return null;
            return NativeMethods.Unprotect(Convert.FromBase64String(File.ReadAllText(FilePath))) is { } key
                ? Encoding.UTF8.GetString(key)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    public static bool Save(string key)
    {
        if (NativeMethods.Protect(Encoding.UTF8.GetBytes(key)) is not { } encrypted) return false;
        try
        {
            Directory.CreateDirectory(AppPaths.Data);
            File.WriteAllText(FilePath, Convert.ToBase64String(encrypted));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static void Clear()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal static class NativeMethods
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_DONOTROUND = 1;
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    /// <summary>Keeps Windows 11 from rounding the corners of the borderless window.</summary>
    public static void DisableRoundedCorners(IntPtr hwnd)
    {
        int preference = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }

    public static byte[]? Protect(byte[] data) => Dpapi(data, protect: true);

    public static byte[]? Unprotect(byte[] data) => Dpapi(data, protect: false);

    private static byte[]? Dpapi(byte[] data, bool protect)
    {
        var pinned = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new DataBlob { cbData = data.Length, pbData = pinned.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok) return null;
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            pinned.Free();
        }
    }
}
