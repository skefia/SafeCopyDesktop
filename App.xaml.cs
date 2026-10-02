using System.Threading;
using System.Windows;

namespace SafeCopy.Desktop;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\SafeCopy.Desktop.SingleInstance",
            createdNew: out bool createdNew);

        if (!createdNew)
        {
            MessageBox.Show(
                "SafeCopy is already running. Finish or cancel the existing operation before starting another instance.",
                "SafeCopy",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }


    public void ReleaseSingleInstanceForRestart()
    {
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
    }

    public bool TryReacquireSingleInstanceAfterRestartFailure()
    {
        if (_singleInstanceMutex is not null) return true;
        try
        {
            _singleInstanceMutex = new Mutex(
                initiallyOwned: true,
                name: @"Local\SafeCopy.Desktop.SingleInstance",
                createdNew: out bool createdNew);
            if (!createdNew)
            {
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                return false;
            }
            return true;
        }
        catch
        {
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            return false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
