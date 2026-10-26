using System.Text;
using PersonalAI.Core.Interfaces;
using PersonalAI.Core.Models.Entities;
using Renci.SshNet;
using Renci.SshNet.Common;
using PersonalAI.Core.Services.Secrets;

namespace PersonalAI.Core.Services.Ssh;

public sealed class SshNetRunner(ISecretsRepository secrets) : ISshRunner
{
    public async Task<SshCommandResult> RunAsync(SshServer server, string command, TimeSpan timeout, CancellationToken ct)
    {
        // Enforced here as well as on save, so a server stored earlier can never send another secret off-host.
        if (!SshSecrets.IsAllowed(server.SecretRef))
            return Failure(SshSecrets.NotAllowedMessage(server.SecretRef), null);

        var credential = await secrets.GetValueAsync(server.SecretRef, ct);
        if (string.IsNullOrEmpty(credential))
            return Failure($"Secret '{server.SecretRef}' was not found or is empty.", null);

        ConnectionInfo info;
        try
        {
            info = BuildConnectionInfo(server, credential, timeout);
        }
        catch (Exception ex) when (ex is SshException or ArgumentException or InvalidOperationException)
        {
            return Failure($"Could not use the credential in secret '{server.SecretRef}': {ex.Message}", null);
        }

        string? observedFingerprint = null;
        string? hostKeyError = null;

        return await Task.Run(() =>
        {
            using var client = new SshClient(info);
            client.HostKeyReceived += (_, e) =>
            {
                observedFingerprint = "SHA256:" + e.FingerPrintSHA256; // same form ssh-keygen -l prints
                e.CanTrust = server.HostKeyFingerprint is null || server.HostKeyFingerprint == observedFingerprint;
                if (!e.CanTrust)
                    hostKeyError = $"Host key changed: pinned {server.HostKeyFingerprint}, server presented {observedFingerprint}. " +
                        "Reset the pinned host key if the server was legitimately rebuilt.";
            };

            try
            {
                ct.ThrowIfCancellationRequested();
                client.Connect();
            }
            catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException or TimeoutException or IOException)
            {
                return Failure(hostKeyError ?? ex.Message, observedFingerprint);
            }

            try
            {
                using var cmd = client.CreateCommand(command);
                cmd.CommandTimeout = timeout;
                var stdout = cmd.Execute();
                return new SshCommandResult(true, cmd.ExitStatus ?? -1, stdout, cmd.Error ?? "", null, observedFingerprint);
            }
            catch (Exception ex) when (ex is SshException or IOException)
            {
                return new SshCommandResult(true, -1, "", "", ex.Message, observedFingerprint);
            }
            finally
            {
                if (client.IsConnected) client.Disconnect();
            }
        }, ct);
    }

    private static ConnectionInfo BuildConnectionInfo(SshServer server, string credential, TimeSpan timeout)
    {
        AuthenticationMethod auth;
        if (credential.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(credential.Replace("\r\n", "\n")));
            auth = new PrivateKeyAuthenticationMethod(server.Username, new PrivateKeyFile(stream));
        }
        else
        {
            auth = new PasswordAuthenticationMethod(server.Username, credential);
        }

        return new ConnectionInfo(server.Address, server.Port, server.Username, auth) { Timeout = timeout };
    }

    private static SshCommandResult Failure(string error, string? fingerprint)
    {
        return new(false, -1, "", "", error, fingerprint);
    }
}
