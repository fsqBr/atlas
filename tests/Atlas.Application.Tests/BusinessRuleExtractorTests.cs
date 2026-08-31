using Atlas.Application.Ai;
using Atlas.Domain.Ai;
using Atlas.Language.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Atlas.Application.Tests;

public class BusinessRuleExtractorTests
{
    private static BusinessRuleCandidate Candidate(string symbol, int size = 100) =>
        new("src/Orders/OrderService.cs", symbol, 10, 40, 7, 12.5, new string('x', size));

    [Fact]
    public void Parses_a_plain_json_array_and_maps_snippet_index_to_the_candidate()
    {
        var batch = new[] { Candidate("OrderService.Validate"), Candidate("OrderService.Price") };
        const string reply = """
            [
              {"snippet": 2, "symbol": "OrderService.Price", "name": "Volume discount", "descriptionEn": "Orders above 10 items get 5% off.", "descriptionPt": "Pedidos acima de 10 itens ganham 5% de desconto.", "category": "Pricing", "conditions": ["quantity > 10"], "confidence": 0.9},
              {"snippet": 1, "name": "Customer must be active", "descriptionEn": "Inactive customers cannot order.", "category": "Eligibility", "confidence": 0.8}
            ]
            """;

        var rules = BusinessRuleExtractor.Parse(reply, batch);

        Assert.Equal(2, rules.Count);
        Assert.Equal("OrderService.Price", rules[0].Symbol);
        Assert.Equal(BusinessRuleCategory.Pricing, rules[0].Category);
        Assert.Equal(["quantity > 10"], rules[0].Conditions);
        Assert.Equal("Pedidos acima de 10 itens ganham 5% de desconto.", rules[0].DescriptionPt);
        Assert.Equal("OrderService.Validate", rules[1].Symbol);
        Assert.Equal("Inactive customers cannot order.", rules[1].DescriptionPt); // falls back to English
    }

    [Fact]
    public void Tolerates_fences_prose_and_object_wrappers_and_drops_garbage()
    {
        var batch = new[] { Candidate("A.B") };

        var fenced = BusinessRuleExtractor.Parse("Sure! ```json\n[{\"snippet\":1,\"name\":\"R\",\"descriptionEn\":\"D\",\"category\":\"nonsense\",\"confidence\":7}]\n```", batch);
        Assert.Single(fenced);
        Assert.Equal(BusinessRuleCategory.Other, fenced[0].Category);
        Assert.Equal(1.0, fenced[0].Confidence);

        var wrapped = BusinessRuleExtractor.Parse("{\"rules\":[{\"snippet\":1,\"name\":\"R\",\"descriptionEn\":\"D\"}]}", batch);
        Assert.Single(wrapped);

        Assert.Empty(BusinessRuleExtractor.Parse("I could not find any rules.", batch));
        Assert.Empty(BusinessRuleExtractor.Parse("[{\"name\": \"missing description\"}]", batch));
        Assert.Empty(BusinessRuleExtractor.Parse("[not json", batch));
    }

    [Fact]
    public void Batches_by_count_and_by_size()
    {
        var candidates = new List<BusinessRuleCandidate>();
        for (var i = 0; i < 9; i++)
        {
            candidates.Add(Candidate($"M{i}"));
        }

        candidates.Add(Candidate("Huge", BusinessRuleExtractor.MaxBatchChars));

        var batches = BusinessRuleExtractor.Batch(candidates).ToList();

        Assert.Equal([4, 4, 1, 1], batches.Select(b => b.Count).ToArray());
        Assert.Equal("Huge", batches[3][0].Symbol);
    }

    [Fact]
    public async Task Extraction_sums_tokens_and_keeps_going_after_a_failed_batch_but_stops_on_bad_credentials()
    {
        var candidates = Enumerable.Range(0, 8).Select(i => Candidate($"M{i}")).ToList();
        var flaky = new ScriptedClient(
            _ => throw new ChatProviderException("boom", 500),
            _ => new ChatResult("[{\"snippet\":1,\"name\":\"R\",\"descriptionEn\":\"D\"}]", 100, 20, "m"));
        var extractor = new BusinessRuleExtractor(NullLogger<BusinessRuleExtractor>.Instance);

        var outcome = await extractor.ExtractAsync(flaky, candidates, CancellationToken.None);

        Assert.Equal(1, outcome.FailedBatches);
        Assert.Equal(4, outcome.SnippetsSent);
        Assert.Single(outcome.Rules);
        Assert.Equal(100, outcome.InputTokens);

        var unauthorized = new ScriptedClient(_ => throw new ChatProviderException("bad key", 401));
        await Assert.ThrowsAsync<ChatProviderException>(() => extractor.ExtractAsync(unauthorized, candidates, CancellationToken.None));
    }

    [Fact]
    public void Prompt_numbers_snippets_and_names_the_symbol_and_location()
    {
        var text = BusinessRulePrompts.User([Candidate("OrderService.Validate"), Candidate("OrderService.Price")]);

        Assert.Contains("### Snippet 1 — OrderService.Validate (src/Orders/OrderService.cs:10)", text);
        Assert.Contains("### Snippet 2 — OrderService.Price", text);
        Assert.Contains("JSON array ONLY", BusinessRulePrompts.System);
    }

    private sealed class ScriptedClient(params Func<ChatRequest, ChatResult>[] steps) : IChatClient
    {
        private int _index;

        public string Provider => "test";

        public string Model => "m";

        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
        {
            var step = steps[Math.Min(_index, steps.Length - 1)];
            _index++;
            return Task.FromResult(step(request));
        }
    }
}
