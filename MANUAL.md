# Glacier.Bundle — Developer Manual

The **Glacier.Bundle** library provides a unified registry and fluent orchestration builder to aggregate data from Glacier's tabular (`Glacier.Polaris`), vector similarity (`Glacier.Vector`), relational network (`Glacier.Graph`), and structural document (`Glacier.DocTree`) engines. It compiles cross-paradigm query results into zero-allocation, token-budgeted prompt context blocks for consumption by LLMs or autonomous agents under Native AOT.

---

## 1. System Architecture

In advanced RAG (Retrieval-Augmented Generation) and agentic workflows, information is rarely stored in a single data structure. An agent might need to look up a customer profile (`DataFrame` / tabular), find semantically similar past chat sessions (`VectorIndex` / `HnswVectorIndex`), map the customer's social or transit network (`GraphStore` / `CsrGraphStore`), and verify security policy constraints (`DocNode` hierarchical document tree).

`Glacier.Bundle` solves this by introducing:
- **`BundleContext`**: A thread-safe, central repository matching storage engine instances (vector indexes, graphs, document trees, Polaris `DataFrame`s, and typed tabular objects) to string identifiers, while caching stateless traversal wrappers (`GraphSearch`, `CsrGraphSearch`, `TreeSearch`) without taking disposal ownership.
- **`BundleBuilder`**: A fluent, low-allocation builder API that queries the registered engine instances and appends formatted reports to a unified context frame.
- **`CompactingBundleBuilder` & `TokenBudget`**: Reservation-aware token budgeting and multi-strategy compaction (`StrictPrune`, `HeadTailTruncate`, `ProportionalDecay`) with direct `IBufferWriter<char>` streaming.

```
       +--------------------------------------------+
       |               BundleContext                |
       +-----+----------+-----------+----------+-----+
             |          |           |          |
        DataFrame    Vector       Graph     DocTree
        (Polaris)   (SIMD/HNSW) (FS/CSR)   (Markdown)
             |          |           |          |
             +----------+-----+-----+----------+
                              |
              BundleBuilder / CompactingBundleBuilder
                              |
                [Token-Budgeted Text Frame]
                              |
                         LlmAgent Run
```

---

## 2. API Reference & Specifications

### BundleContext
Registers and resolves active storage engine resources.

```csharp
namespace Glacier.Bundle.Core;

public class BundleContext
{
    public void RegisterVectorIndex(string key, VectorIndex index);
    public void RegisterHnswVectorIndex(string key, HnswVectorIndex index);
    public void RegisterGraphStore(string key, GraphStore store);
    public void RegisterCsrGraphStore(string key, CsrGraphStore store);
    public void RegisterDocTree(string key, DocNode rootNode);
    public void RegisterDataFrame(string key, DataFrame dataFrame);
    public void RegisterTabularData<T>(string key, T dataFrame) where T : class;

    public VectorIndex GetVectorIndex(string key);
    public HnswVectorIndex GetHnswVectorIndex(string key);
    public GraphStore GetGraphStore(string key);
    public CsrGraphStore GetCsrGraphStore(string key);
    public DocNode GetDocTree(string key);
    public DataFrame GetDataFrame(string key);
    public T GetTabularData<T>(string key) where T : class;
}
```

* Thread safety is guaranteed via internal `ConcurrentDictionary` registries (`TryGet*` overloads are also provided).
* `RegisterDataFrame` and `RegisterTabularData<DataFrame>` synchronize automatically so callers can use either retrieval path cleanly under Native AOT without `dynamic`.

### BundleBuilder
Constructs the final text context bundle.

```csharp
public class BundleBuilder
{
    public BundleBuilder(BundleContext context);
    public BundleBuilder BeginBundle(string bundleName);
    public BundleBuilder AppendTabularRow(string header, Func<StringBuilder, StringBuilder> formatter);
    public BundleBuilder AppendDataFrame(string dataFrameKey, int maxRows = 20, string label = "DATA LAYER");
    public BundleBuilder AppendGraphTopology(string graphKey, string centerNodeId, int maxHops, string label = "RELATIONAL MAP");
    public BundleBuilder AppendCsrGraphTopology(string csrGraphKey, string centerNodeId, int maxHops, string label = "RELATIONAL MAP");
    public BundleBuilder AppendDocumentTreeSection(string treeKey, string sectionHeader, string label = "DOCUMENT TREE POLICY");
    public BundleBuilder AppendVectorContext(string vectorKey, float[] queryVector, int topK, string label = "SEMANTIC HISTORY");
    public BundleBuilder AppendHnswVectorContext(string hnswKey, ReadOnlySpan<float> queryVector, int topK, string label = "SEMANTIC HISTORY", int efSearch = 64);
    public string Build();
}
```

### CompactingBundleBuilder & TokenBudget
Enforces token limits, reservations, and priority-based compaction with zero-intermediate-string `WriteTo(IBufferWriter<char>)` streaming.

```csharp
public class CompactingBundleBuilder
{
    public CompactingBundleBuilder(TokenBudget budget, BundleContext? context = null);
    public CompactingBundleBuilder(BundleContext context, int totalBudget, CompactionStrategy defaultStrategy = CompactionStrategy.StrictPrune);
    public CompactingBundleBuilder AddSection(string name, int priority, string content, bool isFixed = false, CompactionStrategy? strategyOverride = null);
    public CompactingBundleBuilder BeginBundle(string bundleName, int priority = int.MaxValue);
    public CompactingBundleBuilder AppendDataFrame(string name, int priority, string dataFrameKey, int maxRows = 20, string label = "DATA LAYER");
    public CompactingBundleBuilder AppendCsrGraphTopology(string name, int priority, string csrGraphKey, string centerNodeId, int maxHops, string label = "RELATIONAL MAP");
    public CompactingBundleBuilder AppendHnswVectorContext(string name, int priority, string hnswKey, ReadOnlySpan<float> queryVector, int topK, string label = "SEMANTIC HISTORY", int efSearch = 64);
    public void WriteTo(IBufferWriter<char> writer, CompactionStrategy? overrideStrategy = null);
    public string Build(CompactionStrategy? overrideStrategy = null);
}
```

