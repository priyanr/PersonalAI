namespace PersonalAI.Core.Services.Ssh;

/// <summary>Which vault secrets an SSH server may use. A server stores only a secret name, and testing or
/// collecting from it sends that secret's value (a key or password) to the server's address. Without a
/// restriction, anyone who can create a server could point one at a host they control, name an unrelated secret
/// (an API key, say), and have it delivered there as the "SSH password". So only secrets deliberately named for
/// SSH, starting with <see cref="Prefix"/>, are usable; everything else in the vault stays unreachable from servers.</summary>
public static class SshSecrets
{
    public const string Prefix = "SSH_";

    public static bool IsAllowed(string? secretName) =>
        secretName is not null && secretName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) && secretName.Length > Prefix.Length;

    public static string NotAllowedMessage(string secretName) =>
        $"Secret '{secretName}' can't be used for SSH. Only secrets named {Prefix}<something> (for example {Prefix}HOMELAB_KEY) " +
        "can be used by servers; create one on the Secrets page.";
}
