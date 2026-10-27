namespace Jiaolong.Contracts.Models;

public sealed record ClientControlSessionHandle(Guid SessionId, long HardwareEpoch, string DeviceKey,
    DateTimeOffset CapturedAtUtc);

public sealed record ClientSessionExitConfiguration(Guid SessionId, ExitDeviceDefaults? DeviceDefaults);
