namespace ClaudeUsage.Pricing;

/// <summary>
/// 	API prices for one model, in US dollars per million tokens.
/// </summary>
/// <param name="Input">
/// 	Price of base input tokens.
/// </param>
/// <param name="Output">
/// 	Price of output tokens.
/// </param>
/// <param name="CacheWrite">
/// 	Price of tokens written to the prompt cache (5 minute cache).
/// </param>
/// <param name="CacheRead">
/// 	Price of tokens read from the prompt cache.
/// </param>
internal sealed record TokenPrice(double Input, double Output, double CacheWrite, double CacheRead);

/// <summary>
/// 	Calculates estimated API cost for Claude models.
/// </summary>
/// <remarks>
/// 	This class is the single source of prices.
/// 	The dashboard page gets the same table from the server, so the CLI and the page always agree.
/// </remarks>
/// <param name="prices">
/// 	The price table, keyed by model id or model id prefix.
/// </param>
internal sealed class ModelPricing(IReadOnlyDictionary<string, TokenPrice> prices)
{
	/// <summary>
	/// 	Model name keywords that identify an Anthropic model.
	/// 	Only these models get a cost.
	/// </summary>
	public static readonly string[] BillableKeywords = ["fable", "mythos", "opus", "sonnet", "haiku"];

	/// <summary>
	/// 	Prices from <see href="https://claude.com/pricing#api"/>, checked October 2026.
	/// </summary>
	/// <remarks>
	/// 	Opus 4.1 and older, Sonnet 4 and older, and Haiku 3.x come from earlier price lists.
	/// 	Without them, the family fallback would give those models the current (lower) prices.
	/// </remarks>
	public static readonly IReadOnlyDictionary<string, TokenPrice> BuiltInPrices = new Dictionary<string, TokenPrice>(StringComparer.Ordinal)
	{
		["claude-fable-5-1"] = new(10.00, 50.00, 12.50, 0.25),
		["claude-fable-5"] = new(10.00, 50.00, 12.50, 1.00),
		["claude-mythos-5"] = new(10.00, 50.00, 12.50, 1.00),
		["claude-opus-5-5"] = new(4.00, 20.00, 5.00, 0.20),
		["claude-opus-5"] = new(5.00, 25.00, 6.25, 0.50),
		["claude-opus-4-8"] = new(5.00, 25.00, 6.25, 0.50),
		["claude-opus-4-7"] = new(5.00, 25.00, 6.25, 0.50),
		["claude-opus-4-6"] = new(5.00, 25.00, 6.25, 0.50),
		["claude-opus-4-5"] = new(5.00, 25.00, 6.25, 0.50),
		["claude-opus-4-1"] = new(15.00, 75.00, 18.75, 1.50),
		["claude-opus-4"] = new(15.00, 75.00, 18.75, 1.50),
		["claude-3-opus"] = new(15.00, 75.00, 18.75, 1.50),
		["claude-sonnet-5-5"] = new(2.00, 10.00, 2.50, 0.20),
		["claude-sonnet-5"] = new(2.00, 10.00, 2.50, 0.20),
		["claude-sonnet-4-7"] = new(3.00, 15.00, 3.75, 0.30),
		["claude-sonnet-4-6"] = new(3.00, 15.00, 3.75, 0.30),
		["claude-sonnet-4-5"] = new(3.00, 15.00, 3.75, 0.30),
		["claude-sonnet-4"] = new(3.00, 15.00, 3.75, 0.30),
		["claude-3-7-sonnet"] = new(3.00, 15.00, 3.75, 0.30),
		["claude-haiku-4-7"] = new(1.00, 5.00, 1.25, 0.10),
		["claude-haiku-4-6"] = new(1.00, 5.00, 1.25, 0.10),
		["claude-haiku-4-5"] = new(1.00, 5.00, 1.25, 0.10),
		["claude-3-5-haiku"] = new(0.80, 4.00, 1.00, 0.08),
		["claude-3-haiku"] = new(0.25, 1.25, 0.30, 0.03),
	};

	/// <summary>
	/// 	Price keys to use when a model id matches no key and no key prefix.
	/// 	The first keyword that the model id contains wins.
	/// 	Each family falls back to its current model.
	/// </summary>
	public static readonly IReadOnlyList<(string Keyword, string PriceKey)> FamilyFallbacks =
	[
		("fable", "claude-fable-5-1"),
		("mythos", "claude-mythos-5"),
		("opus", "claude-opus-5-5"),
		("sonnet", "claude-sonnet-5-5"),
		("haiku", "claude-haiku-4-5"),
	];

	private static readonly JsonSerializerOptions _overrideJsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	private static readonly Lazy<ModelPricing> _current = new(() => LoadWithOverrides(AppPaths.PricingOverridePath));

