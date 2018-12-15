﻿using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shielded.Standard;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Shielded.Gossip.Tests
{
    [TestClass]
    public class ConsistentTests : GossipBackendThreeNodeTestsBase<ConsistentGossipBackend, TcpTransport>
    {
        public class TestClass
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public int Counter { get; set; }
        }

        protected override ConsistentGossipBackend CreateBackend(ITransport transport, GossipConfiguration configuration)
        {
            return new ConsistentGossipBackend(transport, configuration);
        }

        protected override TcpTransport CreateTransport(string ownId, IPEndPoint localEndpoint,
            IEnumerable<KeyValuePair<string, IPEndPoint>> servers)
        {
            var transport = new TcpTransport(ownId, localEndpoint,
                new ShieldedDict<string, IPEndPoint>(servers, null, StringComparer.InvariantCultureIgnoreCase));
            transport.Error += OnListenerError;
            transport.StartListening();
            return transport;
        }

        [TestMethod]
        public void Consistent_Basics()
        {
            var testEntity = new TestClass { Id = 1, Name = "One" };

            Assert.IsTrue(_backends[A].RunConsistent(() => { _backends[A].SetVc("key", testEntity.Clock(A)); }).Result);

            CheckProtocols();

            var (success, multi) = _backends[B].RunConsistent(() => _backends[B].TryGetClocked<TestClass>("key"), 100)
                .Result;

            Assert.IsTrue(success);
            var read = multi.Single();
            Assert.AreEqual(testEntity.Id, read.Value.Id);
            Assert.AreEqual(testEntity.Name, read.Value.Name);
            Assert.AreEqual((A, 1), read.Clock);
        }

        [TestMethod]
        public void Consistent_Race()
        {
            const int transactions = 500;
            const int fieldCount = 50;

            foreach (var back in _backends.Values)
            {
                back.Configuration.DirectMail = DirectMailType.StartGossip;
            }

            var bools = Task.WhenAll(ParallelEnumerable.Range(1, transactions).Select(i =>
            {
                var backend = _backends.Values.Skip(i % 3).First();
                var id = (i % fieldCount);
                var key = "key" + id;
                return backend.RunConsistent(() =>
                {
                    var newVal = backend.TryGetClocked<TestClass>(key)
                        .SingleOrDefault()
                        .NextVersion(backend.Transport.OwnId);
                    if (newVal.Value == null)
                        newVal.Value = new TestClass { Id = id };
                    newVal.Value.Counter = newVal.Value.Counter + 1;
                    backend.SetVc(key, newVal);
                }, 100);
            })).Result;
            var expected = bools.Count(b => b);

            CheckProtocols();

            var read = _backends[B].RunConsistent(() =>
                Enumerable.Range(0, fieldCount).Sum(i =>
                    _backends[B].TryGetClocked<TestClass>("key" + i).SingleOrDefault().Value?.Counter),
                100).Result;

            Assert.IsTrue(read.Success);
            Assert.AreEqual(expected, read.Value);
            Assert.AreEqual(transactions, read.Value);
        }

        [TestMethod]
        public void Consistent_Versioned()
        {
            var testEntity = new TestClass { Id = 1, Name = "New entity" };

            _backends[A].RunConsistent(() => { _backends[A].Set("key", testEntity.Version()); }).Wait();

            Versioned<TestClass> read = default, next = default;
            _backends[B].RunConsistent(() =>
            {
                read = _backends[B].TryGetVersioned<TestClass>("key");
                // if the commit is not yet fully complete, we won't see anything here. since the code
                // later here depends on reading it, we'll just busy-wait by rolling back. NB that
                // if we had continued, this transaction would get retried anyway because of that
                // incomplete commit.
                if (read.Value == null)
                    Shield.Rollback();
                Assert.AreEqual(testEntity.Name, read.Value.Name);

                next = read.NextVersion();
                next.Value = new TestClass { Id = 1, Name = "Version 2" };
                _backends[B].Set("key", next);
            }).Wait();

            read = _backends[C].RunConsistent(() => _backends[C].TryGetVersioned<TestClass>("key")).Result.Value;
            Assert.AreEqual(next.Value.Name, read.Value.Name);
        }
    }
}
