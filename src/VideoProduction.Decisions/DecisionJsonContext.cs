using System.Text.Json.Serialization;

namespace VideoProduction.Decisions.Experimental;

/// <summary>Explicit metadata for the incubated public wire contracts. Applications supply their own state/result metadata.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(DecisionRequest))]
[JsonSerializable(typeof(DecisionResponse))]
[JsonSerializable(typeof(DecisionQuestion))]
[JsonSerializable(typeof(DecisionAnswer))]
[JsonSerializable(typeof(DecisionOptions))]
[JsonSerializable(typeof(DecisionClientMetadata))]
[JsonSerializable(typeof(DecisionProvenance))]
[JsonSerializable(typeof(DecisionProbability))]
[JsonSerializable(typeof(DecisionCandidate))]
[JsonSerializable(typeof(DecisionScoreLevel))]
[JsonSerializable(typeof(Microsoft.Extensions.AI.UsageDetails))]
[JsonSerializable(typeof(Microsoft.Extensions.AI.AdditionalPropertiesDictionary))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(bool))]
public partial class DecisionJsonContext : JsonSerializerContext;