	// Longest key first, so "claude-opus-4-5" wins over "claude-opus-4" for "claude-opus-4-5-20251101".
	private readonly string[] _prefixKeys = [.. prices.Keys.OrderByDescending(key => key.Length).ThenBy(key => key, StringComparer.Ordinal)];

	/// <summary>
	/// 	Gets the built-in prices with no user overrides.
	/// </summary>
	public static ModelPricing BuiltIn { get; } = new(BuiltInPrices);

	/// <summary>
	/// 	Gets the built-in prices merged with the user override file, if it exists.
	/// </summary>
	public static ModelPricing Current => _current.Value;

	/// <summary>
	/// 	Gets the price table.
	/// </summary>
	public IReadOnlyDictionary<string, TokenPrice> Prices => prices;

	/// <summary>
	/// 	Returns true when the model is an Anthropic model that has a cost.
	/// </summary>
	/// <param name="model">
	/// 	The model id (ex. <c>claude-opus-5-5</c>).
	/// </param>
	/// <returns>
	/// 	True when the model id contains a billable keyword.
	/// </returns>
	public static bool IsBillable(string? model)
	{
		if (string.IsNullOrEmpty(model))
		{
			return false;
		}

		return BillableKeywords.Any(keyword => model.Contains(keyword, StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// 	Finds the price for a model.
	/// </summary>
	/// <remarks>
	/// 	The lookup tries an exact match, then the longest key that is a prefix, then the family keyword.
	/// </remarks>
	/// <param name="model">
	/// 	The model id.
	/// </param>
	/// <returns>
	/// 	The price, or null when the model is not a known Anthropic model.
	/// </returns>
	public TokenPrice? Find(string? model)
	{
		if (string.IsNullOrEmpty(model))
		{
			return null;
		}

		if (prices.TryGetValue(model, out TokenPrice? exact))
		{
			return exact;
		}

		foreach (string key in _prefixKeys)
		{
			if (model.StartsWith(key, StringComparison.Ordinal))
			{
				return prices[key];
			}
		}

		foreach ((string keyword, string priceKey) in FamilyFallbacks)
		{
			if (model.Contains(keyword, StringComparison.OrdinalIgnoreCase))
			{
				return prices.GetValueOrDefault(priceKey);
			}
		}

		return null;
	}

	/// <summary>
	/// 	Calculates the estimated API cost of token usage.
	/// </summary>
	/// <param name="model">
	/// 	The model id.
	/// </param>
	/// <param name="input">
	/// 	Base input tokens.
	/// </param>
	/// <param name="output">
	/// 	Output tokens.
	/// </param>
	/// <param name="cacheRead">
	/// 	Tokens read from the prompt cache.
	/// </param>
	/// <param name="cacheCreation">
	/// 	Tokens written to the prompt cache.
	/// </param>
	/// <returns>
	/// 	The cost in US dollars, or 0 for a model that is not billable.
	/// </returns>
	public double CalculateCost(string? model, long input, long output, long cacheRead, long cacheCreation)
	{
		if (IsBillable(model) is false || Find(model) is not TokenPrice price)
		{
			return 0;
		}

		return (input * price.Input
			+ output * price.Output
			+ cacheRead * price.CacheRead
			+ cacheCreation * price.CacheWrite) / 1_000_000;
	}

	/// <summary>
	/// 	Creates the pricing from the built-in table and an optional override file.
	/// </summary>
	/// <remarks>
	/// 	The override file is a JSON object keyed by model id.
	/// 	Each value has <c>input</c>, <c>output</c>, <c>cache_write</c> and <c>cache_read</c>.
	/// 	An entry replaces a built-in entry with the same key, or adds a new one.
	/// 	A file that is not valid gives a warning, and the built-in prices stay in use.
	/// </remarks>
	/// <param name="overridePath">
	/// 	The path of the override file.
	/// </param>
	/// <returns>
	/// 	The merged pricing.
	/// </returns>
	public static ModelPricing LoadWithOverrides(string overridePath)
	{
		if (File.Exists(overridePath) is false)
		{
			return BuiltIn;
		}

		try
		{
			Dictionary<string, TokenPrice>? overrides = JsonSerializer.Deserialize<Dictionary<string, TokenPrice>>(
				File.ReadAllText(overridePath), _overrideJsonOptions);
			Dictionary<string, TokenPrice> merged = new(BuiltInPrices, StringComparer.Ordinal);
			foreach ((string key, TokenPrice price) in overrides ?? [])
			{
				merged[key] = price;
			}

			return new ModelPricing(merged);
		}
		catch (Exception exception) when (exception is JsonException or IOException)
		{
			Console.Error.WriteLine($"Warning: ignored pricing override file {overridePath}: {exception.Message}");
			return BuiltIn;
		}
	}
}
