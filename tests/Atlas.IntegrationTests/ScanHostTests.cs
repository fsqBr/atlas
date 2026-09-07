using System.Diagnostics;
using Atlas.Application.Assessments;
using Atlas.Domain.Findings;
using Atlas.Scanner.Abstractions;
using Atlas.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.IntegrationTests;

/// <summary>The scan-host wire format and the child-process executor, without a database.</summary>
public sealed class ScanHostTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("atlas-scanhost-ws").FullName;

    public ScanHostTests()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "Legacy"));
        File.WriteAllText(Path.Combine(_workspace, "Legacy", "Legacy.csproj"), """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <PropertyGroup><TargetFrameworkVersion>v4.5.2</TargetFrameworkVersion><OutputType>Library</OutputType></PropertyGroup>
              <ItemGroup><Reference Include="System.Web" /></ItemGroup>
              <ItemGroup><Compile Include="Service.cs" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_workspace, "Legacy", "packages.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <packages><package id="Newtonsoft.Json" version="6.0.8" targetFramework="net452" /></packages>
            """);
        File.WriteAllText(Path.Combine(_workspace, "Legacy", "Service.cs"), """
            using System.Data.SqlClient;
            public class Service
            {
                public void Run(string id)
                {
                    var cmd = new SqlCommand("select * from t where id = " + id);
                    var password = "P@ssw0rd-hardcoded-123456";
                }
            }
            """);
    }

    private static WorkspaceScanRequest RequestFor(string root, IEnumerable<string> scannerIds) => new(
        Guid.NewGuid(), "repo", root, scannerIds.ToDictionary(id => id, _ => Guid.NewGuid(), StringComparer.Ordinal), new DateOnly(2026, 8, 29));

    private static ServiceProvider BuildScanningServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Atlas:Secrets:HmacKeyBase64"] = Convert.ToBase64String(new byte[32]) })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAtlasScanning(configuration);
        services.AddSingleton<InProcessScanExecutor>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Outcome_survives_the_json_round_trip()
    {
        await using var provider = BuildScanningServices();
        var scannerIds = provider.GetServices<IScanner>().Select(s => s.Descriptor.Id).ToList();
        var request = RequestFor(_workspace, scannerIds);

        var outcome = await provider.GetRequiredService<InProcessScanExecutor>().ExecuteAsync(request, CancellationToken.None);

        Assert.Contains("csharp", outcome.Languages.Keys);
        Assert.Equal(scannerIds.Count, outcome.Scanners.Count);
        Assert.All(outcome.Scanners, s => Assert.True(s.Succeeded, s.Error));
        var candidates = outcome.Scanners.Sum(s => s.Candidates.Count);
        Assert.True(candidates >= 3, $"expected findings from the legacy project, got {candidates}");

        var path = Path.Combine(_workspace, "outcome.json");
        await ScanWire.WriteAsync(path, outcome, CancellationToken.None);
        var restored = await ScanWire.ReadAsync<WorkspaceScanOutcome>(path, CancellationToken.None);

        Assert.Equal(outcome.Languages["csharp"].Totals, restored.Languages["csharp"].Totals);
        Assert.Equal(outcome.Languages["csharp"].Projects.Count, restored.Languages["csharp"].Projects.Count);
        Assert.Equal(outcome.Languages["csharp"].Projects[0].TargetFramework, restored.Languages["csharp"].Projects[0].TargetFramework);
        Assert.Equal(outcome.Scanners.Select(s => s.Candidates.Count), restored.Scanners.Select(s => s.Candidates.Count));
        var original = outcome.Scanners.First(s => s.Candidates.Count > 0).Candidates[0];
        var back = restored.Scanners.First(s => s.ScannerId == outcome.Scanners.First(x => x.Candidates.Count > 0).ScannerId).Candidates[0];
        Assert.Equal(original.RuleId, back.RuleId);
        Assert.Equal(original.Severity, back.Severity);
        Assert.Equal(original.Evidence, back.Evidence);
        Assert.Equal(original.Data?.Count ?? 0, back.Data?.Count ?? 0);

        var requestPath = Path.Combine(_workspace, "request.json");
        await ScanWire.WriteAsync(requestPath, request, CancellationToken.None);
        Assert.Equal(request, await ScanWire.ReadAsync<WorkspaceScanRequest>(requestPath, CancellationToken.None) with { ScanIdsByScanner = request.ScanIdsByScanner });
    }

    [Fact]
    public void Child_launch_reuses_the_current_runtime_and_caps_the_heap()
    {
        var startInfo = ChildProcessScanExecutor.BuildStartInfo("/tmp/r.json", "/tmp/o.json", new ScanningOptions { ChildMemoryLimitMb = 512 });

        Assert.Equal(Environment.ProcessPath, startInfo.FileName);
        Assert.Contains(ScanHost.Command, startInfo.ArgumentList);
        Assert.Equal("/tmp/o.json", startInfo.ArgumentList[^1]);
        Assert.Equal("0x20000000", startInfo.Environment["DOTNET_GCHeapHardLimit"]);
        Assert.True(ScanHost.IsScanHostInvocation([ScanHost.Command, "a", "b"]));
        Assert.False(ScanHost.IsScanHostInvocation(["--urls", "x"]));
    }

    [Fact]
    public async Task Missing_scanner_in_the_host_is_reported_not_thrown()
    {
        await using var provider = BuildScanningServices();
        var outcome = await provider.GetRequiredService<InProcessScanExecutor>().ExecuteAsync(RequestFor(_workspace, ["scanner.does-not-exist"]), CancellationToken.None);
        var only = Assert.Single(outcome.Scanners);
        Assert.False(only.Succeeded);
        Assert.Contains("not registered", only.Error);
    }

    [Fact]
    public async Task Child_process_end_to_end_when_the_worker_assembly_is_runnable()
    {
        // The test host is `testhost`, so the executor would launch it; run the worker assembly through `dotnet exec` with
        // THIS project's runtimeconfig/deps: the test output carries the worker's dependencies (and the ASP.NET shared
        // framework that provides Microsoft.Extensions.Hosting) while the worker's own runtimeconfig does not.
        var worker = typeof(ScanHost).Assembly.Location;
        var testAssembly = typeof(ScanHostTests).Assembly.Location;
        var runtimeConfig = Path.ChangeExtension(testAssembly, ".runtimeconfig.json");
        var depsFile = Path.ChangeExtension(testAssembly, ".deps.json");
        if (!File.Exists(runtimeConfig) || !File.Exists(depsFile) || Environment.ProcessPath is null)
        {
            return; // cannot spawn the worker from this test layout
        }

        var dotnet = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (!File.Exists(dotnet))
        {
            dotnet = "dotnet";
        }

        var dir = Directory.CreateTempSubdirectory("atlas-scanhost-io").FullName;
        var requestPath = Path.Combine(dir, "request.json");
        var outcomePath = Path.Combine(dir, "outcome.json");
        await ScanWire.WriteAsync(requestPath, RequestFor(_workspace, ["dependency.nuget", "security.patterns"]), CancellationToken.None);

        var psi = new ProcessStartInfo(dotnet) { RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("--runtimeconfig");
        psi.ArgumentList.Add(runtimeConfig);
        psi.ArgumentList.Add("--depsfile");
        psi.ArgumentList.Add(depsFile);
        psi.ArgumentList.Add(worker);
        psi.ArgumentList.Add(ScanHost.Command);
        psi.ArgumentList.Add(requestPath);
        psi.ArgumentList.Add(outcomePath);
        psi.Environment["Atlas__Secrets__HmacKeyBase64"] = Convert.ToBase64String(new byte[32]);

        using var process = Process.Start(psi)!;
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, $"scan host exit {process.ExitCode}: {stderr[^Math.Min(stderr.Length, 800)..]}");
        var outcome = await ScanWire.ReadAsync<WorkspaceScanOutcome>(outcomePath, CancellationToken.None);
        Assert.Equal(2, outcome.Scanners.Count);
        Assert.Contains(outcome.Scanners, s => s.ScannerId == "dependency.nuget" && s.Succeeded && s.Candidates.Any(c => c.Severity >= Severity.Medium));
        Directory.Delete(dir, recursive: true);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
