# Glacier.Bundle

[![NuGet Version](https://img.shields.io/nuget/v/Glacier.Bundle.svg?style=flat-square)](https://www.nuget.org/packages/Glacier.Bundle/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/Glacier.Bundle.svg?style=flat-square)](https://www.nuget.org/packages/Glacier.Bundle/)

**Glacier.Bundle** is the unified, high-density AI semantic orchestration engine and prompt context compiler for .NET 10. It coordinates, resolves, and aggregates multi-shape datasets from the entire **Glacier High-Performance Storage Suite** into single, high-density, context-rich prompt frames for LLMs under 20 milliseconds.

Rather than performing separate slow queries, Glacier.Bundle acts as the single central broker—binding relational topology, document hierarchies, semantic similarity, and tabular CRM data together to give AI models complete, unified truth.

---

## Sibling Storage Engines

Glacier.Bundle leverages the following specialized high-performance C# libraries to compile context:

*   🌐 **[Glacier.Graph](https://github.com/ian-cowley/Glacier.Graph)**: High-performance, zero-allocation array-backed graph database utilizing Forward Star representation for sub-millisecond local relation and fraud neighborhood tracing.
*   📂 **[Glacier.DocTree](https://github.com/ian-cowley/Glacier.DocTree)**: Zero-dependency semantic Markdown document parser and breadcrumb tree traversal engine for absolute contextual RAG retrieval.
*   🧠 **[Glacier.Vector](https://github.com/ian-cowley/Glacier.Vector)**: Hardware-accelerated (SIMD-optimized) vector similarity search index for instant semantic retrieval.
*   📊 **[Glacier.Polaris](https://github.com/ian-cowley/PolarsPlus)**: Blazing-fast, C#-native high-speed tabular DataFrame engine (derived from PolarsPlus).

---

## Key Features

*   🔄 **Unified Bundle Context**: A thread-safe, central registry (`BundleContext`) coordinating vectors, graph topologies, document node hierarchies, and tabular schemas.
*   🛠️ **Fluent BundleBuilder**: A zero-allocation builder pattern allowing programmatically structured formatting and assembly of prompts.
*   🌐 **Multi-Layer Integration**: Connects tabular data CRM rows, Graph neighbor lists, strict DocTree section texts, and Vector semantic results seamlessly.
*   ⚡ **Sub-20ms Compilations**: Fully optimized C# code paths that resolve massive multi-shape datasets in milliseconds.

---

## Installation

Install Glacier.Bundle using the .NET CLI:

```bash
dotnet add package Glacier.Bundle
```

---

## Quick Start

### 1. Build and Register the Registry Layer

```csharp
using Glacier.Bundle.Core;
using Glacier.Vector.Index;
using Glacier.Graph.Storage;
using Glacier.DocTree.Core;

// Initialize central bundle registry
var bundleCtx = new BundleContext();

// 1. Register tabular CRM databases
var clientData = new Dictionary<string, object>(); // Representing Polaris DataFrames
bundleCtx.RegisterTabularData("Polaris_Clients", clientData);

// 2. Register hardware-accelerated Vector search indices
VectorIndex vectorIdx = ...;
bundleCtx.RegisterVectorIndex("Vector_Tickets", vectorIdx);

// 3. Register Graph relational databases
GraphStore graphStore = ...;
bundleCtx.RegisterGraphStore("Graph_Topology", graphStore);

// 4. Register parsed SLA Markdown documents
DocNode docRoot = ...;
bundleCtx.RegisterDocTree("DocTree_SLA", docRoot);
```

### 2. Compile Multidimensional Context via BundleBuilder

```csharp
var queryVector = new float[1536] { 1.0f, ... };

string contextPrompt = new BundleBuilder(bundleCtx)
    .BeginBundle("Customer Escalation Resolution")
    .AppendTabularRow("Client CRM Profile", sb => sb
        .AppendLine("  ID: User_9021")
        .AppendLine("  Name: DeltaCorp Ltd")
        .AppendLine("  Tier: Enterprise"))
    .AppendGraphTopology("Graph_Topology", "User_9021", maxHops: 2, label: "Relational Fraud Mapping")
    .AppendDocumentTreeSection("DocTree_SLA", "Enterprise SLAs", label: "Guaranteed SLA Policies")
    .AppendVectorContext("Vector_Tickets", queryVector, topK: 1, label: "Semantic Ticket History")
    .Build();

Console.WriteLine(contextPrompt);
```

---

## Interactive Demo Output

When running the bundled orchestrator demo (`samples/Glacier.Bundle.Demo`), Glacier.Bundle resolves CRM details, relational fraud paths, strict SLA Markdown structures, and ticket history from raw bytes under 20ms:

```text
======================================================================
 Glacier.Bundle | Unified AI Semantic Orchestration Engine
======================================================================

[1] Populating Polaris Tabular Stub...
[2] Populating Vector Search Store (SIMD)...
[3] Populating Relational Graph Store...
[4] Parsing SLA Document Tree...

All high-performance storage engines registered in BundleContext!

[5] Executing high-speed Bundle compilation...

Bundle compiled in 17.4316 ms!

======================================================================
 SYSTEM RESOLVED CONTEXT BUNDLE: CUSTOMER ESCALATION: DELTACORP LTD
 GENERATED AT: 2026-05-18 17:14:55 UTC
======================================================================

--- [DATA LAYER] CLIENT CRM PROFILE ---
  ID: User_9021
  Name: DeltaCorp Ltd
  Tier: Enterprise
  Value: £85,200.00
  Status: Active

--- [RELATIONAL LAYER] RELATIONAL FRAUD VERIFICATION (2 HOPS FROM User_9021) ---
Target Entity: User_9021
Connected Network Entities (3):
  SubAccount_B, SubAccount_A, Suspicious_IP_88

--- [HIERARCHICAL LAYER] STRUCTURAL SLA TERMS ---
Document Path: Document Root > Service Level Agreements > Enterprise SLAs
Content Frame:
Enterprise SLAs
Enterprise accounts enjoy a guaranteed 99.99% uptime with immediate 15-minute response times.
No hardware throttling is applied.

--- [SEMANTIC LAYER] SEMANTIC HISTORICAL MATCHING ---
[Rank 1 | Match Score: 1.0000] Historical Ticket #4823: Customer requested custom API access rate expansion. Granted temporary bypass.

======================================================================
 END OF RESOLVED CONTEXT BUNDLE
======================================================================
```

---

## Contributing

We welcome community contributions! Please read [CONTRIBUTING.md](CONTRIBUTING.md) for local setups, branch models, and PR checklist details.

## Credits

Developed by **Ian Cowley** and **Antigravity (Google DeepMind)**.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
