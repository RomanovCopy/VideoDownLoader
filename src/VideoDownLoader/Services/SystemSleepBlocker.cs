using System.Runtime.InteropServices;

namespace VideoDownLoader.Services;

public sealed class SystemSleepBlocker : IDisposable
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;
    private bool _disposed;

    public SystemSleepBlocker()
    {
        SetThreadExecutionState(EsContinuous | EsSystemRequired);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        SetThreadExecutionState(EsContinuous);
        _disposed = true;
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint executionState);
}
