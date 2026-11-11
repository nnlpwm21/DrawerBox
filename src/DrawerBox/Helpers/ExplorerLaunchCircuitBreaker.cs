using DeskBoxWhite.Platform;

namespace DeskBoxWhite.Helpers;

/// <summary>
/// Stops the Explorer-hosted launch path from being retried against a sick
/// shell (#455). Every explorer-hosted open performs several synchronous
/// cross-process COM calls on the Explorer desktop thread, so once Explorer
/// starts answering with RPC-class failures, each further click re-pays the
/// same storm and can drag Explorer into a hang or crash. The breaker opens
/// on RPC-class failures only and re-arms solely when the shell process
/// identity changes — that is, Explorer actually restarted. Direct
/// <see cref="ExplorerShellLaunchService"/> callers such as the AOT shell
/// smoke diagnostics bypass the breaker by design: they must observe the real
/// Explorer path, not a locally recovered one.
/// </summary>
internal static class ExplorerLaunchCircuitBreaker
{
    // One RPC-class failure is already the signature of a wedged desktop
    // thread; a second attempt only re-pays the same cross-process hang.
    internal const int RpcFailureThreshold = 1;

    private static readonly ExplorerLaunchCircuitBreakerState State =
        new(RpcFailureThreshold, ProbeShellProcessId);

    internal static bool ShouldBypassExplorerHostedLaunch() =>
        State.ShouldBypass();

    internal static void RecordFailure(int hresult) =>
        State.RecordFailure(hresult);

    internal static void RecordSuccess() =>
        State.RecordSuccess();

    internal static void Reset() =>
        State.Reset();

    internal static bool IsRpcClassFailure(int hresult) =>
        ExplorerLaunchCircuitBreakerState.IsRpcClassFailure(hresult);

    private static uint ProbeShellProcessId()
    {
        IntPtr shellWindow = Win32Helper.GetShellWindow();
        if (shellWindow == IntPtr.Zero)
        {
            return 0;
        }

        _ = Win32Helper.GetWindowThreadProcessId(shellWindow, out uint processId);
        return processId;
    }
}

/// <summary>
/// The breaker's decision state, separated from the Win32 shell probe so the
/// transitions can be exercised without a live desktop.
/// </summary>
internal sealed class ExplorerLaunchCircuitBreakerState
{
    // An HRESULT with FACILITY_WIN32 wrapping a Win32 RPC error (1700-1799)
    // covers RPC_S_SERVER_UNAVAILABLE (0x800706BA), RPC_S_CALL_FAILED
    // (0x800706BE) and friends: the transport died on the way to the shell.
    // User cancellations, missing associations and plain Win32 errors must
    // not read as a broken desktop.
    private const int FacilityWin32 = 7;
    private const int RpcWin32ErrorLow = 1700;
    private const int RpcWin32ErrorHigh = 1799;

    private readonly object _gate = new();
    private readonly int _rpcFailureThreshold;
    private readonly Func<uint> _probeShellProcessId;

    private int _consecutiveRpcFailures;
    private bool _open;
    private uint _shellProcessIdAtOpen;

    internal ExplorerLaunchCircuitBreakerState(
        int rpcFailureThreshold,
        Func<uint> probeShellProcessId)
    {
        _rpcFailureThreshold = rpcFailureThreshold;
        _probeShellProcessId = probeShellProcessId;
    }

    internal static bool IsRpcClassFailure(int hresult)
    {
        int facility = (hresult >> 16) & 0x7FF;
        if (facility != FacilityWin32)
        {
            return false;
        }

        int win32Error = hresult & 0xFFFF;
        return win32Error is >= RpcWin32ErrorLow and <= RpcWin32ErrorHigh;
    }

    internal bool ShouldBypass()
    {
        lock (_gate)
        {
            if (!_open)
            {
                return false;
            }

            uint currentShellProcessId = _probeShellProcessId();
            if (currentShellProcessId == 0)
            {
                // No shell window at all: keep the breaker open. A missing
                // desktop cannot host launches anyway, and the next probe
                // will notice a restarted shell.
                return true;
            }

            if (currentShellProcessId == _shellProcessIdAtOpen)
            {
                return true;
            }

            // The shell process changed since the breaker opened, so Explorer
            // was restarted: give the explorer-hosted path a fresh chance.
            _consecutiveRpcFailures = 0;
            _open = false;
            _shellProcessIdAtOpen = 0;
            return false;
        }
    }

    internal void RecordFailure(int hresult)
    {
        lock (_gate)
        {
            if (!IsRpcClassFailure(hresult))
            {
                // Non-RPC failures say nothing about shell health.
                return;
            }

            _consecutiveRpcFailures++;
            if (!_open && _consecutiveRpcFailures >= _rpcFailureThreshold)
            {
                _open = true;
                _shellProcessIdAtOpen = _probeShellProcessId();
            }
        }
    }

    internal void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveRpcFailures = 0;
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            _consecutiveRpcFailures = 0;
            _open = false;
            _shellProcessIdAtOpen = 0;
        }
    }
}
