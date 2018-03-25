﻿using System;

namespace Shielded.Gossip
{
    public class VectorClock : VectorBase<VectorClock, int>
    {
        public VectorClock() : base() { }
        public VectorClock(params VectorItem<int>[] items) : base(items) { }
        public VectorClock(string ownServerId, int init) : base(ownServerId, init) { }

        protected override int Merge(int left, int right) => Math.Max(left, right);

        public VectorClock Next(string ownServerId)
        {
            if (string.IsNullOrWhiteSpace(ownServerId))
                throw new ArgumentNullException();
            checked
            {
                return Modify(ownServerId, n => n + 1);
            }
        }

        public new VectorRelationship CompareWith(VectorClock other) => base.CompareWith(other);
    }
}