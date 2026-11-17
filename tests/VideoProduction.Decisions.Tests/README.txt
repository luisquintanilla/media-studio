Optional decision-only console test harness

Offline fixtures (no native loading, model downloads, generation or cloud calls):
  dotnet restore tests\VideoProduction.Decisions.Tests\VideoProduction.Decisions.Tests.csproj --locked-mode
  dotnet run --project tests\VideoProduction.Decisions.Tests\VideoProduction.Decisions.Tests.csproj --no-restore

On an empty package cache, explicitly select the project-scoped feed configuration:
  dotnet restore tests\VideoProduction.Decisions.Tests\VideoProduction.Decisions.Tests.csproj --locked-mode --configfile src\VideoProduction.Decisions\NuGet.Config

Actual native CPU inference, only after the three approved assets have been
provisioned into an explicit directory outside the checkout:
  dotnet run --project tests\VideoProduction.Decisions.Tests\VideoProduction.Decisions.Tests.csproj --no-restore -- --native-smoke --model-directory DIRECTORY --output RECEIPT.json

The smoke performs hash verification before loading; one synthetic storyboard
brief is inferred twice to distinguish cold/warm calls. It checks actual
tokenizer reserved-token and context-budget rejection plus precancellation.
The receipt retains the full domain/distribution, raw logits, all asset/source/
dependency pins, input fingerprint and load/inference latency. Null usage means
the provider did not report usage; it is not a fabricated zero.
The receipt also records hashes of the actual loaded native ORT/tokenizer DLLs,
the exact operating system/process architecture and the minimal locked package
closure (official Abstractions 10.11.0, Tensors 10.0.12 to preserve the existing
integrated MAF 1.26 / AI 10.10.0 dependency minimum).
This Windows native proof makes no Ubuntu/Linux parity or AOT claim.

Evidence\native-smoke.json is an actual captured smoke, not a fixture or quality
benchmark. Its timing is one machine/run, not a latency guarantee. Probabilities
are uncalibrated. No application development or held-out datasets are read here.
Routing evaluation and generation fixtures belong to the application harness.

Bounded native mapping sanity (exactly six native calls; no prompt sweep):
  dotnet run --project tests\VideoProduction.Decisions.Tests\VideoProduction.Decisions.Tests.csproj --no-restore -- --mapping-sanity --model-directory DIRECTORY --output RECEIPT.json

The mapping sanity runs positive/negative sentiment examples and one synthetic
garden brief, each in normal and reversed candidate order. A tracing scorer
passes the exact captured JuliaInputBatch to the actual synchronous ORT scorer.
Independent native tokenizer encoding reconstructs the complete framing and
verifies that state, question and both option descriptions reach that scorer
without truncation. Captured tensors retain shapes, types, values, attention,
marker positions, masks and SHA-256; outputs retain raw logits, their hash,
winner index, full distribution and exact mapped public application IDs.
Tensor hashes are over contiguous native bytes (little-endian int64/float32
and one-byte bool on this Windows x64 run); shape/type are separate fields.
There is no policy override, renormalization or forced balance in this harness.
Known-answer/position-sensitivity outcomes are recorded as observed, not repaired
or silently dropped. This is wiring evidence, not video-routing utility.

Evidence\mapping-sanity.json captures all six calls and 25 structural assertions.
Both sentiment examples selected their known answers under both candidate
orders. The garden brief selected planning-fast under both orders; this says
nothing about video quality or suitability of routing policy/candidate wording.
The original receipts remain frozen under their original names and document
the then-tested 10.0.9 closure. Later compatible-closure reruns use -v2 names;
do not infer updated dependencies from the old frozen receipts.

To replace the garden pair with the application's exact captured storyboard
state/question/options while keeping the six-call bound, add:
  --native-request APPLICATION-REQUEST.json
The file must contain one Choice request ({state,questions:[...]}, optionally
wrapped as {request:...}); IDs, descriptions and full JSON state are retained.
The receipt binds the input file's SHA-256. The garden default is a synthetic
smoke only and must never be represented as the actual application input.

JSON reflection serialization is deliberately disabled in this executable.
All wire/state/result serialization uses explicit generated metadata.
VPD001 is this application's incubation warning, not Microsoft's warning ID.
