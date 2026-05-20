# Glacier.Bundle — Developer Manual

The **Glacier.Bundle** library provides a unified registry and fluent orchestration builder to aggregate data from Glacier's tabular (`Glacier.Polaris`), vector similarity (`Glacier.Vector`), relational network (`Glacier.Graph`), and structural document (`Glacier.DocTree`) engines. It compiles cross-paradigm query results into zero-allocation, high-density prompt context blocks for consumption by LLMs or autonomous agents.

---

## 1. System Architecture

In advanced RAG (Retrieval-Augmented Generation) and agentic workflows, information is rarely stored in a single data structure. An agent might need to look up a customer profile (tabular), find semantically similar past chat sessions (vector), map the customer's social network or transit connections (graph), and verify security policy constraints (hierarchical document tree).

`Glacier.Bundle` solves this by introducing:
- **`BundleContext`**: A thread-safe, central repository matching storage engine instances (indexes, graphs, document trees, tabular dataframes) to string identifiers.
- **`BundleBuilder`**: A fluent, low-allocation builder API that queries the registered engine instances and appends formatted reports to a unified context frame.

```
       +--------------------------------------------+
       |               BundleContext                |
       +-----+----------+-----------+----------+-----+
             |          |           |          |
       Tabular DF    Vector      Graph       DocTree
       (Polaris)     (Vector)    (Graph)    (DocTree)
             |          |           |          |
             +----------+-----+-----+----------+
                              |
                       BundleBuilder
                              |
                    [Formatted Text Frame]
                              |
                         LlmAgent Run
```

---

## 2. API Reference & Specifications

### BundleContext
Registers and resolves active database engine resources.

```csharp
namespace Glacier.Bundle.Core;

public class BundleContext
{
    public void RegisterVectorIndex(string key, VectorIndex index);
    public void RegisterGraphStore(string key, GraphStore store);
    public void RegisterDocTree(string key, DocNode rootNode);
    public void RegisterTabularData<T>(string key, T dataFrame) where T : class;

    public VectorIndex GetVectorIndex(string key);
    public GraphStore GetGraphStore(string key);
    public DocNode GetDocTree(string key);
    public T GetTabularData<T>(string key) where T : class;
}
```

* Thread safety is guaranteed via internal `ConcurrentDictionary` registries.
* Tabular registrations are generic to prevent tight coupling with specific dataframe layouts.

### BundleBuilder
Constructs the final text context bundle.

```csharp
public class BundleBuilder
{
    public BundleBuilder(BundleContext context);
    public BundleBuilder BeginBundle(string bundleName);
    public BundleBuilder AppendTabularRow(string header, Func<StringBuilder, StringBuilder> formatter);
    public BundleBuilder AppendGraphTopology(string graphKey, string centerNodeId, int maxHops, string label = "RELATIONAL MAP");
    public BundleBuilder AppendDocumentTreeSection(string treeKey, string sectionHeader, string label = "DOCUMENT TREE POLICY");
    public BundleBuilder AppendVectorContext(string vectorKey, float[] queryVector, int topK, string label = "SEMANTIC HISTORY");
    public string Build();
}
```

* **`BeginBundle`**: Resets the internal builder buffer and appends standard audit headers.
* **`AppendTabularRow`**: Appends tabular metadata formatted via a custom builder delegate.
* **`AppendGraphTopology`**: Queries `Glacier.Graph` using BFS search to find connected nodes and writes their layout.
* **`AppendDocumentTreeSection`**: Traverses `Glacier.DocTree` using header search to retrieve and format structural section texts.
* **`AppendVectorContext`**: Calls SIMD-accelerated cosine similarity search in `Glacier.Vector` and lists top matched item metadata.

---

## 3. End-to-End Code Example

Here is a production example demonstrating how to register multiple engines into a `BundleContext` and generate a single context bundle.

```csharp
using System;
using System.Text;
using Glacier.Bundle.Core;
using Glacier.Vector.Index;
using Glacier.Graph.Storage;
using Glacier.DocTree.Core;

// 1. Initialize engines
var context = new BundleContext();

// A. Register Vector Index
var vectorIndex = new VectorIndex(dimension: 128);
// ... populate vectorIndex ...
context.RegisterVectorIndex("VibeEmbeddings", vectorIndex);

// B. Register Graph Store
var graph = new GraphStore();
// ... populate graph ...
context.RegisterGraphStore("TransitNetwork", graph);

// C. Register Document Tree
var docRoot = new DocNode(DocNodeType.Header, "Security Policy");
// ... populate document tree ...
context.RegisterDocTree("CompanyPolicies", docRoot);

// D. Register Tabular Data Frame (e.g. from Polaris)
var clientProfile = new { Id = "CUST-402", Name = "Alice Smith", Tier = "VIP" };
context.RegisterTabularData("ActiveUser", clientProfile);

// 2. Aggregate data using BundleBuilder
var builder = new BundleBuilder(context);
string promptContext = builder
    .BeginBundle("User Assessment Portfolio")
    .AppendTabularRow("Customer CRM Metadata", sb => 
    {
        var profile = context.GetTabularData<dynamic>("ActiveUser");
        return sb.AppendLine($"Client: {profile.Name} | Tier: {profile.Tier} (ID: {profile.Id})");
    })
    .AppendVectorContext(
        vectorKey: "VibeEmbeddings", 
        queryVector: new float[128], // search vector
        topK: 3, 
        label: "Semantic Preferences"
    )
    .AppendGraphTopology(
        graphKey: "TransitNetwork", 
        centerNodeId: "StationA", 
        maxHops: 2, 
        label: "Neighborhood Connectivity"
    )
    .AppendDocumentTreeSection(
        treeKey: "CompanyPolicies", 
        sectionHeader: "Refund Terms", 
        label: "Policy Boundary Checks"
    )
    .Build();

// 3. Inject the bundle directly into LlmAgent
Console.WriteLine(promptContext);
```

---

## 4. Performance & Memory Management

`Glacier.Bundle` is optimized for zero-allocation performance:
- **`StringBuilder` Re-use**: The builder initializes with a default 2048-character buffer and appends segments directly to prevent mid-operation garbage collection allocations.
- **Lazy Evaluation**: Custom tabular attributes are passed as formatting delegates, deferring string allocation until final compilation.
- **Index-Based Graph and Tree Traversals**: Traversal helpers navigate indexes directly without duplicating node objects or converting subgraphs into temporary collections.
