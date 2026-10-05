namespace Jiaolong_ControlCenter.Services;

public sealed class ProductOsdService
{
    public event EventHandler<string>? Requested;

    public Task<bool> ShowAsync(string message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(message) || message.Length > 120) return Task.FromResult(false);
        Requested?.Invoke(this, message);
        return Task.FromResult(true);
    }
}
