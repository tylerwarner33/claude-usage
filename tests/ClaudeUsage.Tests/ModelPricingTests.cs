using ClaudeUsage.Pricing;

namespace ClaudeUsage.Tests;

public sealed class ModelPricingTests
{
	private readonly ModelPricing _pricing = ModelPricing.BuiltIn;

	[Theory]
	[InlineData("claude-opus-5-5", 4.00)]
	[InlineData("claude-opus-5", 5.00)]
	[InlineData("claude-sonnet-5-5", 2.00)]
	[InlineData("claude-fable-5-1", 10.00)]
	[InlineData("claude-haiku-4-5-20251001", 1.00)]
	[InlineData("claude-opus-4-5-20251101", 5.00)]
	[InlineData("claude-opus-4-20250514", 15.00)]
	[InlineData("claude-3-5-haiku-20241022", 0.80)]
	public void Find_UsesExactOrLongestPrefixMatch(string model, double expectedInputPrice)
	{
		Assert.Equal(expectedInputPrice, _pricing.Find(model)?.Input);
	}

	[Theory]
	[InlineData("claude-opus-9-9", "claude-opus-5-5")]
	[InlineData("us.anthropic.claude-sonnet-x", "claude-sonnet-5-5")]
	[InlineData("some-fable-model", "claude-fable-5-1")]
	public void Find_FallsBackToCurrentModelOfTheFamily(string model, string expectedKey)
	{
		Assert.Same(_pricing.Prices[expectedKey], _pricing.Find(model));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("gpt-4o")]
	[InlineData("<synthetic>")]
	public void CalculateCost_IsZeroForModelsThatAreNotBillable(string? model)
	{
		Assert.False(ModelPricing.IsBillable(model));
		Assert.Equal(0, _pricing.CalculateCost(model, 1_000_000, 1_000_000, 1_000_000, 1_000_000));
	}

	[Fact]
	public void CalculateCost_UsesEachTokenTypePrice()
	{
		// Opus 5.5: $4 input, $20 output, $0.20 cache read, $5 cache write per million.
		double cost = _pricing.CalculateCost("claude-opus-5-5", 1_000_000, 500_000, 10_000_000, 2_000_000);

		Assert.Equal(4.00 + 10.00 + 2.00 + 10.00, cost, precision: 6);
	}

	[Fact]
	public void LoadWithOverrides_ReplacesAndAddsEntries()
	{
		using TemporaryDirectory directory = new();
		string path = directory.Combine("pricing.json");
		File.WriteAllText(path, """
			{
				// Comments are allowed.
				"claude-opus-5-5": { "input": 1, "output": 2, "cache_write": 3, "cache_read": 4 },
				"claude-new-model": { "input": 9, "output": 9, "cache_write": 9, "cache_read": 9 },
			}
			""");

		ModelPricing pricing = ModelPricing.LoadWithOverrides(path);

		Assert.Equal(new TokenPrice(1, 2, 3, 4), pricing.Find("claude-opus-5-5"));
		Assert.Equal(9, pricing.Prices["claude-new-model"].Input);
		Assert.Equal(2.00, pricing.Find("claude-sonnet-5-5")?.Input);
	}

	[Fact]
	public void LoadWithOverrides_KeepsBuiltInPricesWhenFileIsNotValid()
	{
		using TemporaryDirectory directory = new();
		string path = directory.Combine("pricing.json");
		File.WriteAllText(path, "{ not json");

		Assert.Same(ModelPricing.BuiltIn, ModelPricing.LoadWithOverrides(path));
	}
}
