# Glacier.Bundle User Manual

## 1. Registering Stores (`BundleContext`)

`BundleContext` (`Glacier.Bundle.Core`) maps string keys to storage engines using thread-safe concurrent dictionaries without taking ownership of disposable stores:

```csharp
using Glacier.Bundle.Core;
using Glacier.DocTree.Core;
using Glacier.Graph.Storage;
using Glacier.Polaris;
using Glacier.Vector.Index;

var context = new BundleContext();

context.RegisterDataFrame("ClientsDf", dataFrame);
context.RegisterTabularData("Profile", new ClientProfile("CUST-402", "Alice", "Enterprise"));
context.RegisterGraphStore("Topology", graphStore);
context.RegisterCsrGraphStore("StaticTopology", csrStore);
context.RegisterDocTree("Policies", docRoot);
context.RegisterVectorIndex("Tickets", vectorIndex);
context.RegisterHnswVectorIndex("HnswTickets", hnswIndex);
```

Lookup pairs follow the same naming pattern (`GetDataFrame` / `TryGetDataFrame`, `GetTabularData<T>`, `GetGraphStore`, `GetCsrGraphStore`, `GetDocTree`, `GetVectorIndex`, `GetHnswVectorIndex`). Registering via `RegisterDataFrame` and `RegisterTabularData<DataFrame>` keeps both lookup paths synchronized.

---

## 2. Unbounded Context Assembly (`BundleBuilder`)

Use `BundleBuilder` when your prompt fits comfortably inside the model context window:

```csharp
var queryVector = new float[128];

string prompt = new BundleBuilder(context)
    .BeginBundle("Escalation Portfolio")
    .AppendTabularRow("CRM Profile", sb =>
    {
        var p = context.GetTabularData<ClientProfile>("Profile");
        return sb.AppendLine($"Client: {p.Name} ({p.Tier})");
    })
    .AppendDataFrame("ClientsDf", maxRows: 10, label: "DATA LAYER")
    .AppendGraphTopology("Topology", centerNodeId: "CUST-402", maxHops: 2, label: "RELATIONAL MAP")
    .AppendCsrGraphTopology("StaticTopology", centerNodeId: "CUST-402", maxHops: 2, label: "CSR MAP")
    .AppendDocumentTreeSection("Policies", sectionHeader: "Enterprise SLAs", label: "SLA TERMS")
    .AppendVectorContext("Tickets", queryVector, topK: 3, label: "SEMANTIC HISTORY")
    .AppendHnswVectorContext("HnswTickets", queryVector, topK: 3, label: "HNSW HISTORY", efSearch: 64)
    .Build();
```

---

## 3. Token-Budgeted Compaction (`CompactingBundleBuilder` & `TokenBudget`)

`CompactingBundleBuilder` (`Glacier.Bundle.Core` & `Glacier.Bundle.Budgeting`) packs prioritized sections inside `TokenBudget`:

- **`StrictPrune`:** Drops lower-priority optional sections once remaining budget runs out.
- **`HeadTailTruncate`:** Preserves opening (`headRatio = 0.6`) and closing text of oversized sections.
- **`ProportionalDecay`:** Scales section allocations proportionally across remaining variable sections.

```csharp
using System.Buffers;
using Glacier.Bundle.Budgeting;
using Glacier.Bundle.Core;

var budget = new TokenBudget(
    totalBudget: 1024,
    defaultStrategy: CompactionStrategy.ProportionalDecay);

budget.Reserve(128); // Reserve tokens for system instructions or footer

var compacting = new CompactingBundleBuilder(budget, context)
    .BeginBundle("Support Brief", priority: int.MaxValue)
    .AddSection("Instructions", priority: 1000, content: "Answer strictly from SLA terms.", isFixed: true)
    .AppendDataFrame("Accounts", priority: 90, dataFrameKey: "ClientsDf", maxRows: 10)
    .AppendCsrGraphTopology("Network", priority: 70, csrGraphKey: "StaticTopology", centerNodeId: "CUST-402", maxHops: 2)
    .AppendHnswVectorContext("Similar", priority: 50, hnswKey: "HnswTickets", queryVector, topK: 5)
    .AddSection("RawNotes", priority: 10, content: longNotes, strategyOverride: CompactionStrategy.HeadTailTruncate);

// Stream directly without allocating intermediate strings when using HeuristicTokenEstimator:
var buffer = new ArrayBufferWriter<char>();
compacting.WriteTo(buffer);

// Or materialize a string:
string output = compacting.Build();
```

Implement `ITokenEstimator` when pairing `TokenBudget` with a model-specific BPE tokenizer instead of the default `HeuristicTokenEstimator`.

---

## 4. Running the Sample

```powershell
dotnet run --project samples/Glacier.Bundle.Demo/Glacier.Bundle.Demo.csproj -c Release
```