---

## 3. End-to-End Code Example (Native AOT Safe)

Here is a compiling, AOT-safe example demonstrating how to register engines into a `BundleContext` and compile both standard and token-budgeted context bundles without `dynamic`:

```csharp
using System;
using System.Buffers;
using System.Text;
using Glacier.Bundle.Budgeting;
using Glacier.Bundle.Core;
using Glacier.DocTree.Core;
using Glacier.Graph.Storage;
using Glacier.Polaris;
using Glacier.Polaris.Data;
using Glacier.Vector.Index;
using Glacier.Vector.Storage;

// 1. Initialize engines (caller owns lifetimes)
var context = new BundleContext();

// A. Register Vector Index (or HnswVectorIndex via RegisterHnswVectorIndex)
using var vectorStorage = new InMemoryVectorStorage(128);
using var vectorIndex = new VectorIndex(vectorStorage);
// ... populate vectorIndex ...
context.RegisterVectorIndex("VibeEmbeddings", vectorIndex);

// B. Register Graph Store (or CsrGraphStore via RegisterCsrGraphStore)
using var graph = new GraphStore();
// ... populate graph ...
context.RegisterGraphStore("TransitNetwork", graph);

// C. Register Document Tree
var docRoot = new DocNode { Type = NodeType.Header1, Content = "Security Policy" };
// ... populate document tree ...
context.RegisterDocTree("CompanyPolicies", docRoot);

// D. Register Polaris DataFrame and/or typed AOT-safe tabular model
using var clientDf = new DataFrame(new ISeries[]
{
    Utf8StringSeries.FromStrings("Id", new[] { "CUST-402" }),
    Utf8StringSeries.FromStrings("Name", new[] { "Alice Smith" }),
    Utf8StringSeries.FromStrings("Tier", new[] { "VIP" })
});
context.RegisterDataFrame("ActiveUsersDf", clientDf);

var clientProfile = new ClientProfile("CUST-402", "Alice Smith", "VIP");
context.RegisterTabularData("ActiveUser", clientProfile);

// 2. Aggregate data using BundleBuilder
var builder = new BundleBuilder(context);
string promptContext = builder
    .BeginBundle("User Assessment Portfolio")
    .AppendTabularRow("Customer CRM Metadata", sb =>
    {
        var profile = context.GetTabularData<ClientProfile>("ActiveUser");
        return sb.AppendLine($"Client: {profile.Name} | Tier: {profile.Tier} (ID: {profile.Id})");
    })
    .AppendDataFrame("ActiveUsersDf", maxRows: 5, label: "Polaris Client Table")
    .AppendVectorContext(
        vectorKey: "VibeEmbeddings",
        queryVector: new float[128],
        topK: 3,
        label: "Semantic Preferences")
    .AppendGraphTopology(
        graphKey: "TransitNetwork",
        centerNodeId: "StationA",
        maxHops: 2,
        label: "Neighborhood Connectivity")
    .AppendDocumentTreeSection(
        treeKey: "CompanyPolicies",
        sectionHeader: "Refund Terms",
        label: "Policy Boundary Checks")
    .Build();

// 3. Or stream token-budgeted output directly into an IBufferWriter<char>
var budget = new TokenBudget(totalBudget: 512, defaultStrategy: CompactionStrategy.ProportionalDecay);
var compacting = new CompactingBundleBuilder(budget, context)
    .BeginBundle("Budgeted Portfolio")
    .AppendDataFrame("Table", priority: 80, "ActiveUsersDf", maxRows: 5)
    .AddSection("Footer", priority: 1000, content: "[END_OF_BUNDLE]", isFixed: true);

var writer = new ArrayBufferWriter<char>();
compacting.WriteTo(writer);
Console.WriteLine(promptContext);

sealed record ClientProfile(string Id, string Name, string Tier);
```

---

## 4. Performance & Memory Management

`Glacier.Bundle` is optimized for low-allocation, deterministic execution:
- **`StringBuilder` & Working Buffer Reuse**: `BundleBuilder` and `CompactingBundleBuilder` reuse internal `StringBuilder` and section working buffers across calls, and `WriteTo(IBufferWriter<char>)` streams directly into caller spans without intermediate `string` allocations when using `HeuristicTokenEstimator`.
- **Cached Traversal Wrappers**: `BundleContext` caches stateless `GraphSearch`, `CsrGraphSearch`, and `TreeSearch` wrappers in a `ConditionalWeakTable` keyed by engine instance.
- **Pooled Zero-Array-Allocation Queries**: `AppendHnswVectorContext` and `AppendCsrGraphTopology` rent scratch buffers from `ArrayPool<T>.Shared` rather than allocating per-query arrays or lists.
- **Unicode-Aware Budgeting**: `TokenBudget.TruncateHeadTail` and `HeuristicTokenEstimator.TruncateToTokens` snap cuts to `StringInfo` grapheme cluster boundaries and word boundaries without splitting surrogate pairs, combining marks, ZWJ emojis, or `\r\n` sequences.
