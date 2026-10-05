using Jiaolong.Diagnostics;

namespace Jiaolong.Diagnostics.Tests;

[TestClass]
public sealed class RedactionPolicyTests
{
    [TestMethod]
    public void Logs_redact_usernames_sids_tokens_and_user_paths()
    {
        var text = RedactionPolicy.RedactText(
            "Administrator S-1-5-21-123-456-789-1001 secret-token C:\\Users\\Administrator\\secret.json");

        Assert.IsFalse(text.Contains("Administrator", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("S-1-5-21-", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains("secret-token", StringComparison.Ordinal));
        Assert.IsFalse(text.Contains(@"C:\Users\", StringComparison.Ordinal));
    }
}
