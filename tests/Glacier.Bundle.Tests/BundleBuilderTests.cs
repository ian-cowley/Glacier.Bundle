using System;
using System.Collections.Generic;
using Glacier.Bundle.Budgeting;
using Glacier.Bundle.Core;
using Glacier.DocTree.Core;
using Glacier.Graph.Storage;
using Glacier.Vector.Index;
using Glacier.Vector.Storage;
using Xunit;

namespace Glacier.Bundle.Tests
{
    public class BundleBuilderTests
    {
        [Fact]
        public void BundleBuilder_EndToEnd_MultiEngineAssembly()
        {
            var context = new BundleContext();

            // 1. Tabular data
            var clientData = new Dictionary<string, string>
            {
                { "plan", "Enterprise" },
                { "mrr", "$50,000" }
            };
            context.RegisterTabularData("client_metrics", clientData);

            // 2. Vector index
            using var vectorStorage = new InMemoryVectorStorage(4);
            using var vectorIndex = new VectorIndex(vectorStorage);
            vectorIndex.Add(new float[] { 1f, 0f, 0f, 0f }, "Ticket #101: Priority support resolved in 5m.");
            context.RegisterVectorIndex("tickets", vectorIndex);

            // 3. Graph store
            var graphStore = new GraphStore(100, 500);
            graphStore.AddEdge("CorpA", "Dept1", "OWNS");
            graphStore.AddEdge("Dept1", "Server99", "OPERATES");
            context.RegisterGraphStore("topology", graphStore);

            // 4. DocTree
            var parser = new MarkdownTreeParser();
            var docTree = parser.Parse(@"# Policies
## Enterprise SLAs
Enterprise SLAs provide 99.99% availability.
## Standard SLAs
Standard SLAs provide 99.0% availability.");
            context.RegisterDocTree("sla_docs", docTree);

            // Build bundle
            var builder = new BundleBuilder(context);
            string prompt = builder
                .BeginBundle("Security Incident Resolution")
                .AppendTabularRow("Client Metrics", sb => sb.AppendLine("Client: CorpA | Plan: Enterprise | MRR: $50,000"))
                .AppendGraphTopology("topology", "CorpA", 2, "INFRASTRUCTURE MAP")
                .AppendDocumentTreeSection("sla_docs", "Enterprise SLAs", "ACTIVE SLA")
                .AppendVectorContext("tickets", new float[] { 1f, 0f, 0f, 0f }, 1, "SIMILAR INCIDENTS")
                .Build();

            // Assert output contains all layers
            Assert.Contains("SYSTEM RESOLVED CONTEXT BUNDLE: SECURITY INCIDENT RESOLUTION", prompt);
            Assert.Contains("--- [DATA LAYER] CLIENT METRICS ---", prompt);
            Assert.Contains("Client: CorpA | Plan: Enterprise", prompt);
            Assert.Contains("--- [RELATIONAL LAYER] INFRASTRUCTURE MAP (2 HOPS FROM CorpA) ---", prompt);
            Assert.Contains("CorpA", prompt);
            Assert.Contains("--- [HIERARCHICAL LAYER] ACTIVE SLA ---", prompt);
            Assert.Contains("Enterprise SLAs provide 99.99% availability.", prompt);
            Assert.Contains("--- [SEMANTIC LAYER] SIMILAR INCIDENTS ---", prompt);
            Assert.Contains("Ticket #101", prompt);
            Assert.Contains("END OF RESOLVED CONTEXT BUNDLE", prompt);
        }

        [Fact]
        public void CompactingBundleBuilder_EndToEnd_CompactsWithinBudget()
        {
            var context = new BundleContext();

            // Tabular data
            context.RegisterTabularData("data", "Sample");

            // Graph store
            var graphStore = new GraphStore(100, 500);
            graphStore.AddEdge("Node1", "Node2", "LINK");
            context.RegisterGraphStore("graph", graphStore);

            // DocTree
            var parser = new MarkdownTreeParser();
            var docTree = parser.Parse(@"# Root
## Very Long Section
" + new string('W', 800));
            context.RegisterDocTree("docs", docTree);

            // Vector index
            using var vectorStorage = new InMemoryVectorStorage(4);
            using var vectorIndex = new VectorIndex(vectorStorage);
            vectorIndex.Add(new float[] { 0f, 1f, 0f, 0f }, "Vector Match: " + new string('V', 400));
            context.RegisterVectorIndex("vec", vectorIndex);

            // Total tokens without compaction would exceed 350 tokens
            int budgetLimit = 120;
            var budget = new TokenBudget(budgetLimit, defaultStrategy: CompactionStrategy.StrictPrune);
            var builder = new CompactingBundleBuilder(budget, context);

            builder.BeginBundle("Budgeted Run")
                .AppendTabularRow("Data", 10, "DATA LAYER", sb => sb.AppendLine("Metric: 42"))
                .AppendGraphTopology("Graph", 50, "graph", "Node1", 1)
                .AppendDocumentTreeSection("DocTree", 30, "docs", "Very Long Section")
                .AppendVectorContext("Vectors", 20, "vec", new float[] { 0f, 1f, 0f, 0f }, 1);

            string prompt = builder.Build();

            int estimatedTokens = budget.EstimateTokens(prompt);
            Assert.True(estimatedTokens <= budgetLimit, $"Expected <= {budgetLimit}, but was {estimatedTokens}");
            // Fixed header must survive
            Assert.Contains("SYSTEM RESOLVED CONTEXT BUNDLE", prompt);
        }
    }
}
