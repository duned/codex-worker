namespace CodexWorker.Tests;

public sealed class ProvisioningPolicySourceTests
{
    [Trait("TestType", "Integration")]
    [Trait("Duration", "Fast")]
    [Fact]
    public void PolicyChangesApplyWithoutRestartAndInvalidFilesKeepTheLastPolicy()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"worker-policy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "worker.yml");
            var written = DateTime.UtcNow.AddMinutes(-10);
            void Write(string provisioning)
            {
                File.WriteAllText(path, $"projects:\n  directory: ./projects\nworker:\n  provisioning:\n{provisioning}");
                File.SetLastWriteTimeUtc(path, written = written.AddMinutes(1));
            }
            Write("    enabled: false\n");
            var configuration = GlobalWorkerConfiguration.Load(path);
            var messages = new List<string>();
            var source = new ProvisioningPolicySource(configuration, messages.Add);
            Assert.False(source.Current.Enabled);
            Assert.Empty(messages);

            Write("    enabled: true\n    allowedPrivilegedActions: [\"worker:service:restart\"]\n");
            Assert.True(source.Current.Enabled);
            Assert.Equal(["worker:service:restart"], source.Current.AllowedPrivilegedActions);
            Assert.Contains("reloaded", Assert.Single(messages), StringComparison.Ordinal);

            Write("    enabled: maybe\n");
            Assert.True(source.Current.Enabled);
            Assert.True(source.Current.Enabled);
            Assert.Equal(2, messages.Count);
            Assert.Contains("previous provisioning policy stays in effect", messages[1], StringComparison.Ordinal);

            // A rewrite that leaves the policy as it is changes nothing and says nothing.
            Write("    enabled: true\n    allowedPrivilegedActions: [\"worker:service:restart\"]\n");
            Assert.True(source.Current.Enabled);
            Assert.Equal(2, messages.Count);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
