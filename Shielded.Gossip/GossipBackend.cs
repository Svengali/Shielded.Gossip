﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Shielded.Gossip
{
    /// <summary>
    /// A backend supporting a key/value store which is distributed using a simple gossip protocol
    /// implementation. Use it in ordinary <see cref="Shield"/> transactions.
    /// Values should be CRDTs, implementing <see cref="IMergeable{T}"/>, or you can use the
    /// <see cref="Multiple{T}"/> and <see cref="Vc{T}"/> wrappers to make them a CRDT. If a type
    /// implements <see cref="IDeletable"/>, it can be deleted from the storage.
    /// </summary>
    public class GossipBackend : IGossipBackend, IDisposable
    {
        private readonly ShieldedDictNc<string, MessageItem> _local = new ShieldedDictNc<string, MessageItem>();
        private readonly ReverseTimeIndex _freshIndex;
        private readonly Shielded<VersionHash> _databaseHash = new Shielded<VersionHash>();
        private readonly ShieldedLocal<Dictionary<string, MessageItem>> _toMail = new ShieldedLocal<Dictionary<string, MessageItem>>();

        private readonly Timer _gossipTimer;
        private readonly Timer _deletableTimer;

        public readonly ITransport Transport;
        public readonly GossipConfiguration Configuration;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="transport">The message transport to use. The backend will dispose it when it gets disposed.</param>
        /// <param name="configuration">The configuration.</param>
        public GossipBackend(ITransport transport, GossipConfiguration configuration)
        {
            Transport = transport;
            Configuration = configuration;
            _freshIndex = new ReverseTimeIndex(GetItem);
            _gossipTimer = new Timer(_ => SpreadRumors(), null, Configuration.GossipInterval, Configuration.GossipInterval);
            _deletableTimer = new Timer(GetDeletableTimerMethod(), null, Configuration.DeletableCleanUpInterval, Configuration.DeletableCleanUpInterval);

            Transport.MessageHandler = Transport_MessageHandler;
        }

        private TimerCallback GetDeletableTimerMethod()
        {
            var lastFreshness = new Shielded<long>();
            var lockObj = new object();
            return _ =>
            {
                bool lockTaken = false;
                try
                {
                    Monitor.TryEnter(lockObj, ref lockTaken);
                    if (!lockTaken)
                        return;
                    var (toRemove, freshness) = Shield.InTransaction(() =>
                        (_freshIndex.SkipWhile(i => i.Freshness > lastFreshness)
                            .Where(i => i.Item.Deletable)
                            .Select(i => i.Item)
                            .ToArray(),
                        _freshIndex.LastFreshness));
                    // minor issue - the following transaction will make all the deletable items removable
                    // from the time index, but they won't be actually removed until the next iteration.
                    Shield.InTransaction(() =>
                    {
                        foreach (var item in toRemove)
                            if (_local.TryGetValue(item.Key, out var mi) && mi == item)
                                _local.Remove(item.Key);
                        lastFreshness.Value = freshness;
                    });
                }
                finally
                {
                    if (lockTaken)
                        Monitor.Exit(lockObj);
                }
            };
        }

        private void DoDirectMail(MessageItem[] items)
        {
            if (Configuration.DirectMail == DirectMailType.Off || items.Length == 0)
                return;
            var package = new DirectMail { Items = items };
            if (Configuration.DirectMail == DirectMailType.Always)
            {
                Transport.Broadcast(package);
            }
            else
            {
                foreach (var server in Transport.Servers)
                    SendMail(server, package);
            }
        }

        private void SendMail(string server, DirectMail package)
        {
            if (Configuration.DirectMail == DirectMailType.Always ||
                Configuration.DirectMail == DirectMailType.GossipSupressed && !IsGossipActive(server))
                Transport.Send(server, package, false);
            else if (Configuration.DirectMail == DirectMailType.StartGossip)
                StartGossip(server);
        }

        private enum MessageType
        {
            Start,
            Reply,
            End
        }

        private class GossipState
        {
            public readonly int? LastReceivedMsgId;
            public readonly int LastSentMsgId;
            public ReverseTimeIndex.Enumerator LastWindowStart { get; private set; }
            public readonly int LastPackageSize;
            public readonly MessageType LastSentMsgType;
            // used only when LastSentMsgType == MessageType.Start
            public readonly int? PreviousSentEndMsgId;

            public readonly int CreationTickCount = Environment.TickCount;

            public GossipState(int? lastReceivedMsgId, int lastSentMsgId, ReverseTimeIndex.Enumerator lastWindowStart,
                int lastPackageSize, MessageType lastSentMsgType, int? previousSentEndMsgId = null)
            {
                LastReceivedMsgId = lastReceivedMsgId;
                LastSentMsgId = lastSentMsgId;
                LastWindowStart = lastWindowStart;
                LastPackageSize = lastPackageSize;
                LastSentMsgType = lastSentMsgType;
                PreviousSentEndMsgId = previousSentEndMsgId;
            }

            public void ReleaseEnumerator()
            {
                LastWindowStart = default;
            }
        }

        private ShieldedDictNc<string, GossipState> _gossipStates = new ShieldedDictNc<string, GossipState>(StringComparer.InvariantCultureIgnoreCase);

        private bool HasTimedOut(GossipState state) =>
            unchecked(Environment.TickCount - state.CreationTickCount) >= Configuration.AntiEntropyIdleTimeout;

        private bool IsGossipActive(string server) => Shield.InTransaction(() =>
        {
            if (!_gossipStates.TryGetValue(server, out var state) || state.LastSentMsgType == MessageType.End)
                return false;
            if (HasTimedOut(state))
            {
                state.ReleaseEnumerator();
                return false;
            }
            return true;
        });

        private void SpreadRumors()
        {
            try
            {
                Shield.InTransaction(() =>
                {
                    var servers = Transport.Servers;
                    if (servers == null || !servers.Any())
                        return;
                    var limit = Configuration.AntiEntropyHuntingLimit;
                    var rand = new Random();
                    string server;
                    do
                    {
                        server = servers.Skip(rand.Next(servers.Count)).First();
                    }
                    while (!StartGossip(server) && --limit >= 0);
                });
            }
            catch { } // TODO
        }

        private bool StartGossip(string server) => Shield.InTransaction(() =>
        {
            if (IsGossipActive(server))
                return false;
            var lastReceivedId = _gossipStates.TryGetValue(server, out var oldState) ? oldState.LastReceivedMsgId : null;
            var toSend = GetPackage(Configuration.AntiEntropyInitialSize, default, null, null, null, out var newWindowStart);
            var msg = new GossipStart
            {
                From = Transport.OwnId,
                DatabaseHash = _databaseHash.Value,
                Items = toSend.Length == 0 ? null : toSend,
                WindowStart = toSend.Length == 0 || newWindowStart.IsDefault ? 0 : newWindowStart.Current.Freshness,
                WindowEnd = toSend.Length == 0 ? _freshIndex.LastFreshness : toSend[0].Freshness,
                ReplyToId = lastReceivedId,
            };
            _gossipStates[server] = new GossipState(null, msg.MessageId, newWindowStart, Configuration.AntiEntropyInitialSize,
                MessageType.Start, oldState?.LastSentMsgType == MessageType.End ? (int?)oldState.LastSentMsgId : null);
            Shield.SideEffect(() => Transport.Send(server, msg, true));
            return true;
        });

        private object Transport_MessageHandler(object msg)
        {
            switch (msg)
            {
                case DirectMail trans:
                    ApplyItems(trans.Items, true);
                    return null;

                case GossipMessage gossip:
                    var pkg = gossip as NewGossip;
                    long? ignoreUpToFreshness = null;
                    HashSet<string> keysToIgnore = null;
                    if (pkg?.Items != null && gossip.DatabaseHash != _databaseHash.Value)
                    {
                        Shield.InTransaction(() =>
                        {
                            keysToIgnore = ApplyItems(pkg.Items, true);
                            if (keysToIgnore != null)
                            {
                                if (!_local.Changes.Any())
                                    ignoreUpToFreshness = _freshIndex.LastFreshness;
                                else
                                    Shield.SyncSideEffect(() =>
                                    {
                                        // we don't want to send back the same things we just received. so, ignore all keys from
                                        // the incoming msg for which the result of application was Greater or Equal, which means our
                                        // local value is (now) identical to the received one, unless they also appear in _toMail,
                                        // which means a Changed handler made further changes to them.
                                        // we need the max freshness in case these fields change after this transaction.
                                        ignoreUpToFreshness = _freshIndex.LastFreshness;
                                        if (_toMail.HasValue)
                                            keysToIgnore.ExceptWith(_toMail.Value.Keys);
                                    });
                            }
                        });
                    }
                    return GetReply(gossip, ignoreUpToFreshness, keysToIgnore);

                case KillGossip kill:
                    Shield.InTransaction(() =>
                    {
                        if (_gossipStates.TryGetValue(kill.From, out var state) && state.LastSentMsgId == kill.ReplyToId)
                            _gossipStates.Remove(kill.From);
                    });
                    return null;

                default:
                    throw new ApplicationException($"Unexpected message type: {msg.GetType()}");
            }
        }

        private readonly ApplyMethods _applyMethods = new ApplyMethods(typeof(GossipBackend)
            .GetMethod("SetInternal", BindingFlags.Instance | BindingFlags.NonPublic));

        private static readonly ShieldedLocal<long> _freshnessContext = new ShieldedLocal<long>();

        /// <summary>
        /// Applies the given items internally, does not cause any direct mail. Applies them
        /// starting from the last, and if they have different Freshness values, they will be
        /// indexed in this backend with different values too. It is assumed they are sorted
        /// by descending freshness. Result contains keys whose values are (now) equal in our
        /// DB to the received values.
        /// </summary>
        internal HashSet<string> ApplyItems(MessageItem[] items, bool respectFreshness) => Shield.InTransaction(() =>
        {
            if (items == null || items.Length == 0)
                return null;
            long prevItemFreshness = items[items.Length - 1].Freshness;
            bool freshnessUtilized = false;
            HashSet<string> equalKeys = null;
            for (var i = items.Length - 1; i >= 0; i--)
            {
                var item = items[i];
                if (item.Data == null)
                    continue;
                if (_local.TryGetValue(item.Key, out var curr) && IsByteEqual(curr.Data, item.Data))
                {
                    if (equalKeys == null)
                        equalKeys = new HashSet<string>();
                    equalKeys.Add(item.Key);
                    continue;
                }
                if (respectFreshness && prevItemFreshness != item.Freshness)
                {
                    prevItemFreshness = item.Freshness;
                    if (freshnessUtilized)
                        _freshnessContext.Value = _freshnessContext.GetValueOrDefault() + 1;
                    freshnessUtilized = false;
                }
                var obj = item.Value;
                var method = _applyMethods.Get(this, obj.GetType());
                var itemResult = method(item.Key, obj);
                freshnessUtilized |= (itemResult & VectorRelationship.Greater) != 0;
                if (itemResult == VectorRelationship.Greater || itemResult == VectorRelationship.Equal)
                {
                    if (equalKeys == null)
                        equalKeys = new HashSet<string>();
                    equalKeys.Add(item.Key);
                }
            }
            return equalKeys;
        });

        private static bool IsByteEqual(byte[] one, byte[] two)
        {
            if (one == null && two == null)
                return true;
            if (one == null || two == null || one.Length != two.Length)
                return false;
            var len = one.Length;
            for (int i = 0; i < len; i++)
                if (one[i] != two[i])
                    return false;
            return true;
        }

        private bool ShouldReply(GossipMessage msg, out GossipState currentState, out bool sendKill)
        {
            currentState = null;
            sendKill = false;
            var isStarter = msg is GossipStart;
            var hisReply = msg as GossipReply;
            // if our state is obsolete, we will only accept starter messages.
            if (!_gossipStates.TryGetValue(msg.From, out var state) || HasTimedOut(state))
            {
                sendKill = hisReply != null;
                return isStarter;
            }

            // we have an active state. handling starter messages first.
            if (isStarter)
            {
                // this means he was aware of our last message, whatever it was, and chose to send us this. OK.
                // this may happen if he sent us a GossipEnd before, but we did not receive it (yet).
                if (msg.ReplyToId == state.LastSentMsgId)
                    return true;
                else
                    // otherwise, we can only accept a starter if our msg was an end msg, or in case of simultaneous start,
                    // if the other server has higher "prio".
                    return state.LastSentMsgType == MessageType.End ||
                        state.LastSentMsgType == MessageType.Start &&
                            StringComparer.InvariantCultureIgnoreCase.Compare(msg.From, Transport.OwnId) < 0;
            }

            // he's replying. special case: he's replying to our end message, and we already sent a GossipStart after
            // that end message. we give preference to continuing the old chain in that case.
            if (state.LastSentMsgType == MessageType.Start &&
                state.PreviousSentEndMsgId != null && state.PreviousSentEndMsgId == msg.ReplyToId)
            {
                // when replying to our end message, it must be a GossipReply and he should send us LastWindowStart == 0.
                if (hisReply == null || hisReply.LastWindowStart > 0)
                    throw new ApplicationException("Reply chain logic failure.");
                return true;
            }
            // otherwise if he's replying to something else, he must send us a correct ReplyToId
            if (state.LastSentMsgId != msg.ReplyToId)
            {
                sendKill = hisReply != null && hisReply.MessageId != state.LastReceivedMsgId;
                return false;
            }

            // so, he's replying. this is just a safety check, to see if the windows match. they will.
            var ourLastStart = hisReply?.LastWindowStart ?? 0;
            if (ourLastStart > 0 && ourLastStart != (state.LastWindowStart.IsDefault ? 0 : state.LastWindowStart.Current.Freshness))
                throw new ApplicationException("Reply chain logic failure.");

            // OK, everything checks out
            currentState = state;
            return true;
        }

        private object GetReply(GossipMessage replyTo,
            long? ignoreUpToFreshness = null, HashSet<string> keysToIgnore = null) => Shield.InTransaction<object>(() =>
        {
            var server = replyTo.From;
            var hisNews = replyTo as NewGossip;
            var hisReply = replyTo as GossipReply;
            var hisEnd = replyTo as GossipEnd;

            if (!ShouldReply(replyTo, out var currentState, out var sendKill))
            {
                if (sendKill)
                    return new KillGossip { From = Transport.OwnId, ReplyToId = replyTo.MessageId };
                return null;
            }

            var lastWindowStart = hisReply?.LastWindowStart ?? 0;
            var lastWindowEnd = hisReply?.LastWindowEnd ?? hisEnd?.LastWindowEnd;

            var ownHash = _databaseHash.Value;
            if (ownHash == replyTo.DatabaseHash)
            {
                if (hisEnd != null)
                {
                    _gossipStates.Remove(server);
                    return null;
                }
                else
                    return PrepareEnd(hisNews, currentState?.LastPackageSize ?? 0, true);
            }

            var packageSize = currentState == null
                ? Configuration.AntiEntropyInitialSize
                : Math.Max(Configuration.AntiEntropyInitialSize,
                    Math.Min(Configuration.AntiEntropyCutoff, currentState.LastPackageSize * 2));
            var toSend = GetPackage(packageSize,
                lastWindowStart > 0 ? currentState.LastWindowStart : default, lastWindowEnd,
                ignoreUpToFreshness, keysToIgnore, out var newStartEnumerator);

            if (toSend.Length == 0)
            {
                if (hisNews == null)
                {
                    _gossipStates.Remove(server);
                    return null;
                }
                else if (hisNews.Items == null || hisNews.Items.Length == 0)
                    return PrepareEnd(hisNews, currentState?.LastPackageSize ?? 0, false);
                else
                    return PrepareReply(server, new GossipReply
                    {
                        From = Transport.OwnId,
                        DatabaseHash = ownHash,
                        Items = null,
                        WindowStart = 0,
                        WindowEnd = _freshIndex.LastFreshness,
                        LastWindowStart = hisNews.WindowStart,
                        LastWindowEnd = hisNews.WindowEnd,
                        ReplyToId = replyTo.MessageId,
                    }, newStartEnumerator, currentState?.LastPackageSize ?? 0);
            }

            var windowStart = newStartEnumerator.IsDefault ? 0 : newStartEnumerator.Current.Freshness;
            var windowEnd = _freshIndex.LastFreshness;
            return PrepareReply(server, new GossipReply
            {
                From = Transport.OwnId,
                DatabaseHash = ownHash,
                Items = toSend,
                WindowStart = windowStart,
                WindowEnd = windowEnd,
                LastWindowStart = hisNews?.WindowStart ?? 0,
                LastWindowEnd = (hisNews?.WindowEnd ?? hisEnd?.WindowEnd).Value,
                ReplyToId = replyTo.MessageId,
            }, newStartEnumerator, packageSize);
        });

        private GossipEnd PrepareEnd(NewGossip hisNews, int lastPackageSize, bool success)
        {
            var ourHash = _databaseHash.Value;
            var maxFresh = _freshIndex.LastFreshness;
            var endMsg = new GossipEnd
            {
                From = Transport.OwnId,
                Success = success,
                DatabaseHash = ourHash,
                WindowEnd = maxFresh,
                LastWindowEnd = hisNews.WindowEnd,
                ReplyToId = hisNews.MessageId,
            };
            // if we're sending GossipEnd, we clear this in transaction, to make sure
            // IsGossipActive is correct, and to guarantee that we actually are done.
            _gossipStates[hisNews.From] = new GossipState(hisNews.MessageId, endMsg.MessageId, default, lastPackageSize, MessageType.End);
            return endMsg;
        }

        private GossipReply PrepareReply(string server, GossipReply msg, ReverseTimeIndex.Enumerator startEnumerator, int newPackageSize)
        {
            Shield.SideEffect(() => Shield.InTransaction(() =>
            {
                // reply transactions are kept read-only since they conflict too easily,
                // and it really makes no difference, whatever we skipped now, we'll see
                // in the next reply. so we change this only as a side-effect.
                _gossipStates[server] = new GossipState(
                    msg.ReplyToId, msg.MessageId, startEnumerator, newPackageSize, MessageType.Reply);
            }));
            return msg;
        }

        private MessageItem[] GetPackage(int packageSize, ReverseTimeIndex.Enumerator lastWindowStart, long? lastWindowEnd,
            long? ignoreUpToFreshness, HashSet<string> keysToIgnore, out ReverseTimeIndex.Enumerator newWindowStart)
        {
            if (packageSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(packageSize), "The size of an anti-entropy package must be greater than zero.");
            int cutoff = Configuration.AntiEntropyCutoff;
            ReverseTimeIndex.Enumerator prevFreshnessStart = default;
            var result = new List<MessageItem>();
            bool interrupted = false;

            newWindowStart = _freshIndex.GetCloneableEnumerator();
            while (newWindowStart.MoveNext())
            {
                var item = newWindowStart.Current;
                if (item.Freshness <= lastWindowEnd)
                    break;
                if (prevFreshnessStart.IsDefault || prevFreshnessStart.Current.Freshness != item.Freshness)
                {
                    if (result.Count >= cutoff || (lastWindowEnd == null && result.Count >= packageSize))
                        return result.ToArray();
                    prevFreshnessStart = newWindowStart;
                }
                if (keysToIgnore == null || item.Freshness > ignoreUpToFreshness.Value || !keysToIgnore.Contains(item.Item.Key))
                {
                    if (result.Count == cutoff && !prevFreshnessStart.IsDefault)
                    {
                        newWindowStart = prevFreshnessStart;
                        var index = result.FindIndex(mi => mi.Freshness == item.Freshness);
                        result.RemoveRange(index, result.Count - index);
                        return result.ToArray();
                    }
                    result.Add(item.Item);
                }
            }
            newWindowStart.Dispose();

            newWindowStart = lastWindowStart;
            if (newWindowStart.IsDefault)
                return result.ToArray();

            // last time we iterated this one, we stopped without adding the current item. so this
            // will be a do/while. but first, let's see if that item is still up to date.
            if (newWindowStart.Current.Item != GetItem(newWindowStart.Current.Item.Key) && !newWindowStart.MoveNext())
            {
                newWindowStart.Dispose();
                newWindowStart = default;
                return result.ToArray();
            }
            do
            {
                var item = newWindowStart.Current;
                if (prevFreshnessStart.IsDefault || prevFreshnessStart.Current.Freshness != item.Freshness)
                {
                    if (result.Count >= cutoff || result.Count >= packageSize)
                    {
                        interrupted = true;
                        break;
                    }
                    prevFreshnessStart = newWindowStart;
                }
                if (result.Count == cutoff && !prevFreshnessStart.IsDefault)
                {
                    newWindowStart = prevFreshnessStart;
                    var index = result.FindIndex(mi => mi.Freshness == item.Freshness);
                    result.RemoveRange(index, result.Count - index);
                    return result.ToArray();
                }
                result.Add(item.Item);
            } while (newWindowStart.MoveNext());
            if (!interrupted)
            {
                newWindowStart.Dispose();
                newWindowStart = default;
            }
            return result.ToArray();
        }

        /// <summary>
        /// Tries to read the value under the given key. The type of the value must be a CRDT.
        /// </summary>
        public bool TryGet<TItem>(string key, out TItem item) where TItem : IMergeable<TItem>
        {
            item = default;
            if (!_local.TryGetValue(key, out MessageItem i))
                return false;
            item = (TItem)i.Value;
            return true;
        }

        internal MessageItem GetItem(string key)
        {
            return _local.TryGetValue(key, out MessageItem i) ? i : null;
        }

        private VersionHash GetHash<TItem>(string key, TItem i) where TItem : IMergeable<TItem>
        {
            return VersionHash.Hash(
                new[] { Encoding.UTF8.GetBytes(key) }
                .Concat(i.GetVersionBytes()));
        }

        /// <summary>
        /// A non-update, which ensures that when your local transaction is transmitted to other servers, this
        /// field will be transmitted as well, even if you did not change its value.
        /// </summary>
        /// <param name="key">The key to touch.</param>
        public void Touch(string key) => Shield.InTransaction(() =>
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException("key");
            if (!_local.TryGetValue(key, out var mi))
                return;
            var newItem = new MessageItem
            {
                Key = key,
                Data = mi.Data,
                Deletable = mi.Deletable,
                FreshnessOffset = _freshnessContext.GetValueOrDefault()
            };
            _local[key] = newItem;
            _freshIndex.Append(newItem);
            AddToMail(newItem);
        });

        private void AddToMail(MessageItem item)
        {
            var dict = _toMail.GetValueOrDefault();
            if (dict == null)
            {
                _toMail.Value = dict = new Dictionary<string, MessageItem>();
                Shield.SideEffect(() => DoDirectMail(dict.Values.ToArray()));
            }
            dict[item.Key] = item;
        }

        /// <summary>
        /// Sets the given value under the given key, merging it with any already existing value
        /// there. Returns the relationship of the new to the old value, or
        /// <see cref="VectorRelationship.Greater"/> if there is no old value. The storage gets affected
        /// only if the result of comparison is Greater or Conflict.
        /// </summary>
        public VectorRelationship Set<TItem>(string key, TItem val) where TItem : IMergeable<TItem>
            => Shield.InTransaction(() =>
        {
            return SetInternalWithAddToMail(key, val, true);
        });

        private VectorRelationship SetInternal<TItem>(string key, TItem val) where TItem : IMergeable<TItem>
        {
            return SetInternalWithAddToMail(key, val, false);
        }

        private VectorRelationship SetInternalWithAddToMail<TItem>(string key, TItem val, bool addToMail) where TItem : IMergeable<TItem>
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentNullException("key");
            if (val == null)
                throw new ArgumentNullException("val");
            if (_local.TryGetValue(key, out MessageItem oldItem))
            {
                var oldVal = (TItem)oldItem.Value;
                var cmp = val.VectorCompare(oldVal);
                if (cmp == VectorRelationship.Less || cmp == VectorRelationship.Equal)
                    return cmp;
                // we support this only for safety - a CanDelete should never accept any changes, nor switch to !CanDelete.
                var oldDeletable = oldVal is IDeletable oldDel && oldDel.CanDelete;
                var oldHash = oldDeletable ? default : GetHash(key, oldVal);
                // in case someone screws up the MergeWith impl, we call it after extracting the critical info above.
                val = oldVal.MergeWith(val);
                if (val == null)
                    throw new ApplicationException("IMergeable.MergeWith should not return null.");

                var deletable = val is IDeletable del && del.CanDelete;
                var newItem = new MessageItem
                {
                    Key = key,
                    Value = val,
                    Deletable = deletable,
                    FreshnessOffset = _freshnessContext.GetValueOrDefault(),
                };
                _local[key] = newItem;
                _freshIndex.Append(newItem);
                if (addToMail)
                    AddToMail(newItem);
                var hash = oldHash ^ (deletable ? default : GetHash(key, val));
                _databaseHash.Commute((ref VersionHash h) => h ^= hash);

                OnChanged(key, oldVal, val);
                return cmp;
            }
            else
            {
                if (val is IDeletable del && del.CanDelete)
                    return VectorRelationship.Equal;
                var newItem = new MessageItem
                {
                    Key = key,
                    Value = val,
                    FreshnessOffset = _freshnessContext.GetValueOrDefault(),
                };
                _local[key] = newItem;
                _freshIndex.Append(newItem);
                if (addToMail)
                    AddToMail(newItem);
                var hash = GetHash(key, val);
                _databaseHash.Commute((ref VersionHash h) => h ^= hash);

                OnChanged(key, default(TItem), val);
                return VectorRelationship.Greater;
            }
        }

        private void OnChanged(string key, object oldVal, object newVal)
        {
            var ev = new ChangedEventArgs(key, oldVal, newVal);
            Changed.Raise(this, ev);
        }

        /// <summary>
        /// Fired when any key changes.
        /// </summary>
        public readonly ShieldedEvent<ChangedEventArgs> Changed = new ShieldedEvent<ChangedEventArgs>();

        public void Dispose()
        {
            Transport.Dispose();
            _gossipTimer.Dispose();
            _deletableTimer.Dispose();
        }

        /// <summary>
        /// An enumerable of keys read or written into by the current transaction. Includes
        /// keys that did not have a value.
        /// </summary>
        public IEnumerable<string> Reads => _local.Reads;

        /// <summary>
        /// An enumerable of keys written into by the current transaction.
        /// </summary>
        public IEnumerable<string> Changes => _local.Changes;
    }
}
