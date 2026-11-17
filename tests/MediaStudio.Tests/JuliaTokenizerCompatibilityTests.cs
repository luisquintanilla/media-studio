using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.Tokenizers;
using VideoProduction.Decisions.Experimental.Julia;
using Generator = VideoProduction.Decisions.Experimental.Generator;
using HfTokenizer = Tokenizers.HuggingFace.Tokenizer.Tokenizer;

internal static class JuliaTokenizerCompatibilityTests
{
    // HF 3.23.1 / ML 2.0.0, exact Julia tokenizer hash below; no model is loaded.
    // The Microsoft abstraction must preserve raw HF behavior on every retained
    // component and all 19 decision inputs, without skips or unknown-token exceptions.
    internal static int Run(string tokenizerPath, string fixtureDirectory)
    {
        int assertions = 0;
        const string expectedHash = "609d8f4c067cd3950f88594c5a802616cea245823836ef5848ee4fc40aab5b6f";
        byte[] bytes = File.ReadAllBytes(tokenizerPath);
        Require(Convert.ToHexStringLower(SHA256.HashData(bytes)) == expectedHash, "Wrong Julia tokenizer asset.");
        using var document = JsonDocument.Parse(bytes);
        using var adapter = new JuliaTokenizer(tokenizerPath);
        Tokenizer candidate = adapter;
        using var baseline = HfTokenizer.FromFile(tokenizerPath);
        using var production = new PrepareDecisionInputs(tokenizerPath);
        int fixtureParts = 0, candidateMatches = 0;
        List<string> differences = [];
        foreach (string name in new[] { "mapping-sanity.json", "mapping-sanity-v2.json" })
        {
            using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixtureDirectory, name)));
            foreach (var observation in fixture.RootElement.GetProperty("observations").EnumerateArray())
            {
                CompareFixture(observation.GetProperty("encodedStateText").GetString()!, observation.GetProperty("stateTokenIds"));
                CompareFixture(observation.GetProperty("encodedHeadText").GetString()!, observation.GetProperty("headTokenIds"));
                var options = observation.GetProperty("encodedOptionTexts").EnumerateArray().ToArray();
                var tokens = observation.GetProperty("optionTokenIds").EnumerateArray().ToArray();
                for (int i = 0; i < options.Length; i++) CompareFixture(options[i].GetString()!, tokens[i]);
                CompareProductionBatch(observation);
            }
        }

        string[] decisionInputs =
        [
            "", " ", "  ", "hello", " hello", "hello  world", "line\nbreak\tend",
            "café", "e\u0301", "中文", "🙂", "\U0010FFFE", "\0",
            "choice question: Choose one option.", "score question: Assess the state against the ordered rubric.",
            "noul question: Is this sufficient?", " false", " true", "{\"brief\": \"A synthetic garden overview.\"}"
        ];
        foreach (string text in decisionInputs)
        {
            long[] hf = EncodeBaseline(text);
            long[] adapted = EncodeCandidate(text);
            Console.WriteLine($"Julia tokenizer {JsonSerializer.Serialize(text)} HF=[{string.Join(",", hf)}] Adapter=[{string.Join(",", adapted)}]");
            Compare(text, hf);
        }
        Require(fixtureParts == 48, "Retained decision fixture coverage changed.");
        if (differences.Count != 0)
            throw new InvalidOperationException("Julia tokenizer migration BLOCKED:\n" + string.Join("\n", differences));
        Require(candidateMatches == fixtureParts, "Retained decision fixture parity changed.");
        Console.WriteLine($"PASS exact parity gate: {candidateMatches}/{fixtureParts} retained fixture components, " +
            $"{decisionInputs.Length}/{decisionInputs.Length} decision inputs (including U+10FFFE, NUL, newline/tab).");

        string[] regressions =
        [
            "a\0\U0010FFFEb", "\U0010FFFE\0\U0010FFFE", "\nhello", "a\n\nbreak", "line\r\nbreak",
            "a\t\tbreak", " \n break", "a\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\n\nbreak",
            "a<2mass>break", "🙂\0🙂", "\u2581\U0010FFFE", "\u00a0hello", "\u2003hello"
        ];
        foreach (string text in regressions) Compare(text, EncodeBaseline(text));
        foreach (var token in document.RootElement.GetProperty("added_tokens").EnumerateArray())
        {
            string content = token.GetProperty("content").GetString()!;
            string text = "a " + content + " break";
            Compare(text, EncodeBaseline(text));
        }
        Require(differences.Count == 0, "Extended parity failed:\n" + string.Join("\n", differences));
        Require(EncodeCandidate("\U0010FFFE").SequenceEqual([235248L, 461, 360, 408, 407]), "Wrong scalar byte fallback.");
        Require(EncodeCandidate("\0").SequenceEqual([235248L, 217]), "Wrong NUL byte fallback.");

        string offsetInput = "a\0\U0010FFFEb";
        var offsetTokens = candidate.EncodeToTokens(offsetInput, out string? normalized);
        Require(normalized is null, "HF does not expose a normalized-text result; do not invent one.");
        var fallback = offsetTokens.Where(t => t.Value.StartsWith("<0x", StringComparison.Ordinal)).ToArray();
        Require(fallback.Length == 5 && fallback[0].Offset.Equals(1..2) &&
            fallback.Skip(1).All(t => t.Offset.Equals(2..4)), "Fused unknown offsets must map each UTF-8 byte to its original UTF-16 scalar.");
        Require(offsetTokens[0].Offset.Equals(0..1) && offsetTokens[^1].Offset.Equals(4..5), "Prefix or neighboring token offset drift.");
        Require(candidate.EncodeToTokens(offsetInput.AsSpan(), out _).SequenceEqual(offsetTokens), "Span/string token offsets differ.");
        Require(candidate.CountTokens(offsetInput) == offsetTokens.Count &&
            candidate.CountTokens(offsetInput.AsSpan()) == offsetTokens.Count, "Count must include expanded fallback bytes.");
        Reject(() => candidate.EncodeToIds(" hello", considerNormalization: false));
        Reject(() => candidate.EncodeToIds("hello", considerPreTokenization: false));
        Require(typeof(JuliaDecisionClient).GetConstructors().Any(c => c.GetParameters().FirstOrDefault()?.ParameterType == typeof(Tokenizer)),
            "JuliaDecisionClient must accept the Microsoft tokenizer abstraction.");
        Require(typeof(PrepareDecisionInputs).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Any(f => f.FieldType == typeof(Tokenizer)), "Production preparation must hold the Microsoft tokenizer abstraction.");
        Require(typeof(PrepareDecisionInputs).Assembly.GetReferencedAssemblies().Any(a => a.Name == "Tokenizers.HuggingFace"),
            "The requested HF implementation dependency was removed.");
        Reject(() => candidate.EncodeToIds(offsetInput, 2, out _, out _));
        Reject(() => candidate.GetIndexByTokenCount(offsetInput, 2, out _, out _));
        Reject(() => candidate.GetIndexByTokenCountFromEnd(offsetInput, 2, out _, out _));
        int[] decodeIds = candidate.EncodeToIds("a\0\U0010FFFEb").ToArray();
        string decoded = baseline.Decode(decodeIds.Select(id => (uint)id), skipSpecialTokens: false);
        Require(candidate.Decode(decodeIds) == decoded, "Decode no longer delegates to HF.");
        char[] destination = new char[decoded.Length];
        Require(candidate.Decode(decodeIds, destination, out int idsConsumed, out int charsWritten) == System.Buffers.OperationStatus.Done &&
            idsConsumed == decodeIds.Length && charsWritten == decoded.Length && new string(destination) == decoded,
            "Span decode lost HF content or consumption counts.");
        char[] small = new char[Math.Max(0, decoded.Length - 1)];
        Array.Fill(small, '?');
        Require(candidate.Decode(decodeIds, small, out idsConsumed, out charsWritten) == System.Buffers.OperationStatus.DestinationTooSmall &&
            idsConsumed == 0 && charsWritten == 0 && small.All(c => c == '?'), "Short destination must not consume or write a partial decode.");
        try { candidate.EncodeToIds("\uD800"); throw new InvalidOperationException("Invalid UTF-16 was accepted."); }
        catch (System.Text.EncoderFallbackException) { assertions++; }
        Reject(() => production.Prepare(Input("<mask>")));
        RejectBudget(() => production.Prepare(Input(string.Concat(Enumerable.Repeat("garden ", 1500)))));
        RejectBudget(() => production.Prepare(Input("brief", string.Concat(Enumerable.Repeat("\U0010FFFE", 100)))));
        RejectBudget(() => production.Prepare(Input("brief", option: string.Concat(Enumerable.Repeat("\U0010FFFE", 13)))));
        Require(production.Prepare(Input("brief")).BatchSize == 1, "Budget rejection corrupted the production tokenizer.");
        var disposed = new JuliaTokenizer(tokenizerPath);
        disposed.Dispose();
        disposed.Dispose();
        try { disposed.EncodeToIds("hello"); throw new InvalidOperationException("Disposed adapter encoded."); }
        catch (ObjectDisposedException) { assertions++; }
        try { disposed.Decode([217]); throw new InvalidOperationException("Disposed adapter decoded."); }
        catch (ObjectDisposedException) { assertions++; }
        Console.WriteLine($"PASS HF adapter IDs/tokens/UTF-16 offsets/decode/lifetime and unsupported API regressions; {regressions.Length + 249} additional exact HF comparisons.");
        return assertions;

        long[] EncodeBaseline(string text) => baseline.Encode(text, addSpecialTokens: false).Single().Ids
            .Select(id => (long)id).ToArray();

        long[] EncodeCandidate(string text) => candidate.EncodeToIds(text).Select(id => (long)id).ToArray();

        void CompareFixture(string text, JsonElement ids)
        {
            long[] expected = ids.EnumerateArray().Select(id => id.GetInt64()).ToArray();
            Require(EncodeBaseline(text).SequenceEqual(expected), $"Existing HF fixture IDs changed: {JsonSerializer.Serialize(text)}.");
            fixtureParts++;
            if (Compare(text, expected)) candidateMatches++;
        }

        bool Compare(string text, long[] expected)
        {
            assertions++;
            long[] actual = EncodeCandidate(text);
            if (actual.SequenceEqual(expected))
            {
                var raw = baseline.Encode(text, addSpecialTokens: false, includeTokens: true, includeOffsets: true, charOffsets: true).Single();
                var adapted = candidate.EncodeToTokens(text, out _);
                int[] positions = ScalarPositions(text);
                Require(adapted.Select(t => t.Value).SequenceEqual(raw.Tokens), "Adapter changed HF token strings.");
                Require(adapted.Select(t => t.Offset).SequenceEqual(raw.Offsets.Select(o =>
                    new Range(positions[(int)o.Start], positions[(int)o.End]))), "Adapter changed HF offsets beyond scalar-to-UTF-16 units.");
                Require(candidate.Decode(actual.Select(id => (int)id)) ==
                    baseline.Decode(raw.Ids, skipSpecialTokens: false), "Adapter decode differs from HF.");
                return true;
            }
            differences.Add($"{JsonSerializer.Serialize(text)} HF=[{string.Join(",", expected)}] Adapter=[{string.Join(",", actual)}]");
            return false;
        }

        static int[] ScalarPositions(string text)
        {
            List<int> positions = [0];
            int position = 0;
            foreach (var rune in text.EnumerateRunes()) { position += rune.Utf16SequenceLength; positions.Add(position); }
            return positions.ToArray();
        }

        void Reject(Action action)
        {
            try { action(); }
            catch (NotSupportedException) { assertions++; return; }
            throw new InvalidOperationException("Unsupported Tokenizer API silently succeeded.");
        }

        void RejectBudget(Action action)
        {
            try { action(); }
            catch (ArgumentException) { assertions++; return; }
            throw new InvalidOperationException("Production decision input was silently truncated or admitted over budget.");
        }

        static Generator.DecisionInput Input(string state, string instructions = "Choose.", string option = "false") =>
            new(JsonSerializer.SerializeToElement(new { brief = state }),
                [new Generator.ChoiceDecisionQuestion("route", instructions,
                    [new Generator.DecisionCandidate("first", option), new Generator.DecisionCandidate("second", "true")])]);

        void CompareProductionBatch(JsonElement observation)
        {
            var question = new Generator.ChoiceDecisionQuestion(observation.GetProperty("questionId").GetString()!,
                observation.GetProperty("instructions").GetString()!, observation.GetProperty("candidates").EnumerateArray()
                    .Select(c => new Generator.DecisionCandidate(c.GetProperty("id").GetString()!, c.GetProperty("description").GetString()!)));
            var batch = production.Prepare(new(observation.GetProperty("state"), [question]));
            foreach (var input in observation.GetProperty("inputs").EnumerateArray())
            {
                string name = input.GetProperty("name").GetString()!;
                if (name == "qtype") continue;
                bool markers = name.StartsWith("marker_", StringComparison.Ordinal);
                int length = markers ? batch.MarkerWidth : batch.SequenceLength;
                Require(input.GetProperty("shape")[1].GetInt32() == length, $"Production {name} shape changed.");
                if (name == "marker_mask")
                {
                    bool[] expected = input.GetProperty("boolValues").EnumerateArray().Select(x => x.GetBoolean()).ToArray();
                    for (int i = 0; i < length; i++)
                        Require(batch.MarkerMask[0, i] == expected[i], $"Production marker mask changed at {i}.");
                }
                else
                {
                    long[] expected = input.GetProperty("int64Values").EnumerateArray().Select(x => x.GetInt64()).ToArray();
                    for (int i = 0; i < length; i++)
                    {
                        long actual = name switch {
                            "input_ids" => batch.InputIds[0, i],
                            "attention_mask" => batch.AttentionMask[0, i],
                            "marker_pos" => batch.MarkerPositions[0, i],
                            _ => throw new InvalidOperationException($"Unexpected retained input {name}.")
                        };
                        Require(actual == expected[i], $"Production {name} changed at {i}.");
                    }
                }
            }
        }

        void Require(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
