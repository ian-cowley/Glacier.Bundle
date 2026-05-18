using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Glacier.Bundle.Core;
using Glacier.Vector.Index;
using Glacier.Vector.Storage;
using Glacier.Graph.Storage;
using Glacier.DocTree.Core;

namespace Glacier.Bundle.Demo
{
    // A lightweight representation of a client record to model Glacier.Polaris schema data
    public class PolarisRowStub
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string PlanTier { get; set; } = string.Empty;
        public decimal LifetimeValue { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    class Program
    {
        static void Main()
        {
            Console.WriteLine("======================================================================");
            Console.WriteLine(" Glacier.Bundle | Unified AI Semantic Orchestration Engine");
            Console.WriteLine("======================================================================\n");

            // 1. Initializing the Registry
            var bundleCtx = new BundleContext();

            // 2. Setup Polaris Layer Stub (Tabular)
            Console.WriteLine("[1] Populating Polaris Tabular Stub...");
            var clientDatabase = new Dictionary<string, PolarisRowStub>
            {
                { "User_9021", new PolarisRowStub { Id = "User_9021", Name = "DeltaCorp Ltd", PlanTier = "Enterprise", LifetimeValue = 85200.00m, Status = "Active" } }
            };
            bundleCtx.RegisterTabularData("Polaris_Clients", clientDatabase);

            // 3. Setup Vector Layer (Semantic)
            Console.WriteLine("[2] Populating Vector Search Store (SIMD)...");
            using var vectorStorage = new InMemoryVectorStorage(1536);
            using var vectorIndex = new VectorIndex(vectorStorage);

            // Register a dummy 1536 vector for demo
            var mockVector = new float[1536];
            mockVector[0] = 1.0f; // Normalized
            vectorIndex.Add(mockVector, "Historical Ticket #4823: Customer requested custom API access rate expansion. Granted temporary bypass.");
            bundleCtx.RegisterVectorIndex("Vector_Tickets", vectorIndex);

            // 4. Setup Graph Layer (Relational)
            Console.WriteLine("[3] Populating Relational Graph Store...");
            var graphStore = new GraphStore(1000, 5000);
            // Build simple topology: DeltaCorp owns sub-accounts and shares an API gateway
            graphStore.AddEdge("User_9021", "SubAccount_A", "OWNS");
            graphStore.AddEdge("User_9021", "SubAccount_B", "OWNS");
            graphStore.AddEdge("SubAccount_B", "Suspicious_IP_88", "ACCESSED_BY");
            bundleCtx.RegisterGraphStore("Graph_Topology", graphStore);

            // 5. Setup DocTree Layer (Hierarchical Document Parser)
            Console.WriteLine("[4] Parsing SLA Document Tree...");
            string sldMarkdown = @"
# Service Level Agreements

Welcome to the company policy registry.

## Enterprise SLAs

Enterprise accounts enjoy a guaranteed 99.99% uptime with immediate 15-minute response times.
No hardware throttling is applied.

## Free Tier SLAs

Free tier accounts have a best-effort 99% uptime target with standard 48-hour response times.
";
            var docTreeParser = new MarkdownTreeParser();
            var docRoot = docTreeParser.Parse(sldMarkdown);
            bundleCtx.RegisterDocTree("DocTree_SLA", docRoot);

            Console.WriteLine("\nAll high-performance storage engines registered in BundleContext!");

            // 6. COMPILE CONTEXT BUNDLE IN REAL-TIME
            Console.WriteLine("\n[5] Executing high-speed Bundle compilation...");

            var sw = Stopwatch.StartNew();

            string targetCustomerId = "User_9021";
            var clientData = bundleCtx.GetTabularData<Dictionary<string, PolarisRowStub>>("Polaris_Clients")[targetCustomerId];

            var bundle = new BundleBuilder(bundleCtx)
                .BeginBundle($"Customer Escalation: {clientData.Name}")
                .AppendTabularRow("Client CRM Profile", sb => sb
                    .AppendLine($"  ID: {clientData.Id}")
                    .AppendLine($"  Name: {clientData.Name}")
                    .AppendLine($"  Tier: {clientData.PlanTier}")
                    .AppendLine($"  Value: {clientData.LifetimeValue:C}")
                    .AppendLine($"  Status: {clientData.Status}"))
                .AppendGraphTopology("Graph_Topology", targetCustomerId, maxHops: 2, label: "Relational Fraud Verification")
                .AppendDocumentTreeSection("DocTree_SLA", $"{clientData.PlanTier} SLAs", label: "Structural SLA Terms")
                .AppendVectorContext("Vector_Tickets", mockVector, topK: 1, label: "Semantic Historical Matching")
                .Build();

            sw.Stop();

            Console.WriteLine($"\nBundle compiled in {sw.Elapsed.TotalMilliseconds:F4} ms!");
            Console.WriteLine("\n" + bundle);
        }
    }
}