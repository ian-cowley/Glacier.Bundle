![Glacier.Bundle Banner](assets/banner.jpg)

# Glacier.Bundle

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Native AOT](https://img.shields.io/badge/Native%20AOT-Ready-brightgreen.svg)](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
[![NuGet](https://img.shields.io/nuget/v/Glacier.Bundle.svg)](https://www.nuget.org/packages/Glacier.Bundle/)
[![Website](https://img.shields.io/badge/Website-glaciersoftware.dev-66fcf1.svg)](https://glaciersoftware.dev/)

`Glacier.Bundle` is a prompt-assembly and token-budgeting library for .NET 10. It gives AI applications a single `BundleContext` registry to query across `Glacier.Polaris` tables, `Glacier.Vector` indices, `Glacier.Graph` networks, and `Glacier.DocTree` Markdown trees into one compact text frame.

It is built for LLM orchestration pipelines that pull structured records, graph hops, document sections, and dense vector hits under strict token limits and Native AOT constraints without reflection or `dynamic` dispatch.

## Install

```powershell
dotnet add package Glacier.Bundle
```

## Quick Start

```csharp
using System.Buffers;
using Glacier.Bundle.Budgeting;
using Glacier.Bundle.Core;

var context = new BundleContext();
// Register Polaris DataFrames, GraphStore/CsrGraphStore, DocNode trees, and Vector/Hnsw indices.

var budget = new TokenBudget(totalBudget: 512, defaultStrategy: CompactionStrategy.ProportionalDecay);
var builder = new CompactingBundleBuilder(budget, context)
    .BeginBundle("Customer Escalation", priority: int.MaxValue)
    .AddSection("Summary", priority: 100, content: "Enterprise SLA escalation context.", isFixed: true);

var writer = new ArrayBufferWriter<char>();
builder.WriteTo(writer);
```

## What You Can Do

- **Register multiple stores (`BundleContext`):** Bind named keys to `DataFrame`, typed tabular objects (`RegisterTabularData<T>`), `GraphStore`, `CsrGraphStore`, `DocNode`, `VectorIndex`, and `HnswVectorIndex` inside one thread-safe registry.
- **Assemble formatted context (`BundleBuilder`):** Chain `BeginBundle`, `AppendTabularRow`, `AppendDataFrame`, `AppendGraphTopology`, `AppendCsrGraphTopology`, `AppendDocumentTreeSection`, `AppendVectorContext`, and `AppendHnswVectorContext` into a structured prompt string.
- **Enforce hard token budgets (`CompactingBundleBuilder` & `TokenBudget`):** Reserve header/footer headroom, honor `TokenBudget.AvailableBudget`, override compaction per section (`StrictPrune`, `HeadTailTruncate`, `ProportionalDecay`), and stream straight to `IBufferWriter<char>` via `WriteTo`.
- **Estimate & truncate text safely (`HeuristicTokenEstimator`):** Estimate token budgets cleanly and truncate text along Unicode `StringInfo` grapheme and word boundaries without splitting surrogate pairs or emojis.

## Platform & Compatibility

`Glacier.Bundle` targets `.NET 10` (`net10.0`) with `<IsAotCompatible>true</IsAotCompatible>` and zero reflection-based `dynamic` lookups.

## Documentation

- [User Manual](docs/manual.md) — Complete API reference for `BundleContext`, `BundleBuilder`, `CompactingBundleBuilder`, `TokenBudget`, and sample demo usage
- [Architecture](docs/architecture.md) — Thread-safe registries, `ConditionalWeakTable` search wrappers, `ArrayPool<T>` query scratch buffers, and Unicode truncation
- [Changelog](CHANGELOG.md) — Release history (`v1.1.0` – `v1.1.1`)

## License & Credits

Licensed under the [MIT License](LICENSE).

Developed by Ian Cowley, Antigravity (Google DeepMind) and Claude.
