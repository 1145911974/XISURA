using Jiaolong.Service.Commands;

namespace Jiaolong.Service.Tests;

[TestClass]
public sealed class CircuitBreakerTests
{
    [TestMethod]
    public void Three_communication_failures_open_for_sixty_seconds()
    {
        var breaker = new CircuitBreaker();
        breaker.RecordCommunicationFailure();
        breaker.RecordCommunicationFailure();
        breaker.RecordCommunicationFailure();

        Assert.IsTrue(breaker.IsOpen);
        Assert.IsFalse(breaker.CanWrite);
    }
}
