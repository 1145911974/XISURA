using Jiaolong.Contracts.Commands;
using Jiaolong.Contracts.Models;

namespace Jiaolong_ControlCenter.Services;

// One process-wide lease is shared by the main and fast mode connections.
public sealed class ClientControlSessionLifecycle(
    Func<CancellationToken, Task<CommandResult>> begin,
    Func<ClientSessionExitConfiguration, CancellationToken, Task<CommandResult>> configure,
    Func<Guid, CancellationToken, Task<CommandResult>> end,
    Func<string, ExitDeviceDefaults?> loadDefaults)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ClientControlSessionHandle? handle;
    private bool configured;
    private int closing;
    private CommandResult? completed;

    internal bool IsReady => Volatile.Read(ref configured) && Volatile.Read(ref closing) == 0;

    public async Task StartAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (Volatile.Read(ref closing) != 0) throw new InvalidOperationException("控制会话正在退出恢复。");
            if (configured) return;
            if (handle is null)
            {
                var result = await begin(token);
                if (result is not { State: CommandState.Applied, Error: null, ClientSession: not null })
                    throw new InvalidOperationException("无法建立可恢复的控制会话，未应用设置。");
                handle = result.ClientSession;
                if (handle.SessionId == Guid.Empty || handle.HardwareEpoch <= 0 || !DeviceRecoveryKey.IsValid(handle.DeviceKey))
                { handle = null; throw new InvalidDataException("服务返回的控制会话无效。"); }
            }
            var configuredResult = await configure(new(handle.SessionId, loadDefaults(handle.DeviceKey)), token);
            if (configuredResult is not { State: CommandState.Applied, Error: null })
                throw new InvalidOperationException("退出恢复目标未确认，未应用设置。");
            configured = true;
        }
        finally { gate.Release(); }
    }

    public async Task<CommandResult> SendAsync(HardwareCommand command,
        Func<HardwareCommand, CancellationToken, Task<CommandResult>> send, CancellationToken token)
    {
        if (Volatile.Read(ref closing) != 0) throw new InvalidOperationException("控制会话正在退出恢复。");
        await gate.WaitAsync(token);
        try
        {
            if (!configured || handle is null || Volatile.Read(ref closing) != 0)
                throw new InvalidOperationException("控制会话未就绪，未应用设置。");
            if (command.ClientSessionId is { } supplied && supplied != handle.SessionId)
                throw new InvalidOperationException("控制命令的会话不匹配。");
            return await send(command with { ClientSessionId = handle.SessionId }, token);
        }
        finally { gate.Release(); }
    }

    public async Task<CommandResult?> EndAsync(CancellationToken token)
    {
        // Stop new submissions before draining an already submitted operation.
        Interlocked.Exchange(ref closing, 1);
        await gate.WaitAsync(token);
        try
        {
            if (completed is not null) return completed;
            if (handle is null) return null;
            var result = await end(handle.SessionId, token);
            if (result is { State: CommandState.Applied, Error: null }) completed = result;
            return result;
        }
        finally { gate.Release(); }
    }
}
