using Atlas.Application.Assessments;
using Atlas.Application.Credentials;
using Atlas.Connector.Abstractions;
using Atlas.Domain.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace Atlas.IntegrationTests;

public sealed class CredentialsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Store_rotate_resolve_and_delete_with_in_use_protection()
    {
        await using var provider = fixture.BuildServices();
        var name = "gh-" + Guid.NewGuid().ToString("N")[..8];

        // Store: metadata comes back, never the secret.
        using (var scope = provider.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<CredentialsService>();
            var summary = await service.UpsertAsync(name, null, "ghp_first", "GitHub org token", CancellationToken.None);
            Assert.Equal(name, summary.Name);
            Assert.Null(summary.Username);
            Assert.Equal(0, summary.UsedByAssessments);
            Assert.Null(summary.LastUsedAtUtc);

            await Assert.ThrowsAsync<ArgumentException>(() => service.UpsertAsync("bad name!", null, "x", null, CancellationToken.None));
        }

        // The connector-facing provider decrypts through its own scope and records use.
        var resolver = provider.GetRequiredService<ICredentialProvider>();
        var value = await resolver.ResolveAsync(new Atlas.Domain.Sources.SourceReference("github", "org/repo", null, name), CancellationToken.None);
        Assert.Equal("ghp_first", value!.Secret);
        Assert.Null(value.Username);
        Assert.Null(await resolver.ResolveAsync(new Atlas.Domain.Sources.SourceReference("github", "org/repo", null, "does-not-exist"), CancellationToken.None));

        // Rotate keeps the name, changes the value.
        using (var scope = provider.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<CredentialsService>();
            var summary = await service.UpsertAsync(name, "svc-atlas", "ghp_second", null, CancellationToken.None);
            Assert.Equal("svc-atlas", summary.Username);
            Assert.NotNull(summary.LastUsedAtUtc);
            Assert.Single((await service.ListAsync(CancellationToken.None)).Where(c => c.Name == name));
        }

        var rotated = await resolver.ResolveAsync(new Atlas.Domain.Sources.SourceReference("github", "org/repo", null, name), CancellationToken.None);
        Assert.Equal(("svc-atlas", "ghp_second"), (rotated!.Username, rotated.Secret));

        // An assessment referencing the credential blocks deletion; unknown names are rejected at creation.
        using (var scope = provider.CreateScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CreateAssessmentHandler>();
            await Assert.ThrowsAsync<ArgumentException>(() => handler.HandleAsync(
                "Private repo", new SourceReference(SourceReference.Kinds.LocalFolder, fixture.WorkspaceRoot, CredentialName: "missing"), CancellationToken.None));

            await handler.HandleAsync(
                "Private repo", new SourceReference(SourceReference.Kinds.LocalFolder, fixture.WorkspaceRoot, CredentialName: name), CancellationToken.None);
        }

        using (var scope = provider.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<CredentialsService>();
            var ex = await Assert.ThrowsAsync<CredentialInUseException>(() => service.DeleteAsync(name, CancellationToken.None));
            Assert.Equal(1, ex.Assessments);
            Assert.Equal(1, (await service.ListAsync(CancellationToken.None)).Single(c => c.Name == name).UsedByAssessments);
        }

        // Consume the queued job so it does not leak into other tests.
        using (var scope = provider.CreateScope())
        {
            var job = await scope.ServiceProvider.GetRequiredService<IScanJobQueue>().ClaimAsync("test-worker", TimeSpan.FromMinutes(1), CancellationToken.None);
            Assert.NotNull(job);
        }
    }

    [Fact]
    public async Task Delete_removes_an_unused_credential()
    {
        await using var provider = fixture.BuildServices();
        var name = "tmp-" + Guid.NewGuid().ToString("N")[..8];

        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<CredentialsService>();
        await service.UpsertAsync(name, null, "secret", null, CancellationToken.None);

        Assert.True(await service.DeleteAsync(name, CancellationToken.None));
        Assert.False(await service.DeleteAsync(name, CancellationToken.None));
        Assert.DoesNotContain((await service.ListAsync(CancellationToken.None)), c => c.Name == name);
    }
}
