using Atlas.Language.Abstractions;
using Atlas.Language.CSharp;
using Atlas.Scanner.Runtime;

namespace Atlas.Language.Tests;

public class SecurityPatternTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("atlas-secpatterns").FullName;
    private IReadOnlyList<PatternFact> _patterns = null!;

    public async Task InitializeAsync()
    {
        File.WriteAllText(Path.Combine(_root, "Insecure.cs"), """
            using System.Data.SqlClient;
            using System.Diagnostics;
            using System.Net;
            using System.Security.Cryptography;
            using System.Xml;
            using Newtonsoft.Json;

            public class Repo
            {
                public void Bad(string name)
                {
                    var cmd = new SqlCommand("SELECT * FROM Users WHERE Name = '" + name + "'");
                    var md5 = MD5.Create();
                    var bf = new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter();
                    var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.All };
                    ServicePointManager.ServerCertificateValidationCallback = (a, b, c, d) => true;
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls;
                    var xml = new XmlReaderSettings();
                    xml.DtdProcessing = DtdProcessing.Parse;
                    Process.Start("cmd.exe", "/c " + name);
                }

                public void Good(string name)
                {
                    var cmd = new SqlCommand("SELECT * FROM Users WHERE Name = @name");
                    cmd.Parameters.AddWithValue("@name", name);
                    var sha = SHA256.Create();
                }

                [ValidateInput(false)]
                public void Post() { }
            }
            """);

        var result = await new CSharpLanguageAnalyzer().AnalyzeAsync(new ContainedArtifactReader(_root), CancellationToken.None);
        _patterns = result.Patterns;
    }

    [Theory]
    [InlineData(SecurityPatternIds.SqlStringConcatenation)]
    [InlineData(SecurityPatternIds.WeakHash)]
    [InlineData(SecurityPatternIds.BinaryFormatter)]
    [InlineData(SecurityPatternIds.TypeNameHandling)]
    [InlineData(SecurityPatternIds.CertificateValidationDisabled)]
    [InlineData(SecurityPatternIds.LegacyTlsProtocol)]
    [InlineData(SecurityPatternIds.XmlDtdProcessing)]
    [InlineData(SecurityPatternIds.ProcessStartConcatenation)]
    [InlineData(SecurityPatternIds.RequestValidationDisabled)]
    public void Detects_pattern(string patternId)
    {
        Assert.Contains(_patterns, p => p.PatternId == patternId);
    }

    [Fact]
    public void Parameterized_sql_and_strong_hash_are_not_flagged()
    {
        var sql = Assert.Single(_patterns, p => p.PatternId == SecurityPatternIds.SqlStringConcatenation);
        Assert.Equal("Repo.Bad", sql.Symbol);
        Assert.Single(_patterns, p => p.PatternId == SecurityPatternIds.WeakHash);
    }

    [Fact]
    public void Facts_carry_enclosing_member_and_line()
    {
        var validate = Assert.Single(_patterns, p => p.PatternId == SecurityPatternIds.RequestValidationDisabled);
        Assert.Equal("Repo.Post", validate.Symbol);
        Assert.True(validate.Line > 20);
        Assert.All(_patterns, p => Assert.EndsWith("Insecure.cs", p.FilePath));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }
}
