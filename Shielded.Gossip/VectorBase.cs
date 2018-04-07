﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;

namespace Shielded.Gossip
{
    [DataContract(Namespace = "")]
    public struct VectorItem<T>
    {
        [DataMember]
        public string ServerId { get; set; }
        [DataMember]
        public T Value { get; set; }

        public VectorItem(string serverId, T value)
        {
            if (string.IsNullOrWhiteSpace(serverId))
                throw new ArgumentNullException(nameof(serverId));
            ServerId = serverId;
            Value = value;
        }
    }

    [DataContract(Namespace = "")]
    public abstract class VectorBase<TVec, T> : IEquatable<TVec>, IMergeable<TVec, TVec>
        where TVec : VectorBase<TVec, T>, new()
        where T : IEquatable<T>
    {
        public VectorBase() { }

        public VectorBase(params VectorItem<T>[] items)
        {
            Items = items;
        }

        public VectorBase(string ownServerId, T item) : this(new VectorItem<T>(ownServerId, item)) { }

        [DataMember]
        public VectorItem<T>[] Items { get; set; }

        protected virtual IEqualityComparer<T> EqualityComparer => EqualityComparer<T>.Default;

        public bool Equals(TVec other)
        {
            var eqComparer = EqualityComparer;
            return Join(other, (left, right) => eqComparer.Equals(left, right)).All(b => b);
        }

        public override bool Equals(object obj) => (obj is TVec vec) && Equals(vec);

        public override int GetHashCode()
        {
            if (Items == null || Items.Length == 0)
                return 0;
            return FNV1a32.Hash(YieldFields());
        }

        public static bool operator ==(VectorBase<TVec, T> left, TVec right) => left.Equals(right);
        public static bool operator !=(VectorBase<TVec, T> left, TVec right) => !left.Equals(right);

        private IEnumerable<byte[]> YieldFields()
        {
            var idComparer = StringComparer.OrdinalIgnoreCase;
            var valueComparer = EqualityComparer;
            foreach (var item in Items.OrderBy(vi => vi.ServerId, idComparer))
            {
                yield return Encoding.UTF8.GetBytes(item.ServerId.ToLowerInvariant());
                yield return BitConverter.GetBytes(valueComparer.GetHashCode(item.Value));
            }
        }

        public VersionHash GetVersionHash()
        {
            if (Items == null || Items.Length == 0)
                return 0;
            return FNV1a64.Hash(YieldFields());
        }

        public override string ToString()
        {
            if (Items == null || Items.Length == 0)
                return "";
            return string.Join(" | ", Items.Select(i =>
                string.Format("({0}, {1})", i.ServerId, i.Value)));
        }

        protected virtual VectorRelationship Compare(T left, T right)
        {
            var cmp = Comparer<T>.Default.Compare(left, right);
            return
                cmp == 0 ? VectorRelationship.Equal :
                cmp > 0 ? VectorRelationship.Greater : VectorRelationship.Less;
        }

        public VectorRelationship VectorCompare(TVec other)
        {
            return Join(other, Compare).Aggregate(VectorRelationship.Equal, (a, b) => a | b);
        }

        public IEnumerable<TRes> Join<TRes>(TVec rightVec, Func<T, T, TRes> resultSelector)
        {
            return Join(rightVec, (_, l, r) => resultSelector(l, r));
        }

        public IEnumerable<TRes> Join<TRes>(TVec rightVec, Func<string, T, T, TRes> resultSelector)
        {
            var lefts = Items ?? Enumerable.Empty<VectorItem<T>>();
            var rights = rightVec?.Items ?? Enumerable.Empty<VectorItem<T>>();

            foreach (var grp in lefts.Select(i => (left: true, item: i))
                .Concat(rights.Select(i => (left: false, item: i)))
                .GroupBy(t => t.item.ServerId, StringComparer.OrdinalIgnoreCase))
            {
                // note that this validates that the same server ID is not present multiple times in either.
                var left = grp.SingleOrDefault(t => t.left).item;
                var right = grp.SingleOrDefault(t => !t.left).item;
                var serverId = left.ServerId ?? right.ServerId;
                if (string.IsNullOrWhiteSpace(serverId))
                    throw new InvalidOperationException("Vector server IDs may not be empty.");

                yield return resultSelector(serverId, left.Value, right.Value);
            }
        }

        public TVec Modify(string ownServerId, T value)
        {
            return Modify(ownServerId, _ => value);
        }

        public TVec Modify(string ownServerId, Func<T, T> modifier)
        {
            return new TVec
            {
                Items = GetModifiedItems(ownServerId, modifier).ToArray()
            };
        }

        protected IEnumerable<VectorItem<T>> GetModifiedItems(string ownServerId, Func<T, T> modifier)
        {
            bool foundIt = false;
            foreach (var item in (Items ?? Enumerable.Empty<VectorItem<T>>()))
            {
                if (string.IsNullOrWhiteSpace(item.ServerId))
                    throw new InvalidOperationException("Vector server IDs may not be empty.");
                if (item.ServerId.Equals(ownServerId, StringComparison.OrdinalIgnoreCase))
                {
                    if (foundIt)
                        throw new InvalidOperationException("Vector may not mention the same server multiple times.");
                    foundIt = true;
                    yield return new VectorItem<T>(ownServerId, modifier(item.Value));
                }
                else
                    yield return item;
            }
            if (!foundIt)
                yield return new VectorItem<T>(ownServerId, modifier(default(T)));
        }

        protected abstract T Merge(T left, T right);

        public TVec MergeWith(TVec other)
        {
            return new TVec
            {
                Items = Join(other, (serverId, left, right) => new VectorItem<T>
                {
                    ServerId = serverId,
                    Value = Merge(left, right),
                }).ToArray()
            };
        }

        public static TVec operator |(VectorBase<TVec, T> first, TVec second) =>
            first?.MergeWith(second) ?? second ?? new TVec();
    }
}
