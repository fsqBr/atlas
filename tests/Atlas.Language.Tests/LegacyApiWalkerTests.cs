using Atlas.Language.Abstractions;
using Atlas.Language.CSharp;
using Microsoft.CodeAnalysis.CSharp;

namespace Atlas.Language.Tests;

public class LegacyApiWalkerTests
{
    private const string Source = """
        using System;
        using System.Net;
        using System.Threading;
        using System.Web;

        public class Legacy
        {
            public void Run(Thread worker)
            {
                var client = new WebClient();
                var request = WebRequest.Create("http://x");
                worker.Abort();
                var user = HttpContext.Current.User;
                var setting = System.Configuration.ConfigurationManager.AppSettings["x"];
                var http = new HttpClient();
                Console.WriteLine(client.ToString() + request + user + setting + http);
            }
        }
        """;

    [Fact]
    public void Reports_legacy_apis_with_reasons_and_enclosing_members()
    {
        var facts = LegacyApiWalker.Collect(CSharpSyntaxTree.ParseText(Source), "Legacy.cs", CancellationToken.None);

        Assert.All(facts, f => Assert.Equal(QualityPatternIds.LegacyApi, f.PatternId));
        Assert.All(facts, f => Assert.Equal("Legacy.Run", f.Symbol));
        Assert.Contains(facts, f => f.Detail.StartsWith("new WebClient()"));
        Assert.Contains(facts, f => f.Detail.StartsWith("WebRequest.Create"));
        Assert.Contains(facts, f => f.Detail.StartsWith("HttpContext.Current"));
        Assert.Contains(facts, f => f.Detail.StartsWith("ConfigurationManager.AppSettings"));
        Assert.DoesNotContain(facts, f => f.Detail.StartsWith("new HttpClient("));
        Assert.DoesNotContain(facts, f => f.Detail.Contains("Thread.Abort")); // instance call: needs symbols, not names
    }
}
