using System;
using System.Collections.Generic;
using Glacier.Bundle.Core;
using Glacier.DocTree.Core;
using Glacier.Graph.Storage;
using Glacier.Vector.Index;
using Glacier.Vector.Storage;
using Xunit;

namespace Glacier.Bundle.Tests
{
    public class BundleContextTests
    {
        [Fact]
        public void RegisterAndRetrieve_VectorIndex_Succeeds()
        {
            var context = new BundleContext();
            using var storage = new InMemoryVectorStorage(128);
            using var vectorIndex = new VectorIndex(storage);

            context.RegisterVectorIndex("embeddings", vectorIndex);

            var retrieved = context.GetVectorIndex("embeddings");
            Assert.Same(vectorIndex, retrieved);
        }

        [Fact]
        public void RegisterAndRetrieve_GraphStore_Succeeds()
        {
            var context = new BundleContext();
            var graphStore = new GraphStore();

            context.RegisterGraphStore("knowledge_graph", graphStore);

            var retrieved = context.GetGraphStore("knowledge_graph");
            Assert.Same(graphStore, retrieved);
        }

        [Fact]
        public void RegisterAndRetrieve_DocTree_Succeeds()
        {
            var context = new BundleContext();
            var docNode = new DocNode { Type = NodeType.Root, Content = "Doc Root" };

            context.RegisterDocTree("policy_manual", docNode);

            var retrieved = context.GetDocTree("policy_manual");
            Assert.Same(docNode, retrieved);
        }

        [Fact]
        public void RegisterAndRetrieve_TabularData_Succeeds()
        {
            var context = new BundleContext();
            var sampleTable = new List<string> { "Row 1", "Row 2" };

            context.RegisterTabularData("crm_metrics", sampleTable);

            var retrieved = context.GetTabularData<List<string>>("crm_metrics");
            Assert.Same(sampleTable, retrieved);
        }

        [Fact]
        public void GetMissingKey_ThrowsKeyNotFoundException()
        {
            var context = new BundleContext();

            Assert.Throws<KeyNotFoundException>(() => context.GetVectorIndex("nonexistent"));
            Assert.Throws<KeyNotFoundException>(() => context.GetGraphStore("nonexistent"));
            Assert.Throws<KeyNotFoundException>(() => context.GetDocTree("nonexistent"));
            Assert.Throws<KeyNotFoundException>(() => context.GetTabularData<object>("nonexistent"));
        }

        [Fact]
        public void RegisterNull_ThrowsArgumentNullException()
        {
            var context = new BundleContext();

            Assert.Throws<ArgumentNullException>(() => context.RegisterVectorIndex("key", null!));
            Assert.Throws<ArgumentNullException>(() => context.RegisterGraphStore("key", null!));
            Assert.Throws<ArgumentNullException>(() => context.RegisterDocTree("key", null!));
            Assert.Throws<ArgumentNullException>(() => context.RegisterTabularData<string>("key", null!));
        }
    }
}
